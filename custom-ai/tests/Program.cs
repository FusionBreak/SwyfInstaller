using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Swyf.CustomAI;
using Installer = Swyf.CustomAI.Installation.Program;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var output = Path.Combine(root, "artifacts", "tests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(output);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
async Task Failure(Func<Task> task, string code)
{ try { await task(); throw new Exception("Expected " + code); } catch (ApiFailure e) { Check(e.Code == code, code); } }
var probeConfig=Path.Combine(output,"probe-game","CustomAI","settings.json");
var probeRequest=new JsonObject();
GameProbe.ApplyInstalledSchema(probeRequest,probeConfig);
Check(probeRequest.Count==0,"missing game schema retains bundled probe for older games");
var probeSchemaPath=Path.Combine(output,"probe-game","Scam With Your Friends_Data","StreamingAssets","kolkata-json","core-prompts","system-prompt.schema.json");
Directory.CreateDirectory(Path.GetDirectoryName(probeSchemaPath)!);
File.WriteAllText(probeSchemaPath,"""{"type":"json_schema","json_schema":{"name":"gameplay_caller_turn","schema":{"type":"object","properties":{"dialogue":{"type":"string"},"comedy":{"type":"number","minimum":0,"maximum":1}},"required":["dialogue","comedy"],"additionalProperties":false}}}""");
GameProbe.ApplyInstalledSchema(probeRequest,probeConfig);
GameProbe.ValidateResponse(probeRequest,"""{"dialogue":"Hello","comedy":0.5}""");
Check(true,"probe reads installed schema and accepts its new field");
var optionalSchema=Path.Combine(Path.GetDirectoryName(probeSchemaPath)!,"official-system-prompt.schema.json");
File.Move(probeSchemaPath,optionalSchema);
var omittedFormat=JsonNode.Parse("""{"ai_category":"dialogue","messages":[{"role":"user","content":"hello"}]}""")!.AsObject();
var preparedFormat=GameProbe.PrepareCompletion(omittedFormat,probeConfig);
Check(preparedFormat["response_format"] is JsonObject && omittedFormat["response_format"] is null && JsonNode.DeepEquals(preparedFormat["messages"],omittedFormat["messages"]),"schema-less dialogue uses installed official schema without mutating history or original request");
Check(ReferenceEquals(GameProbe.PrepareCompletion(preparedFormat,probeConfig),preparedFormat),"explicit response format preserved");
var nonDialogue=(JsonObject)omittedFormat.DeepClone();nonDialogue["ai_category"]="boss_review";
Check(ReferenceEquals(GameProbe.PrepareCompletion(nonDialogue,probeConfig),nonDialogue),"non-dialogue requests are not assigned a caller schema");
var optionalProbe=new JsonObject();GameProbe.ApplyInstalledSchema(optionalProbe,probeConfig);
Check(JsonNode.DeepEquals(optionalProbe["response_format"],preparedFormat["response_format"]),"test probe and real dialogue use the same installed schema fallback");
File.Move(optionalSchema,probeSchemaPath);
foreach(var invalid in new[]{"""{"dialogue":"Hello"}""","""{"dialogue":"Hello","comedy":2}""","""{"dialogue":"Hello","comedy":"zero"}""","""{"dialogue":"Hello","comedy":0,"comedy":1}"""}){
 try{GameProbe.ValidateResponse(probeRequest,invalid);throw new Exception("Invalid probe response accepted");}catch(JsonException){Check(true,"installed schema rejects missing, out-of-range, wrong-type or duplicate fields");}
}
File.WriteAllText(probeSchemaPath,"broken schema");
try{GameProbe.ApplyInstalledSchema(probeRequest,probeConfig);throw new Exception("Broken schema accepted");}catch(ApiFailure e){Check(e.Code=="game_schema","updated invalid file fails rather than reusing a stale schema");}
var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
var fake = builder.Build();
JsonObject? observed = null; string? observedAuth = null; int requests = 0;
var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
const string greeting = "{\"dialogue\":\"Hello, I am listening.\",\"trust_percent\":50,\"emotion\":\"NEUTRAL\"}";
var ttsPcm = new byte[512]; for (var i = 0; i < ttsPcm.Length; i++) ttsPcm[i] = (byte)(i % 251);
JsonObject Response(string content) => new() { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) };
fake.MapPost("/{**path}", async (HttpContext ctx) =>
{
    requests++;
    observed = await ctx.Request.ReadFromJsonAsync<JsonObject>();
    observedAuth = ctx.Request.Headers.Authorization;
    var path = ctx.Request.Path.Value!;
    if (path.Contains("no-sampling"))
    {
        var rejected = observed!.ContainsKey("temperature") ? "temperature" : observed.ContainsKey("top_p") ? "top_p" : null;
        if (rejected != null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = $"Unsupported parameter: '{rejected}' is not supported with this model." } });
            return;
        }
    }
    if (path.Contains("slow"))
    { try { await Task.Delay(30000, ctx.RequestAborted); } catch (OperationCanceledException) { cancelled.TrySetResult(); } return; }
    if (path.Contains("unauthorized")) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("secret-must-not-leak"); return; }
    if (path.Contains("missing")) { ctx.Response.StatusCode = 404; return; }
    if (path.Contains("rate")) { ctx.Response.StatusCode = 429; return; }
    if (path.Contains("redirect")) { ctx.Response.StatusCode = 307; ctx.Response.Headers.Location = "/v1/chat/completions"; return; }
    if (path.Contains("invalid")) { await ctx.Response.WriteAsync("broken"); return; }
    if (path.Contains("oversize")) { await ctx.Response.WriteAsync(new string('x', Provider.MaxResponse + 10)); return; }
    if (path.Contains("wrong-schema")) { await ctx.Response.WriteAsJsonAsync(Response("{\"dialogue\":\"hi\"}")); return; }
    if (path.Contains("tts"))
    {
        ctx.Response.ContentType = "application/octet-stream";
        if (path.Contains("huge")) await ctx.Response.Body.WriteAsync(new byte[TtsProvider.MaxResponse + 4096]);
        else await ctx.Response.Body.WriteAsync(ttsPcm);
        return;
    }
    var content = JsonNode.Parse(greeting)!;
    if (observed?["response_format"]?["json_schema"]?["name"]?.ToString() == "gameplay_caller_turn") content["completed_objectives"] = path.Contains("bad-objectives") ? new JsonArray(42) : new JsonArray();
    await ctx.Response.WriteAsJsonAsync(Response(content.ToJsonString()));
});
await fake.StartAsync();
var url = fake.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var provider = new Provider();
var input = JsonNode.Parse("""{"model":"old","messages":[{"role":"system","content":"keep"},{"role":"user","content":"hello"}],"temperature":0.9,"max_tokens":128,"session_id":"private","provider":{"require_parameters":true},"response_format":{"type":"json_schema","json_schema":{"schema":{"type":"object"}}}}""")!.AsObject();
var settings = new Settings(true, url + "/v1/", "fake-model");
string State(Settings s) => JsonSerializer.SerializeToNode(provider.ConnectionStatus(s, null))!["state"]!.GetValue<string>();
Check(State(settings) == "untested", "new provider is not falsely marked connected");
await provider.Complete(input, settings, false, default);
Check(State(settings) == "success", "successful provider response updates connection state");
Check(State(settings with { Model = "different" }) == "untested", "different model does not inherit successful status");
Check(State(settings with { Enabled = false }) == "disabled", "disabled provider state");
Check(string.IsNullOrEmpty(observedAuth) && observed!["model"]!.GetValue<string>() == "fake-model", "keyless API and model override");
Check(observed!["session_id"] == null && observed["provider"] == null && observed["stream"]!.GetValue<bool>() == false, "provider-specific fields removed");
Check(observed["temperature"]!.GetValue<double>() == .9 && observed["response_format"]!["type"]!.GetValue<string>() == "json_schema", "game defaults and schema preserved");
Check(input["session_id"] != null && input["model"]!.GetValue<string>() == "old", "original request untouched");
await provider.Complete(input, settings with { ApiKey = "test-key", Temperature = .2, TopP = .4, MaxTokens = 256, OutputMode = "json" }, false, default);
Check(observedAuth == "Bearer test-key" && observed!["temperature"]!.GetValue<double>() == .2 && observed["max_tokens"]!.GetValue<int>() == 256, "key and generation overrides");
Check(observed!["response_format"]!["type"]!.GetValue<string>() == "json_object" && observed["messages"]!.AsArray().Count == 3, "schema translated into JSON instruction");
var samplingInput = (JsonObject)input.DeepClone(); samplingInput["top_p"] = .95;
int beforeSampling = requests;
await provider.Complete(samplingInput, settings with { BaseUrl = url + "/no-sampling" }, false, default);
Check(requests == beforeSampling + 3 && observed!["temperature"] == null && observed["top_p"] == null, "unsupported inherited sampling parameters removed on explicit rejection");
Check(observed!["response_format"]!["type"]!.GetValue<string>() == "json_schema", "compatibility retry preserves schema");
beforeSampling = requests;
await provider.Complete(samplingInput, settings with { BaseUrl = url + "/no-sampling" }, false, default);
Check(requests == beforeSampling + 1, "learned model capabilities avoid repeated failed requests");
await Failure(async () => await provider.Complete(samplingInput, settings with { BaseUrl = url + "/no-sampling", Temperature = .3 }, false, default), "model_parameters");
await provider.Complete(samplingInput, settings with { BaseUrl = url + "/v1" }, false, default);
Check(observed!["temperature"] != null && observed["top_p"] != null, "other providers keep inherited defaults");
foreach (var pair in new[] { ("unauthorized", "auth"), ("missing", "model_or_endpoint"), ("rate", "rate_limit"), ("redirect", "provider_http"), ("invalid", "invalid_response"), ("oversize", "size") })
    await Failure(async () => await provider.Complete(input, settings with { BaseUrl = url + "/" + pair.Item1 }, false, default), pair.Item2);
Check(State(settings with { BaseUrl = url + "/unauthorized" }) == "error", "authentication failure updates provider state");
using (var cancel = new CancellationTokenSource(150))
{
    try { await provider.Complete(input, settings with { BaseUrl = url + "/slow" }, false, cancel.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Check(true, "caller cancellation preserved"); }
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check(true, "HTTP cancellation reaches provider");
}
await Failure(async () => await provider.Complete(input, settings with { BaseUrl = url + "/slow" }, false, default), "timeout");
var config = Path.Combine(output, "settings.json");
var store = new SettingsStore(config); store.Load(); Check(!store.Read().Enabled, "first launch disabled");
store.Save(settings with { ApiKey = "saved-secret" });
var reloaded = new SettingsStore(config); reloaded.Load(); Check(reloaded.Read().ApiKey == "saved-secret", "settings persist");
Check(!JsonSerializer.Serialize(reloaded.Public()).Contains("saved-secret"), "config GET does not reveal key");
Check(reloaded.Merge(new(true, url, "x")).ApiKey == "saved-secret", "blank key preserves secret");
Check(reloaded.Merge(new(true, url, "x", ClearApiKey: true)).ApiKey == "", "explicit clear removes key");
var hosted = reloaded.Merge(new(true, "https://untrusted.invalid", "ignored", "ignored-key", Temperature: 1.8, UseManifestDeX: true));
Check(hosted.UseManifestDeX && hosted.BaseUrl == settings.BaseUrl && hosted.ApiKey == "saved-secret" && hosted.Temperature == settings.Temperature, "hosted selection preserves private settings and ignores blocked edits");
SettingsStore.Validate(new Settings(Enabled: true, UseManifestDeX: true), true);
using (var hostedClient = new ManifestDeXClient(config, provider))
{
    var effective = hostedClient.Effective(hosted);
    Check(effective.BaseUrl == ManifestDeXClient.ServiceUrl + "/v1" && effective.Model == "ManifestDeX AI" && effective.ApiKey != "saved-secret", "hosted requests use only fixed service endpoint and scoped credentials");
    Check(hostedClient.Effective(settings) == settings, "private provider routing unchanged");
}
File.WriteAllText(config, "bad-json"); reloaded.Load(); Check(reloaded.Read().Enabled && reloaded.LoadError != null, "corrupt config fails closed");

// Custom TTS provider settings: schema, validation, persistence (mirrors the LLM provider conventions above).
static bool TtsRejected(Settings s) { try { SettingsStore.Validate(s, s.Enabled); return false; } catch (ApiFailure e) { return e.Code == "config"; } }
var ttsConfig = Path.Combine(output, "tts-settings.json");
var ttsStore = new SettingsStore(ttsConfig); ttsStore.Load();
Check(!ttsStore.Read().TtsEnabled && ttsStore.Read().TtsBaseUrl == "" && ttsStore.Read().TtsModel == "" && ttsStore.Read().TtsVoice == "" &&
    ttsStore.Read().TtsApiKey == "" && ttsStore.Read().TtsSpeed == null && ttsStore.Read().TtsVoiceMap is null or { Count: 0 }, "first launch keeps custom TTS off");
Check(new Settings() == new Settings() && new Settings(TtsVoiceMap: new() { ["voice/player"] = "alloy" }) == new Settings(TtsVoiceMap: new() { ["voice/player"] = "alloy" }), "voice maps compare by content in settings equality");
var savedTts = new Settings(true, url + "/v1", "fake-model", "saved-secret", TtsEnabled: true, TtsBaseUrl: url + "/tts", TtsModel: "voice-model", TtsVoice: "default-voice", TtsApiKey: "tts-secret", TtsSpeed: 1.25, TtsVoiceMap: new() { ["voice/player/anna"] = "alloy", ["voice/npc/guard"] = "onyx" });
ttsStore.Save(savedTts);
var ttsReload = new SettingsStore(ttsConfig); ttsReload.Load();
Check(ttsReload.Read() == savedTts && ttsReload.Read().TtsVoiceMap!["voice/npc/guard"] == "onyx" && ttsReload.Read().TtsSpeed == 1.25, "custom TTS settings persist and reload unchanged");
File.WriteAllText(ttsConfig, """{"enabled":true,"baseUrl":"http://127.0.0.1:8080/v1","model":"legacy","apiKey":"legacy-key","temperature":0.5,"outputMode":"json"}""");
ttsReload.Load();
Check(ttsReload.Read().ApiKey == "legacy-key" && ttsReload.Read().Temperature == 0.5 && ttsReload.Read().OutputMode == "json" && !ttsReload.Read().TtsEnabled &&
    ttsReload.Read().TtsBaseUrl == "" && ttsReload.Read().TtsApiKey == "" && ttsReload.Read().TtsVoiceMap is null or { Count: 0 } && ttsReload.LoadError == null, "legacy settings without TTS fields load unchanged with TTS off");
File.WriteAllText(ttsConfig, """{"enabled":true,"baseUrl":"http://127.0.0.1:8080/v1","model":"legacy","apiKey":"legacy-key","ttsEnabled":true,"ttsBaseUrl":"http://127.0.0.1:8080/tts","ttsModel":"voice-model","ttsVoice":"default-voice","ttsApiKey":"tts-secret","ttsSpeed":1.25,"ttsVoiceMap":{"voice/player":"alloy"}}""");
ttsReload.Load();
var ttsPublic = JsonSerializer.SerializeToNode(ttsReload.Public(), SettingsStore.Json)!;

Check(ttsPublic["hasTtsApiKey"]!.GetValue<bool>() && ttsPublic["ttsApiKey"] is null && !ttsPublic.ToJsonString().Contains("tts-secret") &&
    ttsPublic["ttsVoiceMap"]!["voice/player"]!.GetValue<string>() == "alloy", "config GET exposes hasTtsApiKey and never the TTS key");
Check(ttsReload.Merge(new(false, "http://127.0.0.1:8080/v1", "legacy")).TtsApiKey == "tts-secret", "blank TTS key preserves the saved secret");
Check(ttsReload.Merge(new(false, "http://127.0.0.1:8080/v1", "legacy", ClearTtsApiKey: true)).TtsApiKey == "", "explicit clear removes the TTS key");
Check(ttsReload.Merge(new(false, "http://127.0.0.1:8080/v1", "legacy", TtsApiKey: "replacement")).TtsApiKey == "replacement", "submitted TTS key replaces the saved one");
var hostedTts = ttsReload.Merge(new(true, "https://untrusted.invalid", "ignored", UseManifestDeX: true, TtsEnabled: true, TtsBaseUrl: "https://tts.example/v2/", TtsModel: "voice-model-2", TtsVoice: "default-voice", TtsSpeed: 1.75, TtsVoiceMap: new() { ["voice/player"] = "nova" }));
Check(hostedTts.UseManifestDeX && hostedTts.BaseUrl == "http://127.0.0.1:8080/v1" && hostedTts.ApiKey == "legacy-key" && hostedTts.TtsEnabled &&
    hostedTts.TtsBaseUrl == "https://tts.example/v2" && hostedTts.TtsModel == "voice-model-2" && hostedTts.TtsApiKey == "tts-secret" &&
    hostedTts.TtsSpeed == 1.75 && hostedTts.TtsVoiceMap!["voice/player"] == "nova", "hosted selection applies valid TTS edits and still ignores blocked LLM edits");
ttsReload.Save(hostedTts);
var hostedTtsReload = new SettingsStore(ttsConfig); hostedTtsReload.Load();
Check(hostedTtsReload.LoadError == null && hostedTtsReload.Read() == hostedTts, "hosted TTS edit persists and reloads unchanged");
try { ttsReload.Merge(new(true, "https://untrusted.invalid", "ignored", UseManifestDeX: true, TtsEnabled: true, TtsBaseUrl: "ftp://tts.example/v1", TtsModel: "voice-model-2", TtsVoice: "default-voice")); throw new Exception("hosted save accepted an invalid TTS base URL"); }
catch (ApiFailure e) { Check(e.Code == "config", "hosted save rejects invalid TTS with a config failure instead of silently dropping it"); }
Check(new[] { "tts.example/v1", "http://user:pw@tts.example/v1", "https://tts.example/v1?voice=x", "https://tts.example/v1#x", "" }
    .All(bad => TtsRejected(new(TtsEnabled: true, TtsBaseUrl: bad, TtsModel: "m", TtsVoice: "v")))
    && !TtsRejected(new(TtsEnabled: true, TtsBaseUrl: "https://tts.example/v1", TtsModel: "m", TtsVoice: "v"))
    && !TtsRejected(new(TtsBaseUrl: "https://tts.example/v1", TtsModel: "m", TtsVoice: "v")), "TTS base URL must be absolute HTTP(S) without credentials, query, or fragment");
Check(TtsRejected(new(TtsEnabled: true, TtsBaseUrl: "https://tts.example/v1")) && TtsRejected(new(TtsEnabled: true, TtsBaseUrl: "https://tts.example/v1", TtsModel: "m")), "enabled TTS requires model and default voice");
SettingsStore.Validate(new(TtsEnabled: false, TtsModel: "leftover-model", TtsVoice: "leftover-voice", TtsApiKey: "leftover key", TtsVoiceMap: new() { ["voice/old"] = "kept" }), false);
Check(true, "disabled TTS keeps leftover values without validation errors");
Check(TtsRejected(new(TtsApiKey: "bad\rkey")) && TtsRejected(new(TtsApiKey: "bad\nkey")), "TTS API key rejects line breaks");
Check(TtsRejected(new(TtsSpeed: 0.1)) && TtsRejected(new(TtsSpeed: 4.5)) && TtsRejected(new(TtsSpeed: double.NaN)) &&
    !TtsRejected(new(TtsSpeed: 0.25)) && !TtsRejected(new(TtsSpeed: 4)) && !TtsRejected(new(TtsSpeed: null)), "TTS speed must be blank or between 0.25 and 4");
Check(TtsRejected(new(TtsVoiceMap: new() { [""] = "alloy" })) && TtsRejected(new(TtsVoiceMap: new() { ["voice/player"] = "" })) &&
    TtsRejected(new(TtsVoiceMap: new() { ["voice\u0001player"] = "alloy" })) && TtsRejected(new(TtsVoiceMap: new() { ["voice/player"] = "all\u0000oy" })), "TTS voice map rejects empty or control-character entries");
Check(TtsRejected(new(TtsVoice: "voice\nplayer")) && TtsRejected(new(TtsVoice: "voice\u0001player")) &&
    !TtsRejected(new(TtsEnabled: true, TtsBaseUrl: "https://tts.example/v1", TtsModel: "m", TtsVoice: "normal-voice")), "default TTS voice rejects control characters");
bool NullProviderVoiceRejected()
{
    try { var parsed = JsonSerializer.Deserialize<Settings>("""{"ttsVoiceMap":{"voice/player":null}}""", SettingsStore.Json); return parsed is null || TtsRejected(parsed); }
    catch (JsonException) { return true; }
}
Check(NullProviderVoiceRejected(), "TTS voice map rejects a null provider voice");
var manyVoices = new Dictionary<string, string>(); for (var i = 0; i <= SettingsStore.MaxTtsVoiceMapEntries; i++) manyVoices["voice/" + i] = "voice-" + i;
Check(!TtsRejected(new(TtsVoiceMap: manyVoices.Take(SettingsStore.MaxTtsVoiceMapEntries).ToDictionary(p => p.Key, p => p.Value))) && TtsRejected(new(TtsVoiceMap: manyVoices)), "TTS voice map is capped at 128 entries");
try { SettingsStore.Validate(new(TtsVoiceMap: manyVoices), false); throw new Exception("Oversized TTS voice map accepted"); }
catch (ApiFailure e) { Check(e.Code == "config" && e.Message.Contains("128"), "oversized TTS voice map reports a clear error"); }
File.WriteAllText(ttsConfig, "broken-tts-json"); ttsReload.Load();
Check(ttsReload.Read().Enabled && !ttsReload.Read().TtsEnabled && ttsReload.Read().TtsVoiceMap is null or { Count: 0 } && ttsReload.LoadError != null, "corrupt config still fails closed with TTS defaults off");

// Custom TTS provider client: raw PCM speech with its own deadline and per-settings connection state.
using var ttsProvider = new TtsProvider();
var voiceSettings = new Settings(true, url + "/v1", "fake-model", "saved-secret", TtsEnabled: true, TtsBaseUrl: url + "/tts", TtsModel: "voice-model",
    TtsVoice: "default-voice", TtsApiKey: "tts-secret", TtsSpeed: 1.25, TtsVoiceMap: new() { ["voice/player/anna"] = "alloy" });
string TtsState(Settings s) => JsonSerializer.SerializeToNode(ttsProvider.ConnectionStatus(s, null))!["state"]!.GetValue<string>();
Check(TtsState(voiceSettings) == "untested" && TtsState(voiceSettings with { TtsEnabled = false }) == "disabled" &&
    TtsState(voiceSettings with { TtsModel = "other-model" }) == "untested", "new TTS provider starts untested and reports disabled or different models honestly");
Check(TtsProvider.ResolveVoice(voiceSettings, "voice/player/anna") == "alloy" && TtsProvider.ResolveVoice(voiceSettings, "voice/npc/stranger") == "default-voice" &&
    TtsProvider.ResolveVoice(voiceSettings, null) == "default-voice", "game voice paths resolve through the voice map with the default voice as fallback");
var ttsBeforeLocalReject = requests;
await Failure(async () => await ttsProvider.Synthesize("   ", "alloy", voiceSettings, default), "config");
await Failure(async () => await ttsProvider.Synthesize(new string('a', TtsProvider.MaxInput + 1), "alloy", voiceSettings, default), "size");
Check(requests == ttsBeforeLocalReject, "blank and oversize TTS text are rejected before any network request");
var spokenPcm = await ttsProvider.Synthesize("Hello caller", "alloy", voiceSettings, default);
Check(spokenPcm.SequenceEqual(ttsPcm), "successful TTS synthesis returns the raw PCM bytes");
Check(observedAuth == "Bearer tts-secret" && observed!["model"]!.GetValue<string>() == "voice-model" && observed["voice"]!.GetValue<string>() == "alloy" &&
    observed["input"]!.GetValue<string>() == "Hello caller" && observed["response_format"]!.GetValue<string>() == "pcm" && observed["speed"]!.GetValue<double>() == 1.25,
    "TTS request posts model, voice, input, pcm response format, speed, and the Bearer key");
await ttsProvider.Synthesize("Hello", "alloy", voiceSettings with { TtsSpeed = null }, default);
Check(observed!["speed"] == null, "a blank TTS speed is omitted from the request");
Check(TtsState(voiceSettings) == "success" && JsonSerializer.SerializeToNode(ttsProvider.ConnectionStatus(voiceSettings, null))!["completed"]!.GetValue<long>() >= 2 &&
    JsonSerializer.SerializeToNode(ttsProvider.ConnectionStatus(voiceSettings, null))!["lastError"] is null, "successful TTS synthesis updates state and the completed counter");
await ttsProvider.Synthesize("Hello", "alloy", voiceSettings with { TtsApiKey = "" }, default);
Check(string.IsNullOrEmpty(observedAuth), "a keyless TTS request sends no Authorization header");
Check(TtsState(voiceSettings with { Model = "other-llm-model", ApiKey = "other-llm-key" }) == "success" &&
    TtsState(voiceSettings with { TtsModel = "other-voice-model" }) == "untested", "TTS state keys on TTS settings only: LLM edits keep a verified voice, a TTS model edit is untested");
foreach (var pair in new[] { ("unauthorized", "auth"), ("missing", "model_or_endpoint"), ("rate", "rate_limit") })
    await Failure(async () => await ttsProvider.Synthesize("Hello", "alloy", voiceSettings with { TtsBaseUrl = url + "/tts-" + pair.Item1 }, default), pair.Item2);
var ttsDeniedMessage = "";
try { await ttsProvider.Synthesize("Hello", "alloy", voiceSettings with { TtsBaseUrl = url + "/tts-unauthorized" }, default); } catch (ApiFailure e) { ttsDeniedMessage = e.Message; }
Check(!ttsDeniedMessage.Contains("secret-must-not-leak") && TtsState(voiceSettings with { TtsBaseUrl = url + "/tts-unauthorized" }) == "error",
    "TTS failures never leak the provider payload and update provider state");
await Failure(async () => await ttsProvider.Synthesize("Hello", "alloy", voiceSettings with { TtsBaseUrl = url + "/tts-huge" }, default), "size");
await Failure(async () => await ttsProvider.Synthesize("Hello", "alloy", voiceSettings with { TtsBaseUrl = url + "/tts-slow" }, default, deadlineSeconds: 1), "timeout");
Check(TtsState(voiceSettings with { TtsBaseUrl = url + "/tts-slow" }) == "error", "TTS timeout updates provider state");

// Exercise the actual companion process, security middleware, endpoints and EOF lifecycle.
string browserToken = new('b', 48), internalToken = new('i', 48);
var panelDll = Path.Combine(AppContext.BaseDirectory, "SWYF.CustomAI.Panel.dll");
using var panel = new Process { StartInfo = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
panel.StartInfo.ArgumentList.Add(panelDll); panel.Start(); var stderr = panel.StandardError.ReadToEndAsync();
var panelConfig = Path.Combine(output, "http-game", "CustomAI", "settings.json");
Directory.CreateDirectory(Path.GetDirectoryName(panelConfig)!);
await panel.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Startup(internalToken, browserToken, panelConfig, Environment.ProcessId), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
await panel.StandardInput.FlushAsync();
var readyLine = await panel.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
if (readyLine == null) throw new Exception("Panel exited before READY: " + await stderr);
var ready = JsonNode.Parse(readyLine)!;
using var client = new HttpClient { BaseAddress = new Uri(ready["url"]!.GetValue<string>()) };
Check((await client.GetAsync("/api/config")).StatusCode == HttpStatusCode.Unauthorized, "config requires token");
client.DefaultRequestHeaders.Add("X-CustomAI-Token", browserToken);
Check((await client.GetAsync("/api/config")).IsSuccessStatusCode, "browser token accepted");
Check((await client.GetFromJsonAsync<JsonObject>("/api/manifestdex/account"))?["connected"]?.GetValue<bool>()==false, "hosted account endpoint writes JSON when disconnected");
Check((await client.PostAsJsonAsync("/api/manifestdex/poll",new{})).Content.Headers.ContentType?.MediaType=="application/json", "hosted poll endpoint writes JSON without an active flow");
client.DefaultRequestHeaders.Add("Origin", "https://example.com");
Check((await client.GetAsync("/api/config")).StatusCode == HttpStatusCode.Forbidden, "foreign origin rejected");
client.DefaultRequestHeaders.Remove("Origin");
using (var wrongHost = new HttpRequestMessage(HttpMethod.Get, "/api/config"))
{ wrongHost.Headers.Host = "example.com"; Check((await client.SendAsync(wrongHost)).StatusCode == HttpStatusCode.Forbidden, "foreign host rejected"); }
Check((await client.PostAsJsonAsync("/internal/chat/completions", input)).StatusCode == HttpStatusCode.Unauthorized, "browser token cannot call internal API");
var form = new SettingsInput(true, url + "/v1", "fake", "saved-secret");
Check((await client.PutAsJsonAsync("/api/config", form)).IsSuccessStatusCode, "save endpoint");
Check(!(await client.GetStringAsync("/api/config")).Contains("saved-secret"), "HTTP config masks API key");
Check((await client.PostAsJsonAsync("/api/test", form)).IsSuccessStatusCode, "connection test validates real schema");
Check((await client.PostAsJsonAsync("/api/test", form with { BaseUrl = url + "/wrong-schema" })).StatusCode == HttpStatusCode.BadGateway, "test rejects invalid gameplay schema");
Check((await client.PostAsJsonAsync("/api/test", form with { BaseUrl = url + "/bad-objectives" })).StatusCode == HttpStatusCode.BadGateway, "test rejects non-string completed objectives");
Check((await client.GetStringAsync("/api/config")).Contains("/v1"), "test does not save draft settings");
var ttsForm = form with { TtsEnabled = true, TtsBaseUrl = url + "/tts", TtsModel = "voice-model", TtsVoice = "default-voice", TtsApiKey = "tts-http-secret", TtsSpeed = 1.5, TtsVoiceMap = new() { ["voice/player"] = "alloy" } };
Check((await client.PutAsJsonAsync("/api/config", ttsForm)).IsSuccessStatusCode, "save endpoint accepts custom TTS settings");
var ttsDocument = await client.GetFromJsonAsync<JsonObject>("/api/config");
Check(ttsDocument!["ttsEnabled"]!.GetValue<bool>() && ttsDocument["ttsBaseUrl"]!.GetValue<string>() == url + "/tts" && ttsDocument["ttsSpeed"]!.GetValue<double>() == 1.5 &&
    ttsDocument["ttsVoiceMap"]!["voice/player"]!.GetValue<string>() == "alloy" && ttsDocument["hasTtsApiKey"]!.GetValue<bool>() && ttsDocument["ttsApiKey"] is null, "config PUT saves and GET returns the whole TTS document");
Check(!(await client.GetStringAsync("/api/config")).Contains("tts-http-secret") && !(await client.GetStringAsync("/api/config")).Contains("saved-secret"), "HTTP config masks both API keys");
Check((await client.PutAsJsonAsync("/api/config", form with { ClearTtsApiKey = true })).IsSuccessStatusCode && !(await client.GetFromJsonAsync<JsonObject>("/api/config"))!["ttsEnabled"]!.GetValue<bool>(), "saving a form without TTS fields turns custom TTS back off");
Check((await client.GetAsync("/")).Headers.TryGetValues("Content-Security-Policy", out var panelCsp) && panelCsp.Single().Contains("media-src blob:"), "the panel CSP allows blob audio so the browser can play voice tests");
Check((await client.PostAsJsonAsync("/internal/tts/speech", new { text = "should not reach" })).StatusCode == HttpStatusCode.Unauthorized, "browser token cannot call internal TTS speech");
Check((await client.PutAsJsonAsync("/api/config", ttsForm)).IsSuccessStatusCode, "custom TTS settings saved again for endpoint tests");
var ttsDiskBefore = File.ReadAllText(panelConfig);
var ttsDraftTest = await client.PostAsJsonAsync("/api/tts/test", new { ttsBaseUrl = url + "/tts", ttsModel = "draft-model", ttsVoice = "draft-voice", ttsApiKey = "draft-key", input = "Draft greeting" });
Check(ttsDraftTest.IsSuccessStatusCode && ttsDraftTest.Content.Headers.ContentType?.MediaType == "application/octet-stream" &&
    ttsDraftTest.Headers.TryGetValues("X-Tts-Sample-Rate", out var testRates) && testRates.Single() == "24000" &&
    ttsDraftTest.Headers.TryGetValues("X-Tts-Voice", out var testVoices) && testVoices.Single() == "draft-voice" &&
    (await ttsDraftTest.Content.ReadAsByteArrayAsync()).SequenceEqual(ttsPcm), "the browser TTS test streams raw PCM with sample-rate and voice headers");
Check(observed!["model"]!.GetValue<string>() == "draft-model" && observedAuth == "Bearer draft-key" && observed["input"]!.GetValue<string>() == "Draft greeting",
    "the browser TTS test sends the draft model, voice, and key for exactly one request");
var ttsBlankKeyTest = await client.PostAsJsonAsync("/api/tts/test", new { ttsBaseUrl = url + "/tts", ttsModel = "voice-model", ttsVoice = "test-voice" });
Check(ttsBlankKeyTest.IsSuccessStatusCode && observedAuth == "Bearer tts-http-secret" && observed!["input"]!.GetValue<string>() == TtsProvider.DefaultTestInput,
    "a blank TTS test key reuses the saved secret and omitted input synthesizes the default greeting");
Check(File.ReadAllText(panelConfig) == ttsDiskBefore && !(await client.GetStringAsync("/api/config")).Contains("draft-model") && !(await client.GetStringAsync("/api/config")).Contains("draft-key"),
    "the browser TTS test never saves draft settings");
var ttsInvalidBefore = requests;
var ttsInvalidTest = await client.PostAsJsonAsync("/api/tts/test", new { ttsBaseUrl = "ftp://tts.invalid", ttsModel = "voice-model", ttsVoice = "test-voice" });
Check(ttsInvalidTest.StatusCode == HttpStatusCode.BadRequest && requests == ttsInvalidBefore, "the browser TTS test validates the draft base URL before contacting the provider");
var ttsOversizeBefore = requests;
var ttsOversizeTest = await client.PostAsJsonAsync("/api/tts/test", new { ttsBaseUrl = url + "/tts", ttsModel = "voice-model", ttsVoice = "test-voice", input = new string('x', TtsProvider.MaxInput + 1) });
var ttsOversizeBody = await ttsOversizeTest.Content.ReadAsStringAsync();
Check(ttsOversizeTest.StatusCode == (HttpStatusCode)413 && JsonNode.Parse(ttsOversizeBody)!["error"]!.GetValue<string>() == "size" && requests == ttsOversizeBefore,
    "the browser TTS test rejects oversize draft input with 413 size before contacting the provider");
var ttsControlBefore = requests;
var ttsControlTest = await client.PostAsJsonAsync("/api/tts/test", new { ttsBaseUrl = url + "/tts", ttsModel = "voice-model", ttsVoice = "voice\u0001player" });
Check(ttsControlTest.StatusCode == HttpStatusCode.BadRequest && JsonNode.Parse(await ttsControlTest.Content.ReadAsStringAsync())!["error"]!.GetValue<string>() == "config" && requests == ttsControlBefore,
    "the browser TTS test rejects control characters in the draft voice before contacting the provider");
var ttsFailedTest = await client.PostAsJsonAsync("/api/tts/test", new { ttsBaseUrl = url + "/tts-unauthorized", ttsModel = "voice-model", ttsVoice = "test-voice" });
var ttsFailedBody = await ttsFailedTest.Content.ReadAsStringAsync();
Check(ttsFailedTest.StatusCode == HttpStatusCode.BadGateway && JsonNode.Parse(ttsFailedBody)!["error"]!.GetValue<string>() == "auth" && !ttsFailedBody.Contains("secret-must-not-leak"),
    "the browser TTS test returns the normalized failure without provider payloads");
client.DefaultRequestHeaders.Remove("X-CustomAI-Token"); client.DefaultRequestHeaders.Add("X-CustomAI-Token", internalToken);
Check((await client.GetFromJsonAsync<JsonObject>("/internal/status"))!["state"]!.GetValue<string>() == "success", "game status reflects saved provider test");
Check((await client.PostAsJsonAsync("/internal/chat/completions", input)).IsSuccessStatusCode, "internal request forwarded");
var httpSchemaPath=Path.Combine(output,"http-game","Scam With Your Friends_Data","StreamingAssets","kolkata-json","core-prompts","official-system-prompt.schema.json");
Directory.CreateDirectory(Path.GetDirectoryName(httpSchemaPath)!);
var expectedHttpFormat=JsonNode.Parse("""{"type":"json_schema","json_schema":{"name":"gameplay_caller_turn","strict":true,"schema":{"type":"object","properties":{"dialogue":{"type":"string"}},"required":["dialogue"],"additionalProperties":false}}}""")!;
File.WriteAllText(httpSchemaPath,expectedHttpFormat.ToJsonString());
Check((await client.PostAsJsonAsync("/internal/chat/completions",omittedFormat)).IsSuccessStatusCode && JsonNode.DeepEquals(observed!["response_format"],expectedHttpFormat),"schema-less real game request receives installed schema before upstream dispatch");
var sentBeforeMissing=requests;
File.Delete(httpSchemaPath);
Check((await client.PostAsJsonAsync("/internal/chat/completions",omittedFormat)).StatusCode==HttpStatusCode.BadRequest && requests==sentBeforeMissing,"missing dialogue schema fails before sending a model request");
Check(!(await client.GetStringAsync("/internal/status")).Contains("saved-secret"), "game status does not expose API credentials");
Check((await client.GetAsync("/api/config")).StatusCode == HttpStatusCode.Unauthorized, "internal token cannot read browser config");
Check((await client.PostAsJsonAsync("/api/tts/test", new { ttsBaseUrl = url + "/tts", ttsModel = "voice-model", ttsVoice = "test-voice" })).StatusCode == HttpStatusCode.Unauthorized, "internal token cannot call the browser TTS test");
var gameSpeech = await client.PostAsJsonAsync("/internal/tts/speech", new { text = "Guards incoming", voicePath = "voice/player" });
Check(gameSpeech.IsSuccessStatusCode && gameSpeech.Headers.TryGetValues("X-Tts-Sample-Rate", out var gameRates) && gameRates.Single() == "24000" &&
    gameSpeech.Headers.TryGetValues("X-Tts-Voice", out var mappedVoices) && mappedVoices.Single() == "alloy" &&
    (await gameSpeech.Content.ReadAsByteArrayAsync()).SequenceEqual(ttsPcm) && observed!["voice"]!.GetValue<string>() == "alloy" &&
    observed["input"]!.GetValue<string>() == "Guards incoming" && observedAuth == "Bearer tts-http-secret", "game speech maps the voice path, uses the saved key, and returns provider PCM");
var fallbackSpeech = await client.PostAsJsonAsync("/internal/tts/speech", new { text = "Who goes there", voicePath = (string?)null });
Check(fallbackSpeech.IsSuccessStatusCode && fallbackSpeech.Headers.TryGetValues("X-Tts-Voice", out var fallbackVoices) && fallbackVoices.Single() == "default-voice" &&
    observed!["voice"]!.GetValue<string>() == "default-voice", "an unmapped game voice uses the default provider voice");
var emptySpeech = await client.PostAsJsonAsync("/internal/tts/speech", new { text = "   " });
Check(emptySpeech.StatusCode == HttpStatusCode.BadRequest && (await emptySpeech.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>() == "config", "blank game speech text is rejected");
var gameStatus = await client.GetFromJsonAsync<JsonObject>("/internal/status");
Check(gameStatus!["state"]!.GetValue<string>() == "success" && gameStatus["tts"]!["state"]!.GetValue<string>() == "success" &&
    gameStatus["tts"]!["model"]!.GetValue<string>() == "voice-model" && gameStatus["tts"]!["lastError"] is null &&
    gameStatus["tts"]!["completed"]!.GetValue<long>() >= 2 && !gameStatus.ToJsonString().Contains("tts-http-secret"), "internal status reports the TTS block without exposing the key");
client.DefaultRequestHeaders.Remove("X-CustomAI-Token"); client.DefaultRequestHeaders.Add("X-CustomAI-Token", browserToken);
Check((await client.PutAsJsonAsync("/api/config", form with { ClearTtsApiKey = true })).IsSuccessStatusCode, "custom TTS switched back off for the disabled path");
client.DefaultRequestHeaders.Remove("X-CustomAI-Token"); client.DefaultRequestHeaders.Add("X-CustomAI-Token", internalToken);
var speechBeforeDisabled = requests;
var speechDisabled = await client.PostAsJsonAsync("/internal/tts/speech", new { text = "Silence" });
Check(speechDisabled.StatusCode == HttpStatusCode.Conflict && JsonNode.Parse(await speechDisabled.Content.ReadAsStringAsync())!["error"]!.GetValue<string>() == "disabled" &&
    requests == speechBeforeDisabled, "disabled custom TTS answers 409 before reaching the provider");
var disabledStatus = await client.GetFromJsonAsync<JsonObject>("/internal/status");
Check(disabledStatus!["tts"]!["state"]!.GetValue<string>() == "disabled" && disabledStatus["tts"]!["lastError"] is null, "disabled custom TTS reports disabled state without error churn");
panel.StandardInput.Close(); await panel.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); Check(panel.ExitCode == 0, "helper shuts down on parent pipe EOF");

// Removes the custom voice gate prefix the installer prepends (argument pushes plus call, brfalse and ret) from
// the static five-parameter voice entry, leaving the shipped body untouched. Shared by the withdrawn pre-v9 layout
// fixtures and the offline v8 repair simulation, which must both present the exact pre-gate instruction counts.
void StripVoiceGate(Mono.Cecil.ModuleDefinition module, string entryName, string gateName)
{
    var voiceMethod = module.Types.Single(t => t.Name == "PocketTtsSingleton").Methods.Single(m => m.Name == entryName && m.IsStatic && m.Parameters.Count == 5);
    var gateCall = voiceMethod.Body.Instructions.First(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Call && i.Operand is Mono.Cecil.MethodReference r && r.Name == gateName);
    var gateLength = voiceMethod.Body.Instructions.IndexOf(gateCall) + 3; // Argument pushes plus call, brfalse and ret.
    for (var i = 0; i < gateLength; i++) voiceMethod.Body.Instructions.RemoveAt(0);
}

// Disposable copies only: real game remains untouched by this test.
if (!args.Contains("--provider-only"))
{
var game = Path.Combine(output, "game & release test"); var managed = Path.Combine(game, "Scam With Your Friends_Data", "Managed"); Directory.CreateDirectory(managed);
var realManaged = Path.Combine(Environment.GetEnvironmentVariable("SWYF_GAME_DIR") ?? throw new InvalidOperationException("Set SWYF_GAME_DIR to your game installation folder."), "Scam With Your Friends_Data", "Managed");
foreach (var library in Directory.GetFiles(realManaged, "*.dll").Where(p => Path.GetFileName(p) is not ("Assembly-CSharp.dll" or "ScriptsAssDef.dll" or "SWYF.CustomAI.Bridge.dll")))
    File.Copy(library, Path.Combine(managed, Path.GetFileName(library)));
foreach (var name in new[] { "Assembly-CSharp.dll", "ScriptsAssDef.dll" })
{
    var live = Path.Combine(realManaged, name);
    var mod = Path.GetFullPath(Path.Combine(realManaged, "../../CustomAI"));
    var manifestPath = Path.Combine(mod, "manifest.json");
    var installed = File.Exists(manifestPath) ? JsonSerializer.Deserialize<Swyf.CustomAI.Installation.Manifest>(File.ReadAllText(manifestPath)) : null;
    var record = installed?.Files.Single(x => x.Name == name);
    var source = record != null && Installer.Hash(live) == record.PatchedHash
        ? installed!.Version < 3 ? Path.Combine(mod, "backup", name) : Path.Combine(mod, "backup", record.OriginalHash, name)
        : live;
    File.Copy(source, Path.Combine(managed, name));
}
var dist = Path.Combine(root, "dist");
var gameConfig = Path.Combine(game, "customai.toml");
var beforeInvalidInstall = Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll"));
Check(Installer.Main(["install", game, dist, "--disable-kolkata=true"]) != 0 && !File.Exists(gameConfig) &&
    !File.Exists(Path.Combine(game, "CustomAI", "manifest.json")) && Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll")) == beforeInvalidInstall,
    "removed installer option is rejected before game files change");
Check(Installer.Main(["install", game, dist]) == 0 && !File.Exists(gameConfig), "new installation creates no startup configuration");
Check(Installer.Main(["verify", game]) == 0, "patched assembly hook verification");
GameEntryTests.Run(Path.Combine(managed, "Assembly-CSharp.dll"), Check);
var legacyAssemblyPath = Path.Combine(managed, "Assembly-CSharp.dll");
var legacyManifestPath = Path.Combine(game, "CustomAI", "manifest.json");
var privateSettings = Path.Combine(game, "CustomAI", "settings.json");
const string privateSettingsText = "{\"enabled\":false,\"apiKey\":\"fixture-private-key\"}";
File.WriteAllText(privateSettings, privateSettingsText);
using (var bridge = Mono.Cecil.AssemblyDefinition.ReadAssembly(Path.Combine(dist, "bridge", "SWYF.CustomAI.Bridge.dll")))
{
    Check(!bridge.MainModule.Types.Any(t => t.Name == "BackendConfig") &&
        !bridge.MainModule.Types.Single(t => t.Name == "Runtime").Methods.Any(m => m.Name is "ConfigureBackend" or "ReadBackendDisabled"),
        "release bridge has no backend-disable feature or configuration parser");
}
void CheckStandardHooks(string message)
{
    using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(legacyAssemblyPath);
    var api = assembly.MainModule.Types.Single(t => t.Name == "KolkataApi");
    var freeze = assembly.MainModule.Types.Single(t => t.Name == "AIConversation").Methods.SingleOrDefault(m => m.Name == "FreezePromptAsync");
    if (freeze != null) Check(freeze.Body.Instructions[0].OpCode == Mono.Cecil.Cil.OpCodes.Ldarg_1 &&
        freeze.Body.Instructions[1].Operand is Mono.Cecil.MethodReference hook && hook.Name == "PrepareConversation" && hook.DeclaringType.FullName == "Swyf.CustomAI.Runtime",
        "current prompt preparation starts with the local-provider routing hook");
    Check(api.Methods.Single(m => m.Name == "Awake").Body.Instructions.Count(i => i.Operand is Mono.Cecil.MethodReference m &&
        m.DeclaringType.FullName == "Swyf.CustomAI.Runtime" && m.Name == "EnsureInitialized") == 1 &&
        !assembly.MainModule.GetMemberReferences().Any(m => m.DeclaringType.FullName == "Swyf.CustomAI.Runtime" && m.Name is "ConfigureBackend" or "SkipAuth"), message);
}
CheckStandardHooks("new installation preserves backend authentication and initializes the custom provider");
VoiceGateTests.Run(legacyAssemblyPath, Check);
// Recreate the withdrawn test layouts only in disposable copies and migrate from verified backups.
foreach (var legacyVersion in new[] { 4, 5, 7 })
{
    using (var resolver = new Mono.Cecil.DefaultAssemblyResolver())
    {
        resolver.AddSearchDirectory(managed);
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(legacyAssemblyPath, new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
        var module = assembly.MainModule;
        var api = module.Types.Single(t => t.Name == "KolkataApi");
        var entry = api.Methods.SingleOrDefault(m => m.Name == "get_CanStartGame");
        var entryHook = entry?.Body.Instructions.SingleOrDefault(i => i.Operand is Mono.Cecil.MethodReference m && m.Name == "UseCustomProviderForGameEntry");
        if (entryHook != null)
        {
            var index = entry!.Body.Instructions.IndexOf(entryHook);
            for (var i = 0; i < 4; i++) entry.Body.Instructions.RemoveAt(index);
        }
        var freeze = module.Types.Single(t => t.Name == "AIConversation").Methods.SingleOrDefault(m => m.Name == "FreezePromptAsync");
        if (legacyVersion < 7 && freeze != null && freeze.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference m && m.Name == "PrepareConversation"))
        { freeze.Body.Instructions.RemoveAt(0); freeze.Body.Instructions.RemoveAt(0); }
        // Versions before 8 shipped no custom voice gates; strip the pair carved from the v9 assembly so
        // the per-version hook verification counts match.
        foreach (var (voiceEntry, gateName) in new[] { ("Speak", "TrySpeak"), ("SpeakTracked", "TrySpeakTracked") })
            StripVoiceGate(module, voiceEntry, gateName);
        var awake = api.Methods.Single(m => m.Name == "Awake");
        var initialize = awake.Body.Instructions.Single(i => i.Operand is Mono.Cecil.MethodReference m && m.DeclaringType.FullName == "Swyf.CustomAI.Runtime" && m.Name == "EnsureInitialized");
        var runtime = ((Mono.Cecil.MethodReference)initialize.Operand).DeclaringType;
        if (legacyVersion == 5)
        {
            var configure = new Mono.Cecil.MethodReference("ConfigureBackend", module.TypeSystem.Void, runtime);
            configure.Parameters.Add(new Mono.Cecil.ParameterDefinition(new Mono.Cecil.TypeReference("UnityEngine", "Component", module,
                module.AssemblyReferences.Single(r => r.Name == "UnityEngine.CoreModule"))));
            awake.Body.GetILProcessor().InsertBefore(initialize, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            initialize.Operand = configure;
        }
        else if (legacyVersion == 4)
        {
            foreach (var getter in new[] { "get_BackendAuthenticated", "get_LobbyAuthenticated" })
            {
                // Recreate removed legacy getters only in the disposable migration fixture.
                var method = api.Methods.SingleOrDefault(m => m.Name == getter);
                if (method == null)
                {
                    method = new Mono.Cecil.MethodDefinition(getter, Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Boolean);
                    api.Methods.Add(method);
                    method.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4_0));
                    method.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
                }
                var first = method.Body.Instructions[0];
                var il = method.Body.GetILProcessor();
                il.InsertBefore(first, il.Create(Mono.Cecil.Cil.OpCodes.Call, new Mono.Cecil.MethodReference("SkipAuth", module.TypeSystem.Boolean, runtime)));
                il.InsertBefore(first, il.Create(Mono.Cecil.Cil.OpCodes.Brfalse, first));
                il.InsertBefore(first, il.Create(Mono.Cecil.Cil.OpCodes.Ldc_I4_1));
                il.InsertBefore(first, il.Create(Mono.Cecil.Cil.OpCodes.Ret));
            }
        }
        assembly.Write(legacyAssemblyPath + ".legacy");
    }
    File.Move(legacyAssemblyPath + ".legacy", legacyAssemblyPath, true);
    var legacyManifest = JsonNode.Parse(File.ReadAllText(legacyManifestPath))!;
    legacyManifest["Version"] = legacyVersion;
    legacyManifest["LegacyMetadata"] = new JsonObject { ["mode"] = "preserved" };
    legacyManifest["Files"]!.AsArray().Single(f => f!["Name"]!.GetValue<string>() == "Assembly-CSharp.dll")!["PatchedHash"] = Installer.Hash(legacyAssemblyPath);
    File.WriteAllText(legacyManifestPath, legacyManifest.ToJsonString());
    File.WriteAllText(gameConfig, "disable_kolkata_api = true\n");
    Check(Installer.Main(["install", game, dist]) == 0 && Installer.Main(["verify", game]) == 0, $"version {legacyVersion} test installation migrates to current hooks");
    var migratedManifest = JsonNode.Parse(File.ReadAllText(legacyManifestPath))!;
    Check(migratedManifest["Version"]!.GetValue<int>() == 9 && migratedManifest["LegacyMetadata"]?["mode"]?.GetValue<string>() == "preserved" &&
        !File.Exists(gameConfig) && File.ReadAllText(privateSettings) == privateSettingsText,
        $"version {legacyVersion} migration removes obsolete config and preserves private settings and manifest metadata");
    CheckStandardHooks($"version {legacyVersion} migration preserves backend authentication state");
    VoiceGateTests.Run(legacyAssemblyPath, Check);
    GameEntryTests.Run(legacyAssemblyPath, Check);
}
File.WriteAllText(gameConfig, "obsolete test configuration");
Check(Installer.Main(["uninstall", game]) == 0 && !File.Exists(gameConfig) && File.ReadAllText(privateSettings) == privateSettingsText,
    "uninstall removes obsolete config while preserving private settings");
Check(Installer.Main(["verify", game, dist]) == 0, "restored files remain compatible");
var releaseZip = Path.Combine(root, "artifacts", "SWYF-Custom-AI-win-x64.zip");
using (var archive = System.IO.Compression.ZipFile.OpenRead(releaseZip))
{
    Check(archive.Entries.Any(e => e.FullName == "Install Custom AI.cmd") && archive.Entries.All(e =>
        !e.FullName.EndsWith("settings.json") && !e.FullName.Contains("/backup/") && !e.FullName.EndsWith("Assembly-CSharp.dll") && !e.FullName.EndsWith("ScriptsAssDef.dll")), "release ZIP has root installer and no game files or secrets");
    Check(archive.GetEntry("CustomAI/package/prerequisites.ps1") is not null, "release ZIP includes the Windows PowerShell prerequisite bootstrap");
    using var installerText = new StreamReader(archive.GetEntry("Install Custom AI.cmd")!.Open());
    var installScript = installerText.ReadToEnd();
    Check(!installScript.Contains("choice /C") && !installScript.Contains("--disable-kolkata") &&
        installScript.Contains("Press any key to continue"), "packaged installer has English messages and no backend selection prompt");
}
System.IO.Compression.ZipFile.ExtractToDirectory(releaseZip, game, true);
File.WriteAllText(Path.Combine(game, "Scam With Your Friends.exe"), "test presence marker; never executed");
async Task<int> RunWrapper(string name, string input = "")
{
    using var wrapper = new Process { StartInfo = new ProcessStartInfo("cmd.exe", "/d /s /c \"\"" + Path.Combine(game, name) + "\"\"")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = output, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
    wrapper.Start();
    var stdout = wrapper.StandardOutput.ReadToEndAsync(); var errors = wrapper.StandardError.ReadToEndAsync();
    await wrapper.StandardInput.WriteLineAsync(input); wrapper.StandardInput.Close();
    await wrapper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    if (wrapper.ExitCode != 0) Console.WriteLine(await stdout + await errors);
    return wrapper.ExitCode;
}
var packagedPanelConfig = Path.Combine(game, "CustomAI/package/panel/SWYF.CustomAI.Panel.runtimeconfig.json");
var beforeFailedSetup = Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll"));
File.Move(packagedPanelConfig, packagedPanelConfig + ".test-backup");
try
{
    Check(await RunWrapper("Install Custom AI.cmd") != 0 &&
        Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll")) == beforeFailedSetup &&
        !File.Exists(gameConfig),
        "failed prerequisite check stops the CMD wrapper before changing game files");
}
finally { File.Move(packagedPanelConfig + ".test-backup", packagedPanelConfig); }
Check(await RunWrapper("Install Custom AI.cmd") == 0 && !File.Exists(gameConfig), "double-click installer needs no selection and handles spaces and ampersands from another working directory");
Check(await RunWrapper("Install Custom AI.cmd") == 0 && File.ReadAllText(privateSettings) == privateSettingsText, "double-click reinstall preserves private settings");
Check(await RunWrapper("Uninstall Custom AI.cmd") == 0, "double-click uninstall restores extracted installation");
File.WriteAllText(Path.Combine(game, "steam_appid.txt"), "not-a-steam-id");
Check(await RunWrapper("Play with Custom AI.cmd") != 0, "play wrapper stops on invalid Steam metadata without launching");
void SimulateUpdate(string name, bool breakHook = false, bool breakEntry = false)
{
    var path = Path.Combine(managed, name);
    using var resolver = new Mono.Cecil.DefaultAssemblyResolver(); resolver.AddSearchDirectory(managed);
    using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(path, new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
    assembly.MainModule.Mvid = Guid.NewGuid();
    if (breakHook) assembly.MainModule.Types.Single(t => t.Name == "KolkataApi").Methods.Single(m => m.Name == "CompleteOpenRouterAsync").Name = "ChangedCompletionContract";
    if (breakEntry)
    {
        var entry = assembly.MainModule.Types.Single(t => t.Name == "KolkataApi").Methods.Single(m => m.Name == "get_CanStartGame");
        ((Mono.Cecil.MethodReference)entry.Body.Instructions[0].Operand).Name = "ChangedSteamInitialization";
    }
    assembly.Write(path + ".updated");
    assembly.Dispose();
    File.Move(path + ".updated", path, true);
}
var originalAi = Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll"));
SimulateUpdate("Assembly-CSharp.dll");
var updatedAi = Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll"));
Check(updatedAi != originalAi && Installer.Main(["verify", game, dist]) == 0, "new compatible game hash accepted");
Check(Installer.Main(["install", game, dist]) == 0, "compatible update installs without rebuilding bridge");
var manifestFile = Path.Combine(game, "CustomAI", "manifest.json");
var manifest = JsonSerializer.Deserialize<Swyf.CustomAI.Installation.Manifest>(File.ReadAllText(manifestFile))!;
var uiRecord = manifest.Files.Single(f => f.Name == "ScriptsAssDef.dll");
File.Copy(Path.Combine(game, "CustomAI", "backup", uiRecord.OriginalHash, uiRecord.Name), Path.Combine(managed, uiRecord.Name), true);
SimulateUpdate("ScriptsAssDef.dll");
var updatedUi = Installer.Hash(Path.Combine(managed, "ScriptsAssDef.dll"));
Check(Installer.Main(["install", game, dist]) == 0, "partial Steam update repaired using current generation");
Check(Installer.Main(["uninstall", game]) == 0 && Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll")) == updatedAi && Installer.Hash(Path.Combine(managed, "ScriptsAssDef.dll")) == updatedUi, "uninstall restores latest generation rather than original release");
Check(Installer.Main(["install", game, dist]) == 0, "reinstall updated generation");
manifest = JsonSerializer.Deserialize<Swyf.CustomAI.Installation.Manifest>(File.ReadAllText(manifestFile))!;
var aiRecord = manifest.Files.Single(f => f.Name == "Assembly-CSharp.dll");
File.Copy(Path.Combine(game, "CustomAI", "backup", aiRecord.OriginalHash, aiRecord.Name), Path.Combine(managed, aiRecord.Name), true);
SimulateUpdate("Assembly-CSharp.dll", breakHook: true);
var incompatibleHash = Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll"));
var unchangedUi = Installer.Hash(Path.Combine(managed, "ScriptsAssDef.dll"));
Check(Installer.Main(["install", game, dist]) != 0 && Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll")) == incompatibleHash && Installer.Hash(Path.Combine(managed, "ScriptsAssDef.dll")) == unchangedUi, "incompatible update rejected before either live assembly changes");
Check(Installer.Main(["uninstall", game]) == 0 && Installer.Hash(Path.Combine(managed, "Assembly-CSharp.dll")) == incompatibleHash, "uninstall preserves incompatible Steam replacement");
File.Copy(Path.Combine(game, "CustomAI", "backup", aiRecord.OriginalHash, aiRecord.Name), Path.Combine(managed, aiRecord.Name), true);
bool hasEntryGate;
using (var cleanAi = Mono.Cecil.AssemblyDefinition.ReadAssembly(Path.Combine(managed, aiRecord.Name)))
    hasEntryGate = cleanAi.MainModule.Types.Single(t => t.Name == "KolkataApi").Methods.Any(m => m.Name == "get_CanStartGame");
if (hasEntryGate)
{
    SimulateUpdate("Assembly-CSharp.dll", breakEntry: true);
    var changedGate = Installer.Hash(Path.Combine(managed, aiRecord.Name));
    Check(Installer.Main(["install", game, dist]) != 0 && Installer.Hash(Path.Combine(managed, aiRecord.Name)) == changedGate,
        "changed Steam entry contract is rejected before live files are patched");
}
}
// Offline installer check: patch the clean shipping assemblies in a disposable scratch copy only. Runnable on
// Linux together with --provider-only; the live game directory is read but never written.
if (args.Contains("--installer-offline"))
{
    var offlineGame = Path.Combine(output, "offline-game");
    var offlineManaged = Path.Combine(offlineGame, "Scam With Your Friends_Data", "Managed");
    Directory.CreateDirectory(offlineManaged);
    var offlineRealManaged = Path.Combine(Environment.GetEnvironmentVariable("SWYF_GAME_DIR") ?? throw new InvalidOperationException("Set SWYF_GAME_DIR to your game installation folder."), "Scam With Your Friends_Data", "Managed");
    foreach (var library in Directory.GetFiles(offlineRealManaged, "*.dll").Where(p => Path.GetFileName(p) is not ("Assembly-CSharp.dll" or "ScriptsAssDef.dll" or "SWYF.CustomAI.Bridge.dll")))
        File.Copy(library, Path.Combine(offlineManaged, Path.GetFileName(library)));
    var offlineMod = Path.GetFullPath(Path.Combine(offlineRealManaged, "../../CustomAI"));
    var offlineManifestPathOnDisk = Path.Combine(offlineMod, "manifest.json");
    var liveManifest = File.Exists(offlineManifestPathOnDisk) ? JsonSerializer.Deserialize<Swyf.CustomAI.Installation.Manifest>(File.ReadAllText(offlineManifestPathOnDisk)) : null;
    var cleanSources = new Dictionary<string, string>();
    foreach (var name in new[] { "Assembly-CSharp.dll", "ScriptsAssDef.dll" })
    {
        var live = Path.Combine(offlineRealManaged, name);
        var record = liveManifest?.Files.Single(x => x.Name == name);
        var source = record != null && Installer.Hash(live) == record.PatchedHash
            ? liveManifest!.Version < 3 ? Path.Combine(offlineMod, "backup", name) : Path.Combine(offlineMod, "backup", record.OriginalHash, name)
            : live;
        cleanSources[name] = source;
        File.Copy(source, Path.Combine(offlineManaged, name));
    }
    var cleanAiHash = Installer.Hash(cleanSources["Assembly-CSharp.dll"]);
    var offlineBridge = new[] { Path.Combine(root, "dist", "bridge", "SWYF.CustomAI.Bridge.dll"), Path.Combine(root, "src", "Bridge", "bin", "Release", "net471", "SWYF.CustomAI.Bridge.dll") }
        .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Build the bridge before running the offline installer check.");
    var offlinePackage = Path.Combine(output, "offline-package");
    Directory.CreateDirectory(Path.Combine(offlinePackage, "bridge"));
    File.Copy(offlineBridge, Path.Combine(offlinePackage, "bridge", "SWYF.CustomAI.Bridge.dll"), true);
    Directory.CreateDirectory(Path.Combine(offlinePackage, "panel"));
    File.WriteAllText(Path.Combine(offlinePackage, "panel", "SWYF.CustomAI.Panel.exe"), "test presence marker; never executed");
    var offlineAssembly = Path.Combine(offlineManaged, "Assembly-CSharp.dll");
    var offlineManifest = Path.Combine(offlineGame, "CustomAI", "manifest.json");
    Check(Installer.Main(["install", offlineGame, offlinePackage]) == 0 && File.Exists(offlineManifest), "offline installation patches the clean shipping assemblies");
    var offlineDocument = JsonSerializer.Deserialize<Swyf.CustomAI.Installation.Manifest>(File.ReadAllText(offlineManifest))!;
    Check(offlineDocument.Version == 9 && offlineDocument.Files.Single(f => f.Name == "Assembly-CSharp.dll").OriginalHash == cleanAiHash,
        "offline installation writes the v9 manifest and backs up the clean assembly");
    VoiceGateTests.Run(offlineAssembly, Check);
    int CurrentAiHooks(string file)
    {
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(file);
        return (assembly.MainModule.Types.Single(t => t.Name == "AIConversation").Methods.Any(m => m.Name == "FreezePromptAsync") ? 3 : 2) +
            (assembly.MainModule.Types.Single(t => t.Name == "KolkataApi").Methods.Any(m => m.Name == "get_CanStartGame") ? 1 : 0);
    }
    bool HookVerificationFails(string file, int expected)
    { try { Installer.VerifyPatched(file, expected); return false; } catch (InvalidOperationException) { return true; } }
    int OfflineRuntimeCalls(string file)
    {
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(file);
        return assembly.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Count(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Call && i.Operand is Mono.Cecil.MethodReference r && r.DeclaringType.FullName == "Swyf.CustomAI.Runtime");
    }
    var expectedOfflineHooks = CurrentAiHooks(cleanSources["Assembly-CSharp.dll"]) + Installer.CustomVoiceGateCount;
    Installer.VerifyPatched(offlineAssembly, expectedOfflineHooks, "EnsureInitialized");
    Check(HookVerificationFails(offlineAssembly, expectedOfflineHooks - 1) && HookVerificationFails(offlineAssembly, expectedOfflineHooks + 1),
        "hook verification counts both custom voice gates exactly and rejects any other total");
    var halfInstalled = Path.Combine(output, "half-installed-Assembly-CSharp.dll");
    using (var resolver = new Mono.Cecil.DefaultAssemblyResolver())
    {
        resolver.AddSearchDirectory(offlineManaged);
        using var half = Mono.Cecil.AssemblyDefinition.ReadAssembly(offlineAssembly, new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
        var gate = half.MainModule.Types.Single(t => t.Name == "PocketTtsSingleton").Methods.Single(m => m.Name == "Speak" && m.Parameters.Count == 5);
        while (gate.Body.Instructions[0].OpCode != Mono.Cecil.Cil.OpCodes.Ret) gate.Body.Instructions.RemoveAt(0);
        gate.Body.Instructions.RemoveAt(0); // The first ret of a patched entry is always the gate return.
        half.Write(halfInstalled);
    }
    Check(HookVerificationFails(halfInstalled, expectedOfflineHooks), "stripping one voice gate fails the full hook verification");
    // The shared helper the legacy migration fixtures use strips both gates from a scratch copy of the
    // fresh v9 install: the runtime call count drops by exactly the gate count and v9 verification fails.
    var strippedGates = Path.Combine(output, "gate-stripped-Assembly-CSharp.dll");
    using (var resolver = new Mono.Cecil.DefaultAssemblyResolver())
    {
        resolver.AddSearchDirectory(offlineManaged);
        using var stripped = Mono.Cecil.AssemblyDefinition.ReadAssembly(offlineAssembly, new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
        foreach (var (voiceEntry, gateName) in new[] { ("Speak", "TrySpeak"), ("SpeakTracked", "TrySpeakTracked") })
            StripVoiceGate(stripped.MainModule, voiceEntry, gateName);
        stripped.Write(strippedGates);
    }
    Check(OfflineRuntimeCalls(strippedGates) == expectedOfflineHooks - Installer.CustomVoiceGateCount,
        "the shared gate helper strips both voice gates leaving exactly the other patch hooks");
    Check(HookVerificationFails(strippedGates, expectedOfflineHooks),
        "hook verification fails when both voice gates are stripped below the v9 expected count");
    var firstOfflineInstall = Installer.Hash(offlineAssembly);
    Check(Installer.Main(["install", offlineGame, offlinePackage]) == 0 && Installer.Hash(offlineAssembly) == firstOfflineInstall &&
        Installer.Main(["verify", offlineGame]) == 0, "reinstalling from the verified backup adds no second voice gate");
    VoiceGateTests.Run(offlineAssembly, Check);
    try
    {
        Installer.Patch(offlineAssembly, Path.Combine(output, "double-patched-Assembly-CSharp.dll"), offlineBridge, offlineManaged);
        throw new Exception("A patched assembly was patched a second time.");
    }
    catch (InvalidOperationException) { Check(true, "patching an already patched assembly is refused outright"); }
    // v8 to v9 repair, recreated on the disposable copy: strip both gate prefixes with the shared helper
    // and stamp the manifest back.
    using (var resolver = new Mono.Cecil.DefaultAssemblyResolver())
    {
        resolver.AddSearchDirectory(offlineManaged);
        using var v8sim = Mono.Cecil.AssemblyDefinition.ReadAssembly(offlineAssembly, new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
        foreach (var (voiceEntry, gateName) in new[] { ("Speak", "TrySpeak"), ("SpeakTracked", "TrySpeakTracked") })
            StripVoiceGate(v8sim.MainModule, voiceEntry, gateName);
        v8sim.Write(offlineAssembly + ".v8sim");
    }
    File.Move(offlineAssembly + ".v8sim", offlineAssembly, true);
    var offlineV8 = JsonNode.Parse(File.ReadAllText(offlineManifest))!;
    offlineV8["Version"] = 8;
    offlineV8["Files"]!.AsArray().Single(f => f!["Name"]!.GetValue<string>() == "Assembly-CSharp.dll")!["PatchedHash"] = Installer.Hash(offlineAssembly);
    File.WriteAllText(offlineManifest, offlineV8.ToJsonString());
    Check(Installer.Main(["verify", offlineGame]) == 0 && JsonNode.Parse(File.ReadAllText(offlineManifest))!["Version"]!.GetValue<int>() == 8,
        "a version 8 installation verifies while waiting for the repair install");
    Check(Installer.Main(["install", offlineGame, offlinePackage]) == 0 && JsonNode.Parse(File.ReadAllText(offlineManifest))!["Version"]!.GetValue<int>() == 9 &&
        OfflineRuntimeCalls(offlineAssembly) == expectedOfflineHooks, "a version 8 installation is repaired into the version 9 custom voice gates");
    Check(Installer.Main(["uninstall", offlineGame]) == 0 && Installer.Hash(offlineAssembly) == cleanAiHash,
        "uninstall restores the clean assembly from the backup and removes both voice gates");
    foreach (var contractEntry in new[] { "Speak", "SpeakTracked" })
    {
        using (var resolver = new Mono.Cecil.DefaultAssemblyResolver())
        {
            resolver.AddSearchDirectory(offlineManaged);
            using var broken = Mono.Cecil.AssemblyDefinition.ReadAssembly(cleanSources["Assembly-CSharp.dll"], new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
            broken.MainModule.Types.Single(t => t.Name == "PocketTtsSingleton").Methods.Single(m => m.Name == contractEntry && m.Parameters.Count == 5).Name = "ChangedVoiceContract";
            broken.Write(offlineAssembly + ".contract-broken");
        }
        File.Move(offlineAssembly + ".contract-broken", offlineAssembly, true);
        var rejectedHash = Installer.Hash(offlineAssembly);
        var rejectedAssDefHash = Installer.Hash(Path.Combine(offlineManaged, "ScriptsAssDef.dll"));
        Check(Installer.Main(["install", offlineGame, offlinePackage]) != 0 && Installer.Hash(offlineAssembly) == rejectedHash &&
            Installer.Hash(Path.Combine(offlineManaged, "ScriptsAssDef.dll")) == rejectedAssDefHash && !File.Exists(offlineManifest),
            $"a renamed voice entry {contractEntry} is rejected before either live assembly changes");
    }
}

if (args.Contains("--live"))
{
    var livePath = Path.Combine(Environment.GetEnvironmentVariable("SWYF_GAME_DIR") ?? throw new InvalidOperationException("Set SWYF_GAME_DIR to your game installation folder."), "CustomAI", "settings.json");
    var live = JsonSerializer.Deserialize<Settings>(File.ReadAllText(livePath), SettingsStore.Json)!;
    var liveInput = (JsonObject)samplingInput.DeepClone();
    liveInput["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with JSON: dialogue is a greeting, trust_percent is 50, emotion is NEUTRAL." });
    liveInput["response_format"] = JsonNode.Parse("""{"type":"json_schema","json_schema":{"name":"caller_turn","strict":true,"schema":{"type":"object","properties":{"dialogue":{"type":"string"},"trust_percent":{"type":"integer","minimum":0,"maximum":100},"emotion":{"type":"string","enum":["TRUSTING","SUSPICIOUS","ANGRY","NEUTRAL"]}},"required":["dialogue","trust_percent","emotion"],"additionalProperties":false}}}""");
    var liveResponse = await provider.Complete(liveInput, live, false, default);
    var text = JsonNode.Parse(Provider.ExtractText(liveResponse))!;
    Check(text["dialogue"]!.GetValue<string>().Length > 0 && text["trust_percent"]!.GetValue<int>() == 50 && text["emotion"]!.GetValue<string>() == "NEUTRAL", "configured live provider returns valid caller JSON with game defaults");
}
await fake.StopAsync(); await fake.DisposeAsync();

using(var handler=new PairingHandler())
using(var pairing=new ManifestDeXClient(Path.Combine(output,"race","settings.json"),provider,handler)){
 await pairing.Connect(default);var pending=pairing.Poll(default);await handler.Started.Task;
 pairing.Disconnect();handler.Release.TrySetResult();await pending;
 Check(pairing.Effective(new Settings(UseManifestDeX:true)).ApiKey=="","Disconnect ignores a late OAuth completion");
}
using(var handler=new PairingHandler())
using(var pairing=new ManifestDeXClient(Path.Combine(output,"single-flight","settings.json"),provider,handler)){
 await pairing.Connect(default);handler.Release.TrySetResult();await pairing.Poll(default);
 await Task.WhenAll(Enumerable.Range(0,8).Select(_=>pairing.Account(default)));
 Check(handler.AccountCalls==1,"concurrent account refreshes share one combined service request");
}

using(var handler=new RateLimitedPairingHandler())
using(var pairing=new ManifestDeXClient(Path.Combine(output,"rate-limit","settings.json"),provider,handler)){
 for(int i=0;i<2;i++){
  try{await pairing.Connect(default);throw new Exception("Expected rate limit");}
  catch(ApiFailure e){Check(e.Status==429&&e.RetryAfter is >590 and <=600,"pairing preserves Retry-After and caches cooldown");}
 }
 Check(handler.Calls==1,"repeated pairing clicks do not resend during cooldown");
}

foreach(var reason in new[]{HttpRequestError.NameResolutionError,HttpRequestError.SecureConnectionError,HttpRequestError.ConnectionError})
using(var handler=new NetworkFailureHandler(reason))
using(var networkClient=new ManifestDeXClient(Path.Combine(output,"network-"+reason,"settings.json"),provider,handler)){
    var result=JsonSerializer.SerializeToNode(await networkClient.Status(default))!;
    Check(result["signInAvailable"]!.GetValue<bool>()==false && result["notice"]!.GetValue<string>().Contains(reason.ToString()) && result["notice"]!.GetValue<string>().Contains("AccessDenied") && !result.ToJsonString().Contains("private-exception-detail"),"network failure identifies "+reason+" without exposing raw exception details");
}
Console.WriteLine($"ALL {passed} CHECKS PASSED");
File.WriteAllText(Path.Combine(output, "result.txt"), $"{passed} checks passed at {DateTimeOffset.UtcNow:O}");
sealed class PairingHandler:HttpMessageHandler
{
 public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public int AccountCalls;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  if(request.RequestUri!.AbsolutePath=="/api/auth/start")return Json("""{"flowId":"test-flow","completionKey":"test-key","authorizationUrl":"https://swyf-ai.manifestdex.com/oauth/start"}""");
  if(request.RequestUri.AbsolutePath=="/api/auth/poll"){Started.TrySetResult();await Release.Task;return Json("""{"status":"approved","ticket":"swyf2_race_test_ticket"}""");}
  if(request.RequestUri.AbsolutePath=="/api/me"){Interlocked.Increment(ref AccountCalls);await Task.Delay(100,ct);return Json("""{"user":{"id":"m"},"quota":{"windows":[]},"service":{"available":true}}""");}
  return Json("{}");
 }
 private static HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value,Encoding.UTF8,"application/json")};
}
sealed class RateLimitedPairingHandler:HttpMessageHandler
{
 public int Calls;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
  Interlocked.Increment(ref Calls);await Task.Delay(10,ct);
  var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("""{"error":"rate_limit","message":"Too many requests."}""",Encoding.UTF8,"application/json")};
  response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));return response;
 }
}

sealed class NetworkFailureHandler(HttpRequestError reason):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new HttpRequestException(reason,"private-exception-detail",new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied));
}
