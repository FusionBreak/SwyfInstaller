using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Mirror;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace Swyf.CustomAI
{
    public sealed class Runtime : MonoBehaviour
    {
        private static Runtime? instance;
        private static string ModDirectory => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Runtime).Assembly.Location)!, "..", "..", "CustomAI"));
        private static string ConfigPath => Path.Combine(ModDirectory, "settings.json");
        // Panel reachability view for the custom voice runtime; same conditions TryComplete requires.
        internal static bool PanelReady => instance != null && instance.url.Length > 0 && instance.helper != null && !instance.helper.HasExited;
        internal static string PanelUrl => instance == null ? "" : instance.url;
        internal static string PanelToken => instance == null ? "" : instance.internalToken;
        private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
        private Process? helper;
        private string internalToken = "", browserToken = "", url = "";
        private string error = "Starting the settings panel...";
        private float startedAt;
        private bool pendingOpen, shuttingDown;
        private JObject? connectionStatus;
        private float nextStatusPoll;
        private bool pollingStatus;
        private float nextTtsRefresh;
        private static bool ReadEnabled()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return false;
                var value = JObject.Parse(File.ReadAllText(ConfigPath))["enabled"];
                return value == null || value.Type != JTokenType.Boolean || value.Value<bool>();
            }
            catch { return true; }
        }

        // This changes local game entry only; no game-service credentials or auth state are created.
        public static bool UseCustomProviderForGameEntry() => ReadEnabled();

        public static void EnsureInitialized()
        {
            if (instance != null) return;
            try
            {
                var go = new GameObject("SWYF Custom AI");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<Runtime>();
                instance.StartHelper();
            }
            catch { Debug.LogError("[CustomAI] Could not initialize the mod."); }
        }

        // The game's System.dll no longer ships Uri.EscapeDataString.
        private static string PercentEncode(string value)
        {
            var builder = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~')
                    builder.Append(c);
                else builder.Append('%').Append(b.ToString("X2"));
            }
            return builder.ToString();
        }

        private static string NewToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private void StartHelper()
        {
            try
            {
                StopHelper();
                url = "";
                connectionStatus = null;
                error = "Starting the settings panel...";
                internalToken = NewToken(); browserToken = NewToken();
                startedAt = Time.realtimeSinceStartup;
                var exe = Path.Combine(ModDirectory, "panel", "SWYF.CustomAI.Panel.exe");
                var process = new Process { StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                }};
                process.OutputDataReceived += (_, e) => { if (e.Data != null) messages.Enqueue(e.Data); };
                // Runtime diagnostics can include paths; never echo arbitrary process/provider output into game logs.
                process.ErrorDataReceived += (_, e) => { };
                if (!process.Start()) throw new IOException();
                helper = process;
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                process.StandardInput.WriteLine(new JObject {
                    ["internalToken"] = internalToken, ["browserToken"] = browserToken,
                    ["configPath"] = ConfigPath, ["parentId"] = Process.GetCurrentProcess().Id
                }.ToString(Newtonsoft.Json.Formatting.None));
                process.StandardInput.Flush();
            }
            catch { error = "Could not start the panel. Check the .NET 10 ASP.NET Core Runtime and mod files. Press F8 to retry."; Debug.LogError("[CustomAI] " + error); }
        }

        private void Update()
        {
            // Keep the cached custom-voice flag in step with the settings file the panel writes.
            if (Time.realtimeSinceStartup >= nextTtsRefresh) { nextTtsRefresh = Time.realtimeSinceStartup + 1f; ReadTtsEnabled(); }
            while (messages.TryDequeue(out var message))
            {
                try
                {
                    var data = JObject.Parse(message);
                    if ((string?)data["type"] == "ready" && Uri.TryCreate((string?)data["url"], UriKind.Absolute, out var address)
                        && address.Scheme == "http" && address.Host == "127.0.0.1")
                    { url = address.GetLeftPart(UriPartial.Authority); error = ""; }
                }
                catch { error = "Could not read the panel startup response."; }
            }
            if (helper != null && helper.HasExited)
            { helper.Dispose(); helper = null; url = ""; error = "The settings panel stopped. Press F8 to reopen it."; }
            if (helper != null && url.Length == 0 && Time.realtimeSinceStartup - startedAt > 10)
            { StopHelper(); error = "The panel did not start within 10 seconds. Press F8 to retry."; }
            if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame) OpenSettings();
            if (pendingOpen && url.Length > 0) { pendingOpen = false; Application.OpenURL(url + "/#" + PercentEncode(browserToken)); }
        }

        public static void OpenSettings()
        {
            EnsureInitialized();
            if (instance == null) return;
            instance.pendingOpen = true;
            if (instance.helper == null || instance.helper.HasExited) instance.StartHelper();
        }

        public static void RefreshConnectionStatus(Component controller)
        {
            var enabled = ReadEnabled();
            if (instance == null || !enabled) return;
            var self = instance;
            if (enabled && self.url.Length > 0 && !self.pollingStatus && Time.realtimeSinceStartup >= self.nextStatusPoll)
            { self.pollingStatus = true; self.nextStatusPoll = Time.realtimeSinceStartup + 2; self.StartCoroutine(self.PollStatus()); }
            var root = controller.GetComponent<UIDocument>()?.rootVisualElement;
            if (root == null) return;
            var label = root.Q<Label>("backend-auth-status");
            var warning = root.Q<VisualElement>("connection-warning");
            var title = root.Q<Label>("connection-warning-title");
            var body = root.Q<Label>("connection-warning-body");
            if (label == null || warning == null || title == null || body == null) return;
            var state = self.url.Length == 0 ? "unavailable" : (string?)self.connectionStatus?["state"] ?? "loading";
            var model = (string?)self.connectionStatus?["model"] ?? "";
            var failed = state == "error" || state == "unavailable" || state == "disabled";
            label.enableRichText = false;
            label.text = "CUSTOM AI: " + (state == "success" ? "LAST REQUEST SUCCEEDED" : state == "connecting" ? "CONNECTING..." :
                state == "untested" ? "NOT TESTED YET" : failed ? "CONNECTION ERROR" : "CHECKING STATUS...") + (model.Length > 0 ? " · " + model : "");
            label.EnableInClassList("connection-state--ready", state == "success");
            label.EnableInClassList("connection-state--failed", failed);
            foreach (var name in new[] { "vip-membership-role", "unlimited-membership-role" })
            { var role = root.Q<Label>(name); if (role != null) role.style.display = DisplayStyle.None; }
            root.Q<VisualElement>("menu-root")?.EnableInClassList("menu-root--auth-recovery", false);
            foreach (var name in new[] { "browser-sign-in-message", "sign-in-support-message" })
            { var message = root.Q<Label>(name); if (message != null) message.style.display = DisplayStyle.None; }
            // The original renderer still owns Steam connectivity warnings.
            if (root.Q<Label>("steam-connection-status")?.ClassListContains("connection-state--failed") == true) return;
            warning.style.display = failed || state == "untested" ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q<VisualElement>("connection-status")?.EnableInClassList("connection-status--warning", failed);
            title.text = failed ? "CUSTOM AI CONNECTION FAILED" : "CUSTOM AI NOT TESTED YET";
            body.style.display = DisplayStyle.Flex;
            body.text = failed ? ((string?)self.connectionStatus?["message"] ?? self.error) + " Press F8 to open settings." :
                "Press F8 to test the connection, or start a call. Game AI sign-in does not indicate the status of this provider.";
        }

        private IEnumerator PollStatus()
        {
            using (var request = UnityWebRequest.Get(url + "/internal/status"))
            {
                request.SetRequestHeader("X-CustomAI-Token", internalToken);
                request.timeout = 3;
                yield return request.SendWebRequest();
                try
                {
                    connectionStatus = request.result == UnityWebRequest.Result.Success ? JObject.Parse(request.downloadHandler.text) :
                        new JObject { ["state"] = "error", ["message"] = "Cannot reach the local settings panel." };
                }
                catch { connectionStatus = new JObject { ["state"] = "error", ["message"] = "Could not read panel status." }; }
                finally { pollingStatus = false; }
            }
        }

        public static void AttachMenu(Component controller)
        {
            EnsureInitialized();
            try
            {
                var document = controller.GetComponent<UIDocument>();
                if (document == null || document.rootVisualElement == null) return;
                var root = document.rootVisualElement;
                if (root.Q<Button>("custom-ai-settings") != null) return;
                var anchor = root.Q<Button>("settings-button") ?? root.Q<Button>("open-player-log-button") ?? root.Q<Button>("feedback-button");
                var button = new Button(OpenSettings) { name = "custom-ai-settings", text = "AI Settings · F8" };
                if (anchor != null)
                {
                    var classes = typeof(VisualElement).GetProperty("classList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(anchor, null) as System.Collections.Generic.IEnumerable<string>;
                    if (classes != null) foreach (var className in classes) button.AddToClassList(className);
                    anchor.parent.Insert(anchor.parent.IndexOf(anchor) + 1, button);
                }
                else
                {
                    button.style.position = Position.Absolute; button.style.right = 20; button.style.bottom = 20;
                    root.Add(button);
                }
                button.tooltip = "Configure the custom AI provider in your browser";
            }
            catch { Debug.LogWarning("[CustomAI] Could not add the menu button; F8 is still available."); }
        }

        public static bool TryComplete(JObject body, CancellationToken cancellation, bool background, out UniTask<JObject> result)
        {
            result = default;
            // Prompt/version lookups are game control requests, not text generation.
            if (body["resolve_prompt_version"] is JValue control && control.Type == JTokenType.Boolean && control.Value<bool>())
                return false;
            EnsureInitialized();
            if (!ReadEnabled()) return false;
            if (!NetworkServer.active || !NetworkServer.listen)
            { result = UniTask.FromException<JObject>(new InvalidOperationException("The custom provider only runs on the lobby host.")); return true; }
            if (instance == null || instance.url.Length == 0 || instance.helper == null || instance.helper.HasExited)
            { result = UniTask.FromException<JObject>(new IOException("The custom AI panel is unavailable; press F8 to reopen it.")); return true; }
            result = instance.Complete(body, cancellation, background);
            return true;
        }

        // Installer gates for the custom voice path (custom-ai/docs/tts-integration-notes.md §9.11). Returning false
        // means the untouched game Speak/SpeakTracked bodies run. The decision is synchronous and side-effect-free,
        // and the panel is already bootstrapped by TryComplete before any caller speech can happen.
        public static bool TrySpeak(string text, string voicePath, uint callNetId) => TtsRuntime.TrySpeak(text, voicePath, callNetId);

        public static bool TrySpeakTracked(string text, string voicePath, uint callNetId, uint completionToken) => TtsRuntime.TrySpeakTracked(text, voicePath, callNetId, completionToken);

        // Refresh the cached ttsEnabled flag. Opt-in like the panel schema: a missing file or key keeps it false.
        public static void ReadTtsEnabled() => TtsRuntime.Enabled = ReadTtsFlag();

        private static bool ReadTtsFlag()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return false;
                var value = JObject.Parse(File.ReadAllText(ConfigPath))["ttsEnabled"];
                return value != null && value.Type == JTokenType.Boolean && value.Value<bool>();
            }
            catch { return false; }
        }

        public static void PrepareConversation(object options)
        {
            // Use the game's shipped prompt path when text generation belongs to this mod.
            if (!ReadEnabled()) return;
            options.GetType().GetProperty("GameplayRoute")!.SetValue(options, "openrouter", null);
        }

        private UniTask<JObject> Complete(JObject body, CancellationToken cancellation, bool background)
        {
            var completion = new UniTaskCompletionSource<JObject>();
            StartCoroutine(Send(body, cancellation, background, completion));
            return completion.Task;
        }

        private IEnumerator Send(JObject body, CancellationToken cancellation, bool background, UniTaskCompletionSource<JObject> completion)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            if (bytes.Length > 512 * 1024) { completion.TrySetException(new IOException("The AI request is too large.")); yield break; }
            using (var request = new UnityWebRequest(url + "/internal/chat/completions", "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("X-CustomAI-Token", internalToken);
                request.SetRequestHeader("X-CustomAI-Background", background ? "1" : "0");
                request.timeout = 16;
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellation.IsCancellationRequested) { request.Abort(); completion.TrySetCanceled(cancellation); yield break; }
                    yield return null;
                }
                if (cancellation.IsCancellationRequested) { completion.TrySetCanceled(cancellation); yield break; }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    error = "The custom provider request failed. Press F8 to open the panel for details.";
                    Debug.LogWarning("[CustomAI] " + error);
                    completion.TrySetException(new IOException(error));
                    yield break;
                }
                try { completion.TrySetResult(JObject.Parse(request.downloadHandler.text)); }
                catch { completion.TrySetException(new IOException("Could not read the panel response.")); }
            }
        }

        private void StopHelper()
        {
            if (helper == null) return;
            try { helper.StandardInput.Close(); } catch { }
            // EOF gives the helper a graceful shutdown; it also monitors this game's process handle.
            try { helper.Dispose(); } catch { }
            helper = null;
        }
        private void OnApplicationQuit() { shuttingDown = true; StopHelper(); }
        private void OnDestroy() { if (!shuttingDown) StopHelper(); if (instance == this) instance = null; }
    }
}
