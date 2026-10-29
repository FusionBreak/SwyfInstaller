using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Swyf.CustomAI.Installation;

// Match the contracts we actually patch, rather than the hash of an entire game release.
public static class Compatibility
{
    public static void RequireUnpatched(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        if (assembly.MainModule.AssemblyReferences.Any(r => r.Name == "SWYF.CustomAI.Bridge") ||
            assembly.MainModule.GetMemberReferences().Any(r => r.DeclaringType?.FullName == "Swyf.CustomAI.Runtime"))
            throw new InvalidOperationException($"Unrecognized existing mod patch in {Path.GetFileName(path)}. Restore game files with Steam before repairing.");
    }

    public static void Validate(IReadOnlyDictionary<string, string> sources, string bridge, string managed)
    {
        if (!File.Exists(bridge)) throw new IOException("The bridge DLL is missing. Supply the complete mod package.");
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        resolver.AddSearchDirectory(Path.GetDirectoryName(bridge)!);
        // Resolve game types against the selected clean generation, including partially updated installations.
        using var ai = AssemblyDefinition.ReadAssembly(sources["Assembly-CSharp.dll"], new ReaderParameters { AssemblyResolver = resolver });
        using var ui = AssemblyDefinition.ReadAssembly(sources["ScriptsAssDef.dll"], new ReaderParameters { AssemblyResolver = resolver });
        using var plugin = AssemblyDefinition.ReadAssembly(bridge, new ReaderParameters { AssemblyResolver = resolver });
        foreach (var assembly in new[] { ai, ui })
        {
            RequireUnpatched(sources[assembly.Name.Name + ".dll"]);
            if (assembly.Name.HasPublicKey) Fail("Strong-named game assemblies are not supported.");
        }
        foreach (var reference in plugin.MainModule.GetMemberReferences())
        {
            try
            {
                if (reference.Resolve() == null) Fail($"Missing game library member: {reference.FullName}");
            }
            catch (Exception e) { throw new InvalidOperationException($"Incompatible game libraries: {reference.FullName}", e); }
        }

        var api = Type(ai, "KolkataApi");
        var entry = GameEntry.Getter(ai);
        if (entry != null) GameEntry.BackendGate(entry);
        if (!InheritsComponent(api)) Fail("KolkataApi is no longer a Unity component.");
        var complete = Method(api, "CompleteOpenRouterAsync", "Cysharp.Threading.Tasks.UniTask`1<Newtonsoft.Json.Linq.JObject>",
            "Newtonsoft.Json.Linq.JObject", "System.Threading.CancellationToken", "System.Boolean");
        var awake = Method(api, "Awake", "System.Void");
        var persistence = Calls(awake, "UnityEngine.Object", "DontDestroyOnLoad").ToArray();
        if (persistence.Length != 1 || persistence[0].OpCode != OpCodes.Call ||
            ((MethodReference)persistence[0].Operand).FullName != "System.Void UnityEngine.Object::DontDestroyOnLoad(UnityEngine.Object)")
            Fail("KolkataApi.Awake no longer contains the expected singleton initialization point.");

        // Async state machine shapes vary; inspect calls within the conversation type and its generated types.
        var callers = AllTypes(new[] { Type(ai, "AIConversation") }).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Where(i => i.Operand is MethodReference r &&
                r.DeclaringType.FullName == "KolkataApi" && r.Name == complete.Name).ToArray();
        if (callers.Length == 0 || callers.Any(i => ((MethodReference)i.Operand).FullName != complete.FullName))
            Fail("The shared AI request contract has changed.");
        var freeze = Type(ai, "AIConversation").Methods.SingleOrDefault(m => m.Name == "FreezePromptAsync");
        if (freeze != null)
        {
            Method(Type(ai, "AIConversation"), "FreezePromptAsync", "Cysharp.Threading.Tasks.UniTask", "AIConversationTurnOptions", "System.Threading.CancellationToken");
            var options = Type(ai, "AIConversationTurnOptions");
            Method(options, "get_GameplayRoute", "System.String");
            var setter = Method(options, "set_GameplayRoute", "System.Void", "System.String");
            if (!setter.IsPublic) Fail("GameplayRoute must have a public setter.");
        }
        // The custom voice gates prepend to both static caller-speech entries (§9.11): guard-free bodies with
        // the exact parameter lists the injected Bridge calls are spelled against.
        var voice = Type(ai, "PocketTtsSingleton");
        StaticMethod(voice, "Speak", "System.Void", "System.String", "System.String", "System.UInt32", "System.String", "System.Boolean");
        StaticMethod(voice, "SpeakTracked", "System.Void", "System.String", "System.String", "System.UInt32", "System.UInt32", "System.String");

        foreach (var name in new[] { "MainMenuToolkitController", "PauseMenuToolkitController" })
        {
            var type = Type(ui, name);
            if (!InheritsComponent(type)) Fail($"{name} is no longer a Unity component.");
            var initialize = Method(type, "InitializeUi", "System.Void");
            if (initialize.Body.Instructions.Last().OpCode != OpCodes.Ret)
                Fail($"{name}.InitializeUi has an unsupported exit structure.");
        }
        var refresh = Method(Type(ui, "MainMenuToolkitController"), "UpdateConnectionStatus", "System.Void");
        var statusCalls = Calls(refresh, "MainMenuConnectionStatus", "Refresh").ToArray();
        if (statusCalls.Length != 1 || ((MethodReference)statusCalls[0].Operand).ReturnType.FullName != "System.Void")
            Fail("The menu connection-status update point has changed.");
        // Labels are looked up by these names in the bridge. Missing names must not become a silent no-op.
        var labels = AllTypes(ui.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToHashSet();
        foreach (var label in new[] { "connection-status", "connection-warning", "connection-warning-title", "connection-warning-body", "steam-connection-status", "backend-auth-status" })
            if (!labels.Contains(label)) Fail($"The game UI no longer exposes '{label}'.");
    }

    private static TypeDefinition Type(AssemblyDefinition assembly, string name) =>
        assembly.MainModule.Types.SingleOrDefault(t => t.FullName == name) ?? throw new InvalidOperationException($"Incompatible game update: missing {name}.");

    private static MethodDefinition Method(TypeDefinition type, string name, string returns, params string[] parameters) =>
        Method(type, name, returns, false, parameters);

    private static MethodDefinition StaticMethod(TypeDefinition type, string name, string returns, params string[] parameters) =>
        Method(type, name, returns, true, parameters);

    private static MethodDefinition Method(TypeDefinition type, string name, string returns, bool isStatic, params string[] parameters)
    {
        var methods = type.Methods.Where(m => m.Name == name && m.IsStatic == isStatic && !m.HasGenericParameters && m.ReturnType.FullName == returns &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)).ToArray();
        if (methods.Length != 1) Fail($"The {type.Name}.{name} method signature has changed.");
        var method = methods[0];
        if (!method.HasBody || method.Body.Instructions.Count == 0 || method.Body.HasExceptionHandlers)
            Fail($"Unsupported method body: {type.Name}.{name}.");
        return method;
    }

    private static IEnumerable<Instruction> Calls(MethodDefinition method, string type, string name) =>
        method.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is MethodReference r && r.DeclaringType.FullName == type && r.Name == name);

    private static bool InheritsComponent(TypeDefinition type)
    {
        for (var parent = type.BaseType; parent != null; parent = parent.Resolve()?.BaseType)
            if (parent.FullName == "UnityEngine.MonoBehaviour" || parent.FullName == "UnityEngine.Component") return true;
        return false;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
        types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));

    private static void Fail(string message) => throw new InvalidOperationException("Incompatible game update: " + message);
}
