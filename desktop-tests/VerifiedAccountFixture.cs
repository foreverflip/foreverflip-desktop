using ForeverFlip.Desktop;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Signed-in account for upload fixtures, served by its own synthetic auth
// handler (never mixed with the upload mock). Synthetic credentials only.
// The DPAPI vault lives under the fixture's test folder.
internal sealed class VerifiedAccountFixture
{
    public const string EmailAddress="upload-fixture@example.test";
    sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request);
    }
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static HttpResponseMessage Reply(HttpStatusCode code,object body)=>new(code){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};
    static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    const string HandoffPrefix=MarketSync.Service+"/account/#desktop=";
    // Set only by the simulated browser from the app's handoff; the native app never creates the request.
    string requestID="",pollHash="",challenge="",refresh="SYNTHETIC_FIXTURE_REFRESH_1";
    int generation=1;
    public DateTimeOffset Now {get;set;}=DateTimeOffset.UtcNow;
    public string Path {get;}
    public string Access {get;private set;}="SYNTHETIC_FIXTURE_ACCESS_1";
    // Browser-completed requests (the website's emailed links), not native API calls.
    public int Requests {get;private set;}
    public int Polls {get;private set;}
    public int Refreshes {get;private set;}
    public AccountSession Account {get;private set;}
    VerifiedAccountFixture(string path){Path=path;Account=Session();}
    // A new session over the same protected file, handler and clock.
    public AccountSession Session()=>new(Path,new Handler(Send),()=>Now);
    // Restart: reload from the DPAPI file. Its first upload renews via /auth/refresh.
    public AccountSession Reload()=>Account=Session();
    public bool IsCurrentBearer(HttpRequestMessage request)=>request.Headers.Authorization is {Scheme:"Bearer"} auth&&auth.Parameter==Access;
    public static async Task<VerifiedAccountFixture> SignInAsync(string folder)
    {
        var fixture=new VerifiedAccountFixture(System.IO.Path.Combine(System.IO.Path.GetFullPath(folder),"account.json"));
        var account=fixture.Account;
        Check(account.State==AccountState.SignedOut,"Account fixture started with existing credentials");
        Check(await account.RequestSignInAsync(EmailAddress),"Account fixture sign-in request failed: "+account.Status);
        fixture.CompleteInBrowser(account.PendingVerificationUrl??throw new Exception("Account fixture got no handoff URL: "+account.Status));
        // PollAsync waits 5 seconds after a request.
        fixture.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();
        Check(account.State==AccountState.SignedIn&&fixture.Requests==1&&fixture.Polls==1,"Account fixture did not sign in: "+account.Status);
        return fixture;
    }
    // Simulated website: reads only the public handoff and records its bindings, as the server would.
    void CompleteInBrowser(string url)
    {
        Check(url.StartsWith(HandoffPrefix,StringComparison.Ordinal),"Account fixture handoff left the fixed service origin");
        string data=url[HandoffPrefix.Length..].Replace('-','+').Replace('_','/');
        using var handoff=JsonDocument.Parse(Convert.FromBase64String(data.PadRight(data.Length+(4-data.Length%4)%4,'=')));var root=handoff.RootElement;
        Check(root.GetProperty("client").GetString()=="desktop"&&root.GetProperty("email").GetString()==EmailAddress&&!root.TryGetProperty("pollSecret",out _)&&!root.TryGetProperty("verifier",out _),"Account fixture handoff is malformed or carries raw proof");
        requestID=root.GetProperty("desktopRequestID").GetString()!;pollHash=root.GetProperty("desktopPollHash").GetString()!;challenge=root.GetProperty("challenge").GetString()!;
        Check(Guid.TryParseExact(requestID,"D",out _)&&pollHash.Length==64,"Account fixture handoff has an invalid request ID or poll hash");
        Requests++;
    }
    async Task<HttpResponseMessage> Send(HttpRequestMessage request)
    {
        string route=request.RequestUri!.AbsolutePath;
        Check(request.Method==HttpMethod.Post&&request.Headers.Authorization==null,"Account fixture received an unexpected authenticated or non-POST request "+route);
        using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());var root=body.RootElement;
        long now=Now.ToUnixTimeSeconds();
        if(route=="/api/v1/auth/desktop-poll"){
            Polls++;
            if(requestID=="")return Reply(HttpStatusCode.Accepted,new{status="pending",emailSent=false});
            Check(root.GetProperty("requestID").GetString()==requestID
                &&Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.GetProperty("pollSecret").GetString()!))).ToLowerInvariant()==pollHash
                &&Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(root.GetProperty("verifier").GetString()!)))==challenge,"Account fixture poll proof does not match the browser handoff");
            return Reply(HttpStatusCode.OK,new{status="signed_in",email=EmailAddress,refreshToken=refresh,accessToken=Access,accessExpiresAt=now+900});
        }
        if(route=="/api/v1/auth/refresh"){
            Refreshes++;
            if(root.GetProperty("refreshToken").GetString()!=refresh||root.GetProperty("rotationSecret").GetString()!.Length!=43)
                return Reply(HttpStatusCode.Unauthorized,new{detail="Synthetic fixture refresh rejected"});
            generation++;refresh="SYNTHETIC_FIXTURE_REFRESH_"+generation;Access="SYNTHETIC_FIXTURE_ACCESS_"+generation;
            return Reply(HttpStatusCode.OK,new{refreshToken=refresh,accessToken=Access,accessExpiresAt=now+900,email=EmailAddress});
        }
        // Upload, registration and download routes belong to the upload mock.
        throw new Exception("Account fixture received non-auth request "+route);
    }
}
