using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Swyf.CustomAI;

public sealed record Startup(string InternalToken, string BrowserToken, string ConfigPath, int ParentId);
public sealed record Settings(bool Enabled = false, string BaseUrl = "", string Model = "", string ApiKey = "",
    double? Temperature = null, double? TopP = null, int? MaxTokens = null, string OutputMode = "schema", bool UseManifestDeX = false,
    bool TtsEnabled = false, string TtsBaseUrl = "", string TtsModel = "", string TtsVoice = "", string TtsApiKey = "",
    double? TtsSpeed = null, Dictionary<string, string>? TtsVoiceMap = null)
{
    // Voice maps compare by content so record equality (which drives provider connection state) survives save, load, and PUT round trips.
    public bool Equals(Settings? other) => other is not null && Enabled == other.Enabled && BaseUrl == other.BaseUrl &&
        Model == other.Model && ApiKey == other.ApiKey && Temperature == other.Temperature && TopP == other.TopP &&
        MaxTokens == other.MaxTokens && OutputMode == other.OutputMode && UseManifestDeX == other.UseManifestDeX &&
        TtsEnabled == other.TtsEnabled && TtsBaseUrl == other.TtsBaseUrl && TtsModel == other.TtsModel &&
        TtsVoice == other.TtsVoice && TtsApiKey == other.TtsApiKey && TtsSpeed == other.TtsSpeed &&
        VoiceMapsEqual(TtsVoiceMap, other.TtsVoiceMap);
    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(Enabled, BaseUrl, Model, ApiKey, Temperature, TopP, MaxTokens, OutputMode),
        HashCode.Combine(UseManifestDeX, TtsEnabled, TtsBaseUrl, TtsModel, TtsVoice, TtsApiKey, TtsSpeed),
        VoiceMapHash(TtsVoiceMap));
    private static bool VoiceMapsEqual(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        var left = a ?? EmptyVoiceMap; var right = b ?? EmptyVoiceMap;
        return left.Count == right.Count && left.All(e => right.TryGetValue(e.Key, out var voice) && voice == e.Value);
    }
    private static int VoiceMapHash(Dictionary<string, string>? map) { var hash = 0; foreach (var e in map ?? EmptyVoiceMap) hash ^= HashCode.Combine(e.Key, e.Value); return hash; }
    private static readonly Dictionary<string, string> EmptyVoiceMap = new();
}
public sealed record SettingsInput(bool Enabled, string BaseUrl, string Model, string? ApiKey = null,
    bool ClearApiKey = false, double? Temperature = null, double? TopP = null, int? MaxTokens = null, string OutputMode = "schema", bool UseManifestDeX = false,
    bool TtsEnabled = false, string TtsBaseUrl = "", string TtsModel = "", string TtsVoice = "", string? TtsApiKey = null,
    bool ClearTtsApiKey = false, double? TtsSpeed = null, Dictionary<string, string>? TtsVoiceMap = null);
public sealed class ApiFailure(int status, string code, string message, int? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfter { get; } = retryAfter;
}

public sealed class SettingsStore(string path)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private Settings current = new();
    public string? LoadError { get; private set; }
    public Settings Read() { lock (gate) return current; }

    public void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? throw new JsonException();
            Validate(current, current.Enabled);
        }
        catch
        {
            // Never interpret a damaged enabled configuration as permission to use the game's provider.
            current = new(Enabled: true);
            LoadError = "Could not read the settings file. Save your settings again or explicitly disable the custom provider.";
        }
    }

    public Settings Merge(SettingsInput input)
    {
        var old = Read();
        // Hosted mode blocks untrusted LLM edits only; the whole TTS document applies in both modes and Validate runs before the hosted early-return rejects bad TTS.
        var settings = (input.UseManifestDeX
            ? old with { Enabled = input.Enabled, UseManifestDeX = true }
            : new Settings(input.Enabled, (input.BaseUrl ?? "").Trim().TrimEnd('/'),
                (input.Model ?? "").Trim(), input.ClearApiKey ? "" : input.ApiKey ?? old.ApiKey,
                input.Temperature, input.TopP, input.MaxTokens, input.OutputMode))
            with { TtsEnabled = input.TtsEnabled, TtsBaseUrl = (input.TtsBaseUrl ?? "").Trim().TrimEnd('/'),
                TtsModel = (input.TtsModel ?? "").Trim(), TtsVoice = (input.TtsVoice ?? "").Trim(),
                TtsApiKey = input.ClearTtsApiKey ? "" : input.TtsApiKey ?? old.TtsApiKey,
                TtsSpeed = input.TtsSpeed, TtsVoiceMap = input.TtsVoiceMap ?? new() };
        Validate(settings, settings.Enabled);
        return settings;
    }

    public void Save(Settings settings)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json), new UTF8Encoding(false));
            File.Move(temp, path, true);
            current = settings;
            LoadError = null;
        }
    }

    public const int MaxTtsVoiceMapEntries = 128;

    public static void Validate(Settings s, bool requireConnection)
    {
        ValidateTts(s, s.TtsEnabled);
        if (s.UseManifestDeX) return; // Fixed HTTPS endpoint and scoped ticket are owned by ManifestDeXClient.
        if (requireConnection || !string.IsNullOrEmpty(s.BaseUrl))
        {
            if (!Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
                throw new ApiFailure(400, "config", "Base URL must be an HTTP(S) address without credentials, a query string, or a fragment.");
        }
        if (requireConnection && string.IsNullOrWhiteSpace(s.Model))
            throw new ApiFailure(400, "config", "A model ID is required.");
        if (s.ApiKey.Contains('\r') || s.ApiKey.Contains('\n'))
            throw new ApiFailure(400, "config", "The API key cannot contain line breaks.");
        if (s.Temperature is < 0 or > 2 || s.TopP is < 0 or > 1 || s.MaxTokens is <= 0 or > 131072 ||
            s.Temperature is double t && !double.IsFinite(t) || s.TopP is double p && !double.IsFinite(p))
            throw new ApiFailure(400, "config", "Temperature must be 0-2, top_p 0-1, and the token limit 1-131072.");
        if (s.OutputMode is not ("schema" or "json"))
            throw new ApiFailure(400, "config", "Invalid output mode.");
    }

    // TTS document validation. requireConnection mirrors TtsEnabled so /api/tts/test can verify draft settings before they are saved.
    public static void ValidateTts(Settings s, bool requireConnection)
    {
        if (requireConnection || !string.IsNullOrEmpty(s.TtsBaseUrl))
        {
            if (!Uri.TryCreate(s.TtsBaseUrl, UriKind.Absolute, out var ttsUri) ||
                ttsUri.Scheme is not ("http" or "https") || ttsUri.UserInfo != "" || ttsUri.Query != "" || ttsUri.Fragment != "")
                throw new ApiFailure(400, "config", "The TTS base URL must be an HTTP(S) address without credentials, a query string, or a fragment.");
        }
        if (requireConnection && string.IsNullOrWhiteSpace(s.TtsModel))
            throw new ApiFailure(400, "config", "A TTS model ID is required.");
        if (requireConnection && string.IsNullOrWhiteSpace(s.TtsVoice))
            throw new ApiFailure(400, "config", "A default TTS voice is required for game voices without a mapping.");
        if (s.TtsVoice.Any(char.IsControl))
            throw new ApiFailure(400, "config", "The default TTS voice cannot contain control characters.");
        if (s.TtsApiKey.Contains('\r') || s.TtsApiKey.Contains('\n'))
            throw new ApiFailure(400, "config", "The TTS API key cannot contain line breaks.");
        if (s.TtsSpeed is < 0.25 or > 4 || s.TtsSpeed is double ttsSpeed && !double.IsFinite(ttsSpeed))
            throw new ApiFailure(400, "config", "The TTS speed must be blank or between 0.25 and 4.");
        if (s.TtsVoiceMap is { } voiceMap)
        {
            if (voiceMap.Count > MaxTtsVoiceMapEntries)
                throw new ApiFailure(400, "config", $"The TTS voice map can contain at most {MaxTtsVoiceMapEntries} game voices.");
            if (voiceMap.Any(e => string.IsNullOrWhiteSpace(e.Key) || string.IsNullOrWhiteSpace(e.Value) ||
                e.Key.Any(char.IsControl) || e.Value.Any(char.IsControl)))
                throw new ApiFailure(400, "config", "TTS voice map entries need a game voice path and a provider voice without control characters.");
        }
    }

    public object Public() { var s = Read(); return new { s.Enabled, s.BaseUrl, s.Model, hasApiKey = s.ApiKey.Length > 0,
        s.Temperature, s.TopP, s.MaxTokens, s.OutputMode, s.UseManifestDeX,
        s.TtsEnabled, s.TtsBaseUrl, s.TtsModel, s.TtsVoice, hasTtsApiKey = s.TtsApiKey.Length > 0,
        s.TtsSpeed, ttsVoiceMap = s.TtsVoiceMap ?? new Dictionary<string, string>(), loadError = LoadError }; }
}

public sealed class Provider : IDisposable
{
    public const int MaxRequest = 512 * 1024;
    public const int MaxResponse = 1024 * 1024;
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim all = new(32);
    private readonly SemaphoreSlim background = new(4);
    private readonly ConcurrentDictionary<(string Endpoint, string Model, string Parameter), byte> unsupportedDefaults = new();
    public string? LastError { get; private set; }
    public long Completed;
    private readonly ConcurrentDictionary<(bool, string, string, string, double?, double?, int?, string, bool), (string State, string? Error)> connectionStates = new();
    // Connection state covers only the caller settings; saving unrelated TTS fields must not discard a verified connection.
    private static (bool, string, string, string, double?, double?, int?, string, bool) StateKey(Settings s)
        => (s.Enabled, s.BaseUrl, s.Model, s.ApiKey, s.Temperature, s.TopP, s.MaxTokens, s.OutputMode, s.UseManifestDeX);
    public object ConnectionStatus(Settings settings, string? loadError)
    {
        var status = connectionStates.TryGetValue(StateKey(settings), out var value) ? value : (State: "untested", Error: (string?)null);
        return new { enabled = settings.Enabled, model = settings.Model,
            state = loadError != null ? "error" : !settings.Enabled ? "disabled" : status.State,
            message = loadError ?? status.Error };
    }

    public static JsonObject BuildRequest(JsonObject original, Settings settings)
    {
        SettingsStore.Validate(settings, true);
        var body = (JsonObject)original.DeepClone();
        body["model"] = settings.Model;
        body["stream"] = false;
        body.Remove("provider");
        body.Remove("session_id");
        if (settings.Temperature.HasValue) body["temperature"] = settings.Temperature.Value;
        if (settings.TopP.HasValue) body["top_p"] = settings.TopP.Value;
        if (settings.MaxTokens.HasValue) body["max_tokens"] = settings.MaxTokens.Value;
        if (body["messages"] is not JsonArray messages || messages.Count == 0)
            throw new ApiFailure(400, "request", "The request does not contain conversation messages.");
        if (settings.OutputMode == "json" && body["response_format"]?["type"]?.GetValue<string>() == "json_schema")
        {
            var schema = body["response_format"]!["json_schema"]?["schema"]?.ToJsonString()
                ?? throw new ApiFailure(400, "request", "The response schema is missing.");
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] =
                "Return ONLY a JSON object matching this JSON Schema, without markdown or extra properties: " + schema });
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        return body;
    }

    public static async Task<byte[]> ReadBounded(Stream stream, int maximum, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + count > maximum) throw new ApiFailure(413, "size", "The request or response exceeds the size limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    public static string ExtractText(JsonObject response)
    {
        var content = response["choices"]?[0]?["message"]?["content"];
        if (content is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return text;
        if (content is JsonArray array)
        {
            var joined = string.Concat(array.OfType<JsonObject>().Select(x => x["text"]?.GetValue<string>()));
            if (!string.IsNullOrWhiteSpace(joined)) return joined;
        }
        throw new ApiFailure(502, "invalid_response", "The model response does not contain choices[0].message.content.");
    }

    public async Task<JsonObject> Complete(JsonObject original, Settings settings, bool isBackground, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        bool hasAll = false, hasBackground = false;
        connectionStates[StateKey(settings)] = ("connecting", null);
        try
        {
            if (isBackground) { await background.WaitAsync(token); hasBackground = true; }
            await all.WaitAsync(token); hasAll = true;
            var body = BuildRequest(original, settings);
            bool IsInherited(string parameter) => parameter == "temperature" ? !settings.Temperature.HasValue : !settings.TopP.HasValue;
            foreach (var parameter in new[] { "temperature", "top_p" })
                if (IsInherited(parameter) && unsupportedDefaults.ContainsKey((settings.BaseUrl, settings.Model, parameter))) body.Remove(parameter);
            for (var attempt = 0; ; attempt++)
            {
            var payload = body.ToJsonString();
            if (Encoding.UTF8.GetByteCount(payload) > MaxRequest) throw new ApiFailure(413, "size", "The request is too large.");
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/chat/completions");
            if (settings.BaseUrl == ManifestDeXClient.ServiceUrl + "/v1") {request.Headers.Add("X-CustomAI-Background", isBackground ? "1" : "0");request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));}
            if (settings.ApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                if(settings.BaseUrl==ManifestDeXClient.ServiceUrl+"/v1"){
                    var hostedBytes=await ReadBounded(await response.Content.ReadAsStreamAsync(token),32768,token);
                    string hostedMessage="ManifestDeX AI is temporarily unavailable.";
                    try{hostedMessage=JsonNode.Parse(hostedBytes)?["message"]?.GetValue<string>()??hostedMessage;}catch(JsonException){}
                    throw new ApiFailure(code,"manifestdex",hostedMessage);
                }
                if (code == 400 && attempt < 2)
                {
                    // Only react to an explicit unsupported-parameter error, never an arbitrary provider failure.
                    var errorBytes = await ReadBounded(await response.Content.ReadAsStreamAsync(token), MaxResponse, token);
                    string? message = null;
                    try { message = JsonNode.Parse(errorBytes)?["error"]?["message"]?.GetValue<string>(); } catch (JsonException) { }
                    var match = Regex.Match(message ?? "", "Unsupported parameter:\\s*['\"](?<parameter>temperature|top_p)['\"]", RegexOptions.IgnoreCase);
                    var parameter = match.Groups["parameter"].Value.ToLowerInvariant();
                    if (match.Success && IsInherited(parameter) && body.Remove(parameter))
                    {
                        unsupportedDefaults.TryAdd((settings.BaseUrl, settings.Model, parameter), 0);
                        continue;
                    }
                    if (match.Success && !IsInherited(parameter))
                        throw new ApiFailure(502, "model_parameters", $"The model does not support {parameter}. Leave this field blank in advanced settings.");
                }
                throw code switch
                {
                    401 or 403 => new ApiFailure(502, "auth", "The provider denied access. Check your API key and permissions."),
                    404 => new ApiFailure(502, "model_or_endpoint", "Model or API endpoint not found. Check the Base URL and model ID."),
                    400 or 422 => new ApiFailure(502, "model_parameters", "The provider rejected the model, output format, or generation settings."),
                    429 => new ApiFailure(502, "rate_limit", "The provider quota or rate limit was reached."),
                    _ => new ApiFailure(502, "provider_http", $"The provider returned HTTP {code}.")
                };
            }
            var bytes = await ReadBounded(await response.Content.ReadAsStreamAsync(token), MaxResponse, token);
            var json = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException();
            ExtractText(json);
            LastError = null;
            Interlocked.Increment(ref Completed);
            connectionStates[StateKey(settings)] = ("success", null);
            return json;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { LastError = "The provider did not respond within 15 seconds."; connectionStates[StateKey(settings)] = ("error", LastError); throw new ApiFailure(504, "timeout", LastError); }
        catch (OperationCanceledException)
        { connectionStates[StateKey(settings)] = ("error", "The request was canceled or the game response deadline expired."); throw; }
        catch (HttpRequestException)
        { LastError = "Could not connect to the provider. Check the address, TLS, and network connection."; connectionStates[StateKey(settings)] = ("error", LastError); throw new ApiFailure(502, "connection", LastError); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        { LastError = "The provider did not return a valid Chat Completions response."; connectionStates[StateKey(settings)] = ("error", LastError); throw new ApiFailure(502, "invalid_response", LastError); }
        catch (ApiFailure e) { LastError = e.Message; connectionStates[StateKey(settings)] = ("error", LastError); throw; }
        finally { if (hasAll) all.Release(); if (hasBackground) background.Release(); }
    }

    public void Dispose() { http.Dispose(); all.Dispose(); background.Dispose(); }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // The native apphost resolves both shared frameworks before reaching Main.
        // Setup can verify this without opening a listener or touching user settings.
        if (args is ["--check-runtime"]) { Console.WriteLine("SWYF_RUNTIME_OK"); return 0; }
        try { return await Run(); }
        catch { Console.Error.WriteLine("[CustomAI] Could not start the panel; check the configuration and .NET 10 installation."); return 1; }
    }

    private static async Task<int> Run()
    {
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var line = await Console.In.ReadLineAsync(startupTimeout.Token);
        var startup = JsonSerializer.Deserialize<Startup>(line ?? "", SettingsStore.Json) ?? throw new JsonException();
        if (startup.InternalToken.Length < 32 || startup.BrowserToken.Length < 32 || !Path.IsPathFullyQualified(startup.ConfigPath)) return 2;
        using var parent = Process.GetProcessById(startup.ParentId);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => { server.Listen(IPAddress.Loopback, 0); server.Limits.MaxRequestBodySize = Provider.MaxRequest; });
        var app = builder.Build();
        var store = new SettingsStore(startup.ConfigPath); store.Load();
        using var provider = new Provider();
        using var tts = new TtsProvider();
        using var manifest = new ManifestDeXClient(startup.ConfigPath, provider);
        int port = 0;
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (ctx.Request.Host.Host != "127.0.0.1" || ctx.Request.Host.Port != port)
            { ctx.Response.StatusCode = 403; return; }
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && origin != $"http://127.0.0.1:{port}") { ctx.Response.StatusCode = 403; return; }
            if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/internal"))
            {
                bool internalRoute = ctx.Request.Path.StartsWithSegments("/internal");
                var expected = internalRoute ? startup.InternalToken : startup.BrowserToken;
                var actual = ctx.Request.Headers["X-CustomAI-Token"].ToString();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected)))
                { ctx.Response.StatusCode = 401; return; }
            }
            try { await next(); }
            catch (ApiFailure e) { ctx.Response.StatusCode = e.Status; if(e.RetryAfter is int retry)ctx.Response.Headers.RetryAfter=retry.ToString(); await ctx.Response.WriteAsJsonAsync(new { error = e.Code, message = e.Message, retryAfter=e.RetryAfter }); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
            catch (Exception e) when (e is JsonException or BadHttpRequestException)
            { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "json", message = "Invalid JSON request." }); }
            catch
            { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "internal", message = "The operation failed. Check the settings file and panel status." }); }
        });
        app.MapGet("/", async (HttpContext ctx) =>
        {
            var resource = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(n => n.EndsWith("index.html"));
            using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!);
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            ctx.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'nonce-{nonce}'; script-src 'nonce-{nonce}'; img-src https://api.manifestdex.com; media-src blob:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync((await reader.ReadToEndAsync()).Replace("__NONCE__", nonce));
        });
        app.MapGet("/api/config", () => Results.Json(store.Public()));
        app.MapPut("/api/config", async (HttpContext ctx) =>
        {
            var input = await ctx.Request.ReadFromJsonAsync<SettingsInput>() ?? throw new JsonException();
            if (input.UseManifestDeX && input.Enabled) await manifest.RequireAvailable(ctx.RequestAborted);
            store.Save(store.Merge(input));
            return Results.Json(store.Public());
        });
        app.MapGet("/api/manifestdex/status", async (HttpContext ctx) => await ctx.Response.WriteAsJsonAsync(await manifest.Status(ctx.RequestAborted)));
        app.MapPost("/api/manifestdex/connect", async (HttpContext ctx) => await ctx.Response.WriteAsJsonAsync(await manifest.Connect(ctx.RequestAborted)));
        app.MapPost("/api/manifestdex/poll", async (HttpContext ctx) => await ctx.Response.WriteAsJsonAsync(await manifest.Poll(ctx.RequestAborted)));
        app.MapGet("/api/manifestdex/account",async(HttpContext ctx)=>await ctx.Response.WriteAsJsonAsync(await manifest.Account(ctx.RequestAborted,ctx.Request.Query["refresh"]=="true")));
        app.MapPost("/api/manifestdex/disconnect", async(HttpContext ctx) => { await manifest.DisconnectRemote(ctx.RequestAborted); return Results.Json(new { ok = true }); });
        app.MapGet("/api/status", () => Results.Json(new { ready = true, completed = Interlocked.Read(ref provider.Completed),
            lastError = provider.LastError, loadError = store.LoadError, deadlineSeconds = 15 }));
        app.MapGet("/internal/status", () =>
        {
            var status = JsonSerializer.SerializeToNode(provider.ConnectionStatus(manifest.Effective(store.Read()), store.LoadError), SettingsStore.Json)!.AsObject();
            status["tts"] = JsonSerializer.SerializeToNode(tts.ConnectionStatus(store.Read(), store.LoadError), SettingsStore.Json);
            return Results.Json(status);
        });
        app.MapPost("/api/test", async (HttpContext ctx) =>
        {
            var input = await ctx.Request.ReadFromJsonAsync<SettingsInput>() ?? throw new JsonException();
            var settings = store.Merge(input);
            if (settings.UseManifestDeX) { await manifest.RequireAvailable(ctx.RequestAborted); settings = manifest.Effective(settings); }
            var body = JsonNode.Parse("""
                {"messages":[{"role":"user","content":"Reply in English with a short greeting. Set trust_percent to 50, emotion to NEUTRAL, and completed_objectives to an empty array."}],"max_tokens":128,"response_format":{"type":"json_schema","json_schema":{"name":"gameplay_caller_turn","strict":true,"schema":{"type":"object","properties":{"dialogue":{"type":"string"},"trust_percent":{"type":"integer","minimum":0,"maximum":100},"emotion":{"type":"string","enum":["TRUSTING","SUSPICIOUS","ANGRY","NEUTRAL"]},"completed_objectives":{"type":"array","items":{"type":"string"}}},"required":["dialogue","trust_percent","emotion","completed_objectives"],"additionalProperties":false}}}}
                """)!.AsObject();
            GameProbe.ApplyInstalledSchema(body, startup.ConfigPath);
            // Match the normal caller defaults so a passing test also exercises model compatibility.
            body["temperature"] = 0.9;
            body["top_p"] = 0.95;
            var watch = Stopwatch.StartNew();
            var response = await provider.Complete(body, settings, false, ctx.RequestAborted);
            try
            {
                GameProbe.ValidateResponse(body, Provider.ExtractText(response));
            }
            catch { throw new ApiFailure(502, "schema", "Connected, but the model did not follow the game response schema. Try another model or output mode."); }
            return Results.Json(new { ok = true, elapsedMs = watch.ElapsedMilliseconds, message = "Connection and game response schema verified." });
        });
        app.MapPost("/api/tts/test", async (HttpContext ctx) =>
        {
            var input = await ctx.Request.ReadFromJsonAsync<TtsTestInput>() ?? throw new JsonException();
            // Draft values are validated and used for exactly one synthesis; the saved settings document is never written.
            var draft = new Settings(TtsEnabled: true, TtsBaseUrl: (input.TtsBaseUrl ?? "").Trim().TrimEnd('/'), TtsModel: (input.TtsModel ?? "").Trim(),
                TtsVoice: (input.TtsVoice ?? "").Trim(), TtsApiKey: string.IsNullOrWhiteSpace(input.TtsApiKey) ? store.Read().TtsApiKey : input.TtsApiKey,
                TtsSpeed: input.TtsSpeed);
            SettingsStore.Validate(draft, false);
            var text = string.IsNullOrWhiteSpace(input.Input) ? TtsProvider.DefaultTestInput : input.Input;
            var pcm = await tts.Synthesize(text, draft.TtsVoice, draft, ctx.RequestAborted);
            ctx.Response.Headers["X-Tts-Sample-Rate"] = TtsProvider.SampleRate.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-Tts-Voice"] = draft.TtsVoice;
            return Results.Bytes(pcm, "application/octet-stream");
        });
        app.MapPost("/internal/chat/completions", async (HttpContext ctx) =>
        {
            var settings = store.Read();
            if (!settings.Enabled) throw new ApiFailure(409, "disabled", "The custom provider is disabled; the next game request will use the original service.");
            if (store.LoadError != null) throw new ApiFailure(503, "config", store.LoadError);
            if (settings.UseManifestDeX) settings = manifest.Effective(settings);
            var bytes = await Provider.ReadBounded(ctx.Request.Body, Provider.MaxRequest, ctx.RequestAborted);
            var body = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException();
            body = GameProbe.PrepareCompletion(body, startup.ConfigPath);
            return Results.Json(await provider.Complete(body, settings, ctx.Request.Headers["X-CustomAI-Background"] == "1", ctx.RequestAborted));
        });
        app.MapPost("/internal/tts/speech", async (HttpContext ctx) =>
        {
            var settings = store.Read();
            if (!settings.TtsEnabled) throw new ApiFailure(409, "disabled", "Custom TTS is disabled; game dialogue will not use the custom voice.");
            if (store.LoadError != null) throw new ApiFailure(503, "config", store.LoadError);
            var bytes = await Provider.ReadBounded(ctx.Request.Body, Provider.MaxRequest, ctx.RequestAborted);
            var body = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException();
            var text = body["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var spoken) ? spoken : null;
            if (string.IsNullOrWhiteSpace(text)) throw new ApiFailure(400, "config", "TTS requires text to speak.");
            var voice = TtsProvider.ResolveVoice(settings, body["voicePath"] is JsonValue voicePathNode && voicePathNode.TryGetValue<string>(out var voicePath) ? voicePath : null);
            var pcm = await tts.Synthesize(text, voice, settings, ctx.RequestAborted);
            ctx.Response.Headers["X-Tts-Sample-Rate"] = TtsProvider.SampleRate.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-Tts-Voice"] = voice;
            return Results.Bytes(pcm, "application/octet-stream");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        port = new Uri(address).Port;
        Console.WriteLine(JsonSerializer.Serialize(new { type = "ready", url = address }));
        Console.Out.Flush();
        var eof = Task.Run(async () => { while (await Console.In.ReadLineAsync() is not null) { } });
        var parentExit = parent.WaitForExitAsync();
        var shutdown = app.WaitForShutdownAsync();
        await Task.WhenAny(eof, parentExit, shutdown);
        await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
        await app.DisposeAsync();
        return 0;
    }
}
