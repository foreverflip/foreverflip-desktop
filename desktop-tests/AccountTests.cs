using ForeverFlip.Desktop;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Synthetic account server; no real network, email or credentials.
internal static class AccountTests
{
    const string EmailAddress="player@example.test";
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request);
    }
    static HttpResponseMessage Reply(HttpStatusCode code,object? body=null)=>new(code){Content=new StringContent(JsonSerializer.Serialize(body??new{}),Encoding.UTF8,"application/json")};
    static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    static string Hex(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    const string HandoffPrefix="https://forever.blahh.us/account/#desktop=";
    static JsonElement Handoff(string? url)
    {
        Check(url!=null&&url.StartsWith(HandoffPrefix,StringComparison.Ordinal),"Handoff URL must use the fixed service origin and account page: "+url);
        string data=url![HandoffPrefix.Length..];
        Check(data.Length is >0 and <=4096&&data.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_'),"Handoff fragment must be bounded unpadded base64url");
        data=data.Replace('-','+').Replace('_','/');
        using var doc=JsonDocument.Parse(Convert.FromBase64String(data.PadRight(data.Length+(4-data.Length%4)%4,'=')));
        var root=doc.RootElement.Clone();
        var names=root.EnumerateObject().Select(p=>p.Name).ToArray();
        Check(names.SequenceEqual(new[]{"email","client","deviceName","challenge","desktopRequestID","desktopPollHash","expiresAt"}),"Handoff must contain exactly the documented fields: "+string.Join(",",names));
        return root;
    }
    static string MarketID(string realm)=>"ff1-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("forever-beta:beta-2026:18:1:110:"+realm+":Alliance"))).ToLowerInvariant()[..32];
    // Independent DPAPI read: proves the file is a current-user protected blob.
    static JsonElement Vault(string path)
    {
        using var file=JsonDocument.Parse(File.ReadAllText(path));
        byte[] clear=ProtectedData.Unprotect(Convert.FromBase64String(file.RootElement.GetProperty("protected").GetString()!),Encoding.UTF8.GetBytes("ForeverFlip.Account.v1"),DataProtectionScope.CurrentUser);
        using var doc=JsonDocument.Parse(clear);return doc.RootElement.Clone();
    }
    sealed class FakeAuth
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        // Bindings recorded only from the browser handoff; empty until the simulated website submits it.
        public string Challenge="",Email="",RequestID="",PollHash="";
        public string Refresh="SYNTHETIC_REFRESH_1",Access="SYNTHETIC_ACCESS_1";
        int generation=1;
        public bool Confirmed,LoseNextRefresh,LogoutOffline;
        public (string Prior,string Secret,string Next,string Access,DateTimeOffset At)? Rotation;
        // Requests counts browser-completed (emailed) requests; Routes counts every app call.
        public int Requests,Polls,Refreshes,Installations,Packs,Routes;
        // Server lifetime returned once the email is sent; the app must bound it to 900 seconds.
        public long ExpiresIn=900;
        public List<(string Prior,string Secret)> RefreshBodies=[];
        public List<string?> ObservationBearers=[],SupportBearers=[],AllBearers=[];
        public List<(string Route,string? Bearer,string Body)> Logouts=[];
        public Action<string,string>? BeforeRefresh;
        public (HttpStatusCode Code,string Detail)? PollFailure;
        public TaskCompletionSource? RefreshGate;
        // Now is the PC clock; the server's absolute times use real UTC (Now - PcOffset).
        public TimeSpan PcOffset;
        // False models an older server that sends only absolute expiry times.
        public bool SendTtl=true;
        public Func<Task>? OnObservation;
        // Account the server issues tokens for (defaults to the handoff email); OmitEmail models older refresh replies.
        public string? AccountEmail;
        public bool OmitEmail;
        Dictionary<string,object?> Tokens(string refresh,string access,long now)
        {
            var reply=new Dictionary<string,object?>{["refreshToken"]=refresh,["accessToken"]=access,["accessExpiresAt"]=now+900};
            if(!OmitEmail)reply["email"]=AccountEmail??Email;
            if(SendTtl)reply["accessExpiresIn"]=900;return reply;
        }
        long serverExpiresAt;
        // Simulated website: parses only the app's public handoff, then creates the request and
        // "sends" the email with the supplied ID/poll hash, as the real server does after Turnstile.
        public JsonElement CompleteInBrowser(string? url)
        {
            var handoff=Handoff(url);
            Check(handoff.GetProperty("client").GetString()=="desktop","Handoff must identify the desktop client");
            string device=handoff.GetProperty("deviceName").GetString()!;Check(device.Length is >=1 and <=80,"Device name must be 1-80 characters");
            Challenge=handoff.GetProperty("challenge").GetString()!;Check(Challenge.Length==43&&!Challenge.Contains('=')&&!Challenge.Contains('+')&&!Challenge.Contains('/'),"Challenge must be unpadded base64url SHA-256");
            RequestID=handoff.GetProperty("desktopRequestID").GetString()!;Check(Guid.TryParseExact(RequestID,"D",out _),"Request ID must be a UUID");
            PollHash=handoff.GetProperty("desktopPollHash").GetString()!;Check(PollHash.Length==64&&PollHash.All(c=>c is >='0' and <='9' or >='a' and <='f'),"Poll hash must be lowercase SHA-256 hex");
            Email=handoff.GetProperty("email").GetString()!;
            Requests++;serverExpiresAt=(Now-PcOffset).ToUnixTimeSeconds()+900;return handoff;
        }
        public async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            string route=request.RequestUri!.AbsolutePath;Routes++;
            string? bearer=request.Headers.Authorization?.Scheme=="Bearer"?request.Headers.Authorization.Parameter:null;AllBearers.Add(bearer);
            string text=request.Content==null?"":await request.Content.ReadAsStringAsync();
            JsonElement body=text==""?default:JsonDocument.Parse(text).RootElement.Clone();
            long now=(Now-PcOffset).ToUnixTimeSeconds();
            Check(route is not "/api/v1/auth/request" and not "/api/v1/auth/poll","The native app must not call "+route+"; the browser creates the request and the app uses /auth/desktop-poll");
            if(route=="/api/v1/auth/desktop-poll"){
                Polls++;Check(request.Method==HttpMethod.Post&&bearer==null,"Desktop poll must be an unauthenticated POST");
                // Not yet created by the browser: no state exists and nothing is validated.
                if(RequestID=="")return Reply(HttpStatusCode.Accepted,new{status="pending",emailSent=false});
                Check(body.GetProperty("requestID").GetString()==RequestID,"Poll must use the request ID from the browser handoff");
                Check(Hex(body.GetProperty("pollSecret").GetString()!)==PollHash,"Poll secret must hash to the handoff poll hash");
                string verifier=body.GetProperty("verifier").GetString()!;
                Check(verifier.Length==43&&Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))==Challenge,"Poll verifier must hash to the original challenge");
                if(PollFailure is {} failure)return Reply(failure.Code,new{detail=failure.Detail});
                if(!Confirmed){
                    var pending=new Dictionary<string,object?>{["status"]="pending",["emailSent"]=true,["expiresAt"]=serverExpiresAt};
                    if(SendTtl)pending["expiresIn"]=ExpiresIn;return Reply(HttpStatusCode.Accepted,pending);
                }
                var signedIn=Tokens(Refresh,Access,now);signedIn["status"]="signed_in";signedIn["csrfToken"]="SYNTHETIC_CSRF_UNUSED";
                return Reply(HttpStatusCode.OK,signedIn);
            }
            if(route=="/api/v1/auth/refresh"){
                Refreshes++;Check(bearer==null,"Refresh must not send a bearer token");
                string prior=body.GetProperty("refreshToken").GetString()!,secret=body.GetProperty("rotationSecret").GetString()!;
                Check(secret.Length==43,"Rotation secret must be 32 random bytes in base64url");
                RefreshBodies.Add((prior,secret));BeforeRefresh?.Invoke(prior,secret);
                if(RefreshGate!=null)await RefreshGate.Task;
                if(Refresh!=""&&prior==Refresh){
                    generation++;string next="SYNTHETIC_REFRESH_"+generation,nextAccess="SYNTHETIC_ACCESS_"+generation;
                    Rotation=(prior,secret,next,nextAccess,Now);Refresh=next;Access=nextAccess;
                    if(LoseNextRefresh){LoseNextRefresh=false;throw new HttpRequestException("Synthetic lost response after server rotation");}
                    return Reply(HttpStatusCode.OK,Tokens(next,nextAccess,now));
                }
                // Contract: the same prior token and rotation secret replay the new credentials for 120 seconds.
                if(Rotation is {} r&&r.Prior==prior&&r.Secret==secret&&r.Next==Refresh&&Now-r.At<=TimeSpan.FromSeconds(120))
                    return Reply(HttpStatusCode.OK,Tokens(r.Next,r.Access,now));
                return Reply(HttpStatusCode.Unauthorized,new{detail="Refresh token SYNTHETIC_SHOULD_BE_REDACTED_TOKEN is not valid for "+EmailAddress});
            }
            if(route is "/api/v1/auth/logout" or "/api/v1/auth/logout-all"){
                if(LogoutOffline)throw new HttpRequestException("Synthetic offline sign-out");
                Logouts.Add((route,bearer,text));
                if(bearer!=Access)return Reply(HttpStatusCode.Unauthorized);
                Refresh="";Access="";return Reply(HttpStatusCode.OK);
            }
            if(route=="/api/v1/installations"){Installations++;return Reply(HttpStatusCode.InternalServerError);}
            if(route=="/api/v1/catalog/sources")return Reply(HttpStatusCode.OK);
            if(route.EndsWith("/pack")){Packs++;return Reply(HttpStatusCode.NotFound);}
            if(route=="/api/v1/observations"){
                ObservationBearers.Add(bearer);if(OnObservation!=null)await OnObservation();
                if(bearer!=Access)return Reply(HttpStatusCode.Unauthorized);
                return Reply(HttpStatusCode.OK,new{marketID=MarketID("Classic Beta PvE"),added=body.GetProperty("items").GetArrayLength()});
            }
            if(route=="/api/v1/support/reports"){
                SupportBearers.Add(bearer);if(bearer!=Access)return Reply(HttpStatusCode.Unauthorized);
                return Reply(HttpStatusCode.OK,new{reportID=new string('b',64),requestID=body.GetProperty("requestID").GetString(),receivedAt=now});
            }
            throw new Exception("Unexpected account-fixture request "+route);
        }
    }
    static AccountSession Session(string path,FakeAuth fake)=>new(path,new Handler(fake.Send),()=>fake.Now);
    static async Task SignIn(AccountSession account,FakeAuth fake)
    {
        Check(await account.RequestSignInAsync(EmailAddress),"Sign-in request failed: "+account.Status);
        fake.CompleteInBrowser(account.PendingVerificationUrl);
        fake.Confirmed=true;fake.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();
        Check(account.State==AccountState.SignedIn,"Confirmed link did not sign in: "+account.Status);
    }
    // For other upload fixtures: a signed-in session against its own synthetic auth server.
    // Its access token never expires during a test, so only the fixture's own routes are called.
    public static async Task<AccountSession> SignedInAccount(string folder)
    {
        var fake=new FakeAuth();var account=Session(Path.Combine(Path.GetFullPath(folder),"account.json"),fake);
        await SignIn(account,fake);return account;
    }
    public static async Task Run(string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);
        var failures=new List<string>();
        async Task Case(string name,Func<Task> action){try{await action();Console.WriteLine("PASS "+name);}catch(Exception ex){failures.Add(name+": "+ex.Message);Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        await Case("poll proof, DPAPI pending storage, restart and sign-in",()=>PollProof(Path.Combine(folder,"poll")));
        await Case("pre-mail and post-mail expiry: one bounded extension, no network at deadline",()=>Expiry(Path.Combine(folder,"expiry")));
        await Case("handoff reopen, honest browser-open failure and cancel drop the local proof",()=>ReopenCancel(Path.Combine(folder,"reopen-cancel")));
        await Case("tampered handoff / wrong-account poll and refresh are rejected before adopting credentials",()=>WrongAccount(Path.Combine(folder,"wrong-account")));
        await Case("unwritable storage prevents a local sign-in from starting",()=>BlockedPrepare(Path.Combine(folder,"blocked-prepare")));
        await Case("older native pending request finishes via desktop-poll without handoff",()=>LegacyPending(Path.Combine(folder,"legacy-pending")));
        await Case("poll 409 is terminal with readable detail and no resend",()=>PollConflict(Path.Combine(folder,"conflict")));
        await Case("lost refresh response retries same token and rotation secret",()=>LostRefresh(Path.Combine(folder,"rotation")));
        await Case("lost refresh retried after replay window ends honestly",()=>LostRefreshExpired(Path.Combine(folder,"rotation-expired")));
        await Case("single-flight renewal",()=>SingleFlight(Path.Combine(folder,"single-flight")));
        await Case("rejected refresh signs out without resending email",()=>RejectedRefresh(Path.Combine(folder,"rejected")));
        await Case("sign-out failure stays honest; local-only removal; successful revoke",()=>SignOut(Path.Combine(folder,"sign-out")));
        await Case("unreadable saved sign-in is retained and signed out",()=>Corrupt(Path.Combine(folder,"corrupt")));
        await Case("signed-out uploads pause without anonymous registration",()=>Uploads(Path.Combine(folder,"uploads")));
        await Case("local-only removal mid-upload stops later batches",()=>ForgetMidUpload(Path.Combine(folder,"forget-mid-upload")));
        await Case("PC clock ±1h: sign-in and access freshness use local receipt + TTL",()=>ClockSkew(Path.Combine(folder,"clock-skew")));
        await Case("malformed saved pending times are bounded at startup",()=>MalformedPending(Path.Combine(folder,"malformed-pending")));
        await Case("temporarily unreadable sign-in is not replaced and resumes",()=>LockedStorage(Path.Combine(folder,"locked")));
        await Case("server detail is bounded and redacted",()=>{
            string? clean=AccountSession.Sanitize("Rejected "+EmailAddress+" with token ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\nnext"+new string('x',500));
            Check(clean!=null&&!clean.Contains('@')&&!clean.Contains("ABCDEFGHIJKLMNOP")&&!clean.Contains('\n')&&clean.Length<=201,"Detail leaked an address/token or was unbounded: "+clean);
            return Task.CompletedTask;
        });
        string log=Path.Combine(MarketSync.StateRoot,"desktop.log");
        if(File.Exists(log)){
            string text=File.ReadAllText(log);
            Check(!text.Contains("SYNTHETIC_REFRESH")&&!text.Contains("SYNTHETIC_ACCESS")&&!text.Contains("SYNTHETIC_POLL")&&!text.Contains(EmailAddress)&&!text.Contains("REDACTED_TOKEN")&&!text.Contains("#desktop="),"Desktop log contains account credentials, addresses or handoff URLs");
        }
        if(failures.Count>0)throw new Exception(string.Join(Environment.NewLine,failures));
        Console.WriteLine("PASS: account poll proof, DPAPI storage, expiry, lost-response renewal, single-flight, honest sign-out and signed-out upload/support gating");
    }
    static async Task PollProof(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);
        Check(account.State==AccountState.SignedOut&&!File.Exists(path)&&account.PendingVerificationUrl==null,"A new installation must start signed out without writing credentials");
        Check(!await account.RequestSignInAsync("not an email")&&fake.Routes==0&&!File.Exists(path),"Invalid email reached the service or was saved");
        Check(await account.RequestSignInAsync(EmailAddress)&&account.State==AccountState.Pending&&account.PendingEmail==EmailAddress&&!account.PendingEmailSent,"Pending request was not prepared");
        Check(fake.Routes==0&&fake.Requests==0,"Preparing sign-in contacted the service; it must be purely local");
        Check(account.Status.Contains("browser")&&!account.Status.Contains("link sent"),"Prepared request claimed an email was sent: "+account.Status);
        var vault=Vault(path).GetProperty("Pending");
        string requestID=vault.GetProperty("RequestID").GetString()!,secret=vault.GetProperty("PollSecret").GetString()!,verifier=vault.GetProperty("Verifier").GetString()!;
        Check(Guid.TryParseExact(requestID,"D",out _)&&secret.Length==43&&verifier.Length==43&&secret!=verifier,"Protected pending request lacks a UUID or 32-byte poll secret/verifier");
        string raw=File.ReadAllText(path);
        Check(!raw.Contains(secret)&&!raw.Contains(requestID)&&!raw.Contains(EmailAddress),"Pending request stored in plaintext");
        // The handoff is public: bindings only, never the raw poll secret or verifier (in the URL or its decoded JSON).
        string url=account.PendingVerificationUrl!;var handoff=Handoff(url);string decoded=handoff.GetRawText();
        Check(!url.Contains(secret)&&!url.Contains(verifier)&&!decoded.Contains(secret)&&!decoded.Contains(verifier)&&!decoded.Contains("SYNTHETIC"),"Handoff exposed raw proof");
        Check(handoff.GetProperty("desktopRequestID").GetString()==requestID&&handoff.GetProperty("desktopPollHash").GetString()==Hex(secret)
            &&handoff.GetProperty("challenge").GetString()==Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))&&handoff.GetProperty("email").GetString()==EmailAddress,"Handoff does not bind to the saved proof");
        long expires=handoff.GetProperty("expiresAt").GetInt64(),created=fake.Now.ToUnixTimeSeconds();
        Check(expires>=created+899&&expires<=created+901,"Handoff expiry is not local now + 900");
        account=Session(path,fake);Check(account.State==AccountState.Pending&&account.PendingVerificationUrl==url,"Pending request or its reopen URL did not survive restart");
        await account.PollAsync();Check(fake.Polls==1&&account.State==AccountState.Pending&&!account.PendingEmailSent&&account.PendingVerificationUrl==url,"Restarted app did not wait for the browser step");
        await account.PollAsync();Check(fake.Polls==1,"Polling ignored its 5-second interval");
        fake.CompleteInBrowser(url);fake.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();
        Check(fake.Polls==2&&account.PendingEmailSent&&account.Status.Contains("link sent")&&account.PendingVerificationUrl==null&&account.PendingExpiresAt!=null,"Authenticated emailSent was not shown or the handoff stayed open: "+account.Status);
        Check(Vault(path).GetProperty("Pending").GetProperty("EmailSent").GetBoolean(),"Email-sent state was not persisted");
        account=Session(path,fake);Check(account.PendingEmailSent&&account.PendingVerificationUrl==null,"Email-sent state was lost on restart");
        fake.Confirmed=true;fake.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();
        Check(fake.Polls==3&&fake.Requests==1&&account.State==AccountState.SignedIn&&account.Email==EmailAddress&&account.Status.Contains(EmailAddress),"Confirmed link did not sign in: "+account.Status);
        var saved=Vault(path);string savedText=saved.GetRawText();
        Check(saved.GetProperty("RefreshToken").GetString()==fake.Refresh&&saved.GetProperty("Pending").ValueKind==JsonValueKind.Null,"Signed-in vault kept the pending request or lost the refresh token");
        Check(!savedText.Contains(fake.Access)&&!savedText.Contains("CSRF")&&!File.ReadAllText(path).Contains(fake.Refresh),"Access/CSRF tokens were persisted or the refresh token was stored in plaintext");
        Check(await account.AccessTokenAsync()==fake.Access&&fake.Refreshes==0,"Fresh access token was not reused");
    }
    static async Task Expiry(string folder)
    {
        // Before mail: the browser step never finished within the local 900 seconds.
        string path=Path.Combine(folder,"before","account.json");var fake=new FakeAuth();var account=Session(path,fake);
        Check(await account.RequestSignInAsync(EmailAddress),"Request failed");
        fake.Now+=TimeSpan.FromSeconds(901);
        Check(account.PendingVerificationUrl==null,"An expired handoff could still be reopened");
        await account.PollAsync();
        Check(account.State==AccountState.SignedOut&&fake.Polls==0&&account.Status.Contains("not completed in time")&&Vault(path).GetProperty("Pending").ValueKind==JsonValueKind.Null,"Expired browser step kept polling or stayed pending: "+account.Status);
        Check(fake.Routes==0&&fake.Requests==0,"Expiry contacted the service or sent an email");
        // After mail: one bounded extension from the first emailSent poll, then a hard stop.
        path=Path.Combine(folder,"after","account.json");fake=new FakeAuth{ExpiresIn=5000};account=Session(path,fake);
        Check(await account.RequestSignInAsync(EmailAddress),"Request failed");
        fake.CompleteInBrowser(account.PendingVerificationUrl);fake.Now+=TimeSpan.FromSeconds(800);long receipt=fake.Now.ToUnixTimeSeconds();
        await account.PollAsync();
        long deadline=Vault(path).GetProperty("Pending").GetProperty("LocalDeadline").GetInt64();
        Check(account.PendingEmailSent&&deadline>=receipt+899&&deadline<=receipt+901,"First emailSent poll did not extend once, bounded to 900 seconds (deadline "+(deadline-receipt)+")");
        fake.Now+=TimeSpan.FromSeconds(600);await account.PollAsync();
        Check(account.State==AccountState.Pending&&Vault(path).GetProperty("Pending").GetProperty("LocalDeadline").GetInt64()==deadline,"A later emailSent poll extended the deadline again");
        account=Session(path,fake);fake.Now+=TimeSpan.FromSeconds(290);await account.PollAsync();
        Check(account.State==AccountState.Pending&&fake.Polls==3,"Restart lost the extended deadline");
        fake.Now+=TimeSpan.FromSeconds(11);await account.PollAsync();
        Check(account.State==AccountState.SignedOut&&fake.Polls==3&&account.Status.Contains("expired before it was confirmed")&&Vault(path).GetProperty("Pending").ValueKind==JsonValueKind.Null,"Emailed request did not stop at its bounded expiry: "+account.Status);
        Check(fake.Requests==1,"Expiry automatically requested another email");
    }
    static async Task ReopenCancel(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);
        Check(await account.RequestSignInAsync(EmailAddress),"Request failed");string url=account.PendingVerificationUrl!;
        Check(!await account.RequestSignInAsync(EmailAddress)&&account.PendingVerificationUrl==url&&account.Status.Contains("already waiting"),"A second request replaced the pending one");
        // A failed browser launch is honest and keeps the request for reopening.
        account.ReportBrowserOpenFailed();
        Check(account.State==AccountState.Pending&&account.PendingVerificationUrl==url&&account.Status.Contains("could not be opened")&&Vault(path).GetProperty("Pending").ValueKind==JsonValueKind.Object,"Browser-open failure dropped or hid the pending request: "+account.Status);
        await account.CancelPendingAsync();
        Check(account.State==AccountState.SignedOut&&account.PendingVerificationUrl==null&&Vault(path).GetProperty("Pending").ValueKind==JsonValueKind.Null&&account.Status.Contains("cancelled"),"Cancel did not drop the local proof");
        // A stale browser tab finishing afterwards cannot sign this app in: nothing is polled.
        fake.CompleteInBrowser(url);fake.Confirmed=true;fake.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();await Session(path,fake).PollAsync();
        Check(fake.Polls==0&&account.State==AccountState.SignedOut,"A cancelled request was still polled");
        Check(await account.RequestSignInAsync(EmailAddress),"New request after cancel failed");
        var fresh=Handoff(account.PendingVerificationUrl);
        Check(fresh.GetProperty("desktopRequestID").GetString()!=fake.RequestID&&fresh.GetProperty("desktopPollHash").GetString()!=fake.PollHash,"A new request reused the cancelled proof");
    }
    static async Task WrongAccount(string folder)
    {
        const string other="attacker@example.test";
        string root=ClientRoot(folder),save=Path.Combine(root,"WTF","wrong.lua");long at=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(save,"ForeverFlipDB={[\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70450\",[\"addon\"]=\"0.13.73\"},[\"markets\"]={[\"18:1:110:Classic Beta PvE:Alliance\"]={[\"2589:0:0:0\"]={[\"name\"]=\"Linen\",[\"history\"]={{[\"p\"]=20,[\"q\"]=10,[\"t\"]="+at+"}}}}}}");
        File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);
        var config=new Settings{ClientRoot=root,SavedFile=save,NoticeAccepted=true,ShareMarket=true};
        var sync=new MarketSync(config,Path.Combine(folder,"cache"),null,new Handler(fake.Send)){SaveProcessProbe=_=>GameSaveState.Stopped,Account=account};
        Check(await account.RequestSignInAsync(EmailAddress),"Request failed");
        // A copied handoff keeps the public hashes but carries another address, completed by someone else.
        var handoff=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Handoff(account.PendingVerificationUrl).GetRawText())!;
        var tampered=new{email=other,client="desktop",deviceName=handoff["deviceName"].GetString(),challenge=handoff["challenge"].GetString(),
            desktopRequestID=handoff["desktopRequestID"].GetString(),desktopPollHash=handoff["desktopPollHash"].GetString(),expiresAt=handoff["expiresAt"].GetInt64()};
        fake.CompleteInBrowser(HandoffPrefix+Base64Url(JsonSerializer.SerializeToUtf8Bytes(tampered)));
        Check(fake.Email==other,"Tampered handoff was not modelled");
        fake.Confirmed=true;fake.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();
        Check(fake.Polls==1&&account.State==AccountState.SignedOut&&account.Email==""&&!account.IsCurrentAccess(fake.Access)&&await account.AccessTokenAsync()==null,"Wrong-account credentials were adopted: "+account.Status);
        Check(account.Status.Contains("different email")&&account.Status.Contains(EmailAddress)&&!account.Status.Contains(other),"Wrong-account outcome was not explained honestly: "+account.Status);
        var saved=Vault(path);string savedText=saved.GetRawText();
        Check(saved.GetProperty("RefreshToken").GetString()==""&&saved.GetProperty("Pending").ValueKind==JsonValueKind.Null&&!savedText.Contains(fake.Refresh)&&!savedText.Contains(other),"Wrong-account request or credentials stayed on disk");
        fake.Now+=TimeSpan.FromSeconds(10);await account.PollAsync();await sync.Tick();
        Check(fake.Polls==1&&fake.ObservationBearers.Count==0&&sync.State==MarketSyncState.SignInRequired,"Rejected request kept polling or uploaded");
        // The intended address can start a fresh flow; server case/whitespace differences still match.
        fake.Confirmed=false;fake.AccountEmail="PLAYER@Example.TEST";
        Check(await account.RequestSignInAsync(EmailAddress),"Intended email could not start a new flow: "+account.Status);
        fake.CompleteInBrowser(account.PendingVerificationUrl);fake.Confirmed=true;fake.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();
        Check(account.State==AccountState.SignedIn&&AccountSession.SameEmail(account.Email,EmailAddress),"Intended account did not sign in: "+account.Status);
        await sync.Tick();Check(fake.ObservationBearers.Count==1&&fake.ObservationBearers[0]==fake.Access,"Intended account did not upload with its own token");
        // Renewal: an older reply without email keeps the session; a different account ends it unused.
        fake.OmitEmail=true;fake.Now+=TimeSpan.FromMinutes(16);
        Check(await account.AccessTokenAsync()==fake.Access&&account.State==AccountState.SignedIn,"Legacy refresh without email was rejected");
        fake.OmitEmail=false;fake.AccountEmail=other;fake.Now+=TimeSpan.FromMinutes(16);
        string? token=await account.AccessTokenAsync();string swapped=fake.Access;
        Check(token==null&&account.State==AccountState.SignedOut&&!account.IsCurrentAccess(swapped)&&account.Status.Contains("different account"),"Refresh adopted a different account: "+account.Status);
        Check(Vault(path).GetProperty("RefreshToken").GetString()==""&&!Vault(path).GetRawText().Contains(swapped),"Different-account renewal credentials were saved");
    }
    static async Task BlockedPrepare(string folder)
    {
        Directory.CreateDirectory(folder);string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);
        File.WriteAllText(path,"locked");
        using(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)){
            Check(!await account.RequestSignInAsync(EmailAddress)&&account.State==AccountState.SignedOut&&account.PendingVerificationUrl==null,"Sign-in started without protected storage");
            Check(account.Status.Contains("protected sign-in file")&&fake.Routes==0,"Unsaved request was not explained or contacted the service: "+account.Status);
        }
    }
    static async Task LegacyPending(string folder)
    {
        // A request created natively by an older version: already emailed, no handoff, no extension.
        Directory.CreateDirectory(folder);string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();
        string verifier=new string('v',43),secret=new string('s',43);
        string vault=JsonSerializer.Serialize(new{Email="",RefreshToken="",RotationSecret="",Pending=new{RequestID="req-legacy-0001",PollSecret=secret,Verifier=verifier,Email=EmailAddress,ExpiresAt=fake.Now.ToUnixTimeSeconds()+600,LocalDeadline=fake.Now.ToUnixTimeSeconds()+600}});
        File.WriteAllText(path,JsonSerializer.Serialize(new{schemaVersion=1,@protected=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(vault),Encoding.UTF8.GetBytes("ForeverFlip.Account.v1"),DataProtectionScope.CurrentUser))}));
        fake.RequestID="req-legacy-0001";fake.PollHash=Hex(secret);fake.Challenge=Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));fake.Email=EmailAddress;
        var account=Session(path,fake);
        Check(account.State==AccountState.Pending&&account.PendingEmailSent&&account.PendingVerificationUrl==null,"Older emailed request offered a browser handoff");
        await account.PollAsync();
        Check(Vault(path).GetProperty("Pending").GetProperty("LocalDeadline").GetInt64()<=fake.Now.ToUnixTimeSeconds()+600,"Older request deadline was extended");
        fake.Confirmed=true;fake.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();
        Check(account.State==AccountState.SignedIn&&fake.Requests==0,"Older request could not finish through desktop-poll: "+account.Status);
    }
    static async Task LostRefresh(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);await SignIn(account,fake);
        fake.BeforeRefresh=(prior,secret)=>{var v=Vault(path);Check(v.GetProperty("RefreshToken").GetString()==prior&&v.GetProperty("RotationSecret").GetString()==secret,"Prior refresh token and rotation secret were not protected on disk before sending");};
        fake.Now+=TimeSpan.FromMinutes(16);fake.LoseNextRefresh=true;bool unavailable=false;
        try{await account.AccessTokenAsync();}catch(MarketServiceException){unavailable=true;}
        Check(unavailable&&account.State==AccountState.SignedIn&&account.NetworkProblem,"A lost renewal response was mistaken for sign-out or success");
        var first=fake.RefreshBodies[^1];var pending=Vault(path);
        Check(pending.GetProperty("RefreshToken").GetString()==first.Prior&&pending.GetProperty("RotationSecret").GetString()==first.Secret,"Network failure discarded the pending rotation");
        account=Session(path,fake);fake.Now+=TimeSpan.FromSeconds(30);
        string? token=await account.AccessTokenAsync();var retry=fake.RefreshBodies[^1];
        Check(token==fake.Access&&retry==first,"Restarted retry did not resend the same prior token and rotation secret");
        var rotated=Vault(path);
        Check(rotated.GetProperty("RefreshToken").GetString()==fake.Refresh&&rotated.GetProperty("RotationSecret").GetString()=="","Recovered credentials were not saved or the pending secret was not cleared");
        fake.Now+=TimeSpan.FromMinutes(16);Check(await account.AccessTokenAsync()==fake.Access,"Next renewal failed");
        var next=fake.RefreshBodies[^1];Check(next.Prior==rotated.GetProperty("RefreshToken").GetString()&&next.Secret!=first.Secret&&fake.Refreshes==3,"A later renewal reused an old rotation secret");
    }
    static async Task PollConflict(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);
        Check(await account.RequestSignInAsync(EmailAddress),"Request failed");
        fake.CompleteInBrowser(account.PendingVerificationUrl);
        const string limit="Device limit reached: this account already has 5 signed-in devices. Sign out another device, then try again.";
        fake.PollFailure=(HttpStatusCode.Conflict,limit);fake.Now+=TimeSpan.FromSeconds(6);await account.PollAsync();
        Check(fake.Polls==1&&account.State==AccountState.SignedOut&&account.Status.Contains(limit)&&!account.NetworkProblem,"409 was not terminal or hid the server reason: "+account.Status);
        Check(Vault(path).GetProperty("Pending").ValueKind==JsonValueKind.Null,"Terminal 409 kept the pending request on disk");
        fake.Now+=TimeSpan.FromMinutes(2);await account.PollAsync();await Session(path,fake).PollAsync();
        Check(fake.Polls==1&&fake.Requests==1,"409 kept polling or automatically resent the email");
    }
    static async Task LostRefreshExpired(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);await SignIn(account,fake);
        fake.Now+=TimeSpan.FromMinutes(16);fake.LoseNextRefresh=true;bool unavailable=false;
        try{await account.AccessTokenAsync();}catch(MarketServiceException){unavailable=true;}
        Check(unavailable&&account.State==AccountState.SignedIn&&account.Status.Contains("offline"),"Lost response was not reported as a network failure: "+account.Status);
        var first=fake.RefreshBodies[^1];fake.Now+=TimeSpan.FromSeconds(121);
        Check(await account.AccessTokenAsync()==null&&fake.RefreshBodies[^1]==first,"Late retry did not resend the saved token/secret or stayed signed in");
        Check(account.State==AccountState.SignedOut&&account.Status.Contains("interrupted")&&account.Status.Contains("Send a new sign-in link")&&fake.Requests==1,"Expired replay was not explained honestly or resent email: "+account.Status);
        Check(Vault(path).GetProperty("RefreshToken").GetString()==""&&Vault(path).GetProperty("RotationSecret").GetString()=="","Revoked credentials stayed on disk");
    }
    static async Task SingleFlight(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);await SignIn(account,fake);
        fake.Now+=TimeSpan.FromMinutes(16);fake.RefreshGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var a=account.AccessTokenAsync();
        for(int i=0;i<200&&fake.Refreshes==0;i++)await Task.Delay(10);
        // Queued in order behind the in-flight renewal: a second token request, then sign-out.
        var b=account.AccessTokenAsync();var signOut=account.SignOutAsync(false);await Task.Delay(50);
        Check(fake.Refreshes==1&&fake.Logouts.Count==0,"Concurrent renewals/sign-out raced the rotating refresh token");
        fake.RefreshGate.SetResult();var tokens=await Task.WhenAll(a,b);await signOut;
        Check(fake.Refreshes==1&&tokens[0]==tokens[1]&&tokens[0]!=null,"Concurrent callers did not share one renewal");
        Check(fake.Logouts.Count==1&&account.State==AccountState.SignedOut,"Queued sign-out did not run after renewal");
    }
    static async Task RejectedRefresh(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);await SignIn(account,fake);
        fake.Refresh="SERVER_SIDE_REVOKED";fake.Rotation=null;fake.Now+=TimeSpan.FromMinutes(16);
        Check(await account.AccessTokenAsync()==null&&account.State==AccountState.SignedOut,"Rejected refresh left the app signed in");
        Check(fake.Requests==1&&account.Status.Contains("Send a new sign-in link")&&!account.Status.Contains("REDACTED"),"Rejected refresh resent email or showed raw server text: "+account.Status);
        Check(Vault(path).GetProperty("RefreshToken").GetString()=="","Rejected credentials were kept on disk");
    }
    static async Task SignOut(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var account=Session(path,fake);await SignIn(account,fake);
        fake.LogoutOffline=true;await account.SignOutAsync(true);
        Check(account.State==AccountState.SignedIn&&account.SignOutFailed&&account.Status.Contains("still signed in")&&!account.Status.Contains("Signed out on all devices"),"Failed revoke claimed sign-out: "+account.Status);
        Check(Vault(path).GetProperty("RefreshToken").GetString()!="","Failed revoke cleared credentials without explicit local-only choice");
        await account.ForgetLocalAsync();
        Check(account.State==AccountState.SignedOut&&account.Status.Contains("not revoked")&&Vault(path).GetProperty("RefreshToken").GetString()=="","Local-only removal was not explicit and honest");
        fake=new FakeAuth();path=Path.Combine(folder,"second","account.json");account=Session(path,fake);await SignIn(account,fake);
        string access=fake.Access;await account.SignOutAsync(false);
        Check(fake.Logouts.Count==1&&fake.Logouts[0].Route=="/api/v1/auth/logout"&&fake.Logouts[0].Bearer==access&&fake.Logouts[0].Body=="{}","Sign-out did not POST {} with the bearer access token");
        Check(account.State==AccountState.SignedOut&&account.Status=="Signed out on this PC."&&Vault(path).GetProperty("RefreshToken").GetString()=="","Successful revoke did not clear local credentials");
        fake=new FakeAuth();path=Path.Combine(folder,"third","account.json");account=Session(path,fake);await SignIn(account,fake);
        await account.SignOutAsync(true);
        Check(fake.Logouts.Single().Route=="/api/v1/auth/logout-all"&&account.Status=="Signed out on all devices."&&account.State==AccountState.SignedOut,"Confirmed logout-all was not reported accurately");
    }
    static Task Corrupt(string folder)
    {
        Directory.CreateDirectory(folder);string path=Path.Combine(folder,"account.json");
        File.WriteAllText(path,"{\"schemaVersion\":1,\"protected\":\""+Convert.ToBase64String(Encoding.UTF8.GetBytes("not a DPAPI blob"))+"\"}");
        var account=Session(path,new FakeAuth());
        Check(account.State==AccountState.SignedOut&&!File.Exists(path)&&Directory.EnumerateFiles(folder,"account.json.invalid-*").Any(),"Unreadable sign-in was trusted or deleted instead of retained");
        return Task.CompletedTask;
    }
    static string ClientRoot(string folder)
    {
        string root=Path.Combine(folder,"_classic_beta_");
        Directory.CreateDirectory(Path.Combine(root,"Interface","AddOns","ForeverFlip"));Directory.CreateDirectory(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data"));Directory.CreateDirectory(Path.Combine(root,"WTF"));
        File.WriteAllText(Path.Combine(root,"Wow.exe"),"");File.WriteAllText(Path.Combine(root,"Interface","AddOns","ForeverFlip","ForeverFlip.toc"),"## Version: 999.0.0\nAddon.lua\n");
        File.WriteAllText(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data","ForeverFlip_Data.toc"),"Data.lua\n");File.WriteAllText(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data","Data.lua"),"SYNTHETIC");
        return root;
    }
    static async Task ForgetMidUpload(string folder)
    {
        string root=ClientRoot(folder),save=Path.Combine(root,"WTF","batches.lua");long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var text=new StringBuilder("ForeverFlipDB={[\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70450\",[\"addon\"]=\"0.13.73\"},[\"markets\"]={[\"18:1:110:Classic Beta PvE:Alliance\"]={");
        for(int i=1;i<=250;i++)text.Append("[\"").Append(i).Append(":0:0:0\"]={[\"name\"]=\"Synthetic\",[\"history\"]={{[\"p\"]=20,[\"q\"]=1,[\"t\"]=").Append(now).Append("}}},");
        File.WriteAllText(save,text.Append("}}}").ToString());File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        var fake=new FakeAuth();var account=Session(Path.Combine(folder,"account.json"),fake);await SignIn(account,fake);string copied=fake.Access;
        // The explicit local-only removal lands while the first of three batches is in flight.
        fake.OnObservation=async()=>{if(account.State==AccountState.SignedIn)await account.ForgetLocalAsync();};
        var config=new Settings{ClientRoot=root,SavedFile=save,NoticeAccepted=true,ShareMarket=true};
        var sync=new MarketSync(config,Path.Combine(folder,"cache"),null,new Handler(fake.Send)){SaveProcessProbe=_=>GameSaveState.Stopped,Account=account};
        await sync.Tick();
        Check(account.State==AccountState.SignedOut&&!account.IsCurrentAccess(copied),"Removed session still reports the copied token as current");
        Check(fake.ObservationBearers.Count==1,"A later batch was transmitted with a copied token after local-only removal ("+fake.ObservationBearers.Count+" sends)");
        Check(sync.State==MarketSyncState.Error&&sync.Status.Contains("Upload paused")&&config.LastSignature=="","Interrupted upload was reported complete or unclear: "+sync.Status);
        await sync.Tick();
        Check(fake.ObservationBearers.Count==1&&sync.State==MarketSyncState.SignInRequired,"Signed-out follow-up sync uploaded or did not ask for sign-in");
    }
    static async Task ClockSkew(string folder)
    {
        foreach(int offset in new[]{3600,-3600}){
            foreach(bool ttl in new[]{true,false}){
                string path=Path.Combine(folder,offset+"-"+ttl,"account.json");
                var fake=new FakeAuth{PcOffset=TimeSpan.FromSeconds(offset),SendTtl=ttl};fake.Now=DateTimeOffset.UtcNow+fake.PcOffset;
                var account=Session(path,fake);await SignIn(account,fake);
                Check(fake.Polls==1&&Vault(path).GetProperty("Pending").ValueKind==JsonValueKind.Null,"PC clock "+offset+"s prevented sign-in (ttl="+ttl+"): "+account.Status);
                Check(await account.AccessTokenAsync()==fake.Access&&fake.Refreshes==0,"PC clock "+offset+"s forced an immediate renewal");
                fake.Now+=TimeSpan.FromMinutes(13);Check(await account.AccessTokenAsync()==fake.Access&&fake.Refreshes==0,"Access lifetime was not measured from local receipt");
                fake.Now+=TimeSpan.FromMinutes(2);Check(await account.AccessTokenAsync()==fake.Access&&fake.Refreshes==1,"Access was not renewed inside the 60-second safety margin");
            }
            // Pending deadline is local receipt + TTL and survives restart, regardless of the PC clock.
            string pendingPath=Path.Combine(folder,offset+"-pending","account.json");
            var pendingFake=new FakeAuth{PcOffset=TimeSpan.FromSeconds(offset)};pendingFake.Now=DateTimeOffset.UtcNow+pendingFake.PcOffset;
            var pending=Session(pendingPath,pendingFake);long receipt=pendingFake.Now.ToUnixTimeSeconds();
            Check(await pending.RequestSignInAsync(EmailAddress),"Request failed with skewed clock");
            long local=Vault(pendingPath).GetProperty("Pending").GetProperty("LocalDeadline").GetInt64();
            Check(local>=receipt+900&&local<=receipt+902,"Pending deadline was not saved from local receipt time");
            pendingFake.Now+=TimeSpan.FromSeconds(899);pending=Session(pendingPath,pendingFake);await pending.PollAsync();
            Check(pending.State==AccountState.Pending&&pendingFake.Polls==1,"A skewed PC clock expired a valid link");
        }
    }
    static async Task MalformedPending(string folder)
    {
        Directory.CreateDirectory(folder);string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();
        string vault=JsonSerializer.Serialize(new{Email="",RefreshToken="",RotationSecret="",Pending=new{RequestID="req-malformed-0001",PollSecret=new string('s',43),Verifier=new string('v',43),Email=EmailAddress,ExpiresAt=long.MaxValue,LocalDeadline=long.MaxValue}});
        string blob=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(vault),Encoding.UTF8.GetBytes("ForeverFlip.Account.v1"),DataProtectionScope.CurrentUser));
        File.WriteAllText(path,JsonSerializer.Serialize(new{schemaVersion=1,@protected=blob}));
        var account=Session(path,fake);
        Check(account.State==AccountState.Pending&&account.PendingExpiresAt==null&&account.Status.Contains(EmailAddress),"Malformed saved expiry crashed or was displayed");
        fake.Now+=TimeSpan.FromSeconds(901);await account.PollAsync();
        Check(account.State==AccountState.SignedOut&&fake.Polls==0,"Malformed saved deadline was not bounded to 900 seconds");
    }
    static async Task LockedStorage(string folder)
    {
        string path=Path.Combine(folder,"account.json");var fake=new FakeAuth();var original=Session(path,fake);await SignIn(original,fake);
        byte[] before=File.ReadAllBytes(path);int requests=fake.Requests;
        AccountSession account;
        using(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)){
            account=Session(path,fake);
            Check(account.State==AccountState.SignedOut&&account.Status.Contains("could not be read")&&!account.Status.Contains("Send a new"),"Temporarily unreadable sign-in suggested a new link: "+account.Status);
            Check(!await account.RequestSignInAsync(EmailAddress)&&fake.Requests==requests&&account.Status.Contains("restart"),"Unreadable sign-in allowed a new link request: "+account.Status);
        }
        Check(File.ReadAllBytes(path).SequenceEqual(before),"Unreadable saved sign-in was overwritten");
        Check(!await account.RequestSignInAsync(EmailAddress)&&fake.Requests==requests&&account.State==AccountState.SignedIn&&account.Email==EmailAddress,"Readable stored account did not resume or a link was sent anyway");
        Check(!Directory.EnumerateFiles(folder,"account.json.invalid-*").Any(),"A locked (not corrupt) file was treated as corrupt");
    }
    static async Task Uploads(string folder)
    {
        string root=ClientRoot(folder);
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string id=$"fflog-{now}-7-321",save=Path.Combine(root,"WTF","account.lua");
        File.WriteAllText(save,"ForeverFlipDB={[\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70450\",[\"addon\"]=\"0.13.73\"},[\"markets\"]={[\"18:1:110:Classic Beta PvE:Alliance\"]={[\"2589:0:0:0\"]={[\"name\"]=\"Linen\",[\"history\"]={{[\"p\"]=20,[\"q\"]=10,[\"t\"]="+now+"}}}}},"+
            "[\"supportRequest\"]={[\"schemaVersion\"]=2,[\"consent\"]=\"support-2026-10-17\",[\"requestID\"]=\""+id+"\",[\"requestedAt\"]="+now+",[\"addonVersion\"]=\"0.13.73\",[\"build\"]=\"70450\",[\"reportType\"]=\"feedback\",[\"description\"]=\"Synthetic account gating\",[\"events\"]={{[\"t\"]="+now+",[\"kind\"]=\"craft_pause\",[\"data\"]={[\"reason\"]=\"Supplies needed\"}}}}}");
        File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        string legacy=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("LEGACY_INSTALL_TOKEN"),null,DataProtectionScope.CurrentUser));
        var config=new Settings{ClientRoot=root,SavedFile=save,NoticeAccepted=true,ShareMarket=true,ProtectedToken=legacy,ProtectedDiagnosticsToken=legacy};
        var fake=new FakeAuth();var account=Session(Path.Combine(folder,"account.json"),fake);
        var sync=new MarketSync(config,Path.Combine(folder,"cache"),null,new Handler(fake.Send)){SaveProcessProbe=_=>GameSaveState.Stopped,Account=account};
        await sync.Tick();
        Check(sync.State==MarketSyncState.SignInRequired&&sync.Status.Contains("Sign in"),"Signed-out sharing did not explain paused uploads: "+sync.State+" "+sync.Status);
        Check(fake.Installations==0&&fake.ObservationBearers.Count==0&&fake.SupportBearers.Count==0,"Signed-out app registered anonymously or uploaded");
        Check(fake.Packs==0&&config.MarketIDs.Count==1&&config.SavedItemCount==1,"Signed-out app downloaded AI or stopped local bookkeeping");
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(save).ToUpperInvariant()))).ToLowerInvariant()[..24];
        Check(sync.SupportStatus.Contains("Sign in")&&Directory.EnumerateFiles(Path.Combine(MarketSync.StateRoot,"support",key),"*.report.json").Count()==1,"Explicit support report was not preserved while signed out: "+sync.SupportStatus);
        Check(!fake.AllBearers.Contains("LEGACY_INSTALL_TOKEN")&&config.ProtectedToken==legacy&&config.ProtectedDiagnosticsToken==legacy,"Legacy installation token was used or deleted");
        sync.Account=null;await sync.Tick();
        Check(sync.State==MarketSyncState.SignInRequired&&fake.Installations==0&&fake.ObservationBearers.Count==0,"A missing account fell back to anonymous upload");
        sync.Account=account;await SignIn(account,fake);
        Check(config.ShareMarket&&fake.ObservationBearers.Count==0,"Signing in changed sharing or uploaded before the next sync");
        await sync.Tick();
        Check(fake.Installations==0&&fake.ObservationBearers.Count==1&&fake.ObservationBearers.All(b=>b==fake.Access),"Upload did not use the verified account access token");
        Check(fake.SupportBearers.Count==1&&fake.SupportBearers[0]==fake.Access&&sync.SupportStatus.StartsWith("Report sent"),"Saved support report was not sent with the account token: "+sync.SupportStatus);
        Check(sync.State==MarketSyncState.Synced&&config.ProtectedToken==legacy,"Signed-in upload did not complete or removed the legacy token: "+sync.Status);
    }
}
