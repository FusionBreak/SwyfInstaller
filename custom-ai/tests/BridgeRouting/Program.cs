using System;
using System.Threading;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Swyf.CustomAI;

internal static class Program
{
    private static void Main()
    {
        var request=JObject.Parse("{\"resolve_prompt_version\":true,\"ai_category\":\"dialogue\",\"gameplay_route\":\"official\"}");
        var before=request.ToString();
        if(Runtime.TryComplete(request,CancellationToken.None,false,out var result))
            throw new Exception("Prompt metadata was intercepted as text generation.");
        if(request.ToString()!=before)throw new Exception("Game control request changed.");
        Console.WriteLine("PASS: compiled bridge leaves prompt lookup untouched without starting Unity or the provider.");
        var directory=(string)typeof(Runtime).GetProperty("ModDirectory",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        Directory.CreateDirectory(directory);
        var config=Path.Combine(directory,"settings.json");
        var prior=File.Exists(config)?File.ReadAllBytes(config):null;
        try
        {
            var options=new FakeOptions{GameplayRoute="official"};
            File.WriteAllText(config,"{\"enabled\":false}");
            if(Runtime.UseCustomProviderForGameEntry())throw new Exception("Disabled mod changed game entry.");
            Runtime.PrepareConversation(options);
            if(options.GameplayRoute!="official")throw new Exception("Disabled mod changed routing.");
            File.WriteAllText(config,"{\"enabled\":true}");
            if(!Runtime.UseCustomProviderForGameEntry())throw new Exception("Custom provider still requires game AI sign-in.");
            Runtime.PrepareConversation(options);
            if(options.GameplayRoute!="openrouter")throw new Exception("Custom provider did not use local prompts.");
            Console.WriteLine("PASS: custom provider selects local prompt preparation; disabled mod preserves original route.");
            var ttsType=Type.GetType("Swyf.CustomAI.TtsRuntime, SWYF.CustomAI.Bridge")??throw new Exception("Custom voice runtime is missing.");
            var ttsEnabled=ttsType.GetProperty("Enabled",BindingFlags.NonPublic|BindingFlags.Static)!;
            var gate=ttsType.GetMethod("ShouldHijack",BindingFlags.NonPublic|BindingFlags.Static)??throw new Exception("The custom voice gate decision is missing.");
            bool Hijack(string text,uint callNetId,uint token,bool isFiller,string? languageCode,bool reentry,bool enabled,bool panel,bool singleton)=>
                (bool)gate.Invoke(null,new object?[]{text,callNetId,token,isFiller,languageCode,reentry,enabled,panel,singleton})!;
            if(Hijack("hello",42,0,false,null,false,false,true,true))throw new Exception("Custom voice speaks while the setting is disabled.");
            if(Hijack("hello",42,0,false,null,true,true,true,true))throw new Exception("The fallback re-entry spoke instead of falling through.");
            if(Hijack("   ",42,0,false,null,false,true,true,true))throw new Exception("Blank text hijacked the shipped request.");
            if(Hijack("hello",0,0,false,null,false,true,true,true))throw new Exception("A call id of zero hijacked the shipped request.");
            if(Hijack("hello",4294967198u,0,false,null,false,true,true,true))throw new Exception("The review narrator lost the built-in voice.");
            if(Hijack("hello",3221224472u,0,false,null,false,true,true,true))throw new Exception("The first voice preview id lost the built-in voice.");
            if(Hijack("hello",4294966295u,0,false,null,false,true,true,true))throw new Exception("The last voice preview id lost the built-in voice.");
            if(Hijack("hello",42,0,false,null,false,true,false,true))throw new Exception("Custom voice speaks while the panel is unavailable.");
            if(Hijack("hello",42,0,false,null,false,true,true,false))throw new Exception("Custom voice speaks while the game singleton is unavailable.");
            if(!Hijack("hello",3221224471u,0,false,null,false,true,true,true))throw new Exception("A live id below the preview range was not taken.");
            if(!Hijack("hello",4294966296u,0,false,null,false,true,true,true))throw new Exception("An id above the preview range was not taken.");
            if(!Hijack("hello",42,7,false,null,false,true,true,true))throw new Exception("A tracked request was not taken.");
            if(Hijack("hello",42,0,true,null,false,true,true,true))throw new Exception("An untracked filler word lost the shipped filler gate.");
            if(Hijack("hello",42,7,true,null,false,true,true,true))throw new Exception("A tracked filler word lost the shipped filler gate.");
            if(!Hijack("hello",42,0,false,"fr",false,true,true,true)||!Hijack("hello",42,0,false,null,false,true,true,true))throw new Exception("The language code changed a taken-request decision.");
            if(Hijack("hello",4294967198u,0,false,"fr",false,true,true,true)||Hijack("hello",4294967198u,0,false,null,false,true,true,true))throw new Exception("The language code changed a reserved-id decision.");
            Console.WriteLine("PASS: the custom voice gate takes live calls only and falls through disabled, blank, reserved, filler, panel-less, singleton-less and re-entrant cases; a language code never changes the decision.");
            if(Runtime.TrySpeak("Hello world","voices/test.wav",7,null,false))throw new Exception("Custom voice hijacked without ttsEnabled.");
            if(Runtime.TrySpeakTracked("Hello world","voices/test.wav",7,9,null,false))throw new Exception("Tracked custom voice hijacked without ttsEnabled.");
            File.WriteAllText(config,"{\"enabled\":true,\"ttsEnabled\":true}");
            Runtime.ReadTtsEnabled();
            if(!(bool)ttsEnabled.GetValue(null)!)throw new Exception("ttsEnabled setting was not cached.");
            if(Runtime.TrySpeak("   ","voices/test.wav",7,null,false))throw new Exception("Blank text was hijacked.");
            if(Runtime.TrySpeak("Hello world","voices/test.wav",4294967198,null,false))throw new Exception("Review narrator id was hijacked.");
            if(Runtime.TrySpeak("Hello world","voices/test.wav",3221224472,null,false))throw new Exception("Preview call id range floor was hijacked.");
            if(Runtime.TrySpeak("Hello world","voices/test.wav",4294966295,null,false))throw new Exception("Preview call id range ceiling was hijacked.");
            if(Runtime.TrySpeak("Hello world","voices/test.wav",0,null,false))throw new Exception("Zero call id was hijacked.");
            if(Runtime.TrySpeakTracked("Hello world","voices/test.wav",0,9,null,false))throw new Exception("Tracked request without a call id was hijacked.");
            if(Runtime.TrySpeakTracked("Hello world","voices/test.wav",7,0,null,false))throw new Exception("Tracked request without a completion token was hijacked.");
            if(Runtime.TrySpeak("hello","voices/x.wav",7,null,true))throw new Exception("A filler word was hijacked instead of keeping the shipped filler gate.");
            if(Runtime.TrySpeak("Hello world","voices/test.wav",7,null,false))throw new Exception("Custom voice was hijacked while the panel is unavailable.");
            if(Runtime.TrySpeakTracked("Hello world","voices/test.wav",7,9,null,false))throw new Exception("Tracked custom voice was hijacked while the panel is unavailable.");
            Console.WriteLine("PASS: custom voice entry points decline blank text, reserved ids, tokenless tracking, filler words and an unavailable panel without throwing.");
            foreach(var variant in new string[]{"{\"enabled\":true}","{\"enabled\":true,\"ttsEnabled\":false}","not json"})
            {
                File.WriteAllText(config,variant);
                Runtime.ReadTtsEnabled();
                if((bool)ttsEnabled.GetValue(null)!)throw new Exception("Setting variant \""+variant+"\" enabled custom voice.");
                if(Runtime.TrySpeak("Hello world","voices/test.wav",7,null,false))throw new Exception("Custom voice hijacks with setting variant \""+variant+"\".");
            }
            File.Delete(config);
            Runtime.ReadTtsEnabled();
            if((bool)ttsEnabled.GetValue(null)!)throw new Exception("Missing settings enabled custom voice.");
            if(Runtime.TrySpeakTracked("Hello world","voices/test.wav",7,9,null,false))throw new Exception("Missing settings still hijacks tracked speech.");
            Console.WriteLine("PASS: custom voice is opt-in; missing, disabled and unreadable settings leave the shipped path untouched.");
            if(Runtime.UseCustomProviderForGameEntry())throw new Exception("Missing settings changed game entry.");
            Console.WriteLine("PASS: game AI sign-in is optional only while custom AI is enabled.");
        }
        finally{if(prior==null)File.Delete(config);else File.WriteAllBytes(config,prior);}
    }
}
public sealed class FakeOptions { public string GameplayRoute { get; set; } = ""; }
