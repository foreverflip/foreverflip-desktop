using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ForeverFlip.Desktop;

public sealed partial class AccountSession
{
    public string ConnectionProgress=>State==AccountState.SignedIn?"Connected":vault.Pending?.Pairing==true
        ?vault.Pending.PairSubmitted?"Connecting this app…":"1  Browser open   →   2  Sign in & connect   →   3  Ready"
        :vault.Pending?.EmailSent==true?"Check your email, then return here":"Continue in your browser";
    public string RegistrationStatus {get;private set;}="Checking registration availability…";
    public bool? RegistrationOpen {get;private set;}
    public string ConnectionNotice {get;private set;}="";
    long nextAvailabilityAt;
    int availabilityBusy;
    public async Task RefreshAvailabilityAsync()
    {
        if(Now<nextAvailabilityAt||Interlocked.Exchange(ref availabilityBusy,1)!=0)return;
        try{
            nextAvailabilityAt=Now+60;
            using var response=await http.GetAsync(service+"/api/v1/auth/availability");
            response.EnsureSuccessStatusCode();using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root=body.RootElement;bool open=root.GetProperty("registrationOpen").GetBoolean();
            bool mail=root.GetProperty("emailAvailable").GetBoolean();int remaining=root.GetProperty("registrationsRemaining").GetInt32();
            RegistrationOpen=open;
            RegistrationStatus=open?"Registration open · "+Math.Clamp(remaining,0,30)+" of 30 places left today"
                :!mail?"Registration closed · email sign-in temporarily unavailable"
                :root.TryGetProperty("reason",out var reason)&&reason.GetString()=="paused"?"New registrations paused · existing accounts can sign in"
                :"Registration closed today · existing accounts can sign in · resets midnight Chicago time";
        }catch(Exception ex) when(Network(ex)||ex is JsonException or InvalidOperationException or KeyNotFoundException){
            RegistrationOpen=null;RegistrationStatus="Registration availability unavailable · you can still try signing in";
        }finally{Interlocked.Exchange(ref availabilityBusy,0);Changed?.Invoke();}
    }
    public async Task<bool> CompleteConnectionAsync(DesktopActivation activation)
    {
        if(activation.Action!="pair-done")return false;
        if(!await gate.WaitAsync(TimeSpan.FromSeconds(3))){ConnectionNotice="Your sign-in is busy. Choose Connect desktop app again when it finishes checking.";Changed?.Invoke();return false;}
        bool accepted=false;
        try{
            var p=vault.Pending;
            if(p==null||!p.Pairing||p.RequestID!=activation.RequestID||Now>=p.LocalDeadline){
                ConnectionNotice="This was for an older sign-in. Use the newest browser tab, or start sign-in again in this app.";return false;
            }
            ConnectionNotice="";
            // Persist receipt before sending. If the response is lost, the app can
            // settle the outcome with its private poll proof, without another email.
            p.PairSubmitted=true;Persist();Status="Connecting this app…";
            var (code,body,_)=await Send("/api/v1/auth/pair/complete",new{requestID=p.RequestID,pollSecret=p.PollSecret,verifier=p.Verifier,completionCode=activation.Code});
            using(body){
                if(code==HttpStatusCode.OK&&body?.RootElement.TryGetProperty("bound",out var bound)==true&&bound.ValueKind==JsonValueKind.True){
                    p.Email=Field(body.RootElement,"email",254);accepted=true;Status="Connected on the website. Finishing sign-in…";Persist();
                }else if(code is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone){
                    vault.Pending=null;ConnectionNotice=Status="The app connection expired or was rejected. Try again to open a fresh sign-in.";Persist();
                }else{Status="Checking whether this app connected. Keep the app open.";}
            }
            nextPollAt=0;
        }catch(Exception ex) when(Network(ex)||Storage(ex)||ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException){
            Status="The connection was interrupted. Keep the app open while it checks your sign-in.";
        }finally{gate.Release();Changed?.Invoke();}
        await PollAsync();return accepted||State==AccountState.SignedIn;
    }
}
