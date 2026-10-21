using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ForeverFlip.Desktop;

public sealed partial class MarketSync
{
    readonly Dictionary<string,DateTime> nextPackAt=[];
    static string SyncFailure(Exception ex)=>ex is HttpRequestException
        ?"Service unavailable. Saved data stays local; retrying automatically.":ex.Message;
    static string BatchID(object batch)=>MarketID(JsonSerializer.SerializeToElement(batch).GetProperty("market"));
    static string RejectionKey(string marketID,JsonElement batch)=>"batch:"+marketID+":"+
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            batch.GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("key").GetString()).Order(StringComparer.Ordinal))))).ToLowerInvariant();
    bool Rejected(string key,string signature,out string reason)
    {
        if(Config.UploadRejections.TryGetValue(key,out var row)&&row.Signature==signature
            &&row.RetryAt>DateTimeOffset.UtcNow.ToUnixTimeSeconds()){
            reason=row.Reason;return true;
        }
        reason="";return false;
    }
    void RejectUpload(string key,string signature,string reason)
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach(string expired in Config.UploadRejections.Where(p=>p.Value.RetryAt<=now).Select(p=>p.Key).ToArray())Config.UploadRejections.Remove(expired);
        Config.UploadRejections[key]=new(signature,new string(reason.Where(c=>!char.IsControl(c)).Take(500).ToArray()),DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        if(Config.UploadRejections.Count>100){
            string oldest=Config.UploadRejections.OrderBy(p=>p.Value.RetryAt).First().Key;
            Config.UploadRejections.Remove(oldest);
        }
        Save();
    }
    void RateBackoff(MarketServiceException ex)
    {
        if(ex.StatusCode!=HttpStatusCode.TooManyRequests)return;
        var delay=ex.RetryAfter is TimeSpan t&&t>TimeSpan.Zero?t:TimeSpan.FromMinutes(1);
        Config.UploadRetryAt=DateTimeOffset.UtcNow.Add(delay).ToUnixTimeSeconds();Config.UploadRetryReason=ex.Message;Save();
    }
    async Task SyncMarketUpload(string signature,FileInfo info)
    {
        if(DateTimeOffset.UtcNow.ToUnixTimeSeconds()<Config.UploadRetryAt){State=MarketSyncState.Error;Status=Config.UploadRetryReason;return;}
        State=MarketSyncState.Reading;Status="Reading and filtering saved market data…";Changed?.Invoke();
        Dictionary<string,object?>? savedDB=null;
        var batches=await Task.Run(()=>BuildBatches(Config.SavedFile,db=>savedDB=db,marketIdentityCapability,depthCapability));
        var after=new FileInfo(Config.SavedFile);
        if(signature!=after.Length+":"+after.LastWriteTimeUtc.Ticks)throw new IOException("Save changed during reading; retrying later.");
        RememberAcknowledgement(savedDB!);Config.LastLocalRead=signature;RememberDataset(batches);
        // Download discovery depends on the save, not on every upload succeeding.
        Config.MarketIDs=batches.Select(BatchID).Distinct().ToList();Save();
        // Only a verified account access token can upload. No anonymous
        // registration; legacy installation tokens are deliberately ignored.
        string? credential=Account==null?null:await Account.AccessTokenAsync();
        if(credential==null){State=MarketSyncState.SignInRequired;Status=SignInRequiredStatus;return;}
        State=MarketSyncState.Uploading;Status="Uploading changed observations…";Changed?.Invoke();
        var retained=new HashSet<string>();var accepted=new List<object>();var warnings=new List<string>();
        int added=0,omittedDepth=0;bool sent=false,complete=true;
        foreach(var batch in batches){
            if(!Config.ShareMarket){complete=false;break;}
            using var original=JsonDocument.Parse(JsonSerializer.Serialize(batch));var root=original.RootElement;
            string marketID=MarketID(root.GetProperty("market"));string realm=root.GetProperty("market").GetProperty("realm").GetString()!;
            var changedItems=new List<JsonElement>();var fingerprints=new Dictionary<string,string>();
            foreach(var item in root.GetProperty("items").EnumerateArray()){
                string identity=marketID+":"+item.GetProperty("key").GetString();retained.Add(identity);
                string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.GetRawText())));
                if(Config.ItemFingerprints.GetValueOrDefault(identity)==fingerprint)continue;
                changedItems.Add(item.Clone());fingerprints[identity]=fingerprint;
            }
            var market=root.GetProperty("market");
            string identityFingerprint=market.TryGetProperty("realmID",out var realmID)?realmID.GetRawText()+":"+market.GetProperty("realmObservedAt").GetRawText():"";
            bool identityChanged=identityFingerprint!=""&&Config.RealmFingerprints.GetValueOrDefault(marketID)!=identityFingerprint;
            if(changedItems.Count==0&&identityChanged&&root.GetProperty("items").GetArrayLength()>0)changedItems.Add(root.GetProperty("items")[0].Clone());
            if(changedItems.Count==0){accepted.Add(batch);continue;}
            string rejectionKey=RejectionKey(marketID,root);
            if(Rejected(rejectionKey,signature,out string prior)){
                complete=false;warnings.Add(realm+": "+prior);continue;
            }
            var delta=new{schemaVersion=1,market=root.GetProperty("market").Clone(),addonVersion=root.GetProperty("addonVersion").GetString(),savedAt=root.GetProperty("savedAt").GetInt64(),items=changedItems};
            try{
                using var result=await Post("/api/v1/observations",delta,credential);
                if(result.RootElement.GetProperty("marketID").GetString()!=marketID)throw new InvalidDataException("Upload market mismatch.");
                sent=true;added+=result.RootElement.GetProperty("added").GetInt32();
                foreach(var pair in fingerprints)Config.ItemFingerprints[pair.Key]=pair.Value;
                if(result.RootElement.TryGetProperty("ignoredDepthQuotes",out var omitted)&&omitted.TryGetInt32(out int skipped)&&skipped>0)omittedDepth+=skipped;
                if(identityChanged)Config.RealmFingerprints[marketID]=identityFingerprint;
                Config.UploadRejections.Remove(rejectionKey);accepted.Add(batch);Save();
            }catch(MarketServiceException ex) when(ex.StatusCode==HttpStatusCode.BadRequest){
                complete=false;RejectUpload(rejectionKey,signature,ex.Message);warnings.Add(realm+": "+ex.Message);
            }catch(MarketServiceException ex){RateBackoff(ex);throw;}
        }
        // Never route catalogue facts through a rejected or unacknowledged market.
        if(Config.ShareMarket&&savedDB!=null&&accepted.Count>0){
            string catalogKey="catalog:"+BatchID(accepted[0]);
            if(Rejected(catalogKey,signature,out string prior)){complete=false;warnings.Add("Recipe sharing: "+prior);}
            else try{
                sent=await SyncCatalog(savedDB,accepted,credential,new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())||sent;
                Config.UploadRejections.Remove(catalogKey);
            }catch(Exception ex){
                complete=false;string why=SyncFailure(ex);warnings.Add("Recipe sharing: "+why);
                if(ex is MarketServiceException service&&service.StatusCode==HttpStatusCode.BadRequest)RejectUpload(catalogKey,signature,why);
                if(ex is MarketServiceException limited)RateBackoff(limited);
            }
        }
        if(Config.ShareMarket&&savedDB!=null&&DateTime.UtcNow>=nextResearchFeedbackAt&&DateTimeOffset.UtcNow.ToUnixTimeSeconds()>=Config.UploadRetryAt)
            sent=await SyncResearchFeedbackOptional(savedDB,batches,credential,signature)||sent;
        if(Config.ShareMarket){
            if(complete){
                foreach(string stale in Config.ItemFingerprints.Keys.Except(retained).ToArray())Config.ItemFingerprints.Remove(stale);
                Config.LastSignature=signature;
            }
            if(sent)Config.LastUploaded=DateTime.Now.ToString("g");Save();
            if(complete){State=MarketSyncState.Synced;Status=$"Synced. {added:N0} new observations; {Config.RecipesSynced:N0} recipes and {Config.PatternsSynced:N0} pattern items shared."+(omittedDepth>0?$" {omittedDepth:N0} depth quotes omitted; prices were saved.":"");}
            else{State=MarketSyncState.Error;Status="Some saved data could not sync. "+string.Join(" ",warnings.Distinct().Take(3));}
        }else{State=MarketSyncState.SharingOff;Status="Market sharing is off. Shared data downloads remain available.";}
    }
}
