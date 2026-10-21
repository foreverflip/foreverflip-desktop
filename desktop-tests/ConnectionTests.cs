using ForeverFlip.Desktop;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class ConnectionTests
{
    sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request);}
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static HttpResponseMessage Reply(object body)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};
    public static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);DateTimeOffset now=DateTimeOffset.UtcNow;int calls=0;string requestID="",challenge="",pollHash="";
        const string completion="ccccccccccccccccccccccccccccccccccccccccccc";
        var account=new AccountSession(Path.Combine(folder,"pair.json"),new Handler(async request=>{
            calls++;string route=request.RequestUri!.AbsolutePath;
            if(route.EndsWith("/availability"))return Reply(new{registrationOpen=false,registrationsRemaining=0,registrationLimit=30,emailAvailable=true,reason="daily_limit",resetAt=now.ToUnixTimeSeconds()+3600});
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());var root=body.RootElement;
            Check(root.GetProperty("requestID").GetString()==requestID,"Pairing request ID changed");
            string secret=root.GetProperty("pollSecret").GetString()!,verifier=root.GetProperty("verifier").GetString()!;
            string expected=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
            string hash=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
            Check(pollHash==expected&&challenge==hash,"Native pairing did not retain its private proof");
            if(route.EndsWith("/pair/complete")){Check(root.GetProperty("completionCode").GetString()==completion,"Wrong completion code");return Reply(new{bound=true,email="connected@example.test"});}
            Check(route.EndsWith("/desktop-poll"),"Unexpected native pairing route");
            return Reply(new{status="signed_in",email="connected@example.test",refreshToken="SYNTHETIC_CONNECTION_REFRESH",accessToken="SYNTHETIC_CONNECTION_ACCESS",accessExpiresIn=900});
        }),()=>now);
        Check(await account.RequestConnectAsync(),"One-click connection did not start");
        string url=account.PendingVerificationUrl!;Check(url.StartsWith(MarketSync.Service+"/account/#pair="),"Pairing must use a fixed HTTPS origin");
        string encoded=url.Split("#pair=")[1].Replace('-','+').Replace('_','/');
        using var handoff=JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight(encoded.Length+(4-encoded.Length%4)%4,'=')));var data=handoff.RootElement;
        Check(!data.TryGetProperty("email",out _)&&!data.TryGetProperty("pollSecret",out _)&&!data.TryGetProperty("verifier",out _),"Pair URL exposed credentials or required native email input");
        requestID=data.GetProperty("desktopRequestID").GetString()!;challenge=data.GetProperty("challenge").GetString()!;pollHash=data.GetProperty("desktopPollHash").GetString()!;
        var restarted=new AccountSession(Path.Combine(folder,"pair.json"),new Handler(_=>throw new Exception("Unapproved restart must not use the network")),()=>now);
        Check(restarted.PendingVerificationUrl==url,"Restart did not preserve the device pairing proof");await restarted.PollAsync();
        now+=TimeSpan.FromSeconds(6);await account.PollAsync();Check(calls==0,"App polled a remote approval without a local callback");
        Check(!await account.CompleteConnectionAsync(new("pair-done",Guid.NewGuid().ToString("D"),completion))&&calls==0,"Forged callback used the local app's proof");
        Check(await account.CompleteConnectionAsync(new("pair-done",requestID,completion))&&account.State==AccountState.SignedIn&&account.Email=="connected@example.test","Valid browser pairing did not sign in the app");
        int completed=calls;Check(!await account.CompleteConnectionAsync(new("pair-done",requestID,completion))&&calls==completed,"Completed callback replay made another request");
        await account.RefreshAvailabilityAsync();Check(account.RegistrationOpen==false&&account.RegistrationStatus.Contains("existing accounts"),"Registration capacity blocked existing sign-in copy");
        var refused=new AccountSession(Path.Combine(folder,"refused.json"),new Handler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized){Content=new StringContent("{}") })),()=>now);
        Check(await refused.RequestConnectAsync(),"Rejected-connection fixture did not start");
        string first=refused.PendingVerificationUrl!,encodedRefused=first.Split("#pair=")[1].Replace('-','+').Replace('_','/');
        using var refusedHandoff=JsonDocument.Parse(Convert.FromBase64String(encodedRefused.PadRight(encodedRefused.Length+(4-encodedRefused.Length%4)%4,'=')));
        string refusedID=refusedHandoff.RootElement.GetProperty("desktopRequestID").GetString()!;
        Check(!await refused.CompleteConnectionAsync(new("pair-done",refusedID,completion))&&refused.State==AccountState.SignedOut,"Rejected connection left an unusable request pending");
        Check(await refused.RequestConnectAsync()&&refused.PendingVerificationUrl!=first,"Try again reused a rejected request");
        Check(!await refused.CompleteConnectionAsync(new("pair-done",refusedID,completion))&&refused.ConnectionNotice.Contains("older sign-in"),"Stale browser callback was silently ignored");
        Check(DesktopActivation.Parse("us.blahh.foreverflip://sign-in")?.Action=="sign-in","Website app launch was rejected");
        Check(DesktopActivation.Parse("us.blahh.foreverflip://pair-done?requestID="+requestID+"&code="+completion)?.RequestID==requestID,"Proof-bound completion callback was rejected");
        foreach(string invalid in new[]{"https://evil.test/","us.blahh.foreverflip://sign-in?url=https://evil.test","us.blahh.foreverflip://sign-in/path","us.blahh.foreverflip://sign-in#x","us.blahh.foreverflip://pair-done?requestID="+requestID+"&code="+completion+"&extra=x","us.blahh.foreverflip://user@sign-in"})Check(DesktopActivation.Parse(invalid)==null,"Protocol accepted unexpected input: "+invalid);
        using var stop=new CancellationTokenSource();string pipe="ForeverFlip.Test."+Guid.NewGuid().ToString("N");
        var received=new TaskCompletionSource<DesktopActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listen=DesktopActivation.Listen(a=>received.TrySetResult(a),stop.Token,pipe);
        Check(DesktopActivation.Forward("us.blahh.foreverflip://sign-in",pipe),"Second instance could not forward the website action");
        Check((await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Action=="sign-in","Running tray instance got the wrong activation");
        stop.Cancel();await listen;
        Console.WriteLine("PASS: desktop pairing, local callback proof, no premature polls, replay/URI rejection and registration capacity copy.");
    }
}
