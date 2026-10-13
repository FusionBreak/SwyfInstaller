using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace Swyf.CustomAI;
public sealed class ManifestDeXClient:IDisposable
{
    public const string ServiceUrl="https://swyf-ai.manifestdex.com";
    private readonly string ticketPath;
    private readonly IDataProtector protector;
    private readonly HttpClient http;
    private readonly SemaphoreSlim pairGate=new(1);
    private readonly object stateGate=new();
    private string ticket="";
    private string? pendingDevice,completionKey;
    private long generation;
    private Task<object>? accountRequest;
    private bool accountIsForced;
    private DateTimeOffset nextConnectAt;
    public ManifestDeXClient(string configPath,Provider provider,HttpMessageHandler? handler=null)
    {
        http=new HttpClient(handler??new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(5)};
        var directory=Path.GetDirectoryName(configPath)!;ticketPath=Path.Combine(directory,"manifestdex-session.dat");
        var protection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory,"member-keys")),o=>{o.SetApplicationName("SWYF.ManifestDeX");if(OperatingSystem.IsWindows())o.ProtectKeysWithDpapi();});
        protector=protection.CreateProtector("Membership.v1");
        try{if(File.Exists(ticketPath))ticket=protector.Unprotect(File.ReadAllText(ticketPath));if(!ticket.StartsWith("swyf2_",StringComparison.Ordinal))ticket="";}catch{ticket="";}
    }
    public Settings Effective(Settings original){lock(stateGate)return !original.UseManifestDeX?original:new(original.Enabled,ServiceUrl+"/v1","ManifestDeX AI",ticket);}
    private async Task<JsonObject> Send(string path,HttpMethod method,object? body,CancellationToken ct,string? credential=null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));ct=deadline.Token;
        try{
            using var request=new HttpRequestMessage(method,ServiceUrl+path);
            if(credential is not null)request.Headers.Authorization=new("Bearer",credential);
            if(body is not null)request.Content=JsonContent.Create(body);
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            var bytes=await Provider.ReadBounded(await response.Content.ReadAsStreamAsync(ct),32768,ct);
            JsonObject? json=null;try{json=JsonNode.Parse(bytes) as JsonObject;}catch(JsonException) when(!response.IsSuccessStatusCode){}
            if(!response.IsSuccessStatusCode){
                var delay=response.Headers.RetryAfter?.Delta??(response.Headers.RetryAfter?.Date-DateTimeOffset.UtcNow);
                int? retry=delay is {} wait?(int)Math.Clamp(Math.Ceiling(wait.TotalSeconds),1,int.MaxValue):null;
                if(response.StatusCode==System.Net.HttpStatusCode.TooManyRequests)retry??=60;
                throw new ApiFailure((int)response.StatusCode,json?["error"]?.ToString()??"manifestdex",
                    json?["message"]?.ToString()??"ManifestDeX AI is unavailable.",retry);
            }
            if(json is null)throw new JsonException();
            return json;
        }
        catch(HttpRequestException e){throw new ApiFailure(503,"manifestdex","Cannot reach ManifestDeX AI ("+e.HttpRequestError+(e.InnerException is System.Net.Sockets.SocketException socket?" / "+socket.SocketErrorCode:"")+"). Your private provider settings are preserved.");}
        catch(TaskCanceledException){throw new ApiFailure(503,"manifestdex","ManifestDeX AI connection timed out. Your private provider settings are preserved.");}
        catch(JsonException){throw new ApiFailure(503,"manifestdex","ManifestDeX AI returned an invalid response. Your private provider settings are preserved.");}
    }
    public async Task<object> Status(CancellationToken ct)
    {
        try{var status=await Send("/api/status",HttpMethod.Get,null,ct);lock(stateGate)return new{available=status["enabled"]?.GetValue<bool>()==true,signInAvailable=status["signInAvailable"]?.GetValue<bool>()??true,connected=ticket.Length>0,notice=status["signInNotice"]?.ToString()??status["notice"]?.ToString(),donateUrl="https://manifestdex.com/donate"};}
        catch(ApiFailure e){lock(stateGate)return new{available=false,signInAvailable=false,connected=ticket.Length>0,notice=e.Message,donateUrl="https://manifestdex.com/donate"};}
    }
    public Task<object> Account(CancellationToken ct,bool refresh=false)
    {
        lock(stateGate){
            if(ticket.Length==0)return Task.FromResult<object>(new{connected=false});
            // One shared fetch per panel; caller cancellation does not cancel other waiters.
            if(refresh&&accountRequest is {IsCompleted:false}&&!accountIsForced)return RefreshAfter(accountRequest,ct);
            if(accountRequest is null||accountRequest.IsCompleted){accountIsForced=refresh;accountRequest=ReadAccount(ticket,generation,refresh);}
            return accountRequest.WaitAsync(ct);
        }
    }
    private async Task<object> RefreshAfter(Task<object> pending,CancellationToken ct){try{await pending.WaitAsync(ct);}catch(ApiFailure){}return await Account(ct,true);}
    private async Task<object> ReadAccount(string captured,long version,bool refresh)
    {
        try{
            var result=await Send("/api/me?include=quota"+(refresh?"&refresh=true":""),HttpMethod.Get,null,CancellationToken.None,captured);
            lock(stateGate){
                if(version!=generation||captured!=ticket)return new{connected=false};
                return new{connected=true,user=result["user"]?.DeepClone(),quota=result["quota"]?.DeepClone(),service=result["service"]?.DeepClone()};
            }
        }
        catch(ApiFailure e) when(e.Status==401){lock(stateGate){if(version==generation&&captured==ticket)Clear();return new{connected=false,message=e.Message};}}
    }
    public async Task RequireAvailable(CancellationToken ct)
    {
        lock(stateGate)if(ticket.Length==0)throw new ApiFailure(401,"member","Connect your ManifestDeX account first.");
        var status=await Send("/api/status",HttpMethod.Get,null,ct);
        if(status["enabled"]?.GetValue<bool>()!=true)throw new ApiFailure(503,"disabled","ManifestDeX AI is currently disabled.");
    }
    public async Task<object> Connect(CancellationToken ct)
    {
        long version;lock(stateGate){
            if(nextConnectAt>DateTimeOffset.UtcNow)throw new ApiFailure(429,"rate_limit","Too many sign-in attempts. Wait before trying again.",(int)Math.Ceiling((nextConnectAt-DateTimeOffset.UtcNow).TotalSeconds));
            version=++generation;pendingDevice=null;completionKey=null;accountRequest=null;
        }
        await pairGate.WaitAsync(ct);
        try{
            lock(stateGate)if(nextConnectAt>DateTimeOffset.UtcNow)
                throw new ApiFailure(429,"rate_limit","Too many sign-in attempts. Wait before trying again.",(int)Math.Ceiling((nextConnectAt-DateTimeOffset.UtcNow).TotalSeconds));
            var response=await Send("/api/auth/start",HttpMethod.Post,new{},ct);
            var target=new Uri(response["authorizationUrl"]!.GetValue<string>());
            if(target.GetLeftPart(UriPartial.Authority)!=ServiceUrl||target.AbsolutePath!="/oauth/start")throw new ApiFailure(502,"oauth","Invalid sign-in URL.");
            lock(stateGate){
                if(version!=generation)throw new ApiFailure(409,"oauth","Sign-in was canceled.");
                pendingDevice=response["flowId"]!.ToString();completionKey=response["completionKey"]!.ToString();
            }
            return new{authorizationUrl=target.ToString(),expiresIn=600};
        }catch(ApiFailure e) when(e.RetryAfter is int){lock(stateGate)nextConnectAt=DateTimeOffset.UtcNow.AddSeconds(e.RetryAfter.Value);throw;}
        finally{pairGate.Release();}
    }
    public async Task<object> Poll(CancellationToken ct)
    {
        await pairGate.WaitAsync(ct);
        try{
            string? flow,key;long version;
            lock(stateGate){flow=pendingDevice;key=completionKey;version=generation;}
            if(flow is null)return new{status="none"};
            var result=await Send("/api/auth/poll",HttpMethod.Post,new{flowId=flow,completionKey=key},ct);
            if(result["status"]?.ToString()!="approved")return new{status="pending"};
            var next=result["ticket"]?.GetValue<string>()??throw new JsonException();
            if(!next.StartsWith("swyf2_",StringComparison.Ordinal)||next.Length>120)throw new JsonException();
            lock(stateGate){
                if(version==generation&&flow==pendingDevice){
                    Directory.CreateDirectory(Path.GetDirectoryName(ticketPath)!);
                    File.WriteAllText(ticketPath+".tmp",protector.Protect(next));File.Move(ticketPath+".tmp",ticketPath,true);
                    ticket=next;pendingDevice=null;completionKey=null;accountRequest=null;
                    return new{status="approved"};
                }
            }
            try{await Send("/api/auth/disconnect",HttpMethod.Post,new{},CancellationToken.None,next);}catch(ApiFailure){}
            return new{status="none"};
        }finally{pairGate.Release();}
    }
    public async Task DisconnectRemote(CancellationToken ct)
    {
        string old;lock(stateGate){old=ticket;Clear();}
        if(old.Length>0)await Send("/api/auth/disconnect",HttpMethod.Post,new{},ct,old);
    }
    public void Disconnect(){lock(stateGate)Clear();}
    private void Clear(){generation++;ticket="";pendingDevice=null;completionKey=null;accountRequest=null;if(File.Exists(ticketPath))File.Delete(ticketPath);}
    public void Dispose(){http.Dispose();pairGate.Dispose();}
}
