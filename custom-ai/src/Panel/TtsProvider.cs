using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Swyf.CustomAI;

// Draft form for POST /api/tts/test; a blank or omitted key reuses the saved one and nothing is ever persisted.
public sealed record TtsTestInput(string TtsBaseUrl = "", string TtsModel = "", string TtsVoice = "", string? TtsApiKey = null,
    double? TtsSpeed = null, string? Input = null);

// OpenAI-compatible speech client. Responses are raw PCM (24 kHz mono s16le), never JSON.
public sealed class TtsProvider : IDisposable
{
    public const int MaxInput = 4 * 1024;
    public const int MaxResponse = 8 * 1024 * 1024;
    public const int DeadlineSeconds = 30;
    public const int SampleRate = 24000;
    public const string DefaultTestInput = "Hello, this is a test of your custom voice.";

    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim all = new(8);
    public string? LastError { get; private set; }
    public long Completed;
    private readonly ConcurrentDictionary<(bool, string, string, string, string, double?), (string State, string? Error)> connectionStates = new();
    // TTS connection state covers only TTS settings; LLM edits must not discard a verified voice connection and voice maps never change reachability.
    private static (bool, string, string, string, string, double?) StateKey(Settings s)
        => (s.TtsEnabled, s.TtsBaseUrl, s.TtsModel, s.TtsVoice, s.TtsApiKey, s.TtsSpeed);

    public object ConnectionStatus(Settings settings, string? loadError)
    {
        var status = connectionStates.TryGetValue(StateKey(settings), out var value) ? value : (State: "untested", Error: (string?)null);
        return new { state = loadError != null ? "error" : !settings.TtsEnabled ? "disabled" : status.State,
            model = settings.TtsModel, lastError = loadError ?? status.Error, completed = Interlocked.Read(ref Completed) };
    }

    public static string ResolveVoice(Settings settings, string? voicePath)
    {
        var path = (voicePath ?? "").Trim();
        return path.Length > 0 && settings.TtsVoiceMap is { } voiceMap && voiceMap.TryGetValue(path, out var voice) ? voice : settings.TtsVoice;
    }

    public async Task<byte[]> Synthesize(string text, string voice, Settings settings, CancellationToken cancellation, int deadlineSeconds = DeadlineSeconds)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ApiFailure(400, "config", "TTS requires text to speak.");
        if (Encoding.UTF8.GetByteCount(text) > MaxInput) throw new ApiFailure(413, "size", "The text is too long for one TTS request.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(deadlineSeconds));
        var token = deadline.Token;
        bool hasAll = false;
        connectionStates[StateKey(settings)] = ("connecting", null);
        try
        {
            await all.WaitAsync(token); hasAll = true;
            SettingsStore.ValidateTts(settings, true);
            var body = new JsonObject { ["model"] = settings.TtsModel, ["voice"] = voice, ["input"] = text, ["response_format"] = "pcm" };
            if (settings.TtsSpeed.HasValue) body["speed"] = settings.TtsSpeed.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.TtsBaseUrl.TrimEnd('/') + "/audio/speech");
            if (settings.TtsApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.TtsApiKey);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                // The provider error body is never read: no remote payload may reach messages or logs.
                int code = (int)response.StatusCode;
                throw code switch
                {
                    401 or 403 => new ApiFailure(502, "auth", "The TTS provider denied access. Check your TTS API key and permissions."),
                    404 => new ApiFailure(502, "model_or_endpoint", "TTS model or voice not found. Check the TTS Base URL, model ID, and voice."),
                    400 or 422 => new ApiFailure(502, "model_parameters", "The TTS provider rejected the model, voice, or synthesis settings."),
                    429 => new ApiFailure(502, "rate_limit", "The TTS provider quota or rate limit was reached."),
                    _ => new ApiFailure(502, "provider_http", $"The TTS provider returned HTTP {code}.")
                };
            }
            var bytes = await Provider.ReadBounded(await response.Content.ReadAsStreamAsync(token), MaxResponse, token);
            if (bytes.Length == 0) throw new ApiFailure(502, "invalid_response", "The TTS provider returned no audio.");
            LastError = null;
            Interlocked.Increment(ref Completed);
            connectionStates[StateKey(settings)] = ("success", null);
            return bytes;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { LastError = $"The TTS provider did not respond within {deadlineSeconds} seconds."; connectionStates[StateKey(settings)] = ("error", LastError); throw new ApiFailure(504, "timeout", LastError); }
        catch (OperationCanceledException)
        { connectionStates[StateKey(settings)] = ("error", "The request was canceled."); throw; }
        catch (HttpRequestException)
        { LastError = "Could not connect to the TTS provider. Check the address, TLS, and network connection."; connectionStates[StateKey(settings)] = ("error", LastError); throw new ApiFailure(502, "connection", LastError); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        { LastError = "The TTS provider did not return usable audio."; connectionStates[StateKey(settings)] = ("error", LastError); throw new ApiFailure(502, "invalid_response", LastError); }
        catch (ApiFailure e) { LastError = e.Message; connectionStates[StateKey(settings)] = ("error", LastError); throw; }
        finally { if (hasAll) all.Release(); }
    }

    public void Dispose() { http.Dispose(); all.Dispose(); }
}
