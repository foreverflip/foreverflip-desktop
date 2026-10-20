using ForeverFlip.Desktop;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class SyncIsolationTests
{
    const string Good="Classic Beta PvE",Bad="Unrecognized Test Realm";
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static string ID(string realm)=>"ff1-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("forever-beta:beta-2026:18:1:110:"+realm+":Alliance"))).ToLowerInvariant()[..32];
    static HttpResponseMessage Reply(HttpStatusCode code,object? data=null)=>new(code){Content=new StringContent(JsonSerializer.Serialize(data??new{}))};
    sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request);
    }
    static (Settings config,string save) Fixture(string folder,bool? badFirst=null)
    {
        string root=Path.Combine(folder,"_classic_beta_"),addon=Path.Combine(root,"Interface","AddOns","ForeverFlip"),helper=Path.Combine(root,"Interface","AddOns","ForeverFlip_Data");
        Directory.CreateDirectory(addon);Directory.CreateDirectory(helper);Directory.CreateDirectory(Path.Combine(root,"WTF"));
        File.WriteAllText(Path.Combine(root,"Wow.exe"),"");File.WriteAllText(Path.Combine(addon,"ForeverFlip.toc"),"## Version: 999.0.0\n");
        File.WriteAllText(Path.Combine(helper,"ForeverFlip_Data.toc"),"Data.lua\n");File.WriteAllText(Path.Combine(helper,"Data.lua"),"SYNTHETIC");
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string save=Path.Combine(root,"WTF","fixture.lua");
        string Entry(string realm)=>"[\"18:1:110:"+realm+":Alliance\"]={[\"2589:0:0:0\"]={name=\"Synthetic cloth\",history={{p=20,q=10,t="+now+"}}}},";
        string entries=badFirst==null?Entry(Good):badFirst==true?Entry(Bad)+Entry(Good):Entry(Good)+Entry(Bad);
        File.WriteAllText(save,ProfitTests.Literal("ForeverFlipDB={client={interface=16001,build=\"70450\",addon=\"0.13.112\"},markets={"+entries+"},crafting={vendors={[\"18:70450\"]={{itemID=2320,bundleCopper=10,bundleQuantity=1,unlimited=true,observedAt="+now+"}}}}}"));
        File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        var config=new Settings{ClientRoot=root,SavedFile=save,NoticeAccepted=true,ShareMarket=true};config.ItemFingerprints["retired-market|old-key"]="keep-until-complete";
        return (config,save);
    }
    static MarketSync Sync(Settings config,string folder,Func<HttpRequestMessage,Task<HttpResponseMessage>> send)=>
        new(config,Path.Combine(folder,"packs"),null,new Handler(send)){SaveProcessProbe=_=>GameSaveState.Stopped};
    public static async Task Run(string folder)
    {
        var normalized=new Settings();normalized.UploadRejections[ID(Good)]=new("old","unused legacy key",DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        normalized.UploadRejections["catalog:"+ID(Good)]=new("catalog","retained",DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        normalized.Normalize();Check(!normalized.UploadRejections.ContainsKey(ID(Good))&&normalized.UploadRejections.ContainsKey("catalog:"+ID(Good)),"Obsolete market-wide quarantine survived normalization");
        foreach(int rejectedBatch in new[]{0,1,2}){
            string path=Path.Combine(folder,"same-market-"+rejectedBatch);var (config,save)=Fixture(path);
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string entries=string.Join(",",Enumerable.Range(1000,250).Select(id=>"[\""+id+":0:0:0\"]={name=\"Synthetic\",history={{p=20,q=10,t="+now+"}}}"));
            File.WriteAllText(save,ProfitTests.Literal("ForeverFlipDB={client={interface=16001,build=\"70450\",addon=\"0.13.113\"},markets={[\"18:1:110:"+Good+":Alliance\"]={"+entries+"}}}"));
            File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
            var batches=MarketSync.BuildBatches(save);Check(batches.Count==3,"Fixture did not create three batches");
            var rejectedKeys=JsonSerializer.SerializeToElement(batches[rejectedBatch]).GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("key").GetString()!).ToHashSet();
            int valid=0,rejected=0,packs=0;bool recover=false;
            async Task<HttpResponseMessage> Send(HttpRequestMessage request){
                string route=request.RequestUri!.AbsolutePath;
                if(route=="/api/v1/catalog/sources")return Reply(HttpStatusCode.OK);
                if(route=="/api/v1/installations")return Reply(HttpStatusCode.OK,new{token="SYNTHETIC"});
                if(route=="/api/v1/observations"){
                    using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());var items=body.RootElement.GetProperty("items");
                    if(items.EnumerateArray().Any(i=>rejectedKeys.Contains(i.GetProperty("key").GetString()!))){
                        rejected++;if(!recover)return Reply(HttpStatusCode.BadRequest,new{detail="Synthetic rejected item"});
                    }else valid++;
                    return Reply(HttpStatusCode.OK,new{marketID=ID(Good),added=items.GetArrayLength()});
                }
                if(route.EndsWith("/pack")){packs++;return Reply(HttpStatusCode.NotFound);}
                if(route=="/api/v1/catalog/recipes")return Reply(HttpStatusCode.OK);
                throw new Exception("Unexpected synthetic request "+route);
            }
            var sync=Sync(config,path,Send);await sync.Tick();await sync.Tick();
            int rejectedCount=rejectedKeys.Count;
            Check(valid==2&&rejected==1&&config.ItemFingerprints.Count==251-rejectedCount&&config.LastSignature==""&&packs==2,"A rejected same-market batch suppressed valid batches or lost acknowledgements");
            var persisted=MarketSync.LoadSettings(Path.Combine(MarketSync.StateRoot,"settings.json"));sync=Sync(persisted,path,Send);await sync.Tick();
            Check(valid==2&&rejected==1&&packs==3&&persisted.UploadRejections.Keys.Any(k=>k.StartsWith("batch:")),"Restart lost batch-specific suppression");
            File.AppendAllText(save,"\n-- another game save\n");File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-20));await sync.Tick();
            Check(valid==2&&rejected==2&&persisted.LastSignature=="","New save re-uploaded acknowledged batches or hid the bad batch");
            recover=true;File.AppendAllText(save,"\n-- correction\n");File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-20));await sync.Tick();
            Check(rejected==3&&valid==2&&persisted.LastSignature!=""&&persisted.ItemFingerprints.Count==250,"Corrected batch failed to complete the save without duplicate uploads");
        }
        foreach(bool badFirst in new[]{false,true}){
            string path=Path.Combine(folder,badFirst?"bad-first":"good-first");var (config,save)=Fixture(path,badFirst);
            int good=0,bad=0,packs=0,catalog=0;bool recover=false;string? catalogRealm=null;
            async Task<HttpResponseMessage> Send(HttpRequestMessage request){
                string route=request.RequestUri!.AbsolutePath;
                if(route=="/api/v1/catalog/sources")return Reply(HttpStatusCode.OK);
                if(route=="/api/v1/installations")return Reply(HttpStatusCode.OK,new{token="SYNTHETIC"});
                if(route=="/api/v1/observations"){
                    using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());string realm=body.RootElement.GetProperty("market").GetProperty("realm").GetString()!;
                    if(realm==Bad){bad++;if(!recover)return Reply(HttpStatusCode.BadRequest,new{detail="Unrecognized Forever realm identity"});}else good++;
                    return Reply(HttpStatusCode.OK,new{marketID=ID(realm),added=1});
                }
                if(route=="/api/v1/catalog/recipes"){
                    catalog++;using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());catalogRealm=body.RootElement.GetProperty("market").GetProperty("realm").GetString();return Reply(HttpStatusCode.OK);
                }
                if(route.EndsWith("/pack")){packs++;return Reply(HttpStatusCode.NotFound);}
                throw new Exception("Unexpected fixture request "+route);
            }
            var sync=Sync(config,path,Send);await sync.Tick();await sync.Tick();
            Check(good==1&&bad==1&&packs==4&&catalog==1&&catalogRealm==Good,"One400 blocked another market, catalog or download, or was retried without backoff");
            Check(sync.State==MarketSyncState.Error&&sync.Status.Contains(Bad)&&config.LastSignature==""&&config.ItemFingerprints.Count==2,"Partial upload was reported complete or lost a durable acknowledgement");
            // Restart retains the rejection but public advice downloads continue.
            var persisted=MarketSync.LoadSettings(Path.Combine(MarketSync.StateRoot,"settings.json"));sync=Sync(persisted,path,Send);await sync.Tick();
            Check(bad==1&&good==1&&packs==6,"Restart lost rejection backoff or repeated an acknowledged upload");
            recover=true;File.AppendAllText(save,"\n-- fresh save\n");File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-20));await sync.Tick();
            Check(bad==2&&good==1&&persisted.LastSignature!=""&&persisted.ItemFingerprints.Count==2&&!persisted.ItemFingerprints.ContainsKey("retired-market|old-key"),"Changed save did not recover or prune only after full acknowledgement");
        }
        {
            string path=Path.Combine(folder,"catalog-rejection");var (config,_)=Fixture(path);int observations=0,catalog=0,packs=0;
            async Task<HttpResponseMessage> Send(HttpRequestMessage request){
                string route=request.RequestUri!.AbsolutePath;
                if(route=="/api/v1/catalog/sources")return Reply(HttpStatusCode.OK);
                if(route=="/api/v1/installations")return Reply(HttpStatusCode.OK,new{token="SYNTHETIC"});
                if(route=="/api/v1/observations"){observations++;return Reply(HttpStatusCode.OK,new{marketID=ID(Good),added=1});}
                if(route=="/api/v1/catalog/recipes"){catalog++;return Reply(HttpStatusCode.BadRequest,new{detail="Synthetic catalog rejection"});}
                if(route.EndsWith("/pack")){packs++;return Reply(HttpStatusCode.NotFound);}
                await Task.CompletedTask;throw new Exception("Unexpected fixture request "+route);
            }
            var sync=Sync(config,path,Send);await sync.Tick();await sync.Tick();
            Check(observations==1&&catalog==1&&packs==2&&config.LastSignature==""&&sync.State==MarketSyncState.Error,"Catalog rejection blocked packs or falsely completed/repeated a save");
            config.ShareMarket=false;await sync.Tick();Check(observations==1&&catalog==1&&packs==3,"Opt-out stopped downloads or allowed further uploads");
        }
        {
            string path=Path.Combine(folder,"rate-limit");var (config,_)=Fixture(path);int posts=0,packs=0;
            Task<HttpResponseMessage> Send(HttpRequestMessage request){
                string route=request.RequestUri!.AbsolutePath;
                if(route=="/api/v1/catalog/sources")return Task.FromResult(Reply(HttpStatusCode.OK));
                if(route=="/api/v1/installations")return Task.FromResult(Reply(HttpStatusCode.OK,new{token="SYNTHETIC"}));
                if(route=="/api/v1/observations"){
                    posts++;var response=Reply(HttpStatusCode.TooManyRequests);response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));return Task.FromResult(response);
                }
                if(route.EndsWith("/pack")){packs++;return Task.FromResult(Reply(HttpStatusCode.NotFound));}
                throw new Exception("Unexpected fixture request "+route);
            }
            var sync=Sync(config,path,Send);await sync.Tick();await sync.Tick();
            Check(posts==1&&packs==2&&config.UploadRetryAt>DateTimeOffset.UtcNow.ToUnixTimeSeconds()+60,"429 Retry-After blocked independent downloads or lacked backoff");
            var restored=MarketSync.LoadSettings(Path.Combine(MarketSync.StateRoot,"settings.json"));sync=Sync(restored,path,Send);await sync.Tick();
            Check(posts==1&&packs==3&&restored.LastSignature=="","Restart bypassed rate-limit backoff or acknowledged rejected data");
        }
        Console.WriteLine("PASS rejected-market isolation, both orders, accepted catalog identity, durable400 backoff/recovery, independent advice downloads, catalog isolation, opt-out and persistent429 Retry-After");
    }
}
