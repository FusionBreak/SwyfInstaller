using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Swyf.CustomAI.Installation;

public record FileRecord(string Name, string OriginalHash, string PatchedHash);
public record Manifest(int Version, FileRecord[] Files)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public static class Program
{
    private static readonly string[] GameFiles = ["Assembly-CSharp.dll", "ScriptsAssDef.dll"];
    private const string BridgeName = "SWYF.CustomAI.Bridge.dll";
    public const int CustomVoiceGateCount = 2; // Speak + SpeakTracked gates in Assembly-CSharp.dll.
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    public static int Main(string[] args)
    {
        if (args is ["--check-runtime"]) { Console.WriteLine("SWYF_RUNTIME_OK"); return 0; }
        try
        {
            if (args.Length is < 2 or > 3 || args[0] is not ("install" or "uninstall" or "verify" or "launch"))
                throw new InvalidOperationException("Usage: Installer install|uninstall|verify|launch <game directory> [package directory]");
            var game = Path.GetFullPath(args[1]);
            // Remove the obsolete startup file from the withdrawn test build on upgrade/uninstall.
            var legacyConfig = Path.Combine(game, "customai.toml");
            string? steamAppId = null;
            if (args[0] == "launch")
            {
                steamAppId = File.ReadAllText(Path.Combine(game, "steam_appid.txt")).Trim();
                if (steamAppId.Length == 0 || !steamAppId.All(char.IsAsciiDigit))
                    throw new InvalidOperationException("The game steam_appid.txt does not contain a valid Steam app ID.");
            }
            var managed = Path.Combine(game, "Scam With Your Friends_Data", "Managed");
            var mod = Path.Combine(game, "CustomAI");
            var manifestPath = Path.Combine(mod, "manifest.json");
            if (args[0] != "verify" && (Process.GetProcessesByName("Scam With Your Friends").Length > 0 ||
                Process.GetProcessesByName("SWYF.CustomAI.Panel").Length > 0))
                throw new InvalidOperationException("Close the game first.");
            Manifest? manifest = File.Exists(manifestPath) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath)) : null;
            if (manifest != null && (manifest.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9) || manifest.Files == null || manifest.Files.Length != 2 ||
                manifest.Files.Any(x => x == null || !GameFiles.Contains(x.Name) || !ValidHash(x.OriginalHash) || !ValidHash(x.PatchedHash)) ||
                manifest.Files.Select(x => x.Name).Distinct().Count() != 2))
                throw new InvalidOperationException("Invalid installation manifest.");
            string Backup(FileRecord file) => manifest!.Version < 3 ? Path.Combine(mod, "backup", file.Name) :
                Path.Combine(mod, "backup", file.OriginalHash, file.Name);
            if (args[0] == "uninstall")
            {
                if (manifest == null) { File.Delete(legacyConfig); Console.WriteLine("The mod is not installed."); return 0; }
                var restore = new List<FileRecord>();
                foreach (var file in manifest.Files)
                {
                    var live = Path.Combine(managed, file.Name);
                    if (Hash(live) == file.PatchedHash)
                    { RequireHash(Backup(file), file.OriginalHash); restore.Add(file); }
                    else
                    {
                        // Steam may have replaced only one assembly. Never downgrade that new file.
                        Compatibility.RequireUnpatched(live);
                        Console.WriteLine($"Preserving updated game file: {file.Name}");
                    }
                }
                foreach (var file in restore) AtomicCopy(Backup(file), Path.Combine(managed, file.Name));
                File.Delete(Path.Combine(managed, BridgeName));
                File.Delete(legacyConfig);
                File.Delete(manifestPath);
                Console.WriteLine("Mod uninstalled; original DLLs restored. Settings and backups were preserved in CustomAI.");
                return 0;
            }
            // Select the current clean files (or verified backups for files still carrying our patch).
            var sources = new Dictionary<string, string>();
            var observedHashes = new Dictionary<string, string>();
            bool needsRepair = manifest == null || manifest.Version < 9;
            foreach (var name in GameFiles)
            {
                var live = Path.Combine(managed, name);
                var observed = Hash(live); observedHashes[name] = observed;
                var record = manifest?.Files.Single(x => x.Name == name);
                if (record != null && observed == record.PatchedHash)
                {
                    var backup = Backup(record); RequireHash(backup, record.OriginalHash);
                    sources[name] = backup;
                    VerifyPatched(live, name == "ScriptsAssDef.dll" ? (manifest!.Version == 1 ? 2 : 3) : (manifest!.Version == 4 ? 4 : manifest.Version >= 7 ? CurrentAiHookCount(live, manifest.Version >= 8, manifest.Version >= 9) : 2),
                        manifest.Version == 5 ? "ConfigureBackend" : manifest.Version >= 6 ? "EnsureInitialized" : null);
                }
                else { Compatibility.RequireUnpatched(live); sources[name] = live; needsRepair = true; }
            }
            var package = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;
            var bridge = package != null ? Path.Combine(package, "bridge", BridgeName) : Path.Combine(managed, BridgeName);
            Compatibility.Validate(sources, bridge, managed);
            if (args[0] == "verify")
            {
                if (manifest != null && !needsRepair && !File.Exists(Path.Combine(managed, BridgeName)))
                    throw new IOException("The bridge DLL is missing; run install to repair it.");
                Console.WriteLine(needsRepair ? "Game hook compatibility checks passed. Run install before playing to apply or repair the mod." : "Installed mod and game hook compatibility verified.");
                return 0;
            }
            if (args.Length < 3) throw new ArgumentException("A package directory is required.");
            if (!File.Exists(Path.Combine(package!, "panel", "SWYF.CustomAI.Panel.exe"))) throw new IOException("The package is incomplete; run build.ps1 first.");
            Directory.CreateDirectory(mod);
            Directory.CreateDirectory(Path.Combine(mod, "backup"));
            var stage = Path.Combine(mod, "stage"); Directory.CreateDirectory(stage);
            var records = new List<FileRecord>();
            foreach (var name in GameFiles)
            {
                var originalHash = Hash(sources[name]);
                var backup = Path.Combine(mod, "backup", originalHash, name);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if (!File.Exists(backup))
                    File.Copy(sources[name], backup);
                RequireHash(backup, originalHash);
                Patch(backup, Path.Combine(stage, name), bridge, managed);
                VerifyPatched(Path.Combine(stage, name), name == "ScriptsAssDef.dll" ? 3 : CurrentAiHookCount(backup), "EnsureInitialized");
                records.Add(new(name, originalHash, Hash(Path.Combine(stage, name))));
            }
            // Catch a concurrent Steam update before touching live files.
            foreach (var name in GameFiles) RequireHash(Path.Combine(managed, name), observedHashes[name]);
            // Save rollback copies before the first live mutation, including the previous bridge.
            foreach (var name in GameFiles) File.Copy(Path.Combine(managed, name), Path.Combine(stage, name + ".rollback"), true);
            bool hadBridge = File.Exists(Path.Combine(managed, BridgeName));
            if (hadBridge) File.Copy(Path.Combine(managed, BridgeName), Path.Combine(stage, BridgeName + ".rollback"), true);
            var oldConfig = File.Exists(legacyConfig) ? File.ReadAllBytes(legacyConfig) : null;
            try
            {
                foreach (var source in Directory.GetFiles(Path.Combine(package!, "panel"), "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(mod, "panel", Path.GetRelativePath(Path.Combine(package!, "panel"), source));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!); AtomicCopy(source, dest);
                }
                AtomicCopy(bridge, Path.Combine(managed, BridgeName));
                foreach (var name in GameFiles) AtomicCopy(Path.Combine(stage, name), Path.Combine(managed, name));
                File.Delete(legacyConfig);
                var installed = new Manifest(9, records.ToArray()) { Extra = manifest?.Extra };
                File.WriteAllText(manifestPath + ".tmp", JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(manifestPath + ".tmp", manifestPath, true);
            }
            catch
            {
                foreach (var name in GameFiles) AtomicCopy(Path.Combine(stage, name + ".rollback"), Path.Combine(managed, name));
                if (hadBridge) AtomicCopy(Path.Combine(stage, BridgeName + ".rollback"), Path.Combine(managed, BridgeName));
                else File.Delete(Path.Combine(managed, BridgeName));
                if (oldConfig != null) File.WriteAllBytes(legacyConfig, oldConfig);
                throw;
            }
            Console.WriteLine("Installed. Use the AI Settings button or F8 to open the panel. Existing settings are preserved; new configurations start disabled.");
            if (steamAppId != null)
                Process.Start(new ProcessStartInfo("steam://rungameid/" + steamAppId) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    private static bool ValidHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static void RequireHash(string path, string expected)
    {
        if (!File.Exists(path) || Hash(path) != expected)
            throw new InvalidOperationException($"Unexpected file version; the file was not overwritten: {path}");
    }
    private static void AtomicCopy(string source, string destination)
    { File.Copy(source, destination + ".customai-tmp", true); File.Move(destination + ".customai-tmp", destination, true); }

    public static void Patch(string input, string output, string bridgePath, string managed)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed); resolver.AddSearchDirectory(Path.GetDirectoryName(bridgePath)!);
        using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
        using var bridge = AssemblyDefinition.ReadAssembly(bridgePath, new ReaderParameters { AssemblyResolver = resolver });
        var runtime = bridge.MainModule.Types.Single(x => x.FullName == "Swyf.CustomAI.Runtime");
        var module = assembly.MainModule;
        MethodReference Import(string name) => module.ImportReference(runtime.Methods.Single(m => m.Name == name));
        // Only expand branches in methods we edit; leave unrelated game methods untouched.
        void ExpandBranches(MethodDefinition method)
        {
            foreach (var instruction in method.Body.Instructions)
                if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                    instruction.OpCode = (OpCode)typeof(OpCodes).GetField(instruction.OpCode.Code.ToString()[..^2])!.GetValue(null)!;
        }
        if (module.AssemblyReferences.Any(x => x.Name == "SWYF.CustomAI.Bridge")) throw new InvalidOperationException("Only an unmodified backup can be patched.");
        if (Path.GetFileName(input) == "Assembly-CSharp.dll")
        {
            var type = module.Types.Single(t => t.FullName == "KolkataApi");
            var entry = GameEntry.Getter(assembly);
            if (entry != null)
            {
                var backendGate = GameEntry.BackendGate(entry);
                ExpandBranches(entry);
                var entryIl = entry.Body.GetILProcessor();
                foreach (var instruction in new[] {
                    entryIl.Create(OpCodes.Call, Import("UseCustomProviderForGameEntry")),
                    entryIl.Create(OpCodes.Brfalse, backendGate), entryIl.Create(OpCodes.Ldc_I4_1), entryIl.Create(OpCodes.Ret) })
                    entryIl.InsertBefore(backendGate, instruction);
            }
            var complete = type.Methods.Single(m => m.Name == "CompleteOpenRouterAsync" && m.Parameters.Count == 3);
            ExpandBranches(complete);
            if (complete.Parameters[0].ParameterType.FullName != "Newtonsoft.Json.Linq.JObject" ||
                complete.Parameters[1].ParameterType.FullName != "System.Threading.CancellationToken" ||
                complete.Parameters[2].ParameterType.FullName != "System.Boolean" || complete.ReturnType.FullName != "Cysharp.Threading.Tasks.UniTask`1<Newtonsoft.Json.Linq.JObject>" || complete.IsStatic)
                throw new InvalidOperationException("The AI method signature has changed.");
            var result = new VariableDefinition(complete.ReturnType); complete.Body.Variables.Add(result); complete.Body.InitLocals = true;
            var il = complete.Body.GetILProcessor(); var first = complete.Body.Instructions[0];
            foreach (var instruction in new[] {
                il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Ldarg_3),
                il.Create(OpCodes.Ldloca, result), il.Create(OpCodes.Call, Import("TryComplete")),
                il.Create(OpCodes.Brfalse, first), il.Create(OpCodes.Ldloc, result), il.Create(OpCodes.Ret) }) il.InsertBefore(first, instruction);
            var awake = type.Methods.Single(m => m.Name == "Awake" && m.Parameters.Count == 0);
            ExpandBranches(awake);
            var persist = awake.Body.Instructions.Single(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == "DontDestroyOnLoad");
            var awakeIl = awake.Body.GetILProcessor();
            awakeIl.InsertAfter(persist, awakeIl.Create(OpCodes.Call, Import("EnsureInitialized")));
            var freeze = module.Types.Single(t => t.FullName == "AIConversation").Methods.SingleOrDefault(m => m.Name == "FreezePromptAsync");
            if (freeze != null)
            {
                ExpandBranches(freeze);
                var freezeIl = freeze.Body.GetILProcessor(); var start = freeze.Body.Instructions[0];
                freezeIl.InsertBefore(start, freezeIl.Create(OpCodes.Ldarg_1));
                freezeIl.InsertBefore(start, freezeIl.Create(OpCodes.Call, Import("PrepareConversation")));
            }
            // Custom voice gates for the TTS shim (custom-ai/docs/tts-integration-notes.md §9.11 item 11). The gate
            // pushes the entry's original arguments verbatim and calls the static bridge entry; true returns with
            // the request handled, false branches back into the completely untouched shipped body, so the runtime
            // flag alone decides when custom voice is off. Fillers are declined by the gate itself (false).
            var voice = module.Types.Single(t => t.FullName == "PocketTtsSingleton");
            var speak = voice.Methods.Single(m => m.Name == "Speak" && m.IsStatic && m.Parameters.Count == 5);
            ExpandBranches(speak);
            if (!speak.IsStatic || speak.ReturnType.FullName != "System.Void" || speak.Body.HasExceptionHandlers ||
                speak.Parameters[0].ParameterType.FullName != "System.String" || speak.Parameters[1].ParameterType.FullName != "System.String" ||
                speak.Parameters[2].ParameterType.FullName != "System.UInt32" || speak.Parameters[3].ParameterType.FullName != "System.String" ||
                speak.Parameters[4].ParameterType.FullName != "System.Boolean")
                throw new InvalidOperationException("The caller voice entry signature has changed.");
            var speakIl = speak.Body.GetILProcessor(); var speakStart = speak.Body.Instructions[0];
            foreach (var instruction in new[] {
                speakIl.Create(OpCodes.Ldarg_0), speakIl.Create(OpCodes.Ldarg_1), speakIl.Create(OpCodes.Ldarg_2), speakIl.Create(OpCodes.Ldarg_3),
                speakIl.Create(OpCodes.Ldarg_S, speak.Parameters[4]), speakIl.Create(OpCodes.Call, Import("TrySpeak")),
                speakIl.Create(OpCodes.Brfalse, speakStart), speakIl.Create(OpCodes.Ret) }) speakIl.InsertBefore(speakStart, instruction);
            var tracked = voice.Methods.Single(m => m.Name == "SpeakTracked" && m.IsStatic && m.Parameters.Count == 5);
            ExpandBranches(tracked);
            if (!tracked.IsStatic || tracked.ReturnType.FullName != "System.Void" || tracked.Body.HasExceptionHandlers ||
                tracked.Parameters[0].ParameterType.FullName != "System.String" || tracked.Parameters[1].ParameterType.FullName != "System.String" ||
                tracked.Parameters[2].ParameterType.FullName != "System.UInt32" || tracked.Parameters[3].ParameterType.FullName != "System.UInt32" ||
                tracked.Parameters[4].ParameterType.FullName != "System.String")
                throw new InvalidOperationException("The tracked caller voice entry signature has changed.");
            var trackedIl = tracked.Body.GetILProcessor(); var trackedStart = tracked.Body.Instructions[0];
            // Vanilla SpeakTracked never enqueues fillers, so the tracked gate passes isFiller = false.
            foreach (var instruction in new[] {
                trackedIl.Create(OpCodes.Ldarg_0), trackedIl.Create(OpCodes.Ldarg_1), trackedIl.Create(OpCodes.Ldarg_2), trackedIl.Create(OpCodes.Ldarg_3),
                trackedIl.Create(OpCodes.Ldarg_S, tracked.Parameters[4]), trackedIl.Create(OpCodes.Ldc_I4_0), trackedIl.Create(OpCodes.Call, Import("TrySpeakTracked")),
                trackedIl.Create(OpCodes.Brfalse, trackedStart), trackedIl.Create(OpCodes.Ret) }) trackedIl.InsertBefore(trackedStart, instruction);
        }
        else
        {
            foreach (var typeName in new[] { "MainMenuToolkitController", "PauseMenuToolkitController" })
            {
                var method = module.Types.Single(t => t.FullName == typeName).Methods.Single(m => m.Name == "InitializeUi" && m.Parameters.Count == 0);
                ExpandBranches(method);
                // Only the successful terminal return; missing-root early returns remain unchanged.
                var ret = method.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
                ret.OpCode = OpCodes.Ldarg_0; ret.Operand = null;
                var il = method.Body.GetILProcessor();
                var call = il.Create(OpCodes.Call, Import("AttachMenu")); il.InsertAfter(ret, call); il.InsertAfter(call, il.Create(OpCodes.Ret));
            }
            var refresh = module.Types.Single(t => t.FullName == "MainMenuToolkitController").Methods.Single(m => m.Name == "UpdateConnectionStatus");
            ExpandBranches(refresh);
            var originalRefresh = refresh.Body.Instructions.Single(i => i.Operand is MethodReference mr && mr.DeclaringType.Name == "MainMenuConnectionStatus" && mr.Name == "Refresh");
            var refreshIl = refresh.Body.GetILProcessor();
            var instance = refreshIl.Create(OpCodes.Ldarg_0);
            var refreshCall = refreshIl.Create(OpCodes.Call, Import("RefreshConnectionStatus"));
            refreshIl.InsertAfter(originalRefresh, instance); refreshIl.InsertAfter(instance, refreshCall);
        }
        assembly.Write(output);
    }

    private static int CurrentAiHookCount(string path, bool includeGameEntry = true, bool includeCustomVoice = true)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        return (assembly.MainModule.Types.Single(t => t.Name == "AIConversation").Methods.Any(m => m.Name == "FreezePromptAsync") ? 3 : 2) +
            (includeGameEntry && GameEntry.Getter(assembly) != null ? 1 : 0) +
            // v9+ Assembly-CSharp always carries the two unconditional custom voice gates.
            (includeCustomVoice ? CustomVoiceGateCount : 0);
    }

    public static void VerifyPatched(string path, int expected, string? startupHook = null)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var calls = assembly.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Count(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.DeclaringType.FullName == "Swyf.CustomAI.Runtime");
        if (calls != expected) throw new InvalidOperationException("Could not verify the number of patch hooks.");
        if (startupHook != null && Path.GetFileName(path) == "Assembly-CSharp.dll")
        {
            var api = assembly.MainModule.Types.Single(t => t.FullName == "KolkataApi");
            var awake = api.Methods.Single(m => m.Name == "Awake");
            if (awake.Body.Instructions.Count(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr &&
                mr.DeclaringType.FullName == "Swyf.CustomAI.Runtime" && mr.Name == startupHook) != 1)
                throw new InvalidOperationException("Could not verify the mod initialization hook.");
        }
    }
}
