using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

public enum AccountState {SignedOut,Pending,SignedIn}

// Passwordless desktop account. The app prepares a request locally and hands
// only public binding values (challenge, poll-secret hash) to the website,
// which runs the human check and sends the email; this app never calls
// /auth/request and only polls with its locally held proof. Refresh tokens,
// pending requests and rotation secrets live in one Windows DPAPI (current
// user) blob. Access tokens stay in memory. Legacy installation tokens are never accounts.
public sealed partial class AccountSession
{
    internal sealed class Vault
    {
        public string Email {get;set;}="";
        public string RefreshToken {get;set;}="";
        // Saved before /refresh is sent and reused unchanged after a lost response.
        public string RotationSecret {get;set;}="";
        public PendingSignIn? Pending {get;set;}
    }
    internal sealed class PendingSignIn
    {
        public string RequestID {get;set;}="";
        public string PollSecret {get;set;}="";
        public string Verifier {get;set;}="";
        public string Email {get;set;}="";
        // Server's absolute expiry: display only, the PC clock may be wrong.
        public long ExpiresAt {get;set;}
        // PC-clock deadline: creation + 900s for the browser step, then extended
        // once from the first authenticated emailSent poll (bounded TTL); this stops polling.
        public long LocalDeadline {get;set;}
        // Browser handoff fields. Empty DeviceName marks an older request created
        // natively by a previous version: it has no handoff and was already emailed.
        public string DeviceName {get;set;}="";
        public long HandoffExpiresAt {get;set;}
        public bool EmailSent {get;set;}
        public bool Extended {get;set;}
        public bool Pairing {get;set;}
        public bool PairSubmitted {get;set;}
    }
    const long MaxTtl=900;
    enum Renewal {Renewed,Ended,Unavailable}
    internal static readonly byte[] Entropy=Encoding.UTF8.GetBytes("ForeverFlip.Account.v1");
    const string SignedOutText="Sign in to unlock AI market analysis. Your bookkeeping stays on this PC. Sharing is a separate choice.";
    readonly string path,service;
    readonly HttpClient http;
    readonly Func<DateTimeOffset> clock;
    // Serializes request, poll, refresh and sign-out. Separate from MarketSync.gate.
    readonly SemaphoreSlim gate=new(1);
    Vault vault=new();
    string? access;
    long accessExpiresAt,lastRenewedAt,nextPollAt,nextMaintainAt;
    int pollFailures,maintainFailures;
    // A saved sign-in exists but could not be read (for example, locked). Never overwrite it.
    bool storageReadBlocked;
    public event Action? Changed;
    public string Status {get;private set;}=SignedOutText;
    public bool NetworkProblem {get;private set;}
    public bool SignOutFailed {get;private set;}
    public bool IsBusy=>gate.CurrentCount==0;
    public AccountState State=>vault.RefreshToken!=""?AccountState.SignedIn:vault.Pending!=null?AccountState.Pending:AccountState.SignedOut;
    public string Email=>vault.RefreshToken!=""?vault.Email:"";
    public string PendingEmail=>vault.Pending?.Email??"";
    public DateTimeOffset? PendingExpiresAt=>vault.Pending is PendingSignIn p&&p.ExpiresAt>0?DateTimeOffset.FromUnixTimeSeconds(p.ExpiresAt):null;
    // True once the server confirms (to this app's proof) that the website created the request and emailed the link.
    public bool PendingEmailSent=>vault.Pending?.EmailSent==true;
    // Browser page that finishes the request. Fixed service HTTPS origin only; carries public
    // binding values, never the poll secret, verifier or session tokens. Null once the email
    // was sent, after the local deadline, for older native requests, or when signed out.
    public string? PendingVerificationUrl
    {
        get{
            if(vault.Pending is not PendingSignIn p||p.DeviceName==""||p.EmailSent||Now>=p.LocalDeadline)return null;
            if(!Guid.TryParseExact(p.RequestID,"D",out _)||!Uri.TryCreate(service,UriKind.Absolute,out var origin)||origin.Scheme!=Uri.UriSchemeHttps)return null;
            if(p.Pairing){
                byte[] pair=JsonSerializer.SerializeToUtf8Bytes(new{client="desktop",deviceName=p.DeviceName,challenge=Challenge(p.Verifier),desktopRequestID=p.RequestID,desktopPollHash=PollHash(p.PollSecret),expiresAt=p.HandoffExpiresAt});
                return origin.GetLeftPart(UriPartial.Authority)+"/account/#pair="+Base64Url(pair);
            }
            byte[] handoff=JsonSerializer.SerializeToUtf8Bytes(new{email=p.Email,client="desktop",deviceName=p.DeviceName,challenge=Challenge(p.Verifier),
                desktopRequestID=p.RequestID,desktopPollHash=PollHash(p.PollSecret),expiresAt=p.HandoffExpiresAt});
            return origin.GetLeftPart(UriPartial.Authority)+"/account/#desktop="+Base64Url(handoff);
        }
    }
    public AccountSession(string path,HttpMessageHandler? handler=null,Func<DateTimeOffset>? clock=null,string service=MarketSync.Service)
    {
        this.path=path;this.service=service;this.clock=clock??(()=>DateTimeOffset.UtcNow);
        http=new(handler??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(35),MaxResponseContentBufferSize=1024*1024};
        Load();
    }
    long Now=>clock().ToUnixTimeSeconds();
    bool Fresh=>access!=null&&accessExpiresAt>Now+60;
    static string Local(long at)=>DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("t",CultureInfo.CurrentCulture);
    string PendingText(PendingSignIn p)=>p.Pairing?"Finish signing in on the website, then choose Connect desktop app. This app will connect automatically.":p.EmailSent
        ?"Sign-in link sent to "+p.Email+". Open it and confirm on the website; this app signs in automatically."+(p.ExpiresAt>0?" Link expires at "+Local(p.ExpiresAt)+".":"")
        :"Finish sign-in for "+p.Email+" in your browser: confirm the address and complete the check there so the website can email your link. This app waits and signs in automatically. Available until "+Local(p.LocalDeadline)+".";
    // Validated server lifetime in seconds, bounded to 15 minutes; older servers omit it.
    static long Ttl(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out long seconds)&&seconds>0?Math.Min(seconds,MaxTtl):MaxTtl;
    void Load()
    {
        storageReadBlocked=false;
        if(!File.Exists(path))return;
        try{
            using var file=JsonDocument.Parse(File.ReadAllText(path));
            if(file.RootElement.GetProperty("schemaVersion").GetInt32()!=1)throw new InvalidDataException("Unsupported account file.");
            byte[] clear=ProtectedData.Unprotect(Convert.FromBase64String(file.RootElement.GetProperty("protected").GetString()!),Entropy,DataProtectionScope.CurrentUser);
            var loaded=JsonSerializer.Deserialize<Vault>(clear)??new();CryptographicOperations.ZeroMemory(clear);
            loaded.Email??="";loaded.RefreshToken??="";loaded.RotationSecret??="";
            if(loaded.Pending is PendingSignIn p){
                if(p.RequestID==null||p.PollSecret==null||p.Verifier==null||p.Email==null)loaded.Pending=null;
                else{
                    // Bound saved times so a malformed or pre-deadline file cannot crash startup or poll forever.
                    if(p.ExpiresAt<1||p.ExpiresAt>4102444800)p.ExpiresAt=0;
                    long now=Now;if(p.LocalDeadline<=0||p.LocalDeadline>now+MaxTtl)p.LocalDeadline=now+MaxTtl;
                    p.DeviceName??="";
                    // An older native request was already emailed; never extend or hand it off.
                    if(p.DeviceName==""){p.EmailSent=true;p.Extended=true;}
                    if(p.HandoffExpiresAt<=0||p.HandoffExpiresAt>p.LocalDeadline)p.HandoffExpiresAt=p.LocalDeadline;
                }
            }
            vault=loaded;
        }catch(Exception ex) when(ex is JsonException or CryptographicException or FormatException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException){
            // Keep the unreadable original for diagnosis; never delete saved data.
            try{File.Move(path,path+".invalid-"+Guid.NewGuid().ToString("N"));}
            catch(Exception moveError) when(moveError is IOException or UnauthorizedAccessException){storageReadBlocked=true;}
            MarketSync.Log("Account: saved sign-in unreadable ("+ex.GetType().Name+"); signed out locally.");
            vault=new();Status=storageReadBlocked?BlockedText:"The saved sign-in could not be opened by this Windows account. Send a new sign-in link.";return;
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
            MarketSync.Log("Account: saved sign-in could not be read ("+ex.GetType().Name+").");
            storageReadBlocked=true;vault=new();Status=BlockedText;return;
        }
        Status=State switch{
            AccountState.SignedIn=>"Signed in as "+vault.Email+" · confirming the session with the server…",
            AccountState.Pending=>PendingText(vault.Pending!),_=>SignedOutText};
    }
    const string BlockedText="The saved sign-in file could not be read right now, so uploads stay paused and no new link will be sent. Close anything using it or restart the app, then try again.";
    void Persist()
    {
        if(storageReadBlocked)throw new IOException("Saved sign-in is unreadable; it is not overwritten.");
        byte[] clear=JsonSerializer.SerializeToUtf8Bytes(vault);
        try{
            string blob=Convert.ToBase64String(ProtectedData.Protect(clear,Entropy,DataProtectionScope.CurrentUser));
            MarketSync.Atomic(path,JsonSerializer.Serialize(new{schemaVersion=1,@protected=blob}));
        }finally{CryptographicOperations.ZeroMemory(clear);}
    }
    internal static string RandomSecret()=>Base64Url(RandomNumberGenerator.GetBytes(32));
    internal static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    internal static string Challenge(string verifier)=>Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static string PollHash(string pollSecret)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pollSecret))).ToLowerInvariant();
    internal static string DeviceName()
    {
        string name="ForeverFlip desktop · "+new string(Environment.MachineName.Where(c=>!char.IsControl(c)).ToArray());
        return name.Length>80?name[..80]:name;
    }
    // Matches the backend's normalization (trim, lowercase).
    internal static bool SameEmail(string a,string b)=>string.Equals(a.Trim().ToLowerInvariant(),b.Trim().ToLowerInvariant(),StringComparison.Ordinal);
    public static bool ValidEmail(string email)=>email.Length is >=3 and <=254&&Regex.IsMatch(email,@"^[^\s@]{1,64}@[^\s@]+\.[^\s@]+$")&&!email.Any(char.IsControl);
    // Server detail is untrusted text: bound it and strip addresses/token-like runs.
    internal static string? Sanitize(string? detail)
    {
        if(string.IsNullOrWhiteSpace(detail))return null;
        string text=new(detail.Where(c=>!char.IsControl(c)).Take(400).ToArray());
        text=Regex.Replace(text,@"[^\s@]+@[^\s@]+","[email]");
        text=Regex.Replace(text,@"[A-Za-z0-9_\-\.=+/]{24,}","[redacted]");
        text=text.Trim();return text.Length==0?null:text.Length>200?text[..200]+"…":text;
    }
    static string? Detail(JsonDocument? body)=>body!=null&&body.RootElement.ValueKind==JsonValueKind.Object&&body.RootElement.TryGetProperty("detail",out var d)&&d.ValueKind==JsonValueKind.String?Sanitize(d.GetString()):null;
    static string Field(JsonElement root,string name,int max=4096)
    {
        if(!root.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Missing account field.");
        string text=value.GetString()!;
        if(text.Length==0||text.Length>max||text.Any(char.IsControl))throw new InvalidDataException("Invalid account field.");
        return text;
    }
    static long Time(JsonElement root,string name)
    {
        if(!root.TryGetProperty(name,out var value))throw new InvalidDataException("Missing account time.");
        long at=value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out long seconds)?seconds>100000000000?seconds/1000:seconds
            :value.ValueKind==JsonValueKind.String&&DateTimeOffset.TryParse(value.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed)?parsed.ToUnixTimeSeconds()
            :throw new InvalidDataException("Invalid account time.");
        if(at<1||at>4102444800)throw new InvalidDataException("Invalid account time.");
        return at;
    }
    static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry=response.Headers.RetryAfter;
        return retry?.Delta??(retry?.Date is DateTimeOffset at?at-DateTimeOffset.UtcNow:null);
    }
    async Task<(HttpStatusCode Code,JsonDocument? Body,TimeSpan? Retry)> Send(string route,object body,string? bearer=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,service+route){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};
        if(bearer!=null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
        using var response=await http.SendAsync(request);
        string text=await response.Content.ReadAsStringAsync();JsonDocument? parsed=null;
        try{if(text.Length is >0 and <=65536)parsed=JsonDocument.Parse(text);}catch(JsonException){}
        return (response.StatusCode,parsed,RetryAfter(response));
    }
    static bool Success(HttpStatusCode code)=>(int)code is >=200 and <=299;
    static bool Network(Exception ex)=>ex is HttpRequestException or TaskCanceledException;
    static bool Storage(Exception ex)=>ex is IOException or UnauthorizedAccessException or CryptographicException;
    const string StorageFailure="This PC could not update its protected sign-in file.";
    string SessionOnly(string email)=>"Signed in as "+email+" for this session, but this PC could not update its protected sign-in file. You may need to sign in again after restarting.";
    // Prepares a sign-in locally (no network) and saves its proof with DPAPI before the
    // caller opens PendingVerificationUrl. True means prepared, not that an email was sent.
    public Task<bool> RequestConnectAsync()=>PrepareSignInAsync("",true);
    public Task<bool> RequestSignInAsync(string email)=>PrepareSignInAsync(email,false);
    async Task<bool> PrepareSignInAsync(string email,bool pairing)
    {
        email=email.Trim();
        if(!pairing&&!ValidEmail(email)){Status="Enter a valid email address to receive a sign-in link.";Changed?.Invoke();return false;}
        if(!await gate.WaitAsync(0))return false;
        bool prepared=false;
        try{
            if(storageReadBlocked){
                // Retry the saved file first; a readable stored account resumes instead of being replaced.
                Load();
                if(storageReadBlocked){Status=BlockedText;return false;}
                if(State!=AccountState.SignedOut)return false;
            }
            if(State!=AccountState.SignedOut){Status=State==AccountState.SignedIn?"Already signed in as "+vault.Email+". Sign out first to use another email.":"A sign-in is already waiting. Cancel it before starting another.";return false;}
            long now=Now;
            ConnectionNotice="";
            var pending=new PendingSignIn{RequestID=Guid.NewGuid().ToString("D"),PollSecret=RandomSecret(),Verifier=RandomSecret(),Email=email,
                DeviceName=DeviceName(),LocalDeadline=now+MaxTtl,HandoffExpiresAt=now+MaxTtl,Pairing=pairing};
            vault.Pending=pending;Persist();
            nextPollAt=now+5;pollFailures=0;NetworkProblem=false;SignOutFailed=false;Status=PendingText(pending);prepared=true;
            MarketSync.Log("Account: sign-in prepared for the browser.");
        }catch(Exception ex) when(Storage(ex)){
            vault.Pending=null;Status=StorageFailure+" The sign-in was not started; try again.";MarketSync.Log("Account request storage: "+ex.GetType().Name);
        }finally{gate.Release();Changed?.Invoke();}
        return prepared;
    }
    // The browser could not be launched. The saved request stays usable through the reopen action.
    public void ReportBrowserOpenFailed()
    {
        if(vault.Pending is not PendingSignIn p||PendingVerificationUrl==null)return;
        Status="Your browser could not be opened. Use Open sign-in page to try again; this sign-in stays ready until "+Local(p.LocalDeadline)+".";
        MarketSync.Log("Account: browser could not be opened for sign-in.");Changed?.Invoke();
    }
    public async Task PollAsync()
    {
        if(vault.Pending==null||Now<nextPollAt||!await gate.WaitAsync(0))return;
        bool changed=false;
        try{
            var p=vault.Pending;if(p==null)return;long now=Now;
            if(now>=p.LocalDeadline){
                vault.Pending=null;changed=true;
                Status=p.EmailSent?"The sign-in link expired before it was confirmed. Send a new link when you are ready."
                    :"The browser sign-in step was not completed in time, so no link was sent for this request. Start sign-in again when you are ready.";
                Persist();return;
            }
            // A browser approval must return to THIS PC before its local proof is used.
            // Polling an approval before that callback would allow remote pairing phishing.
            if(p.Pairing&&!p.PairSubmitted)return;
            var (code,body,retry)=await Send("/api/v1/auth/desktop-poll",new{requestID=p.RequestID,pollSecret=p.PollSecret,verifier=p.Verifier});
            using(body){
                string status=body!=null&&body.RootElement.ValueKind==JsonValueKind.Object&&body.RootElement.TryGetProperty("status",out var s)&&s.ValueKind==JsonValueKind.String?s.GetString()!:"";
                if(code==HttpStatusCode.Accepted||code==HttpStatusCode.OK&&status=="pending"){
                    pollFailures=0;nextPollAt=now+5;
                    if(body?.RootElement is {ValueKind:JsonValueKind.Object} pendingRoot&&pendingRoot.TryGetProperty("emailSent",out var emailSent)&&emailSent.ValueKind==JsonValueKind.True&&!p.EmailSent){
                        // First authenticated confirmation that the website sent the link: extend once, bounded to 15 minutes.
                        long received=Now;p.EmailSent=true;
                        if(!p.Extended){p.Extended=true;p.LocalDeadline=received+Ttl(pendingRoot,"expiresIn");}
                        try{p.ExpiresAt=pendingRoot.TryGetProperty("expiresAt",out _)?Time(pendingRoot,"expiresAt"):0;}catch(InvalidDataException){p.ExpiresAt=0;}
                        NetworkProblem=false;Status=PendingText(p);changed=true;
                        MarketSync.Log("Account: sign-in link sent by the website.");Persist();
                    }else if(NetworkProblem){NetworkProblem=false;Status=PendingText(p);changed=true;}
                }else if(code==HttpStatusCode.OK&&status=="signed_in"){
                    var root=body!.RootElement;
                    string email=Field(root,"email",254);
                    if(!(p.Pairing&&p.PairSubmitted&&p.Email=="")&&!SameEmail(email,p.Email)){
                        // A copied handoff can be completed for another address. Never adopt that account's
                        // credentials; end this request honestly and never resend.
                        vault.Pending=null;changed=true;
                        Status="This sign-in was completed for a different email address than "+p.Email+", so it was not accepted on this PC. Start sign-in again when you are ready.";
                        MarketSync.Log("Account poll ended: account email did not match the request.");Persist();return;
                    }
                    string refresh=Field(root,"refreshToken"),token=Field(root,"accessToken");long received=Now;
                    vault=new Vault{Email=email,RefreshToken=refresh};
                    access=token;accessExpiresAt=received+Ttl(root,"accessExpiresIn");lastRenewedAt=received;nextMaintainAt=now+12*3600;maintainFailures=0;
                    NetworkProblem=false;SignOutFailed=false;changed=true;Status="Signed in as "+email+".";
                    MarketSync.Log("Account: signed in.");Persist();
                }else if(code is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone){
                    // Terminal, including 409 (device limit or already completed): show the server's reason, never resend.
                    vault.Pending=null;changed=true;
                    Status=(Detail(body)??"This sign-in request is no longer valid (expired, cancelled or already used).")+" Send a new link when you are ready.";
                    MarketSync.Log("Account poll ended: HTTP "+(int)code);Persist();
                }else if(code==HttpStatusCode.TooManyRequests){
                    nextPollAt=now+(long)Math.Clamp((retry??TimeSpan.FromSeconds(30)).TotalSeconds,5,300);
                }else{
                    pollFailures++;nextPollAt=now+Math.Min(60,5*(1+pollFailures));NetworkProblem=true;changed=true;
                    Status="Waiting for link confirmation. The account service returned HTTP "+(int)code+"; still checking.";
                }
            }
        }catch(Exception ex) when(Network(ex)){
            pollFailures++;nextPollAt=Now+Math.Min(60,5*(1+pollFailures));NetworkProblem=true;changed=true;
            Status="Waiting for link confirmation. Could not reach the account service; still checking.";
        }catch(Exception ex) when(ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException){
            pollFailures++;nextPollAt=Now+Math.Min(60,5*(1+pollFailures));changed=true;
            Status="Waiting for link confirmation. The account service sent an unexpected response; still checking.";MarketSync.Log("Account poll: "+ex.GetType().Name);
        }catch(Exception ex) when(Storage(ex)){
            changed=true;Status=State==AccountState.SignedIn?SessionOnly(vault.Email):StorageFailure;
            MarketSync.Log("Account poll storage: "+ex.GetType().Name);
        }finally{gate.Release();if(changed)Changed?.Invoke();}
    }
    public async Task CancelPendingAsync()
    {
        await gate.WaitAsync();
        try{
            if(vault.Pending==null)return;
            vault.Pending=null;NetworkProblem=false;Status="Sign-in request cancelled. Finishing the browser page or opening an emailed link will not sign in this app.";Persist();
        }catch(Exception ex) when(Storage(ex)){Status=StorageFailure+" The request is cancelled for this session.";}
        finally{gate.Release();Changed?.Invoke();}
    }
    // Caller holds gate. Persists the rotation secret before sending so a lost
    // response is retried with exactly the same prior token and secret.
    async Task<Renewal> RenewCore()
    {
        if(vault.RefreshToken=="")return Renewal.Ended;
        // A saved secret means an earlier renewal response was lost; the server
        // only replays it briefly, then revokes the prior token.
        bool retryingLost=vault.RotationSecret!="";
        try{
            if(vault.RotationSecret==""){vault.RotationSecret=RandomSecret();Persist();}
        }catch(Exception ex) when(Storage(ex)){
            vault.RotationSecret="";NetworkProblem=false;Status="Signed in as "+vault.Email+", but this PC could not update its protected sign-in file. Uploads wait until the session can be renewed safely.";
            MarketSync.Log("Account renewal storage: "+ex.GetType().Name);return Renewal.Unavailable;
        }
        try{
            var (code,body,_)=await Send("/api/v1/auth/refresh",new{refreshToken=vault.RefreshToken,rotationSecret=vault.RotationSecret});
            using(body){
                if(Success(code)&&body!=null){
                    var root=body.RootElement;
                    string refresh=Field(root,"refreshToken"),token=Field(root,"accessToken");long received=Now;
                    // Older servers omit email; when present it must still be this account.
                    string email=root.TryGetProperty("email",out _)?Field(root,"email",254):vault.Email;
                    if(vault.Email!=""&&!SameEmail(email,vault.Email)){
                        vault=new Vault();access=null;accessExpiresAt=0;NetworkProblem=false;SignOutFailed=false;
                        Status="Your sign-in ended because the server returned a different account during renewal. Its credentials were not used. Send a new sign-in link to resume uploads.";
                        MarketSync.Log("Account renewal rejected: account email changed.");
                        try{Persist();}catch(Exception ex) when(Storage(ex)){MarketSync.Log("Account clear storage: "+ex.GetType().Name);}
                        return Renewal.Ended;
                    }
                    vault.RefreshToken=refresh;vault.RotationSecret="";vault.Email=email;
                    // Freshness uses the PC clock from receipt, never the server's absolute time.
                    access=token;accessExpiresAt=received+Ttl(root,"accessExpiresIn");lastRenewedAt=received;NetworkProblem=false;
                    Status="Signed in as "+email+".";
                    try{Persist();}catch(Exception ex) when(Storage(ex)){
                        Status=SessionOnly(email);
                        MarketSync.Log("Account renewal storage: "+ex.GetType().Name);
                    }
                    return Renewal.Renewed;
                }
                if(code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden){
                    // Rejected session: clear local credentials, never resend an email automatically.
                    vault=new Vault();access=null;accessExpiresAt=0;NetworkProblem=false;SignOutFailed=false;
                    Status=retryingLost
                        ?"Your sign-in ended: a session renewal was interrupted by a network failure and could not be completed in time, so the server closed the session for safety. Send a new sign-in link to resume uploads."
                        :"Your sign-in ended (expired after 90 days without use, signed out elsewhere, or revoked). Send a new sign-in link to resume uploads.";
                    MarketSync.Log("Account renewal rejected: HTTP "+(int)code);
                    try{Persist();}catch(Exception ex) when(Storage(ex)){MarketSync.Log("Account clear storage: "+ex.GetType().Name);}
                    return Renewal.Ended;
                }
                NetworkProblem=true;
                Status="Signed in as "+vault.Email+" · the session could not be renewed (HTTP "+(int)code+(Detail(body) is string d?": "+d:"")+"). Uploads wait and retry automatically.";
                MarketSync.Log("Account renewal: HTTP "+(int)code);return Renewal.Unavailable;
            }
        }catch(Exception ex) when(Network(ex)){
            NetworkProblem=true;Status="Signed in as "+vault.Email+" · offline: could not reach the account service. Uploads wait and retry; your session is kept.";
            MarketSync.Log("Account renewal: "+ex.GetType().Name);return Renewal.Unavailable;
        }catch(Exception ex) when(ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException){
            NetworkProblem=true;Status="Signed in as "+vault.Email+" · the account service sent an unexpected renewal response. Retrying automatically.";
            MarketSync.Log("Account renewal: "+ex.GetType().Name);return Renewal.Unavailable;
        }
    }
    // Null means signed out. A signed-in session that cannot be renewed throws,
    // so callers keep uploads pending without mistaking an outage for sign-out.
    internal async Task<string?> AccessTokenAsync()
    {
        if(State!=AccountState.SignedIn)return null;
        if(Fresh)return access;
        await gate.WaitAsync();
        Renewal result;
        try{
            if(State!=AccountState.SignedIn)return null;
            if(Fresh)return access;
            result=await RenewCore();
            if(result==Renewal.Renewed)return access;
        }finally{gate.Release();Changed?.Invoke();}
        if(result==Renewal.Ended)return null;
        throw new MarketServiceException("Signed in, but the account session could not be renewed right now. Uploads wait and retry automatically; saved data stays local.");
    }
    // Write guard: a copied token stops being usable once the session is removed or renewed.
    internal bool IsCurrentAccess(string token)=>State==AccountState.SignedIn&&access is string current&&current==token;
    // A request was rejected with this token; renew before the next use.
    internal void InvalidateAccess(string token)=>Interlocked.CompareExchange(ref access,null,token);
    // Confirms the saved session at startup and keeps it active (90-day inactivity window).
    public async Task MaintainAsync()
    {
        if(State!=AccountState.SignedIn||Now<nextMaintainAt||!await gate.WaitAsync(0))return;
        try{
            if(State!=AccountState.SignedIn)return;
            if(Fresh&&Now-lastRenewedAt<12*3600){nextMaintainAt=lastRenewedAt+12*3600;return;}
            var result=await RenewCore();
            if(result==Renewal.Unavailable){maintainFailures=Math.Min(maintainFailures+1,6);nextMaintainAt=Now+Math.Min(900,30L<<maintainFailures);}
            else{maintainFailures=0;nextMaintainAt=Now+12*3600;}
        }finally{gate.Release();Changed?.Invoke();}
    }
    public async Task SignOutAsync(bool allDevices)
    {
        await gate.WaitAsync();
        try{
            if(State!=AccountState.SignedIn)return;
            Status=allDevices?"Signing out on all devices…":"Signing out…";Changed?.Invoke();
            string route=allDevices?"/api/v1/auth/logout-all":"/api/v1/auth/logout";
            for(int attempt=0;attempt<2;attempt++){
                if(!Fresh){
                    var renewal=await RenewCore();
                    if(renewal==Renewal.Ended){
                        Status="This PC's session had already ended on the server, so its sign-in was removed."+(allDevices?" Other devices were not confirmed signed out.":"");return;
                    }
                    if(renewal==Renewal.Unavailable){SignOutFailed=true;Status=SignOutFailure(allDevices);return;}
                }
                var (code,body,_)=await Send(route,new{},access);body?.Dispose();
                if(Success(code)){
                    ClearLocal();Status=allDevices?"Signed out on all devices.":"Signed out on this PC.";MarketSync.Log("Account: signed out"+(allDevices?" on all devices.":"."));
                    try{Persist();}catch(Exception ex) when(Storage(ex)){Status+=" "+StorageFailure+" The revoked sign-in may remain on disk but cannot be used.";}
                    return;
                }
                if(code==HttpStatusCode.Unauthorized&&attempt==0){access=null;continue;}
                MarketSync.Log("Account sign-out: HTTP "+(int)code);break;
            }
            SignOutFailed=true;Status=SignOutFailure(allDevices);
        }catch(Exception ex) when(Network(ex)){
            SignOutFailed=true;Status=SignOutFailure(allDevices);MarketSync.Log("Account sign-out: "+ex.GetType().Name);
        }finally{gate.Release();Changed?.Invoke();}
    }
    static string SignOutFailure(bool allDevices)=>"Sign-out was not confirmed by the server, so you are still signed in"+(allDevices?" and other devices were not signed out":"")+". Try again, or use Remove from this PC only.";
    void ClearLocal(){vault=new Vault();access=null;accessExpiresAt=0;lastRenewedAt=0;NetworkProblem=false;SignOutFailed=false;}
    // Explicit local-only removal when the server cannot be reached. Honest: no revoke happened.
    public async Task ForgetLocalAsync()
    {
        await gate.WaitAsync();
        try{
            if(State!=AccountState.SignedIn)return;
            ClearLocal();
            Status="Sign-in removed from this PC only. The server session was not revoked; it ends after 90 days without use, or sign out all devices from the website.";
            MarketSync.Log("Account: local sign-in removed without server revoke.");Persist();
        }catch(Exception ex) when(Storage(ex)){Status+=" "+StorageFailure;}
        finally{gate.Release();Changed?.Invoke();}
    }
}
