using ForeverFlip.Desktop;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FollowupTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static string Client(string folder)
    {
        string root=Path.Combine(folder,"_classic_beta_");
        Directory.CreateDirectory(Path.Combine(root,"Interface","AddOns","ForeverFlip"));
        Directory.CreateDirectory(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data"));
        Directory.CreateDirectory(Path.Combine(root,"WTF"));File.WriteAllText(Path.Combine(root,"Wow.exe"),"");
        File.WriteAllText(Path.Combine(root,"Interface","AddOns","ForeverFlip","ForeverFlip.toc"),"## Version: 999.0.0\nAddon.lua\n");
        File.WriteAllText(Path.Combine(root,"Interface","AddOns","ForeverFlip","Addon.lua"),"ORIGINAL_CODE");
        File.WriteAllText(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data","ForeverFlip_Data.toc"),"Data.lua\n");
        File.WriteAllText(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data","Data.lua"),"ORIGINAL_HELPER");
        return root;
    }
    static byte[] Archive(params (string file,string body)[] files)
    {
        using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
            foreach(var file in files){using var writer=new StreamWriter(zip.CreateEntry("ForeverFlip/"+file.file).Open());writer.Write(file.body);}
        return memory.ToArray();
    }
    static bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}catch(IOException){return true;}}
    static void Junction(string link,string destination)
    {
        var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"/c","mklink","/J",link,destination})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;string output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();process.WaitForExit();
        Check(process.ExitCode==0,"Junction fixture unavailable: "+output);
    }
    sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request);
    }
    static HttpResponseMessage Reply(HttpStatusCode status,object? body=null)=>new(status){Content=new StringContent(JsonSerializer.Serialize(body??new{}),Encoding.UTF8,"application/json")};
    public static async Task Run(string folder)
    {
        folder=Path.GetFullPath(folder);
        var failures=new List<string>();
        async Task Case(string name,Func<Task> action){try{await action();Console.WriteLine("PASS "+name);}catch(Exception ex){failures.Add(name+": "+ex.Message);Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        await Case("market multi-batch acknowledgement, partial failure and durable restart",()=>MarketRetry(Path.Combine(folder,"market")));
        await Case("rejected market/catalog isolation and download availability",()=>SyncIsolationTests.Run(Path.Combine(folder,"sync-isolation")));
        await Case("feedback failure cannot block advice polling or cause retry storms",()=>FeedbackFailure(Path.Combine(folder,"feedback")));
        await Case("saved support v2 to wire v1 with sharing off and immutable retry",()=>SupportRoundtrip(Path.Combine(folder,"support")));
        await Case("desktop packets export against independent backend models",()=>ExportContracts(Path.Combine(Path.GetDirectoryName(folder)!,"desktop-contract")));
        await Case("malformed saved metadata cannot poison valid observation batches",()=>MalformedMetadata(Path.Combine(folder,"metadata")));
        await Case("explicit unknown units never become normalized history",()=>UnknownUnits(Path.Combine(folder,"unknown-units")));
        await Case("private accounting loads while offline with market sharing off",()=>OfflineProfit(Path.Combine(folder,"profit-offline")));
        await Case("stopped-client incomplete archive preserves current installation",()=>{
            string root=Client(Path.Combine(folder,"incomplete")),target=Path.Combine(root,"Interface","AddOns","ForeverFlip");
            byte[] bytes=Archive(("ForeverFlip.toc","## Version: 999.0.1\nAddon.lua\nMissing.lua\n"),("Addon.lua","INCOMPLETE_REPLACEMENT"));
            bool rejected=Reject(()=>ClientInstall.InstallArchiveCore(root,bytes,new Version("999.0.1"),false,()=>false));
            Check(rejected&&File.ReadAllText(Path.Combine(target,"Addon.lua"))=="ORIGINAL_CODE","A stopped client accepted a release whose TOC declares Missing.lua and replaced working code");
            return Task.CompletedTask;
        });
        await Case("helper junction preflight preserves external data and installed code",()=>{
            string root=Client(Path.Combine(folder,"helper-link")),helper=Path.Combine(root,"Interface","AddOns","ForeverFlip_Data");
            string external=Path.Combine(folder,"external-helper");Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external,"Data.lua"),"EXTERNAL_DATA");File.WriteAllText(Path.Combine(external,"ForeverFlip_Data.toc"),"EXTERNAL_TOC");
            File.Delete(Path.Combine(helper,"Data.lua"));File.Delete(Path.Combine(helper,"ForeverFlip_Data.toc"));Directory.Delete(helper);Junction(helper,external);
            try{
                bool rejected=Reject(()=>ClientInstall.InstallArchiveCore(root,Archive(("ForeverFlip.toc","## Version: 999.0.1\nAddon.lua\n"),("Addon.lua","NEW_CODE")),new Version("999.0.1"),false,()=>false));
                Check(rejected&&File.ReadAllText(Path.Combine(external,"ForeverFlip_Data.toc"))=="EXTERNAL_TOC"&&File.ReadAllText(Path.Combine(root,"Interface","AddOns","ForeverFlip","Addon.lua"))=="ORIGINAL_CODE","Installer traversed helper junction or changed addon before refusing the unsafe helper");
            }finally{Directory.Delete(helper);}
            return Task.CompletedTask;
        });
        await Case("pack staging rejects helper junction without external writes",()=>{
            string root=Client(Path.Combine(folder,"pack-link")),helper=Path.Combine(root,"Interface","AddOns","ForeverFlip_Data");
            string external=Path.Combine(folder,"external-pack");Directory.CreateDirectory(external);File.WriteAllText(Path.Combine(external,"Data.lua"),"EXTERNAL_PACK");File.WriteAllText(Path.Combine(external,"ForeverFlip_Data.toc"),"Data.lua\n");
            File.Delete(Path.Combine(helper,"Data.lua"));File.Delete(Path.Combine(helper,"ForeverFlip_Data.toc"));Directory.Delete(helper);Junction(helper,external);
            try{
                long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
                string pub=Path.Combine(folder,"pack-public.pem");File.WriteAllText(pub,key.ExportSubjectPublicKeyInfoPem());
                var payload=JsonSerializer.SerializeToElement(new{schema="foreverflip.market-pack",schemaVersion=1,marketID="ff1-"+new string('a',32),scope="18:1:110:Classic Beta PvE:Alliance",packID="isolated-pack",createdAt=now-1,expiresAt=now+3600,recommendations=Array.Empty<object>()});
                byte[] raw=Encoding.UTF8.GetBytes(payload.GetRawText());var envelope=JsonSerializer.SerializeToElement(new{algorithm="ES256",payload=Convert.ToBase64String(raw),signature=Convert.ToBase64String(key.SignData(raw,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence))});
                var sync=new MarketSync(new Settings{ClientRoot=root},Path.Combine(folder,"pack-cache"),pub);
                Check(Reject(()=>sync.StagePackCore(payload,envelope,()=>false))&&File.ReadAllText(Path.Combine(external,"Data.lua"))=="EXTERNAL_PACK","Signed pack staging wrote through a helper junction");
            }finally{Directory.Delete(helper);}
            return Task.CompletedTask;
        });
        if(failures.Count>0)throw new Exception(string.Join(Environment.NewLine,failures));
    }
    static async Task OfflineProfit(string folder)
    {
        string root=Client(folder),save=Path.Combine(root,"WTF","profit.lua");long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(save,ProfitTests.Literal("ForeverFlipDB={ledgers={PRIVATE_CHARACTER={profitView={schema=1,at="+now+",days={},stock={}}}}}"));
        File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        var config=new Settings{ClientRoot=root,SavedFile=save,NoticeAccepted=true,ShareMarket=false,ShareCatalog=false};
        var sync=new MarketSync(config,Path.Combine(folder,"cache"),null,new Handler(request=>{
            Check(request.Method==HttpMethod.Get,"Private accounting attempted an upload while sharing was off");
            throw new HttpRequestException("Synthetic offline fixture");
        })){SaveProcessProbe=_=>GameSaveState.Stopped};
        await sync.Tick();Check(sync.ProfitBooks.Count==1&&sync.ProfitBooks[0].Character=="PRIVATE_CHARACTER","Offline failure hid the local profit snapshot");
    }
    static async Task MarketRetry(string folder)
    {
        string root=Client(folder),save=Path.Combine(root,"WTF","isolated.lua");long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var text=new StringBuilder("ForeverFlipDB={[\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70450\",[\"addon\"]=\"0.13.73\"},[\"markets\"]={[\"18:1:110:Classic Beta PvE:Alliance\"]={");
        for(int i=1;i<=205;i++)text.Append("[\"").Append(i).Append(":0:0:0\"]={[\"name\"]=\"Synthetic item\",[\"history\"]={{[\"p\"]=100,[\"q\"]=1,[\"t\"]=").Append(now).Append("}}},");
        text.Append("}}}");File.WriteAllText(save,text.ToString());File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        var config=new Settings{ClientRoot=root,SavedFile=save,NoticeAccepted=true,ShareMarket=true,ShareCatalog=false};
        config.ItemFingerprints["retired-market|expired-item"]="old fingerprint";
        int posts=0,registrations=0;var sizes=new List<int>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request){
            string path=request.RequestUri!.AbsolutePath;
            if(path=="/api/v1/catalog/sources")return Reply(HttpStatusCode.OK);
            if(path=="/api/v1/installations"){registrations++;return Reply(HttpStatusCode.OK,new{token="SYNTHETIC_INSTALL_TOKEN"});}
            if(path=="/api/v1/observations"){
                using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());var payload=body.RootElement;
                Check(request.Headers.Authorization?.Scheme=="Bearer","Observation request omitted bearer authorization");
                sizes.Add(payload.GetProperty("items").GetArrayLength());posts++;
                if(posts==2)return Reply(HttpStatusCode.BadGateway);
                return Reply(HttpStatusCode.OK,new{marketID="ff1-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("forever-beta:beta-2026:18:1:110:Classic Beta PvE:Alliance"))).ToLowerInvariant()[..32],added=payload.GetProperty("items").GetArrayLength()});
            }
            if(path.EndsWith("/pack"))return Reply(HttpStatusCode.NotFound);
            throw new Exception("Unexpected isolated HTTP request "+path);
        }
        string cache=Path.Combine(folder,"cache");var sync=new MarketSync(config,cache,null,new Handler(Send)){SaveProcessProbe=_=>GameSaveState.Stopped};
        await sync.Tick();Check(sync.State==MarketSyncState.Error&&config.ItemFingerprints.Count==101&&config.LastSignature==""&&config.ItemFingerprints.ContainsKey("retired-market|expired-item"),"Partial failure committed unacknowledged fingerprints, pruned old state or committed the full save signature");
        var persisted=MarketSync.LoadSettings(Path.Combine(MarketSync.StateRoot,"settings.json"));
        sync=new MarketSync(persisted,cache,null,new Handler(Send)){SaveProcessProbe=_=>GameSaveState.Stopped};
        await sync.Tick();Check(sync.State==MarketSyncState.Synced&&persisted.ItemFingerprints.Count==205&&persisted.LastSignature!=""&&persisted.SavedItemCount==205,"Restart failed to acknowledge exactly all retained observations");
        Check(!persisted.ItemFingerprints.ContainsKey("retired-market|expired-item"),"A fully acknowledged save did not prune retired fingerprint keys");
        Check(registrations==1&&posts==4&&sizes.SequenceEqual(new[]{100,100,100,5}),"Restart resent acknowledged first batch or lost a later batch");
        await sync.Tick();Check(posts==4&&!sync.IsBusy,"Unchanged save resent observations or retained the sync gate");
    }
    static async Task FeedbackFailure(string folder)
    {
        string root=Client(folder),save=Path.Combine(root,"WTF","feedback.lua");long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string scope="18:1:110:Classic Beta PvE:Alliance";
        File.WriteAllText(save,"ForeverFlipDB={client={interface=16001,build=\"70450\",addon=\"0.13.112\"},markets={[\""+scope+"\"]={[\"2589:0:0:0\"]={name=\"Linen\",history={{p=20,q=10,t="+now+"}}}}},researchChecks={[\""+scope+"\"]={{id=\"epoch:1\",packID=\"pack1\",recommendationID=\"lead1\",itemKey=\"2589:0:0:0\",strategy=\"resale\",source=\"ai\",startedAt="+(now-1)+",finishedAt="+now+",outcome=\"qualified\"}}}}");
        File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        File.WriteAllText(save,ProfitTests.Literal(File.ReadAllText(save)));File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        int feedback=0,packs=0,observations=0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request){
            string path=request.RequestUri!.AbsolutePath;
            if(path=="/api/v1/catalog/sources")return Reply(HttpStatusCode.OK,new{capabilities=new{researchFeedback=1}});
            if(path=="/api/v1/installations")return Reply(HttpStatusCode.OK,new{token="FIXTURE_TOKEN"});
            if(path=="/api/v1/observations"){
                using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());observations++;
                return Reply(HttpStatusCode.OK,new{marketID="ff1-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("forever-beta:beta-2026:18:1:110:Classic Beta PvE:Alliance"))).ToLowerInvariant()[..32],added=1});
            }
            if(path=="/api/v1/research/feedback"){feedback++;return Reply(HttpStatusCode.BadRequest);}
            if(path.EndsWith("/pack")){packs++;return Reply(HttpStatusCode.NotFound);}
            throw new Exception("Unexpected feedback-fixture route "+path);
        }
        var config=new Settings{ClientRoot=root,SavedFile=save,ShareMarket=true,NoticeAccepted=true};
        var sync=new MarketSync(config,Path.Combine(folder,"cache"),null,new Handler(Send)){SaveProcessProbe=_=>GameSaveState.Stopped};
        await sync.Tick();Check(sync.State==MarketSyncState.Synced&&feedback==1&&packs==1&&observations==1&&config.LastSignature!=""&&config.LastResearchSignature=="",$"Feedback state {sync.State}; status {sync.Status}; feedback {feedback}, packs {packs}, observations {observations}, ack {config.LastResearchSignature}");
        await sync.Tick();Check(feedback==1&&packs==2&&observations==1,"Feedback retry storm or duplicate observations during backoff");
        var field=typeof(MarketSync).GetField("nextResearchFeedbackAt",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(sync,DateTime.MinValue);await sync.Tick();Check(feedback==2&&packs==3&&observations==1,"Deferred feedback did not retry without a new game save");
    }
    static async Task SupportRoundtrip(string folder)
    {
        string root=Client(folder),save=Path.Combine(root,"WTF","support.lua");long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string id=$"fflog-{now}-9-123";
        string Save(string message)=>"ForeverFlipDB={[\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70450\",[\"addon\"]=\"0.13.73\"},[\"supportRequest\"]={[\"schemaVersion\"]=2,[\"consent\"]=\"support-2026-10-17\",[\"requestID\"]=\""+id+"\",[\"requestedAt\"]="+now+",[\"addonVersion\"]=\"0.13.73\",[\"build\"]=\"70450\",[\"reportType\"]=\"feedback\",[\"description\"]="+MarketSync.LuaString(message)+",[\"events\"]={{[\"t\"]="+now+",[\"kind\"]=\"craft_pause\",[\"data\"]={[\"reason\"]=\"Supplies needed\"}}}}}";
        File.WriteAllText(save,Save("Original feedback: \"雪\" 🙂\nSteps need clarity."));File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        int calls=0,registrations=0;string first="";
        async Task<HttpResponseMessage> Send(HttpRequestMessage request){
            Check(request.RequestUri!.Scheme=="https","Support request lost HTTPS");
            if(request.RequestUri.AbsolutePath=="/api/v1/installations"){
                using var registration=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(!registration.RootElement.GetProperty("marketSharing").GetBoolean()&&registration.RootElement.GetProperty("diagnosticsConsent").GetBoolean(),"Support registration enabled market sharing or omitted explicit diagnostics consent");
                registrations++;return Reply(HttpStatusCode.OK,new{token="SYNTHETIC_DIAGNOSTIC_TOKEN"});
            }
            Check(request.RequestUri.AbsolutePath=="/api/v1/support/reports","Sharing-off tick attempted market/catalog HTTP");
            string raw=await request.Content!.ReadAsStringAsync();calls++;
            using var report=JsonDocument.Parse(raw);Check(report.RootElement.GetProperty("schemaVersion").GetInt32()==1&&report.RootElement.GetProperty("reportType").GetString()=="feedback"&&report.RootElement.GetProperty("description").GetString()!.Contains("雪"),"Saved support v2 did not preserve type/Unicode context on wire v1");
            if(calls==1){first=raw;return Reply(HttpStatusCode.BadGateway);}
            Check(raw==first,"An uncertain support retry changed immutable bytes after the save changed");
            return Reply(HttpStatusCode.OK,new{reportID=new string('a',64),requestID=id,receivedAt=now});
        }
        var config=new Settings{ClientRoot=root,SavedFile=save,NoticeAccepted=true,ShareMarket=false,ShareCatalog=false};
        var sync=new MarketSync(config,Path.Combine(folder,"cache"),null,new Handler(Send)){SaveProcessProbe=_=>GameSaveState.Stopped};
        await sync.Tick();Check(sync.State==MarketSyncState.SharingOff&&sync.SupportStatus.Contains("waiting")&&calls==1,"Support failure prevented the sharing-off tick or lost the queued report");
        File.WriteAllText(save,Save("Changed feedback with the same request ID"));File.SetLastWriteTimeUtc(save,DateTime.UtcNow.AddSeconds(-30));
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(save).ToUpperInvariant()))).ToLowerInvariant()[..24];
        string stateFile=Path.Combine(MarketSync.StateRoot,"support",key,"state.json");
        var state=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(stateFile))!;state["NextAttempt"]=0;File.WriteAllText(stateFile,state.ToJsonString());
        sync=new MarketSync(MarketSync.LoadSettings(Path.Combine(MarketSync.StateRoot,"settings.json")),Path.Combine(folder,"cache"),null,new Handler(Send)){SaveProcessProbe=_=>GameSaveState.Stopped};
        await sync.Tick();Check(calls==2&&registrations==1&&sync.SupportStatus.StartsWith("Report sent")&&!sync.Config.ShareMarket&&sync.Config.ProtectedToken=="","Support retry/receipt changed sharing credentials or missed persisted diagnostics token");
        await sync.Tick();Check(calls==2,"An acknowledged support request was uploaded again");
    }
    static async Task ExportContracts(string folder)
    {
        Directory.CreateDirectory(folder);string support=Path.Combine(folder,"support-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(support);await SupportTests.Run(support);
        foreach(string name in new[]{"support-contract.json","support-feedback-contract.json"})File.Copy(Path.Combine(support,name),Path.Combine(folder,name),true);
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string save=Path.Combine(folder,"synthetic.lua");
        File.WriteAllText(save,"ForeverFlipDB={[\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70450\",[\"addon\"]=\"0.13.73\"},[\"markets\"]={[\"18:1:110:Classic Beta PvE:Alliance\"]={[\"2589:0:0:0\"]={[\"name\"]=\"Linen \\\"雪\\\"\",[\"history\"]={{[\"p\"]=12.5,[\"q\"]=20,[\"t\"]="+now+"}}}}},[\"crafting\"]={[\"catalog\"]={[\"18:70450\"]={[\"100\"]={[\"recipeID\"]=100,[\"name\"]=\"Synthetic bag\",[\"profession\"]=\"Tailoring\",[\"category\"]=\"Bags\",[\"outputItemID\"]=200,[\"outputMin\"]=1,[\"outputMax\"]=1,[\"complete\"]=true,[\"observedAt\"]="+now+",[\"reagents\"]={{[\"itemID\"]=2589,[\"quantity\"]=2}}}}},[\"vendors\"]={[\"18:70450\"]={[\"2320\"]={[\"itemID\"]=2320,[\"bundleCopper\"]=10,[\"bundleQuantity\"]=1,[\"unlimited\"]=true,[\"observedAt\"]="+(now-90*86400)+"}}}}}");
        Dictionary<string,object?>? db=null;var markets=MarketSync.BuildBatches(save,value=>db=value);
        File.WriteAllText(Path.Combine(folder,"market-valid.json"),JsonSerializer.Serialize(markets.Single()));
        File.WriteAllText(Path.Combine(folder,"catalog-valid.json"),JsonSerializer.Serialize(MarketSync.BuildCatalogBatches(db!,markets,now).Single()));
        var invalid=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(markets.Single()))!;invalid["items"]![0]!["name"]="Untrusted\ncontrol";
        File.WriteAllText(Path.Combine(folder,"market-invalid-control.json"),invalid.ToJsonString());
        File.WriteAllText(Path.Combine(folder,"expectations.json"),JsonSerializer.Serialize(new{createdAt=now,synthetic=true,accepted=new[]{"market-valid.json","catalog-valid.json","support-contract.json","support-feedback-contract.json"},rejected=new[]{"market-invalid-control.json"},notes="Saved support schema2 serializes to schema1 with context; Unicode and escapes are intentional. The catalog has a 90-day-old unlimited vendor quote whose timestamp must survive."}));
    }
    static Task MalformedMetadata(string folder)
    {
        Directory.CreateDirectory(folder);long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string path=Path.Combine(folder,"saved.lua");
        string Item(string key,string name)=>"["+MarketSync.LuaString(key)+"]={[\"name\"]="+MarketSync.LuaString(name)+",[\"history\"]={{[\"p\"]=12,[\"q\"]=1,[\"t\"]="+now+"}}}";
        File.WriteAllText(path,"ForeverFlipDB={[\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70450\",[\"addon\"]=\"0.13.73\"},[\"markets\"]={[\"18:1:110:Classic Beta PvE:Alliance\"]={"+Item("1:0:0:0","Healthy")+","+Item("2:0:0:0","\n\t")+","+Item(new string('9',100)+":0:0:0","Oversize key")+"},[\"18:0:110:Classic Beta PvE:Alliance\"]={"+Item("1:0:0:0","Bad namespace")+"}},[\"itemCatalog\"]={[\"18:70450\"]={[\"2\"]={[\"vendorPrice\"]=20,[\"checkedAt\"]="+(now+1000)+",[\"equip\"]=\"invalid metadata\"}}}}");
        var batches=MarketSync.BuildBatches(path);File.WriteAllText(Path.Combine(folder,"generated.json"),JsonSerializer.Serialize(batches));
        Check(batches.Count==1,"Out-of-range market namespace reached the upload queue");
        using var batch=JsonDocument.Parse(JsonSerializer.Serialize(batches.Single()));var items=batch.RootElement.GetProperty("items");
        Check(items.GetArrayLength()==2,"Oversized item key can reject a whole valid batch");
        var item=items[1];Check(item.GetProperty("name").GetString()=="2"&&item.GetProperty("equip").GetString()=="","Empty/control item names or invalid equip slots can reject valid observations");
        Check(item.GetProperty("vendorAt").ValueKind==JsonValueKind.Null&&item.GetProperty("vendorCopper").ValueKind==JsonValueKind.Null,"Future payout metadata can reject valid observed history");
        return Task.CompletedTask;
    }
    static Task UnknownUnits(string folder)
    {
        Directory.CreateDirectory(folder);long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string path=Path.Combine(folder,"saved.lua");
        string Point(string flag,long at)=>"{[\"p\"]=12,[\"q\"]=20,[\"t\"]="+at+flag+"}";
        File.WriteAllText(path,"ForeverFlipDB={[\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70450\",[\"addon\"]=\"0.13.92\"},[\"markets\"]={[\"18:1:110:Classic Beta PvE:Alliance\"]={[\"2589:0:0:0\"]={[\"name\"]=\"Cloth\",[\"history\"]={"+
            Point(",[\"unitKnown\"]=false",now-3)+","+Point(",[\"unit_known\"]=false",now-2)+","+Point("",now-1)+","+Point(",[\"unitKnown\"]=true",now)+"}}}}}");
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(MarketSync.BuildBatches(path).Single()));
        var points=doc.RootElement.GetProperty("items")[0].GetProperty("points");
        Check(points.GetArrayLength()==2,"Explicit false unit flags were promoted or all legacy points were lost");
        Check(points.EnumerateArray().All(p=>p.GetProperty("unitKnown").GetBoolean()&&p.GetProperty("at").GetInt64()>=now-1),"Unknown-unit records entered normalized history");
        return Task.CompletedTask;
    }
}
