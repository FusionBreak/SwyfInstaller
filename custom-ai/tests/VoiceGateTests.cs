using Mono.Cecil;
using Mono.Cecil.Cil;

// Verify the custom voice gate spelling the installer prepends (custom-ai/docs/tts-integration-notes.md §9.11
// item 11): the five original arguments pushed verbatim (tracked adds isFiller = false), the static Bridge entry
// called once, true returns with the request handled and false branches back to the untouched shipped body.
// The patched assembly is only inspected here; nothing game-related is loaded or executed.
internal static class VoiceGateTests
{
    public static void Run(string path, Action<bool, string> check)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var tts = assembly.MainModule.Types.Single(t => t.Name == "PocketTtsSingleton");
        var speak = tts.Methods.Single(m => m.Name == "Speak" && m.IsStatic && m.Parameters.Count == 5);
        var tracked = tts.Methods.Single(m => m.Name == "SpeakTracked" && m.IsStatic && m.Parameters.Count == 5);
        bool HasGate(MethodDefinition entry, string hook, string[] hookParameters, bool trackedGate)
        {
            var code = entry.Body.Instructions;
            var pushes = new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3, OpCodes.Ldarg_S };
            for (var i = 0; i < pushes.Length; i++) if (code[i].OpCode != pushes[i]) return false;
            // A re-read assembly resolves the ldarg.s operand to a ParameterDefinition, so accept either form.
            if (code[4].Operand is byte fifthArgument ? fifthArgument != 4 : !(code[4].Operand is ParameterDefinition fifthParameter && fifthParameter.Index == 4)) return false;
            var j = pushes.Length;
            if (trackedGate)
            {
                if (code[j].OpCode != OpCodes.Ldc_I4_0) return false; // Vanilla SpeakTracked never enqueues fillers.
                j++;
            }
            return code[j].Operand is MethodReference call && !call.HasThis && call.DeclaringType.FullName == "Swyf.CustomAI.Runtime" &&
                call.Name == hook && call.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(hookParameters) &&
                code[j + 1].OpCode == OpCodes.Brfalse && code[j + 2].OpCode == OpCodes.Ret &&
                ReferenceEquals(code[j + 1].Operand, code[j + 3]) && // Declines fall into the untouched original body.
                code.Skip(j + 3).Any(i => i.Operand is MethodReference enqueue && enqueue.Name == "EnqueueSpeak") &&
                code.Count(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.DeclaringType.FullName == "Swyf.CustomAI.Runtime") == 1;
        }
        check(HasGate(speak, "TrySpeak", new[] { "System.String", "System.String", "System.UInt32", "System.String", "System.Boolean" }, false),
            "the custom voice gate starts at the head of Speak with the five original arguments and falls through when declined");
        check(HasGate(tracked, "TrySpeakTracked", new[] { "System.String", "System.String", "System.UInt32", "System.UInt32", "System.String", "System.Boolean" }, true),
            "the tracked custom voice gate passes the five tracked arguments plus isFiller = false");
        var overload = tts.Methods.Single(m => m.Name == "Speak" && m.Parameters.Count == 4);
        check(!overload.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Swyf.CustomAI.Runtime"),
            "the four-parameter Speak overload keeps its shipped delegating body");
    }
}
