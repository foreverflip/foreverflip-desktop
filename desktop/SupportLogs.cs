using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

// Separate from market/catalog consent. Only an explicit, saved in-game
// supportRequest can create an upload. Never serialize the whole database.
public static class SupportLogs
{
    public const int MaxBytes=512*1024;
    // Match the addon's bounded selection. Ranks are local implementation
    // details, not extra wire fields or permission to retain private data.
    static readonly HashSet<string> Outcomes=new(("buy sell vendor_sale vendor_delivery scan scan_abort transaction_resolved post_request post_verification purchase_receipt_event craft_batch craft_all_requested craft_output_received craft_chain_output_received craft_vendor_sell_start craft_vendor_sale craft_batch_finished craft_batch_cancelled fulfillment_receipt fulfillment_mail_take fulfillment_late_mail_wait fulfillment_reconciled profit_ah_settlement").Split(' '));
    static readonly HashSet<string> Routine=new(("queue_status api_call ah_event api_throttle_event scan_status window_show vendor_input vendor_search_key_resolve craft_tracker_visibility").Split(' '));
    internal static int Priority(string kind,Dictionary<string,object?> data)
    {
        if(kind is "startup" or "market_identity")return 4;
        if(kind=="api_throttle_event"&&data.GetValueOrDefault("event") is string name&&name is "AUCTION_HOUSE_THROTTLED_MESSAGE_DROPPED" or "AUCTION_HOUSE_BROWSE_FAILURE")return 3;
        foreach(string suffix in new[]{"_error","_failed","_failure","_uncertain","_unconfirmed","_unanswered","_untransmitted","_timeout","_rejected","_blocked"})if(kind.EndsWith(suffix,StringComparison.Ordinal))return 3;
        if(Outcomes.Contains(kind)||kind.StartsWith("repost_",StringComparison.Ordinal))return 2;
        return Routine.Contains(kind)?0:1;
    }
    sealed record Event(long at,string kind,Dictionary<string,object?> data);
    sealed record Error(string message,string stack,string addon,string build,long firstAt,long lastAt,long count);
    internal static readonly HashSet<string> Fields=new(("key itemKey itemID recipeID spellID job quantity count total price unitPrice vendor profit cost maxUnit expectedProceeds paid bought sold crafted needed missing remaining rows pages cursor processed totalRows candidates variants queued complete partial empty matching missingPrice aboveCeiling invalidPrice payoutUnavailable payoutRecovered returnedRows sameFamily pass passes retry elapsed wait duration delay budget floor margin roi success ready enabled active nearFloor belowFloor priority source state mode reason event name status message code class subclass partition pageLimit uniqueBaseItems newBaseItems overlap skipped checked returned observed available bagQuantity mailRemaining bagIncrease invoiceRemoved tokenChanged attempt attempts index size limit revision build addon version method kind clock since at t cancelled").Split(' '));
    internal static readonly HashSet<string> NumericFields=new(("qualified resultCount cacheProbeAvailable cached nativeMetadataAvailable commodity crafts worstBasket guardedBudget locked surveyCap affordable depthAtCap quotedCost quotedCeiling id outputMax profitDrop baselineProfit profitFloor minROI age approvedTotal transportReady basketOK ttl auctionID manual craftJobID craftMaterial time target shown invoiceQuantity batch invoiceCost attachment inflight mailPending attachments windowVisible skillLineID recipes lost matchingBagUnits level vendorEnabled waiting mixed changed unprofitable trackedLocked priceUnavailable untracked verified bags held interface realmID nonStackable tooltipEquipmentQuote accepted uncached equipmentOrUnknownSlot noBuyout omitted candidateItems quarantinedItems recoveredItemTypes responseSeconds trackedItems committedItems tooltipQuotes cacheRetries processingSeconds ownAuction requestAt expiresAt quarantined resumeHunt nominated learning proceeds mail merchant gross fee refund deposit").Split(' ').Concat(new[]{"excluded category","not profitable within settings","not discounted against history","category blocked","historical reference expired","no prior price observations","below profit floor","unstable history"}));
    static readonly Dictionary<string,string[]> EnumFields=new(){["route"]=["vendor","auction"],["sortProfile"]=["commodity price","item buyout/bid"],["cacheSource"]=["server key","commodity key"],["keySource"]=["server key","item key"],["proof"]=["unique_seller_invoice","user_confirmation","bag_increase_user_confirmation"]};
    static SupportLogs(){Fields.UnionWith(NumericFields);Fields.UnionWith(EnumFields.Keys);Fields.Add("queryKey");}
    static readonly Regex RequestID=new(@"^fflog-[0-9]{1,10}-[0-9]{1,8}-[0-9]{1,10}$",RegexOptions.CultureInvariant);
    public static string Scrub(string value,IEnumerable<string>? names=null)
    {
        value=Regex.Replace(value,@"https?://\S+","[url]",RegexOptions.IgnoreCase);
        value=Regex.Replace(value,@"Player-[-0-9A-Fa-f]+","[player]");
        value=Regex.Replace(value,"(?:[A-Z]:[/\\\\]|/(?:home|users)/)[^\\r\\n\"']+","[path]",RegexOptions.IgnoreCase);
        value=Regex.Replace(value,@"(?:Bearer\s+\S+|(?:token|password|secret)\s*[=:]\s*\S+)","[credential]",RegexOptions.IgnoreCase);
        foreach(string name in names??[])if(name.Length>=2&&name.Length<=100)value=Regex.Replace(value,@"(?<![\w])"+Regex.Escape(name)+@"(?![\w])","[character]",RegexOptions.IgnoreCase);
        return new string(value.Where(c=>!char.IsControl(c)||c=='\n'||c=='\t').ToArray());
    }
    static IEnumerable<Dictionary<string,object?>> Rows(Dictionary<string,object?> d)=>d.OrderBy(p=>int.TryParse(p.Key,out int n)?n:int.MaxValue).Select(p=>MarketSync.Table(p.Value));
    static long? Int(Dictionary<string,object?> d,string key,long min=1,long max=4102444800)=>d.GetValueOrDefault(key) is double n&&double.IsFinite(n)&&n==Math.Floor(n)&&n>=min&&n<=max?(long)n:null;
    static string Str(Dictionary<string,object?> d,string key)=>d.GetValueOrDefault(key) as string??"";
    public static string? Build(Dictionary<string,object?> db,string desktopVersion,long? clock=null)
    {
        var request=MarketSync.Sub(db,"supportRequest");long now=clock??DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestSchema=Int(request,"schemaVersion",1,2);
        if(requestSchema==null||Str(request,"consent")!="support-2026-10-17")return null;
        string id=Str(request,"requestID"),addon=Str(request,"addonVersion"),build=Str(request,"build");long? at=Int(request,"requestedAt");
        if(!RequestID.IsMatch(id)||!at.HasValue||at<now-7*86400||at>now+300||!Regex.IsMatch(addon,@"^\d+\.\d+\.\d+$")||addon.Length>24||!Regex.IsMatch(build,@"^\d{4,8}$"))return null;
        if(!Regex.IsMatch(desktopVersion,@"^\d+\.\d+\.\d+$")||desktopVersion.Length>24)throw new InvalidDataException("Invalid support client version.");
        var names=MarketSync.Sub(request,"redactions").Values.OfType<string>().Take(10).ToList();
        // Old account ledgers can hold other character names. Use them only as
        // local redaction inputs, never include ledger keys or values.
        foreach(var key in MarketSync.Sub(db,"ledgers").Keys.Take(200)){
            string last=key.Split(':').Last();if(last.Length>=2)names.Add(last.Split('-')[0]);
        }
        string Clean(string text,int max)=>Scrub(text.Length>max?text[..max]:text,names);
        bool hasFeedback=request.ContainsKey("reportType")||request.ContainsKey("description");
        if(requestSchema==2&&!hasFeedback)return null;
        string reportType=Str(request,"reportType"),description="";
        if(hasFeedback){
            if(reportType is not ("issue" or "feedback")||request.GetValueOrDefault("description") is not string text)return null;
            description=string.Concat(Scrub(string.Concat(text.EnumerateRunes().Take(1000).Select(r=>r.ToString())),names).Trim().EnumerateRunes().Take(1000).Select(r=>r.ToString()));
            if(description.Length==0)return null;
        }
        var events=new List<Event>();
        foreach(var row in Rows(MarketSync.Sub(request,"events")).TakeLast(600)){
            var t=Int(row,"t");string kind=Str(row,"kind");if(!t.HasValue||t>at+300||!Regex.IsMatch(kind,@"^[a-zA-Z0-9_]{1,80}$"))continue;
            var data=new Dictionary<string,object?>();
            foreach(var (key,value) in MarketSync.Sub(row,"data").Where(p=>Fields.Contains(p.Key)).Take(50)){
                if(key=="cancelled"&&value is not bool)continue;
                if(key is "gross" or "fee" or "refund" or "deposit"&&(value is not double money||!double.IsFinite(money)||money<0||money!=Math.Floor(money)))continue;
                if(NumericFields.Contains(key)&&value is not bool&&value is not double)continue;
                if(EnumFields.TryGetValue(key,out var options)&&(value is not string text||!options.Contains(text)))continue;
                if(key=="queryKey"&&(value is not string query||!Regex.IsMatch(query,@"^[1-9][0-9]*:\d+:-?\d+:\d+$")))continue;
                if(value is string s)data[key]=Clean(s,320);
                else if(value is bool)data[key]=value;
                else if(value is double number&&double.IsFinite(number)&&Math.Abs(number)<=1e15)data[key]=number;
            }
            events.Add(new(t.Value,kind,data));
        }
        events=events.OrderBy(e=>e.at).ToList(); // stable for distinct same-second events
        var errors=new List<Error>();
        foreach(var row in Rows(MarketSync.Sub(request,"errors")).TakeLast(30)){
            var first=Int(row,"firstAt");var last=Int(row,"lastAt");var count=Int(row,"count",1,1000000);
            string version=Str(row,"addon"),errorBuild=Str(row,"build");
            if(!first.HasValue||!last.HasValue||!count.HasValue||first>last||last>at+300||!Regex.IsMatch(version,@"^[\w.-]{0,24}$")||!Regex.IsMatch(errorBuild,@"^[\w.-]{0,24}$"))continue;
            errors.Add(new(Clean(Str(row,"message"),2000),Clean(Str(row,"stack"),8000),version,errorBuild,first.Value,last.Value,count.Value));
        }
        var settings=new Dictionary<string,object?>();
        foreach(var (key,value) in MarketSync.Sub(request,"settings").Take(120)){
            if(!Regex.IsMatch(key,@"^(?:scan|trade|craft|mail)\.[A-Za-z][A-Za-z0-9_]{0,39}$"))continue;
            if(value is bool||value is double n&&double.IsFinite(n)&&Math.Abs(n)<=1e15)settings[key]=value;
        }
        string scope=Str(request,"scope");if(!Regex.IsMatch(scope,@"^18:1:[0-9]{1,4}:Classic Beta Pv[EP]:(?:Alliance|Horde|Neutral)$"))scope="";
        string Render(){
            var body=new Dictionary<string,object?>{{"schemaVersion",1},{"consent","support-2026-10-17"},{"requestID",id},{"requestedAt",at.Value},{"addonVersion",addon},{"desktopVersion",desktopVersion},{"build",build},{"scope",scope},{"settings",settings},{"events",events},{"errors",errors},{"debugEnabled",request.GetValueOrDefault("debugEnabled") is true}};
            if(hasFeedback){body["reportType"]=reportType;body["description"]=description;}
            return JsonSerializer.Serialize(body);
        }
        string raw=Render();
        var contextAt=events.Where(e=>Priority(e.kind,e.data)==4).GroupBy(e=>e.kind).ToDictionary(g=>g.Key,g=>g.Max(e=>e.at));
        int bytes=Encoding.UTF8.GetByteCount(raw);
        while(bytes>MaxBytes&&(events.Count>0||errors.Count>0)){
            int eventIndex=-1,priority=int.MaxValue;long oldest=long.MaxValue;
            for(int i=0;i<events.Count;i++){
                int rank=Priority(events[i].kind,events[i].data);
                if(rank==4&&events[i].at<contextAt[events[i].kind])rank=1;
                if(rank<priority||rank==priority&&events[i].at<oldest){eventIndex=i;priority=rank;oldest=events[i].at;}
            }
            int errorIndex=-1;long errorAt=long.MaxValue;
            for(int i=0;i<errors.Count;i++)if(errors[i].lastAt<errorAt){errorIndex=i;errorAt=errors[i].lastAt;}
            // Lua exceptions are critical, too. If only critical evidence is
            // left, discard its oldest record instead of preferring old errors
            // over a fresh transaction failure. Never mutate a saved retry.
            if(errorIndex>=0&&(eventIndex<0||priority>3||priority==3&&errorAt<oldest)){
                bytes-=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(errors[errorIndex]))+(errors.Count>1?1:0);errors.RemoveAt(errorIndex);
            }else{
                bytes-=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(events[eventIndex]))+(events.Count>1?1:0);events.RemoveAt(eventIndex);
            }
        }
        return Render();
    }
}

// Durable, small outbox. An uncertain network response retries the exact same
// bytes/request ID; the server returns the original receipt without duplication.
public sealed class SupportOutbox(string folder)
{
    public string Status {get;private set;}="No support report requested.";
    public sealed class State
    {
        public Dictionary<string,long> Delivered {get;set;}=[];
        public long NextAttempt {get;set;}
        public int Failures {get;set;}
        public string Message {get;set;}="No support report requested.";
    }
    State Load()
    {
        string file=Path.Combine(folder,"state.json");if(!File.Exists(file))return new();
        try{
            var state=JsonSerializer.Deserialize<State>(File.ReadAllText(file))??new();
            state.Delivered??=[];state.Message??="No support report requested.";
            state.Failures=Math.Clamp(state.Failures,0,10);return state;
        }catch(JsonException){
            File.Move(file,file+".invalid-"+Guid.NewGuid().ToString("N"));
            return new(){Message="Support queue recovered. Checking unsent logs."};
        }
    }
    void Save(State s){MarketSync.Atomic(Path.Combine(folder,"state.json"),JsonSerializer.Serialize(s));Status=s.Message;}
    public async Task Process(Dictionary<string,object?> db,string version,Func<string,Task<JsonDocument>> post,long? clock=null,string? blocked=null)
    {
        long now=clock??DateTimeOffset.UtcNow.ToUnixTimeSeconds();Directory.CreateDirectory(folder);var state=Load();Status=state.Message;
        string? raw=SupportLogs.Build(db,version,now);
        if(raw!=null){
            using var report=JsonDocument.Parse(raw);string id=report.RootElement.GetProperty("requestID").GetString()!;
            string file=Path.Combine(folder,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()+".report.json");
            if(!state.Delivered.ContainsKey(id)&&!File.Exists(file)&&Directory.EnumerateFiles(folder,"*.report.json").Count()<3)MarketSync.Atomic(file,raw);
        }
        var files=Directory.EnumerateFiles(folder,"*.report.json").OrderBy(File.GetCreationTimeUtc).ToArray();
        if(files.Length==0)return;
        // Waiting for sign-in is not a delivery failure: keep the report and its backoff unchanged.
        if(blocked!=null){Status=blocked;return;}
        if(state.NextAttempt>now)return;
        string pending=files[0];raw=File.ReadAllText(pending);string requestID;long requestedAt;
        try{
            if(Encoding.UTF8.GetByteCount(raw)>SupportLogs.MaxBytes)throw new InvalidDataException("Local support report exceeds its limit.");
            using var body=JsonDocument.Parse(raw);requestID=body.RootElement.GetProperty("requestID").GetString()!;
            requestedAt=body.RootElement.GetProperty("requestedAt").GetInt64();
            if(!Regex.IsMatch(requestID,@"^fflog-[0-9]{1,10}-[0-9]{1,8}-[0-9]{1,10}$"))throw new InvalidDataException("Invalid pending request ID.");
        }catch(Exception ex) when(ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException){
            File.Move(pending,pending+".invalid-"+Guid.NewGuid().ToString("N"));
            state.Message="Damaged support report kept locally. Use Report issue / feedback again.";state.NextAttempt=0;Save(state);return;
        }
        if(state.Delivered.ContainsKey(requestID)){File.Delete(pending);return;}
        if(requestedAt<now-7*86400){File.Delete(pending);state.Message="Support report expired. Use Report issue / feedback in-game for a fresh report.";state.NextAttempt=0;Save(state);return;}
        try{
            Status="Sending support report…";
            using var response=await post(raw);string reportID=response.RootElement.GetProperty("reportID").GetString()!;
            if(!Regex.IsMatch(reportID,@"^[a-f0-9]{64}$")||response.RootElement.GetProperty("requestID").GetString()!=requestID||response.RootElement.GetProperty("receivedAt").GetInt64()<1)throw new InvalidDataException("Invalid support receipt.");
            state.Delivered[requestID]=now;
            foreach(string stale in state.Delivered.OrderByDescending(p=>p.Value).Skip(20).Select(p=>p.Key).ToArray())state.Delivered.Remove(stale);
            state.NextAttempt=0;state.Failures=0;state.Message="Report sent · "+reportID[..12]+" · "+DateTimeOffset.FromUnixTimeSeconds(now).ToLocalTime().ToString("g");Save(state);File.Delete(pending);
        }catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or MarketServiceException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException){
            state.Failures=Math.Min(10,state.Failures+1);state.NextAttempt=now+Math.Min(3600,30*(1<<state.Failures));
            state.Message=ex is MarketServiceException?"Logs waiting: "+ex.Message:"Logs waiting to send. Retrying automatically; keep the desktop app open.";Save(state);
        }
    }
}
