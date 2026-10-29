using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using PocketTTS;   // PocketTtsFrame ships in PocketTTS.dll (§3.1)
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Swyf.CustomAI
{
    // Bridge-side custom voice runtime (custom-ai/docs/tts-integration-notes.md §9). The Installer-gated
    // Speak/SpeakTracked hand utterances to TrySpeak/TrySpeakTracked. Every gate answer is false unless the
    // mod can safely generate and pump the audio itself, so the untouched shipped body still runs. When a
    // request is taken, the pump reproduces the PocketTtsSingleton state machine (§2.2/§9.5/§9.8) and feeds
    // synthetic 24 kHz mono 960-sample frames through the game's own private HandleFrameGenerated (§3.2).
    internal static class TtsRuntime
    {
        internal const uint ReviewNarrationCallNetId = 4294967198u;   // §8: always fall through to vanilla.
        internal const uint PreviewCallNetIdLow = 3221224472u;        // §8: character-creator preview range.
        internal const uint PreviewCallNetIdHigh = 4294966295u;       // §8: includes the wrap-to-zero top id.

        // Cached view of the panel's ttsEnabled setting, refreshed by Runtime.ReadTtsEnabled. Opt-in: the
        // panel schema defaults TtsEnabled to false, so a missing file/key or unreadable file stays false.
        internal static bool Enabled { get; set; }

        private static readonly object gate = new object();
        private static readonly Queue<Job> queue = new Queue<Job>();   // §9.3: one active utterance, serial FIFO.
        private static Pump? pump;
        private static bool replaying;              // §9.11: the reflected replay re-enters the patched gate, which then falls through.
        private static ushort nextSequence;         // §4: process-monotonic frame counters, never reset per request.
        private static uint nextTimestamp;

        // --- game bindings (§9 bootstrap); everything is fail-closed, and a bind failure keeps vanilla ---
        private static bool bound, unavailable;
        private static Type? singletonType, requestType;
        private static PropertyInfo? instanceProperty;
        private static FieldInfo? fCurrentRequest, fProcessingCallNetId, fActiveCallNetId, fActivePrompt, fIsSpeaking, fLastAudioProgress, fPocketTts;
        private static FieldInfo? rCancelled;
        private static Action<object, bool>? reportCompletion;    // private static ReportTrackedCompletion(TtsRequest, bool)
        private static MethodInfo? handleFrameMethod;             // private HandleFrameGenerated(PocketTtsFrame), bound per instance below
        private static Delegate? handleFrame;
        private static UnityEngine.Object? handleFrameOwner;
        private static MethodInfo? beginTranscript, isBusyForCall, speakOriginal;
        private static PropertyInfo? languageCurrent, languageCode;

        public static bool TrySpeak(string text, string voicePath, uint callNetId, string? languageCode, bool isFiller)
            => TryHandoff(text, voicePath, callNetId, 0u, languageCode, isFiller);

        // Vanilla SpeakTracked drops token==0 requests; hijacking that case would change behaviour, so it is left to vanilla.
        public static bool TrySpeakTracked(string text, string voicePath, uint callNetId, uint completionToken, string? languageCode, bool isFiller)
            => completionToken != 0 && TryHandoff(text, voicePath, callNetId, completionToken, languageCode, isFiller);

        internal static bool IsReservedCallNetId(uint callNetId)
            => callNetId == ReviewNarrationCallNetId || (callNetId >= PreviewCallNetIdLow && callNetId <= PreviewCallNetIdHigh);

        // The whole §9.11 gate decision as a pure static function: synchronous and side-effect-free, so the
        // decision itself can be unit-tested headlessly. False lets the untouched shipped body run; a true
        // answer means the caller may still lose the race to an error, which the handoff handles as a decline.
        // isFiller keeps the shipped CallerFillerWords settings gate (§2.1); languageCode is a pure passthrough
        // carried verbatim for the request and never changes the decision.
        internal static bool ShouldHijack(string text, uint callNetId, uint completionToken, bool isFiller, string? languageCode, bool fallbackReentry, bool ttsEnabled, bool panelAvailable, bool singletonReady)
        {
            if (fallbackReentry) return false;                       // §9.11: the post-failure replay must fall through.
            if (!ttsEnabled) return false;                           // Opt-in; the shipped provider answers while false.
            if (isFiller) return false;                              // §2.1: filler words stay vanilla with their own settings gate.
            if (string.IsNullOrWhiteSpace(text)) return false;       // §2.1 gate; vanilla would drop it anyway.
            if (IsReservedCallNetId(callNetId)) return false;        // §8 recommendations: never hijack these.
            if (callNetId == 0) return false;                        // Silent non-call speech stays vanilla.
            return panelAvailable && singletonReady;                 // Fail fast: no panel or no PocketTTS means clean vanilla fallback.
        }

        // Singleton-side readiness for the gate: the engine field HandleFrameGenerated guards first (§3.2/§10.5).
        private static bool SingletonReady()
        {
            if (!Bind()) return false;
            var singleton = Singleton;
            try { return singleton != null && fPocketTts!.GetValue(singleton) != null; }
            catch { return false; }
        }

        // §9.11: the decision must be synchronous and side-effect-free; only the final enqueue mutates state.
        // Any false lets the completely untouched original Speak/SpeakTracked body run.
        private static bool TryHandoff(string text, string voicePath, uint callNetId, uint completionToken, string? languageCode, bool isFiller)
        {
            try
            {
                var panelAvailable = Runtime.PanelReady;
                var singletonReady = panelAvailable && SingletonReady();
                if (!ShouldHijack(text, callNetId, completionToken, isFiller, languageCode, replaying, Enabled, panelAvailable, singletonReady)) return false;
                if (Singleton == null) return false;                 // Defensive re-check; the singleton vanished mid-decision.
                var job = new Job(text, voicePath ?? "", callNetId, completionToken, languageCode);
                lock (gate)
                {
                    queue.Enqueue(job);
                    if (pump == null)
                    {
                        // §12: pump in a Bridge-owned MonoBehaviour, never from PocketTtsSingleton.Update.
                        var go = new GameObject("SWYF Custom TTS");
                        UnityEngine.Object.DontDestroyOnLoad(go);
                        pump = go.AddComponent<Pump>();
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private static bool Bind()
        {
            if (bound) return !unavailable;
            bound = true;
            try
            {
                singletonType = Type.GetType("PocketTtsSingleton, Assembly-CSharp");
                if (singletonType == null) { unavailable = true; return false; }
                requestType = singletonType.GetNestedType("TtsRequest", BindingFlags.NonPublic);
                instanceProperty = singletonType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                fCurrentRequest = SingletonField("currentRequest"); fProcessingCallNetId = SingletonField("processingCallNetId");
                fActiveCallNetId = SingletonField("activeCallNetId"); fActivePrompt = SingletonField("activePrompt");
                fIsSpeaking = SingletonField("isSpeaking"); fLastAudioProgress = SingletonField("lastAudioProgress");
                fPocketTts = SingletonField("pocketTTS");
                rCancelled = RequestField("Cancelled");
                var report = singletonType.GetMethod("ReportTrackedCompletion", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { requestType!, typeof(bool) }, null);
                handleFrameMethod = singletonType.GetMethod("HandleFrameGenerated", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(PocketTtsFrame) }, null);
                beginTranscript = singletonType.GetMethod("BeginTranscriptRequest", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(uint), typeof(string) }, null);
                isBusyForCall = singletonType.GetMethod("IsBusyForCallInternal", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(uint) }, null);
                // Reflected original entry for the §9.11 mid-pump fallback; the Installer gate falls through while replaying.
                speakOriginal = singletonType.GetMethod("Speak", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string), typeof(uint), typeof(string), typeof(bool) }, null);
                if (requestType == null || instanceProperty == null || report == null || handleFrameMethod == null || beginTranscript == null || isBusyForCall == null || speakOriginal == null ||
                    fCurrentRequest == null || fProcessingCallNetId == null || fActiveCallNetId == null || fActivePrompt == null || fIsSpeaking == null || fLastAudioProgress == null || fPocketTts == null ||
                    rCancelled == null)
                {
                    unavailable = true;
                    Debug.LogWarning("[CustomAI] Custom voice needs the unmodified PocketTtsSingleton hooks; the original voice path stays in use.");
                    return false;
                }
                reportCompletion = (Action<object, bool>)Delegate.CreateDelegate(typeof(Action<object, bool>), report);
                var callLanguage = Type.GetType("CallLanguage, Assembly-CSharp");
                languageCurrent = callLanguage?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                languageCode = languageCurrent?.PropertyType.GetProperty("Code");
                return true;
            }
            catch (Exception e)
            {
                unavailable = true;
                Debug.LogWarning("[CustomAI] Custom voice could not attach to the game: " + e.GetType().Name);
                return false;
            }
        }

        private static FieldInfo? SingletonField(string name) => singletonType!.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo? RequestField(string name) => requestType!.GetField(name, BindingFlags.Public | BindingFlags.Instance);

        private static UnityEngine.Object? Singleton
        {
            get { try { return instanceProperty?.GetValue(null) as UnityEngine.Object; } catch { return null; } }
        }

        // §7: removed calls leave the Mirror client spawn list; while offline or headless we cannot tell,
        // so this stays true and never throws outside a running client.
        private static bool CallPresent(uint callNetId)
        {
            try
            {
                if (!Mirror.NetworkClient.active) return true;
                return Mirror.NetworkClient.spawned.TryGetValue(callNetId, out var identity) && identity != null;
            }
            catch { return true; }
        }

        // Best-effort default language, mirroring what EnqueueSpeak resolves for call requests.
        private static string? CurrentLanguageCode()
        {
            try
            {
                var current = languageCurrent?.GetValue(null);
                return current == null ? null : languageCode?.GetValue(current) as string;
            }
            catch { return null; }
        }

        private static void SetRequest(Job job, string name, object? value)
            => requestType!.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.SetValue(job.Request!, value);

        // §9.11 fallback: hand a line that was never heard back to the untouched shipped path. Always with
        // token 0: a tracked request's token was already reported exactly once before the replay, and
        // re-reporting is forbidden by §6 (the Completed flag would drop it anyway). The caller's languageCode
        // is replayed verbatim; null lets the shipped body resolve its own default (§1).
        private static void ReplayToVanilla(Job job)
        {
            try
            {
                replaying = true;
                speakOriginal!.Invoke(null, new object?[] { job.Text, job.VoicePath, job.CallNetId, job.LanguageCode, false });
            }
            catch (Exception e) { Debug.LogWarning("[CustomAI] Custom voice could not fall back to the original path: " + e.GetType().Name); }
            finally { replaying = false; }
        }

        // Never-activated requests have no TtsRequest to report; raise the completion event directly
        // (private static backing field, §12) so scripted endings never wait for the 120 s deadline (§6.4).
        private static void RaiseTrackedCompletionEvent(uint callNetId, uint completionToken, bool succeeded)
        {
            try
            {
                var field = singletonType!.GetField("OnTrackedRequestCompleted", BindingFlags.NonPublic | BindingFlags.Static);
                if (field?.GetValue(null) is Action<uint, uint, bool> handler) handler(callNetId, completionToken, succeeded);
            }
            catch { }
        }

        private static void PurgeQueued()
        {
            lock (gate) { while (queue.Count > 0) { var job = queue.Dequeue(); if (!job.Done) DropQueued(job); } }
        }

        private static void DropQueued(Job job)
        {
            job.Done = true;
            if (job.CompletionToken != 0) RaiseTrackedCompletionEvent(job.CallNetId, job.CompletionToken, false);
        }

        private sealed class Job
        {
            public readonly string Text, VoicePath;
            public readonly string? LanguageCode;    // Caller's language, or null = resolve the default at activation (§9.1).
            public readonly uint CallNetId, CompletionToken;
            public object? Request;      // The game's TtsRequest once activated; null means "never activated".
            public byte[]? Pcm;          // Raw 24 kHz mono s16le from the panel.
            public int Position;         // Samples consumed.
            public bool Fetching, Failed, Done, Heard, AwaitingTail;
            public Job(string text, string voicePath, uint callNetId, uint completionToken, string? languageCode)
            { Text = text; VoicePath = voicePath; CallNetId = callNetId; CompletionToken = completionToken; LanguageCode = languageCode; }
        }

        // §4 frame geometry, identical to every built-in provider.
        private sealed class Pump : MonoBehaviour
        {
            private const int FrameSamples = 960;
            private const double FrameSeconds = 0.04;
            private const double EndDrainSeconds = 0.12;   // §9.7: let the last frame leave the pipeline before reporting, closer to the provider "drained" semantics than firing at the last dispatch.
            private const int MaxFramesPerTick = 8;     // §4 catch-up cap: at most 8 frames per Update.
            private Job? active;
            private double nextTick, tailAt;

            private void Update()
            {
                try { Tick(); }
                catch (Exception e)
                {
                    // Never leave the game state machine half-activated if a pump step throws.
                    Debug.LogWarning("[CustomAI] Custom voice pump aborted: " + e.GetType().Name);
                    AbortAll();
                }
            }

            private void Tick()
            {
                if (active == null)
                {
                    if (!TryStartNext()) { if (!HasQueued()) Release(); return; }
                }
                var job = active;
                if (job == null) return;

                // Global abort conditions, checked before any pacing (§7, §9.9). Flipping the setting off is an
                // ordinary abort: a line that was never heard still replays so the shipped voice says it (§9.11).
                if (!Enabled) { Finish(job, report: true, succeeded: false, replay: !job.Heard); return; }
                var singleton = Singleton;
                // A destroyed singleton means the game's OnDisable already force-cancelled and reported everything (§7).
                if (singleton == null) { Finish(job, report: false, succeeded: false, replay: false); return; }
                // §9.9: if the game replaced currentRequest (watchdog, interrupt), it owns the teardown from here on.
                // The tracked failure is still reported (§6.4: silence costs a scripted ending 120 s); ReportTracked-
                // Completion is idempotent via Completed, so a game-side report of the same request never doubles.
                if (job.Request != null && fCurrentRequest!.GetValue(singleton) != job.Request)
                { Finish(job, report: true, succeeded: false, replay: false); return; }
                // §7: CancelCall sets Cancelled on the active request; the shim reports the failure itself.
                if (job.Request != null && (bool)rCancelled!.GetValue(job.Request)!)
                { Finish(job, report: true, succeeded: false, replay: false); return; }
                if (job.Failed) { Finish(job, report: true, succeeded: false, replay: !job.Heard); return; }
                // §7 safety net: a call that left NetworkClient.spawned is gone; report the tracked token false
                // instead of speaking into a vanished call. No replay: a line for a dead call must not be re-voiced.
                if (!CallPresent(job.CallNetId)) { Finish(job, report: true, succeeded: false, replay: false); return; }
                // Waiting for the panel leaves the request in the pre-first-audio state the watchdog tolerates (§9.6).
                if (job.Fetching || job.Pcm == null) return;

                if (job.AwaitingTail)
                {
                    if (Time.unscaledTimeAsDouble >= tailAt) Finish(job, report: true, succeeded: true, replay: false);   // §9.7
                    return;
                }

                double now = Time.unscaledTimeAsDouble;
                int dispatched = 0;
                while (now >= nextTick)
                {
                    // §7/§9.6: poll Cancelled between the frames of a catch-up burst too, so a cancellation
                    // that lands mid-burst stops the burst on this very tick instead of a whole Update later.
                    if (job.Request != null && (bool)rCancelled!.GetValue(job.Request)!) break;   // the next tick's top check finishes the job.
                    if (!Dispatch(job, singleton)) return;    // tail started or the job terminated inside
                    nextTick += FrameSeconds;
                    if (++dispatched >= MaxFramesPerTick)
                    {
                        if (now >= nextTick) nextTick = now + FrameSeconds;   // catch-up cap, then re-anchor
                        break;
                    }
                }
            }

            private bool Dispatch(Job job, UnityEngine.Object singleton)
            {
                var pcm = job.Pcm!;
                int total = pcm.Length / 2;
                if (job.Position >= total)
                {
                    // §9.6/§7: once every sample is queued, mark the transcript complete so "tap to interrupt" keeps working,
                    // then wait the short end-drain before reporting like the provider "drained" semantics (§9.7).
                    job.AwaitingTail = true;
                    tailAt = Time.unscaledTimeAsDouble + EndDrainSeconds;
                    requestType!.GetField("TranscriptCompletedAt", BindingFlags.Public | BindingFlags.Instance)?.SetValue(job.Request, Time.realtimeSinceStartupAsDouble);
                    return false;
                }
                if (BoundHandler(singleton) is not Action<PocketTtsFrame> handler)
                { Finish(job, report: true, succeeded: false, replay: !job.Heard); return false; }
                // Fresh float[960] per frame (§4); a short tail is zero-padded, and transcript slices stay empty (§10.9 option A).
                var samples = new float[FrameSamples];
                int n = Math.Min(FrameSamples, total - job.Position);
                for (var i = 0; i < n; i++)
                {
                    var b = (job.Position + i) * 2;
                    samples[i] = (short)(pcm[b] | (pcm[b + 1] << 8)) / 32768f;
                }
                job.Position += n;
                // The game's own guard, watchdog bookkeeping, echo suppression, event raise and transcript pipeline come for free (§3.2).
                handler(new PocketTtsFrame(samples, FrameSamples, 24000, 1, nextSequence++, nextTimestamp, ""));
                nextTimestamp += 960u;
                job.Heard = true;
                return true;
            }

            private Action<PocketTtsFrame>? BoundHandler(UnityEngine.Object singleton)
            {
                if (handleFrame != null && handleFrameOwner == singleton) return (Action<PocketTtsFrame>)handleFrame;
                try
                {
                    handleFrame = Delegate.CreateDelegate(typeof(Action<PocketTtsFrame>), singleton, handleFrameMethod!);
                    handleFrameOwner = singleton;
                    return (Action<PocketTtsFrame>)handleFrame;
                }
                catch { return null; }
            }

            private bool HasQueued() { lock (gate) return queue.Count > 0; }

            private bool TryStartNext()
            {
                Job? job = null;
                lock (gate)
                {
                    while (queue.Count > 0 && queue.Peek().Done) queue.Dequeue();
                    if (queue.Count > 0) job = queue.Peek();
                }
                if (job == null) return false;
                var singleton = Singleton;
                if (!Enabled || singleton == null) { PurgeQueued(); return false; }   // Purge path: resolve queued tokens as false (§6.4).
                // Never activate while the shipped coroutine or a built-in narrator/preview owns the singleton (§9.3).
                // currentRequest may legitimately linger from the last shim request (§2.6 drain), so it is not a busy
                // signal here. A hijacked caller line must never be dropped because the singleton is momentarily busy:
                // keep the job queued and retry on a later tick.
                if ((bool)fIsSpeaking!.GetValue(singleton)! || (uint)fProcessingCallNetId!.GetValue(singleton)! != 0u)
                    return false;
                Activate(job, singleton);
                return true;
            }

            private void Activate(Job job, UnityEngine.Object singleton)
            {
                try
                {
                    var now = Time.realtimeSinceStartupAsDouble;
                    job.Request = Activator.CreateInstance(requestType!);
                    SetRequest(job, "EnqueuedAt", now);
                    SetRequest(job, "Text", job.Text);
                    SetRequest(job, "CallNetId", job.CallNetId);
                    SetRequest(job, "CompletionToken", job.CompletionToken);
                    SetRequest(job, "IsFiller", false);
                    // The caller's explicit language wins; null/empty falls back to the shipped default
                    // resolution, mirroring EnqueueSpeak's languageCode ?? CallLanguage.Current.Code (§9.1).
                    SetRequest(job, "LanguageCode", string.IsNullOrEmpty(job.LanguageCode) ? CurrentLanguageCode() : job.LanguageCode);
                    SetRequest(job, "VoicePath", job.VoicePath);
                    SetRequest(job, "SpokenText", job.Text);
                    SetRequest(job, "FullTranscript", job.Text);
                    // §9.4: mirror EnqueueSpeak's transcript-slot reservation exactly (shim requests are never fillers).
                    if (!(bool)isBusyForCall!.Invoke(singleton, new object[] { job.CallNetId })!)
                        beginTranscript!.Invoke(singleton, new object[] { job.CallNetId, job.Text });
                    // §9.5 activation. FirstAudioAt stays -1 so the 10 s playback watchdog arms only after the first frame.
                    fCurrentRequest!.SetValue(singleton, job.Request);
                    fProcessingCallNetId!.SetValue(singleton, job.CallNetId);
                    fActiveCallNetId!.SetValue(singleton, job.CallNetId);
                    fActivePrompt!.SetValue(singleton, job.Text.Trim());
                    fIsSpeaking!.SetValue(singleton, true);
                    SetRequest(job, "StartedAt", now);
                    fLastAudioProgress!.SetValue(singleton, now);
                    lock (gate) if (queue.Count > 0 && queue.Peek() == job) queue.Dequeue();
                    active = job;
                    job.Fetching = true;
                    StartCoroutine(Fetch(job));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[CustomAI] Custom voice request could not start: " + e.GetType().Name);
                    if (job.Request != null) Finish(job, report: true, succeeded: false, replay: !job.Heard);
                    else
                    {
                        lock (gate) if (queue.Count > 0 && queue.Peek() == job) queue.Dequeue();
                        DropQueued(job);
                        if (!job.Heard) ReplayToVanilla(job);   // Request == null here: never activated, so an unheard line goes back to the shipped path.
                    }
                }
            }

            // Fetches raw s16le 24 kHz mono PCM from the panel's internal endpoint (custom-ai/src/Panel/Program.cs).
            private IEnumerator Fetch(Job job)
            {
                var payload = new JObject { ["text"] = job.Text };
                if (job.VoicePath.Length > 0) payload["voicePath"] = job.VoicePath;
                var bytes = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));
                using (var request = new UnityWebRequest(Runtime.PanelUrl + "/internal/tts/speech", "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(bytes);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("X-CustomAI-Token", Runtime.PanelToken);
                    request.timeout = 45;   // above the panel's 30 s provider deadline; the panel answers 409 while TTS is off.
                    var operation = request.SendWebRequest();
                    while (!operation.isDone && !job.Done) yield return null;
                    if (job.Done) yield break;   // cancelled or aborted meanwhile; the response is not needed anymore.
                    var data = request.downloadHandler?.data;
                    if (request.result == UnityWebRequest.Result.Success && data != null && data.Length > 0)
                    {
                        job.Pcm = data;
                        job.Fetching = false;
                        nextTick = Time.unscaledTimeAsDouble;   // re-anchor pacing after the wait, never burst (§4).
                    }
                    else
                    {
                        // Only the HTTP status code is logged; response bodies must never reach the game log.
                        Debug.LogWarning("[CustomAI] Custom voice request failed with HTTP " + request.responseCode.ToString(CultureInfo.InvariantCulture) + "; the original voice path is used instead.");
                        job.Failed = true;
                        job.Fetching = false;
                    }
                }
            }

            // Mirrors the per-request teardown of AC:28396-AC:28404 plus the drain rule of AC:28407-AC:28416.
            // StopActivePlayback is deliberately never called (§9.8).
            private void Finish(Job job, bool report, bool succeeded, bool replay)
            {
                var singleton = Singleton;
                if (job.Request != null)
                {
                    // §9.8: on failure report before tearing down; PublishFailedTranscript matches against currentRequest.
                    // Deliberately outside the is-current guard below: the game may already own the request, and
                    // ReportTrackedCompletion is idempotent via request.Completed (§6), so a failure report can
                    // never double-fire, while dropping it could stall a scripted ending for 120 s (§6.4).
                    if (report && !succeeded)
                    {
                        try { reportCompletion!.Invoke(job.Request, false); }
                        catch (Exception e) { Debug.LogWarning("[CustomAI] Custom voice completion could not be reported: " + e.GetType().Name); }
                    }
                    // §9 item 9: only touch the singleton's fields while its currentRequest is still ours; once
                    // the game replaced it (watchdog, interrupt) it owns the teardown. A reflection failure here
                    // counts as "not current": state that cannot be verified is left alone.
                    var isCurrent = false;
                    try { isCurrent = singleton != null && fCurrentRequest!.GetValue(singleton) == job.Request; }
                    catch (Exception e) { Debug.LogWarning("[CustomAI] Custom voice could not verify the current request: " + e.GetType().Name); }
                    if (isCurrent)
                    {
                        // Invariant: succeeded:true implies isCurrent, because the tick-top identity check
                        // precedes the only success call site (the §9.7 end-drain) within the same tick.
                        try
                        {
                            fActiveCallNetId!.SetValue(singleton, 0u);
                            fActivePrompt!.SetValue(singleton, null);
                            fIsSpeaking!.SetValue(singleton, false);
                            if (report && succeeded) reportCompletion!.Invoke(job.Request, true);   // §2.5 ordering on success
                            fProcessingCallNetId!.SetValue(singleton, 0u);
                            // Drain semantics: currentRequest is only cleared when no shim request follows immediately.
                            if (!HasQueued() && fCurrentRequest!.GetValue(singleton) == job.Request) fCurrentRequest.SetValue(singleton, null);
                        }
                        catch (Exception e) { Debug.LogWarning("[CustomAI] Custom voice teardown failed: " + e.GetType().Name); }
                    }
                }
                job.Done = true;
                if (active == job) active = null;
                // §9.11: replay is strictly for lines that were never heard; anything with heard audio is
                // left reported-only, so a caller line can never be voiced twice.
                if (replay && !job.Heard) ReplayToVanilla(job);
            }

            // Unexpected pump exception mid-session: finish the active request as a failure and resolve queued tokens (§6.4).
            private void AbortAll()
            {
                var job = active;
                if (job != null) Finish(job, report: true, succeeded: false, replay: false);
                PurgeQueued();
            }

            private void Release()
            {
                lock (gate) if (pump == this) pump = null;
                UnityEngine.Object.Destroy(gameObject);
            }

            // Quit-time paths: the game tears itself down (OnDisable force-cancel), so nothing is reported here.
            private void OnApplicationQuit() { active = null; lock (gate) queue.Clear(); }
            private void OnDestroy() { active = null; lock (gate) { if (pump == this) pump = null; queue.Clear(); } }
        }
    }
}
