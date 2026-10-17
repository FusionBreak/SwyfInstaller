using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Swyf.CustomAI.Installation;

// The game still requires its Steam client for lobbies. Only its AI-service entry gate is optional.
internal static class GameEntry
{
    public static MethodDefinition? Getter(AssemblyDefinition assembly) =>
        assembly.MainModule.Types.Single(t => t.FullName == "KolkataApi").Methods.SingleOrDefault(m => m.Name == "get_CanStartGame");

    public static Instruction BackendGate(MethodDefinition method)
    {
        if (method.IsStatic || method.Parameters.Count != 0 || method.ReturnType.FullName != "System.Boolean" ||
            !method.HasBody || method.Body.HasExceptionHandlers || method.Body.Instructions.Count < 8)
            Fail();
        var code = method.Body.Instructions;
        bool IsSteamCall(int index, string type, string name) => code[index].OpCode == OpCodes.Call &&
            code[index].Operand is MethodReference call && !call.HasThis && call.Parameters.Count == 0 &&
            call.ReturnType.FullName == "System.Boolean" && call.DeclaringType.FullName == type && call.Name == name;
        bool IsFalseBranch(int index) => (code[index].OpCode == OpCodes.Brfalse || code[index].OpCode == OpCodes.Brfalse_S) &&
            code[index].Operand is Instruction target && target.OpCode == OpCodes.Ldc_I4_0 && target.Next?.OpCode == OpCodes.Ret;
        if (!IsSteamCall(0, "SteamManager", "get_Initialized") || !IsFalseBranch(1) ||
            !IsSteamCall(2, "Steamworks.SteamUser", "BLoggedOn") || !IsFalseBranch(3) ||
            !ReferenceEquals(code[1].Operand, code[3].Operand) || code[4].OpCode != OpCodes.Ldarg_0 ||
            code[5].OpCode != OpCodes.Call || code[5].Operand is not MethodReference backend ||
            backend.Name != "get_BackendAuthenticated" || backend.DeclaringType.FullName != "KolkataApi" ||
            !backend.HasThis || backend.Parameters.Count != 0 || backend.ReturnType.FullName != "System.Boolean" || !IsFalseBranch(6))
            Fail();
        return code[4];
    }

    private static void Fail() => throw new InvalidOperationException("Incompatible game update: the Steam and AI-service game-entry gates have changed.");
}
