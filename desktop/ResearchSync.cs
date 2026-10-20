using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

public sealed partial class MarketSync
{
    bool researchFeedbackCapability;
    public static List<object> BuildResearchFeedback(Dictionary<string,object?> db,IEnumerable<object> marketBatches,long? clock=null)
    {
        long now=clock??DateTimeOffset.UtcNow.ToUnixTimeSeconds();var output=new List<object>();var seen=new HashSet<string>();
        foreach(var batch in marketBatches){
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(batch));var m=doc.RootElement.GetProperty("market");
            string marketID=MarketID(m);if(!seen.Add(marketID))continue;
            string scope=$"{m.GetProperty("project").GetInt32()}:{m.GetProperty("major").GetInt32()}:{m.GetProperty("region").GetInt32()}:{m.GetProperty("realm").GetString()}:{m.GetProperty("faction").GetString()}";
            var rows=new List<object>();
            foreach(var raw in Sub(Sub(db,"researchChecks"),scope).Values.TakeLast(500)){
                var r=Table(raw);string id=Text(r,"id"),pack=Text(r,"packID"),key=Text(r,"itemKey"),strategy=Text(r,"strategy"),source=Text(r,"source"),outcome=Text(r,"outcome");
                long? start=Integer(r,"startedAt",now-7*86400,now),end=Integer(r,"finishedAt",now-7*86400,now);
                if(!start.HasValue||!end.HasValue||end<start||end-start>1800||!Regex.IsMatch(id,@"^[A-Za-z0-9._:-]{1,80}$")||pack.Length is <1 or >160||!Regex.IsMatch(key,@"^[1-9][0-9]*:\d+:-?\d+:\d+$"))continue;
                if(strategy is not ("vendor" or "resale" or "pricing_error")||source is not ("ai" or "normal" or "fresh")||outcome is not ("qualified" or "rejected" or "read_failed" or "interrupted"))continue;
                string rec=Text(r,"recommendationID");if(rec.Length>160||source=="ai"&&rec=="")continue;
                // Explicit allowlist: no ledger, settings, names, balances or paths.
                rows.Add(new{id,packID=pack,recommendationID=rec==""?null:rec,itemKey=key,strategy,source,startedAt=start.Value,finishedAt=end.Value,outcome});
            }
            foreach(var chunk in rows.Chunk(100))output.Add(new{schemaVersion=1,marketID,checks=chunk});
        }
        return output;
    }
    async Task<bool> SyncResearchFeedbackOptional(Dictionary<string,object?> db,List<object> batches,string credential,string signature)
    {
        try{
            var (sent,complete)=await SyncResearchFeedback(db,batches,credential);
            if(complete)Config.LastResearchSignature=signature;
            researchFeedbackWarning="";Save();return sent;
        }catch(Exception ex) when(ex is System.Net.Http.HttpRequestException or MarketServiceException or System.IO.IOException or System.Text.Json.JsonException or TaskCanceledException){
            // Optional check feedback must never block downloads of signed
            // advice. Failed reports stay unacknowledged and retry in 5min.
            nextResearchFeedbackAt=DateTime.UtcNow.AddMinutes(5);
            researchFeedbackWarning="Check feedback is waiting to upload; retrying later. Advice downloads continue.";
            Log("Research feedback deferred: "+ex.GetType().Name);return false;
        }
    }
    async Task<(bool Sent,bool Complete)> SyncResearchFeedback(Dictionary<string,object?> db,List<object> batches,string credential)
    {
        if(!Config.ShareMarket||!Config.NoticeAccepted)return (false,false);
        if(!researchFeedbackCapability)return (false,true);
        bool sent=false;var retained=new HashSet<string>();
        foreach(var batch in BuildResearchFeedback(db,batches)){
            if(!Config.ShareMarket)return (sent,false);
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(batch));var body=doc.RootElement;
            string market=body.GetProperty("marketID").GetString()!;var pending=new List<JsonElement>();var ack=new Dictionary<string,string>();
            foreach(var row in body.GetProperty("checks").EnumerateArray()){
                string id=market+":"+row.GetProperty("id").GetString();retained.Add(id);
                string fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(row.GetRawText())));
                if(Config.ResearchFingerprints.GetValueOrDefault(id)==fingerprint)continue;
                pending.Add(row.Clone());ack[id]=fingerprint;
            }
            if(pending.Count==0)continue;
            using var response=await Post("/api/v1/research/feedback",new{schemaVersion=1,marketID=market,checks=pending},credential);sent=true;
            foreach(var pair in ack)Config.ResearchFingerprints[pair.Key]=pair.Value;Save();
        }
        if(!Config.ShareMarket)return (sent,false);
        var represented=new HashSet<string>();
        foreach(var batch in batches){using var d=JsonDocument.Parse(JsonSerializer.Serialize(batch));represented.Add(MarketID(d.RootElement.GetProperty("market")));}
        foreach(string stale in Config.ResearchFingerprints.Keys.Except(retained).Where(k=>represented.Any(m=>k.StartsWith(m+":",StringComparison.Ordinal))).ToArray())Config.ResearchFingerprints.Remove(stale);
        return (sent,true);
    }
}
