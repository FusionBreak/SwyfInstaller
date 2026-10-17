using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

// Execute the installed getter's actual instructions with explicit substitutes for Steam/backend state.
// No Unity/game assembly is loaded or executed by this fixture.
internal static class GameEntryTests
{
    public static void Run(string path, Action<bool, string> check)
    {
        using var game = AssemblyDefinition.ReadAssembly(path);
        var source = game.MainModule.Types.Single(t => t.Name == "KolkataApi").Methods.SingleOrDefault(m => m.Name == "get_CanStartGame");
        if (source == null) return;
        using var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("EntryFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0)), "EntryFixture", ModuleKind.Dll);
        var module = fixture.MainModule;
        var type = new TypeDefinition("", "Entry", TypeAttributes.Public, module.ImportReference(typeof(object)));
        module.Types.Add(type);
        var fields = new Dictionary<string, FieldDefinition>();
        FieldDefinition Field(string name, TypeReference fieldType)
        {
            if (fields.TryGetValue(name, out var prior)) return prior;
            var field = new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static, fieldType);
            fields.Add(name, field); type.Fields.Add(field); return field;
        }
        var method = new MethodDefinition("CanStart", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Boolean);
        type.Methods.Add(method);
        MethodDefinition Getter(string name, TypeReference returns)
        {
            var existing = type.Methods.SingleOrDefault(m => m.Name == name);
            if (existing != null) return existing;
            var getter = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, returns);
            type.Methods.Add(getter);
            getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, Field(name, returns)));
            getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return getter;
        }
        var mapping = source.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
        foreach (var original in source.Body.Instructions)
        {
            var copy = mapping[original]; copy.OpCode = original.OpCode;
            if (original.OpCode == OpCodes.Ldarg_0) { copy.OpCode = OpCodes.Nop; }
            else if (original.Operand is Instruction branch) copy.Operand = mapping[branch];
            else if (original.Operand is FieldReference field)
            {
                copy.OpCode = OpCodes.Ldsfld;
                copy.Operand = Field(field.Name, field.FieldType.FullName == "System.String" ? module.TypeSystem.String : module.TypeSystem.Int32);
            }
            else if (original.Operand is MethodReference call)
            {
                copy.Operand = call.DeclaringType.FullName == "System.String"
                    ? module.ImportReference(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!)
                    : Getter(call.Name, call.ReturnType.FullName == "System.String" ? module.TypeSystem.String : module.TypeSystem.Boolean);
            }
            else copy.Operand = original.Operand;
            method.Body.Instructions.Add(copy);
        }
        using var bytes = new MemoryStream(); fixture.Write(bytes);
        var compiled = Assembly.Load(bytes.ToArray()).GetType("Entry")!;
        void Set(string name, object value) => compiled.GetField(name)!.SetValue(null, value);
        bool CanStart() => (bool)compiled.GetMethod("CanStart")!.Invoke(null, null)!;
        Set("get_Initialized", true); Set("BLoggedOn", true);
        Set("get_BackendAuthenticated", false); Set("edgeScope", "wrong"); Set("get_CurrentEdgeScope", "current"); Set("edgeAuthStatus", 0);
        Set("UseCustomProviderForGameEntry", false);
        check(!CanStart(), "disabled custom AI still requires game-service sign-in");
        Set("UseCustomProviderForGameEntry", true);
        check(CanStart(), "enabled custom AI enters without a backend session or matching backend scope");
        Set("get_Initialized", false);
        check(!CanStart(), "custom AI preserves the Steam initialization requirement");
        Set("get_Initialized", true); Set("BLoggedOn", false);
        check(!CanStart(), "custom AI preserves the Steam client login requirement");
        Set("BLoggedOn", true); Set("UseCustomProviderForGameEntry", false);
        Set("get_BackendAuthenticated", true); Set("edgeScope", "current"); Set("edgeAuthStatus", 2);
        check(CanStart(), "disabled custom AI preserves valid original game entry");
        Set("edgeAuthStatus", 1);
        check(!CanStart(), "disabled custom AI preserves the original auth-status check");
    }
}
