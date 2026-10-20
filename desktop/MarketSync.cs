using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

public enum MarketSyncState {NeedsSetup,Waiting,Reading,Uploading,Synced,SharingOff,Error}
public enum ResearchAvailability {None,Downloaded,Ready,Loaded,Expired,Scheduled}
public sealed record ResearchPack(long CreatedAt,long ExpiresAt,bool Staged,string PackID,string Scope);
public sealed class MarketServiceException(string message,HttpStatusCode? statusCode=null,TimeSpan? retryAfter=null):Exception(message)
{
    public HttpStatusCode? StatusCode {get;}=statusCode;
    public TimeSpan? RetryAfter {get;}=retryAfter;
}
public sealed record UploadRejection(string Signature,string Reason,long RetryAt);

public sealed class Settings
{
    public void Normalize()
    {
        // One sharing choice covers non-personal market data. Keep the old
        // fields readable for downgrade compatibility; no upgrade popup.
        ShareCatalog=ShareMarket;CatalogNoticeAccepted=NoticeAccepted;
        // Valid JSON can still contain nulls. Repair individual fields without
        // discarding credentials or the remaining saved configuration.
        foreach(var property in typeof(Settings).GetProperties()){
            if(property.PropertyType==typeof(string)&&property.GetValue(this)==null)property.SetValue(this,"");
        }
        MarketIDs=(MarketIDs??[]).Where(m=>m!=null&&Regex.IsMatch(m,@"^ff1-[a-f0-9]{32}$")).Distinct().ToList();
        MarketPackIDs=Clean(MarketPackIDs);ItemFingerprints=Clean(ItemFingerprints);ResearchFingerprints=Clean(ResearchFingerprints);
        RealmFingerprints=Clean(RealmFingerprints);CatalogFingerprints=Clean(CatalogFingerprints);
        UploadRejections=(UploadRejections??[]).Where(p=>Regex.IsMatch(p.Key,@"^(?:catalog:ff1-[a-f0-9]{32}|batch:ff1-[a-f0-9]{32}:[a-f0-9]{64})$")
            &&p.Value!=null&&p.Value.Signature!=null&&p.Value.Signature.Length<=100&&p.Value.Reason!=null
            &&p.Value.Reason.Length<=500&&p.Value.RetryAt>=0&&p.Value.RetryAt<=4102444800).Take(100).ToDictionary(p=>p.Key,p=>p.Value);
        if(UploadRetryAt<0||UploadRetryAt>4102444800)UploadRetryAt=0;
        if(UploadRetryReason.Length>500)UploadRetryReason=UploadRetryReason[..500];
    }
    static Dictionary<string,string> Clean(Dictionary<string,string>? source)=>
        (source??[]).Where(p=>p.Value!=null).ToDictionary(p=>p.Key,p=>p.Value);
    public string ClientRoot {get;set;}="";
    public string SavedFile {get;set;}="";
    public bool ShareMarket {get;set;}=true;
    public bool NoticeAccepted {get;set;}
    public bool StartWithWindows {get;set;}
    public bool PendingAddonInstall {get;set;}
    public bool PendingAddonRepair {get;set;}
    public string ProtectedToken {get;set;}="";
    public string ProtectedDiagnosticsToken {get;set;}="";
    public string LastSignature {get;set;}="";
    public string LastResearchSignature {get;set;}="";
    public string LastLocalRead {get;set;}="";
    public string LastUploaded {get;set;}="";
    public string LastPack {get;set;}="";
    public List<string> MarketIDs {get;set;}=[];
    public Dictionary<string,string> MarketPackIDs {get;set;}=[];
    public Dictionary<string,string> ItemFingerprints {get;set;}=[];
    public Dictionary<string,string> RealmFingerprints {get;set;}=[];
    public string LoadedPack {get;set;}="";
    public string LoadedPackScope {get;set;}="";
    public string LoadedAddonVersion {get;set;}="";
    public int? SavedItemCount {get;set;}
    public int? SavedObservationCount {get;set;}
    public string MarketLabel {get;set;}="";
    public Dictionary<string,string> CatalogFingerprints {get;set;}=[];
    public int RecipesSynced {get;set;}
    public int PatternsSynced {get;set;}
    public Dictionary<string,string> ResearchFingerprints {get;set;}=[];
    public Dictionary<string,UploadRejection> UploadRejections {get;set;}=[];
    public long UploadRetryAt {get;set;}
    public string UploadRetryReason {get;set;}="";
    public bool ShareCatalog {get;set;}=true;
    public bool CatalogNoticeAccepted {get;set;}
}

public sealed partial class MarketSync
{
    DateTime nextResearchFeedbackAt=DateTime.MinValue;
    string researchFeedbackWarning="";
    public const string Service="https://forever.blahh.us";
    static readonly string DefaultStateRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ForeverFlip");
    internal static string? TestStateRoot {get;set;}
    public static string StateRoot=>TestStateRoot??DefaultStateRoot;
    public Settings Config {get;}
    readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(35),MaxResponseContentBufferSize=8*1024*1024};
    readonly SemaphoreSlim gate=new(1);
    internal Func<string,GameSaveState> SaveProcessProbe {get;set;}=SavedGameFile.Probe;
    public string Status {get;private set;}="Choose your WoW client to begin.";
    public string SavedStatus {get;private set;}="Not read yet";
    public string PackStatus {get;private set;}="Checking downloaded AI packs…";
    public string SupportStatus {get;private set;}="Use Report issue / feedback in addon Settings. Reports include private diagnostic logs.";
    readonly string packCacheRoot;
    readonly string packPublicKey;
    readonly Dictionary<string,string> packStates=[];
    readonly Dictionary<string,ResearchPack> researchPacks=[];
    public static ResearchAvailability ClassifyResearch(long createdAt,long expiresAt,bool staged,bool loaded,long? clock=null)
    {
        long now=clock??DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if(createdAt<1||expiresAt<=createdAt||expiresAt-createdAt>86400)return ResearchAvailability.None;
        if(now<createdAt)return ResearchAvailability.Scheduled;
        if(now>=expiresAt)return ResearchAvailability.Expired;
        return staged?(loaded?ResearchAvailability.Loaded:ResearchAvailability.Ready):ResearchAvailability.Downloaded;
    }
    ResearchAvailability Phase(string market)
    {
        if(!researchPacks.TryGetValue(market,out var pack))return ResearchAvailability.None;
        return ClassifyResearch(pack.CreatedAt,pack.ExpiresAt,pack.Staged,
            pack.PackID==Config.LoadedPack&&pack.Scope==Config.LoadedPackScope);
    }
    // The addon acknowledges the last character's market, not every market retained in the account database.
    public bool PackLoadConfirmed=>Config.MarketIDs.Any(m=>Phase(m)==ResearchAvailability.Loaded);
    public bool ResearchPending=>!PackLoadConfirmed&&Config.MarketIDs.Any(m=>Phase(m) is ResearchAvailability.Downloaded or ResearchAvailability.Ready or ResearchAvailability.Scheduled);
    public string ResearchIndicator
    {
        get
        {
            var phases=Config.MarketIDs.Select(Phase).ToArray();
            if(PackLoadConfirmed)return "Research loaded (last game save)";
            if(phases.Contains(ResearchAvailability.Ready))return "Research ready after /reload";
            if(phases.Contains(ResearchAvailability.Downloaded))return "Research downloaded; syncing";
            if(phases.Contains(ResearchAvailability.Scheduled))return "Research waiting for its start time";
            return phases.Contains(ResearchAvailability.Expired)?"Research expired":"No current research pack";
        }
    }
    public MarketSyncState State {get;private set;}=MarketSyncState.NeedsSetup;
    public bool IsBusy=>gate.CurrentCount==0;
    public event Action? Changed;
    public IReadOnlyList<ProfitBook> ProfitBooks {get;private set;}=[];
    public MarketSync(Settings? config=null,string? cacheRoot=null,string? publicKeyPath=null)
    {
        packCacheRoot=cacheRoot??Path.Combine(StateRoot,"packs");
        packPublicKey=publicKeyPath??Path.Combine(AppContext.BaseDirectory,"Assets","pack-public.pem");
        if(config!=null)Config=config;
        else{
            Directory.CreateDirectory(StateRoot);
            Config=LoadSettings(Path.Combine(StateRoot,"settings.json"));
        }
        Config.Normalize();RestorePackStatus();
    }
    internal MarketSync(Settings config,string cacheRoot,string? publicKeyPath,HttpMessageHandler handler):this(config,cacheRoot,publicKeyPath)
    {
        http.Dispose();http=new(handler){Timeout=TimeSpan.FromSeconds(35),MaxResponseContentBufferSize=8*1024*1024};
    }
    internal static Settings LoadSettings(string path)
    {
        if(!File.Exists(path))return new();
        string raw;
        try{raw=File.ReadAllText(path);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Log("Settings could not be read; setup remains disabled until accessible.");return new();}
        Settings config=new();bool invalid=false;
        try{
            var node=System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject;
            if(node==null)invalid=true;
            else{
                // Recover valid fields independently when one field has a
                // wrong type. Never log protected credentials or raw settings.
                var repaired=new System.Text.Json.Nodes.JsonObject();
                foreach(var property in typeof(Settings).GetProperties()){
                    if(!node.TryGetPropertyValue(property.Name,out var value))continue;
                    if(value==null){invalid=true;continue;}
                    try{var parsed=JsonSerializer.Deserialize(value.ToJsonString(),property.PropertyType);
                        repaired[property.Name]=JsonSerializer.SerializeToNode(parsed,property.PropertyType);}
                    catch(JsonException){invalid=true;}
                }
                config=repaired.Deserialize<Settings>()??new();
            }
        }catch(JsonException){invalid=true;}
        config.Normalize();
        if(invalid){
            // Preserve the original before a later Save can replace it.
            File.Copy(path,path+".invalid-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmss")+"-"+Guid.NewGuid().ToString("N"),false);
            Log("Recovered invalid settings fields; original retained locally.");
        }
        return config;
    }
    void RestorePackStatus()
    {
        packStates.Clear();
        researchPacks.Clear();
        foreach(string market in Config.MarketIDs.Distinct().Where(m=>Regex.IsMatch(m,@"^ff1-[a-f0-9]{32}$"))){
            try{
                using var envelope=JsonDocument.Parse(File.ReadAllText(Path.Combine(packCacheRoot,market+".json")));
                // Retain verified expired timestamps for status only; Verify still rejects them for downloads/staging.
                var payload=VerifySignature(envelope.RootElement,packPublicKey);
                if(payload.GetProperty("schema").GetString()!="foreverflip.market-pack"||payload.GetProperty("schemaVersion").GetInt32()!=1)throw new InvalidDataException("Unsupported pack schema.");
                if(payload.GetProperty("marketID").GetString()!=market)throw new InvalidDataException("Pack market mismatch.");
                bool staged=IsPackStaged(payload);
                packStates[market]=DescribePack(payload,staged,Config.LoadedPackScope==payload.GetProperty("scope").GetString()?Config.LoadedPack:"");
                researchPacks[market]=new(payload.GetProperty("createdAt").GetInt64(),payload.GetProperty("expiresAt").GetInt64(),staged,payload.GetProperty("packID").GetString()!,payload.GetProperty("scope").GetString()!);
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or CryptographicException or FormatException or KeyNotFoundException or InvalidOperationException){
                packStates[market]="No valid cached AI pack. Sync will check for new advice.";
            }
        }
        RefreshPackStatus();
    }
    void RefreshPackStatus()=>PackStatus=packStates.Count==0?"No verified AI pack yet. Sync will check after setup.":string.Join(Environment.NewLine,packStates.Values);
    bool IsPackStaged(JsonElement payload)
    {
        string dir=Path.Combine(Config.ClientRoot,"Interface","AddOns","ForeverFlip_Data");
        try{return SavedGameFile.Read(Path.Combine(dir,"Data.lua")).Text.Contains(RenderPackEntry(payload),StringComparison.Ordinal)
            &&SavedGameFile.Read(Path.Combine(dir,"ForeverFlip_Data.toc")).Text.Split('\n').Any(line=>line.Trim()=="Data.lua");}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return false;}
    }
    public static string DescribePack(JsonElement payload,bool staged,string loadedPack,long? clock=null)
    {
        long now=clock??DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string id=payload.GetProperty("packID").GetString()!,scope=payload.GetProperty("scope").GetString()!;
        string[] parts=scope.Split(':');string label=parts.Length==5?parts[3]+" · "+parts[4]:"Market";
        if(now<payload.GetProperty("createdAt").GetInt64())return label+": AI pack downloaded; waiting for its start time.";
        if(now>=payload.GetProperty("expiresAt").GetInt64())return label+": AI advice expired; native searches continue.";
        var counts=new Dictionary<string,int>();
        foreach(var rec in payload.GetProperty("recommendations").EnumerateArray()){
            if(rec.GetProperty("expiresAt").GetInt64()<=now)continue;
            string kind=rec.GetProperty("kind").GetString()!;counts[kind]=counts.GetValueOrDefault(kind)+1;
        }
        int crafts=payload.TryGetProperty("crafting",out var crafting)?crafting.GetProperty("plans").EnumerateArray().Count(p=>p.GetProperty("expiresAt").GetInt64()>now):0;
        int resale=counts.GetValueOrDefault("resale_buy")+counts.GetValueOrDefault("pricing_error_buy");
        int vendors=counts.GetValueOrDefault("vendor_buy"),research=counts.Values.Sum()-resale-vendors;
        string summary=$"{vendors} vendor, {resale} resale, {crafts} craft, {research} research lead(s)";
        string state=!staged?"Downloaded; addon data needs staging. Sync to repair.":loadedPack==id?"Confirmed loaded by the last game save.":"Ready for /reload or login; game loading not yet confirmed.";
        return label+": "+summary+". Pack "+id[..Math.Min(8,id.Length)]+". "+state;
    }
    public void Save()=>Atomic(Path.Combine(StateRoot,"settings.json"),JsonSerializer.Serialize(Config));
    public void SelectClient(string root,string savedFile)
    {
        if(IsBusy)throw new InvalidOperationException("Wait for the current sync before changing game installations.");
        Config.ClientRoot=root;Config.SavedFile=savedFile;
        ProfitBooks=[];
        supportReadSignature="";supportDB=null;
        Config.LastSignature="";Config.LastLocalRead="";Config.MarketIDs.Clear();Config.MarketPackIDs.Clear();
        Config.RealmFingerprints.Clear();
        Config.LastPack="";Config.LoadedPack="";Config.LoadedPackScope="";Config.LoadedAddonVersion="";Config.LastUploaded="";
        Config.SavedItemCount=null;Config.SavedObservationCount=null;Config.MarketLabel="";Config.RecipesSynced=0;Config.PatternsSynced=0;catalogReadRequired=true;
        Config.PendingAddonInstall=true;Config.PendingAddonRepair=false;
        packStates.Clear();researchPacks.Clear();RefreshPackStatus();
        SavedStatus="Not read yet";State=MarketSyncState.NeedsSetup;Status="Checking the selected game installation.";
    }
    public static void Atomic(string path,string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temp,text,new UTF8Encoding(false));File.Move(temp,path,true);
    }
    public static Dictionary<string,object?> Table(object? v)=>v as Dictionary<string,object?>??new();
    public static Dictionary<string,object?> Sub(Dictionary<string,object?> d,string k)=>Table(d.GetValueOrDefault(k));
    public static double Number(Dictionary<string,object?> d,string k,double fallback=0)=>d.GetValueOrDefault(k) is double n&&double.IsFinite(n)?n:fallback;
    static string Text(Dictionary<string,object?> d,string k,string fallback="")=>d.GetValueOrDefault(k) as string??fallback;
    internal void RememberAcknowledgement(Dictionary<string,object?> db)
    {
        ProfitBooks=ProfitBook.Read(db);
        Config.LoadedAddonVersion=Text(Sub(db,"client"),"addon");
        var ack=Sub(db,"sharedData");string scope=Text(ack,"scope");
        bool scoped=Regex.IsMatch(scope,@"^18:\d+:\d+:[^:\r\n]+:(Alliance|Horde|Neutral)$");
        Config.LoadedPack=scoped?Text(ack,"packID"):"";
        Config.LoadedPackScope=Config.LoadedPack==""?"":scope;
    }
    static long? Integer(Dictionary<string,object?> d,string k,long min,long max){double n=Number(d,k,-1);return n>=min&&n<=max&&n==Math.Floor(n)?(long)n:null;}
    public static IEnumerable<string> FindSavedFiles(string clientRoot)
    {
        string dir=Path.Combine(clientRoot,"WTF","Account");if(!Directory.Exists(dir))return [];
        return Directory.EnumerateDirectories(dir).Select(p=>Path.Combine(p,"SavedVariables","ForeverFlip.lua")).Where(File.Exists);
    }
    public bool GameRunning()=>ClientInstall.GameRunning();
    public async Task Tick()
    {
        if(!await gate.WaitAsync(0))return;
        try{
            RestorePackStatus();
            State=MarketSyncState.NeedsSetup;
            if(!Config.NoticeAccepted){Status="Read the first-run sharing notice to continue.";return;}
            if(string.IsNullOrEmpty(Config.ClientRoot)){Status="Choose your WoW Forever installation to begin.";return;}
            if(!ClientInstall.SupportedRoot(Config.ClientRoot)){Status="WoW Forever was moved or is unavailable. Choose its new folder.";return;}
            if(ClientInstall.NeedsBundled(Config.ClientRoot))Config.PendingAddonInstall=true;
            if(Config.PendingAddonInstall&&!GameRunning()&&SaveProcessProbe(Config.ClientRoot)==GameSaveState.Stopped){
                await SignedAddonDownload.EnsureAsync(Config.ClientRoot,Config.PendingAddonRepair);Config.PendingAddonInstall=false;Config.PendingAddonRepair=false;Save();Status="Addon installed. Enable ForeverFlip and ForeverFlip_Data on the character screen.";
            }
            if(string.IsNullOrEmpty(Config.SavedFile)&&ClientInstall.ValidRoot(Config.ClientRoot)){
                var discovered=FindSavedFiles(Config.ClientRoot).ToArray();if(discovered.Length==1){Config.SavedFile=discovered[0];Save();}
            }
            if(!File.Exists(Config.SavedFile)){Status=Config.PendingAddonInstall?"Addon installation queued until WoW closes.":"Addon ready. Launch WoW, enable it, then /reload or log out after scanning to create the database.";return;}
            var info=new FileInfo(Config.SavedFile);string signature=SavedGameFile.Signature(info);
            SavedStatus=info.LastWriteTime.ToString("g");
            string? wait=SavedGameFile.WaitReason(SaveProcessProbe(Config.ClientRoot),info.LastWriteTimeUtc,DateTime.UtcNow);
            if(wait!=null){State=MarketSyncState.Waiting;Status=wait;return;}
            // Support uploads have their own explicit in-game consent and must
            // run even with market/catalog sharing off or unchanged market data.
            await SyncSupport(signature);
            if(!Config.ShareMarket&&(signature!=Config.LastLocalRead||Config.MarketIDs.Count==0||!Config.SavedItemCount.HasValue||Config.LoadedPack!=""&&Config.LoadedPackScope=="")){
                Dictionary<string,object?>? localDB=null;
                var local=await Task.Run(()=>BuildBatches(Config.SavedFile,db=>localDB=db));
                var afterLocal=new FileInfo(Config.SavedFile);if(signature!=afterLocal.Length+":"+afterLocal.LastWriteTimeUtc.Ticks)throw new IOException("Save changed during reading; retrying later.");
                RememberAcknowledgement(localDB!);
                RememberDataset(local);
                var ids=new HashSet<string>();foreach(var batch in local){using var d=JsonDocument.Parse(JsonSerializer.Serialize(batch));ids.Add(MarketID(d.RootElement.GetProperty("market")));}
                Config.MarketIDs=ids.ToList();Config.LastLocalRead=signature;Save();
            }
            await RefreshCatalogCapability();
            if(Config.ShareMarket && (signature!=Config.LastSignature||!Config.SavedItemCount.HasValue||Config.LoadedPack!=""&&Config.LoadedPackScope==""||Config.ShareCatalog&&Config.CatalogNoticeAccepted&&catalogReadRequired
                ||signature!=Config.LastResearchSignature&&DateTime.UtcNow>=nextResearchFeedbackAt)){
                try{await SyncMarketUpload(signature,info);}
                catch(Exception ex){
                    State=MarketSyncState.Error;Status=SyncFailure(ex);
                    Log(ex.GetType().Name+": "+Status);
                }
            }else{State=Config.ShareMarket?MarketSyncState.Synced:MarketSyncState.SharingOff;Status=Config.ShareMarket?"Up to date. Waiting for the next game save.":"Market sharing is off. Shared data downloads remain available.";}
            foreach(string stale in packStates.Keys.Except(Config.MarketIDs).ToArray()){packStates.Remove(stale);researchPacks.Remove(stale);}
            foreach(string market in Config.MarketIDs){
                if(nextPackAt.TryGetValue(market,out var retry)&&DateTime.UtcNow<retry)continue;
                try{
                using var response=await http.GetAsync(Service+"/api/v1/markets/"+Uri.EscapeDataString(market)+"/pack");
                if(response.StatusCode==HttpStatusCode.TooManyRequests){
                    var header=response.Headers.RetryAfter;
                    var delay=header?.Delta??(header?.Date is DateTimeOffset at?at-DateTimeOffset.UtcNow:TimeSpan.FromMinutes(1));
                    nextPackAt[market]=DateTime.UtcNow+(delay>TimeSpan.Zero?delay:TimeSpan.FromMinutes(1));
                    throw new MarketServiceException("AI download temporarily limited; retrying automatically.");
                }
                if(response.StatusCode==HttpStatusCode.NotFound){
                    if(!researchPacks.ContainsKey(market))packStates[market]="No current AI pack for "+market[..12]+". Native strategies continue.";
                    continue;
                }response.EnsureSuccessStatusCode();
                using var envelope=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var payload=Verify(envelope.RootElement,packPublicKey);
                if(payload.GetProperty("marketID").GetString()!=market)throw new InvalidDataException("Pack market mismatch.");
                string id=payload.GetProperty("packID").GetString()!;
                bool staged=IsPackStaged(payload);
                if(Config.MarketPackIDs.GetValueOrDefault(market)!=id||!IsPackStaged(payload)){
                    staged=StagePack(payload,envelope.RootElement);Config.LastPack=id;if(staged)Config.MarketPackIDs[market]=id;Save();
                }
                packStates[market]=DescribePack(payload,staged,Config.LoadedPackScope==payload.GetProperty("scope").GetString()?Config.LoadedPack:"");
                if(!staged&&Config.PendingAddonInstall)packStates[market]+=" Close WoW once to install the missing data helper.";
                researchPacks[market]=new(payload.GetProperty("createdAt").GetInt64(),payload.GetProperty("expiresAt").GetInt64(),staged,id,payload.GetProperty("scope").GetString()!);
                }catch(Exception ex){State=MarketSyncState.Error;Status+=" AI download: "+SyncFailure(ex);Log(ex.GetType().Name+": "+SyncFailure(ex));}
            }
            if(researchFeedbackWarning!="")Status+=" "+researchFeedbackWarning;
        }catch(Exception ex){State=MarketSyncState.Error;Status=ex is HttpRequestException?"Service unavailable. Saved data stays local; retrying automatically.":ex.Message;Log(ex.GetType().Name+": "+Status);}
        finally{RefreshPackStatus();gate.Release();Changed?.Invoke();}
    }
    public static (int Items,int Observations,string Label) SummarizeBatches(IEnumerable<object> batches)
    {
        int items=0,observations=0;var labels=new HashSet<string>();
        foreach(var batch in batches){
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(batch));var root=doc.RootElement;
            var market=root.GetProperty("market");labels.Add(market.GetProperty("realm").GetString()+" · "+market.GetProperty("faction").GetString());
            foreach(var item in root.GetProperty("items").EnumerateArray()){items++;observations+=item.GetProperty("points").GetArrayLength();}
        }
        return(items,observations,labels.Count==1?labels.Single():labels.Count>1?$"WoW Forever beta · {labels.Count} markets":"WoW Forever beta");
    }
    void RememberDataset(IEnumerable<object> batches)
    {
        var summary=SummarizeBatches(batches);Config.SavedItemCount=summary.Items;Config.SavedObservationCount=summary.Observations;Config.MarketLabel=summary.Label;
    }
    async Task<JsonDocument> Post(string path,object body,string? token=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Service+path){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};
        if(token!=null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await http.SendAsync(request);
        if(!response.IsSuccessStatusCode){
            if(response.StatusCode==HttpStatusCode.UnprocessableEntity){
                // A server rollback can remove optional capabilities. Recheck
                // before retrying instead of resending a rejected new shape.
                nextCatalogCapability=DateTime.MinValue;depthCapability=false;recipeIdentityCapability=false;
                marketIdentityCapability=false;patternCapability=false;researchFeedbackCapability=false;catalogReadRequired=true;Config.LastSignature="";
            }
            string? detail=null;
            if(response.StatusCode==HttpStatusCode.BadRequest){
                try{using var error=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if(error.RootElement.TryGetProperty("detail",out var text)&&text.ValueKind==JsonValueKind.String)detail=text.GetString();}
                catch(JsonException){}
            }
            var retry=response.Headers.RetryAfter;
            TimeSpan? delay=retry?.Delta??(retry?.Date is DateTimeOffset at?at-DateTimeOffset.UtcNow:null);
            throw new MarketServiceException(RequestFailure(response.StatusCode,detail),response.StatusCode,delay);
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    public static string RequestFailure(HttpStatusCode code,string? detail=null)=>code switch{
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>"Sharing authorization was rejected. Contact support to reconnect this installation; your local data is preserved.",
        HttpStatusCode.BadRequest=>"Saved data was not accepted. "+(string.IsNullOrWhiteSpace(detail)?"Make a new game save with /reload or logout, then sync again.":new string(detail.Where(c=>!char.IsControl(c)).Take(250).ToArray())),
        HttpStatusCode.RequestEntityTooLarge=>"The saved-data upload is too large. Update the app/addon or contact support.",
        HttpStatusCode.UnprocessableEntity=>"The saved-data format was rejected. Update the app/addon, make a new game save and sync again.",
        HttpStatusCode.TooManyRequests=>"The service is limiting requests. Wait before syncing again; your saved data is preserved.",
        _=>"Market service returned HTTP "+(int)code+". Saved data stays local; retry later."
    };
    static string MarketID(JsonElement m)
    {
        string scope=$"{m.GetProperty("project").GetInt32()}:{m.GetProperty("major").GetInt32()}:{m.GetProperty("region").GetInt32()}:{m.GetProperty("realm").GetString()}:{m.GetProperty("faction").GetString()}";
        return "ff1-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.GetProperty("family").GetString()+":"+m.GetProperty("epoch").GetString()+":"+scope))).ToLowerInvariant()[..32];
    }
    static JsonElement VerifySignature(JsonElement envelope,string publicKey)
    {
        if(envelope.GetProperty("algorithm").GetString()!="ES256")throw new InvalidDataException("Unsupported signature.");
        var raw=Convert.FromBase64String(envelope.GetProperty("payload").GetString()!);
        if(raw.Length>4*1024*1024)throw new InvalidDataException("Pack exceeds limit.");
        using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(publicKey));
        if(!key.VerifyData(raw,Convert.FromBase64String(envelope.GetProperty("signature").GetString()!),HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence))throw new InvalidDataException("Invalid pack signature.");
        using var document=JsonDocument.Parse(raw);return document.RootElement.Clone();
    }
    public static JsonElement Verify(JsonElement envelope,string publicKey)
    {
        var payload=VerifySignature(envelope,publicKey);long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if(payload.GetProperty("createdAt").GetInt64()>now+300||payload.GetProperty("expiresAt").GetInt64()<=now)throw new InvalidDataException("Pack expired or from the future.");return payload;
    }
    public static string LuaString(string value)
    {
        var b=new StringBuilder("\"");foreach(byte c in Encoding.UTF8.GetBytes(value)){if(c<32||c>126||c==34||c==92)b.Append('\\').Append(c.ToString("D3",CultureInfo.InvariantCulture));else b.Append((char)c);}return b.Append('"').ToString();
    }
    static string AdvisoryText(JsonElement value,int maxLength,string field)
    {
        if(value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Invalid advisory "+field+".");
        string text=value.GetString()!;
        if(text.Length==0||text.EnumerateRunes().Count()>maxLength)throw new InvalidDataException("Invalid advisory "+field+" length.");
        return LuaString(text);
    }
    public static string RenderPackEntry(JsonElement p)
    {
        if(p.GetProperty("schema").GetString()!="foreverflip.market-pack"||p.GetProperty("schemaVersion").GetInt32()!=1)throw new InvalidDataException("Unsupported pack schema.");
        var body=new StringBuilder("[").Append(LuaString(p.GetProperty("scope").GetString()!)).Append("]={packID=").Append(LuaString(p.GetProperty("packID").GetString()!))
            .Append(",scope=").Append(LuaString(p.GetProperty("scope").GetString()!)).Append(",createdAt=").Append(p.GetProperty("createdAt").GetInt64())
            .Append(",expiresAt=").Append(p.GetProperty("expiresAt").GetInt64()).Append(",recommendations={");
        int count=0;
        foreach(var r in p.GetProperty("recommendations").EnumerateArray()){
            if(++count>1000)throw new InvalidDataException("Too many recommendations.");
            string item=r.GetProperty("itemKey").GetString()!,kind=r.GetProperty("kind").GetString()!;
            if(!Regex.IsMatch(item,@"^[1-9][0-9]*:\d+:-?\d+:\d+$"))throw new InvalidDataException("Invalid item key.");
            body.Append("{id=").Append(LuaString(r.GetProperty("id").GetString()!)).Append(",key=").Append(LuaString(item)).Append(",kind=").Append(LuaString(kind))
                .Append(",priority=").Append(r.GetProperty("priority").GetInt32()).Append(",expiresAt=").Append(r.GetProperty("expiresAt").GetInt64()).Append(",reason=").Append(LuaString(r.GetProperty("reason").GetString()!));
            if(r.TryGetProperty("reasonCode",out var code))body.Append(",reasonCode=").Append(AdvisoryText(code,80,"reasonCode"));
            if(r.TryGetProperty("strategies",out var strategies)){
                if(strategies.ValueKind!=JsonValueKind.Array||strategies.GetArrayLength() is <1 or >4)throw new InvalidDataException("Invalid research strategies.");
                body.Append(",strategies={");var seenStrategies=new HashSet<string>();
                foreach(var strategy in strategies.EnumerateArray()){
                    string value=strategy.GetString()??"";if(value is not ("vendor" or "resale" or "pricing_error" or "crafting")||!seenStrategies.Add(value))throw new InvalidDataException("Invalid research strategy.");
                    body.Append(LuaString(value)).Append(',');
                }body.Append('}');
            }
            if(r.TryGetProperty("profitPlan",out var plan))body.Append(",profitPlan=").Append(AdvisoryText(plan,600,"profitPlan"));
            if(r.TryGetProperty("risks",out var risks)){
                if(risks.ValueKind!=JsonValueKind.Array||risks.GetArrayLength()>3)throw new InvalidDataException("Invalid advisory risks.");
                body.Append(",risks={");foreach(var risk in risks.EnumerateArray())body.Append(AdvisoryText(risk,240,"risk")).Append(',');body.Append('}');
            }
            foreach(string name in new[]{"maxBuyUnitCopper","maxQuantity","suggestedResaleUnitCopper"})if(r.TryGetProperty(name,out var n)){
                long value=n.GetInt64();if(value<1||value>100000000000)throw new InvalidDataException("Invalid advisory amount.");body.Append(',').Append(name).Append('=').Append(value);}
            body.Append("},");
        }
        body.Append('}');
        if(p.TryGetProperty("crafting",out var crafting)){
            if(crafting.GetProperty("schema").GetInt32()!=1||crafting.GetProperty("catalogue").GetArrayLength()>10000||crafting.GetProperty("plans").GetArrayLength()>100)throw new InvalidDataException("Invalid crafting pack.");
            if(crafting.TryGetProperty("patternItems",out var patterns)&&patterns.GetArrayLength()>5000)throw new InvalidDataException("Pattern catalogue exceeds delivery limits.");
            int nodes=0;body.Append(",crafting=").Append(DataLiteral(crafting,0,ref nodes));
        }
        return body.Append("},\n").ToString();
    }
    bool StagePack(JsonElement p,JsonElement envelope)=>StagePackCore(p,envelope,GameRunning);
    internal bool StagePackCore(JsonElement p,JsonElement envelope,Func<bool> gameRunning)
    {
        ClientInstall.CheckInstallPaths(Config.ClientRoot);
        if(!Directory.Exists(Path.Combine(Config.ClientRoot,"Interface","AddOns","ForeverFlip")))throw new InvalidDataException("Install the addon before market data.");
        string market=p.GetProperty("marketID").GetString()!;if(!Regex.IsMatch(market,@"^ff1-[a-f0-9]{32}$"))throw new InvalidDataException("Invalid market ID.");
        string cache=packCacheRoot;Atomic(Path.Combine(cache,market+".json"),envelope.GetRawText());
        string dir=Path.Combine(Config.ClientRoot,"Interface","AddOns","ForeverFlip_Data");
        if(!File.Exists(Path.Combine(dir,"ForeverFlip_Data.toc"))&&gameRunning()){
            Config.PendingAddonInstall=true;return false;
        }
        var body=new StringBuilder("-- Verified advisory data. Personal history is untouched.\nForeverFlipShared={schema=1,markets={\n");
        foreach(var file in Directory.EnumerateFiles(cache,"ff1-*.json")){
            try{using var doc=JsonDocument.Parse(File.ReadAllText(file));var pack=Verify(doc.RootElement,packPublicKey);body.Append(RenderPackEntry(pack));}
            catch(Exception ex) when(ex is InvalidDataException or JsonException or FormatException or CryptographicException or KeyNotFoundException or InvalidOperationException){/* invalid or expired caches cannot prevent staging a verified pack */}
        }body.Append("}}\n");
        Directory.CreateDirectory(dir);Atomic(Path.Combine(dir,"Data.lua"),body.ToString());
        Atomic(Path.Combine(dir,"ForeverFlip_Data.toc"),"## Interface: 16001\n## Title: Forever Flip Data\n## Notes: Verified market recommendations from the Forever Flip desktop app.\n## Version: 0.1.0\nData.lua\n");
        return true;
    }
    public static List<object> BuildBatches(string path,Action<Dictionary<string,object?>>? readLocal=null,bool includeRealmIdentity=false,bool includeDepth=false)
    {
        var snapshot=SavedGameFile.Read(path);
        var db=LuaReader.Read(snapshot.Text);readLocal?.Invoke(db);var client=Sub(db,"client");
        string build=Text(client,"build");int iface=(int)Number(client,"interface");
        if(iface!=16001)throw new InvalidDataException("This pilot supports the Forever beta client; Retail syncing is not enabled yet.");
        var catalog=Sub(Sub(db,"itemCatalog"),"18:"+build);var batches=new List<object>();
        foreach(var (scope,value) in Sub(db,"markets")){
            var parts=scope.Split(':');if(parts.Length!=5||parts[0]!="18"||!int.TryParse(parts[1],out int major)||!int.TryParse(parts[2],out int region))continue;
            if(major<1||major>100||region<1||region>1000||parts[3].Length==0||parts[3].Length>100||parts[3].Any(char.IsControl)||parts[4] is not ("Alliance" or "Horde" or "Neutral"))continue;
            var market=new Dictionary<string,object>{["project"]=18,["major"]=major,["region"]=region,["realm"]=parts[3],["faction"]=parts[4],["family"]="forever-beta",["epoch"]="beta-2026",["interface"]=iface,["build"]=build};
            var realm=Sub(Sub(db,"marketIdentities"),scope);long? realmID=Integer(realm,"realmID",1,10000000),observed=Integer(realm,"observedAt",1,4102444800);
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if(includeRealmIdentity&&Text(realm,"scope")==scope&&Number(realm,"project")==18&&Text(realm,"build")==build&&realm.GetValueOrDefault("conflict") is not true&&realmID.HasValue&&observed.HasValue&&observed>=now-30*86400&&observed<=now+300){market["realmID"]=realmID.Value;market["realmObservedAt"]=observed.Value;}
            var list=new List<object>();int batchBytes=0;
            foreach(var (key,raw) in Table(value)){
                if(key.Length>96||!Regex.IsMatch(key,@"^[1-9][0-9]*:\d+:-?\d+:\d+$"))continue;
                var item=Table(raw);if(item.GetValueOrDefault("errorCommodityKey") is string alias&&alias!=key)continue;
                var meta=Sub(catalog,key.Split(':')[0]);var points=new List<object>();
                foreach(var h in Sub(item,"history").Values){var point=Table(h);
                    // An absent legacy flag is compatible; explicit false is not evidence of unit pricing.
                    if(point.GetValueOrDefault("unitKnown") is false||point.GetValueOrDefault("unit_known") is false)continue;
                    var t=Integer(point,"t",1,4102444800);var q=Integer(point,"q",1,100000000);double price=Number(point,"p");if(t.HasValue&&q.HasValue&&price>0&&price<=100000000000)points.Add(new{at=t.Value,price,stock=q.Value,kind="history",source="retained_history",unitKnown=true});}
                var quote=Sub(item,"tooltipQuote");var qt=Integer(quote,"seen",1,4102444800);var qq=Integer(quote,"quantity",1,100000000);double qp=Number(quote,"price");string source=Text(quote,"source");
                if(qt.HasValue&&qq.HasValue&&qp>0&&qp<=100000000000&&source is "browse" or "snapshot" or "exact" && quote.GetValueOrDefault("missing") is not true)
                    points.Add(new{at=qt.Value,price=qp,stock=qq.Value,kind="quote",source,unitKnown=quote.GetValueOrDefault("unitKnown") is true});
                if(points.Count==0)continue;string name=Clean(Text(item,"name",Text(meta,"name",key.Split(':')[0])),160,key.Split(':')[0]);
                string equip=Text(meta,"equip");if(equip.Length>80||!Regex.IsMatch(equip,@"^[A-Z0-9_]*$"))equip="";
                long? vendorCopper=Integer(meta,"vendorPrice",0,100000000000),vendorAt=Integer(meta,"checkedAt",1,4102444800);
                if(!vendorCopper.HasValue||!vendorAt.HasValue||vendorAt>now+300){vendorCopper=null;vendorAt=null;}
                var shared=new Dictionary<string,object?>{{"key",key},{"name",name},{"vendorCopper",vendorCopper},{"vendorAt",vendorAt},
                    {"stack",Integer(meta,"stack",1,100000)},{"equip",equip},{"commodity",item.GetValueOrDefault("commodity") as bool?},{"points",points.TakeLast(337).ToArray()}};
                var depth=Sub(item,"depthQuote");var depthAt=Integer(depth,"at",now-3600,now);
                if(includeDepth&&depthAt.HasValue&&Number(depth,"schemaVersion")==1&&depth.GetValueOrDefault("complete") is true&&depth.GetValueOrDefault("commodity") is true&&item.GetValueOrDefault("commodity") is true&&key==key.Split(':')[0]+":0:0:0"){
                    var tiers=Sub(depth,"tiers").Values.Select(Table).ToArray();
                    if(tiers.Length is >=1 and <=200&&tiers.All(t=>Integer(t,"unitCopper",1,100000000000).HasValue&&Integer(t,"quantity",1,100000000).HasValue)){
                        long total=tiers.Sum(t=>Integer(t,"quantity",1,100000000)!.Value),minimum=tiers.Min(t=>Integer(t,"unitCopper",1,100000000000)!.Value);
                        if(total<=100000000&&qt==depthAt&&qq==total&&qp==minimum&&source=="exact"&&quote.GetValueOrDefault("unitKnown") is true){
                            shared["depth"]=new{schemaVersion=1,at=depthAt.Value,commodity=true,complete=true,
                                tiers=tiers.Select(t=>new{unitCopper=Integer(t,"unitCopper",1,100000000000)!.Value,quantity=Integer(t,"quantity",1,100000000)!.Value}).ToArray()};
                        }
                    }
                }
                int itemBytes=JsonSerializer.SerializeToUtf8Bytes(shared).Length;
                if(list.Count>0&&batchBytes+itemBytes>3*1024*1024){batches.Add(new{schemaVersion=1,market,addonVersion=Text(client,"addon","0.12.76"),savedAt=new DateTimeOffset(snapshot.SavedAtUtc).ToUnixTimeSeconds(),items=list.ToArray()});list.Clear();batchBytes=0;}
                list.Add(shared);batchBytes+=itemBytes;
                if(list.Count==100){batches.Add(new{schemaVersion=1,market,addonVersion=Text(client,"addon","0.12.76"),savedAt=new DateTimeOffset(snapshot.SavedAtUtc).ToUnixTimeSeconds(),items=list.ToArray()});list.Clear();batchBytes=0;}
            }
            if(list.Count>0)batches.Add(new{schemaVersion=1,market,addonVersion=Text(client,"addon","0.12.76"),savedAt=new DateTimeOffset(snapshot.SavedAtUtc).ToUnixTimeSeconds(),items=list.ToArray()});
        }return batches;
    }
    public static void Log(string text)
    {
        string path=Path.Combine(StateRoot,"desktop.log");try{if(File.Exists(path)&&new FileInfo(path).Length>512000)File.Move(path,path+".previous",true);File.AppendAllText(path,DateTimeOffset.Now+" "+text+Environment.NewLine);}catch{}
    }
}
