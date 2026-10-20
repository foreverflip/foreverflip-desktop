using ForeverFlip.Desktop;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ResearchTests
{
    sealed class Handler(System.Net.HttpStatusCode status,Action? onSend=null):System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage r,CancellationToken c){
            onSend?.Invoke();return Task.FromResult(new System.Net.Http.HttpResponseMessage(status){Content=new System.Net.Http.StringContent("{}")});
        }
    }
    internal static void Run(string? output=null)
    {
        void Assert(bool c,string m){if(!c)throw new Exception(m);}
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string scope="18:1:110:Test Beta:Alliance";
        var row=new Dictionary<string,object?>{{"id","epoch:1"},{"packID","pack1"},{"recommendationID","lead1"},{"itemKey","2589:0:0:0"},{"strategy","resale"},{"source","ai"},{"startedAt",(double)now-1},{"finishedAt",(double)now},{"outcome","qualified"},{"character","DO NOT SHARE"},{"balance",123456d},{"reason","PRIVATE"}};
        var db=new Dictionary<string,object?>{{"researchChecks",new Dictionary<string,object?>{{scope,new Dictionary<string,object?>{{"1",row}}}}}};
        var batch=new{market=new{project=18,major=1,region=110,realm="Test Beta",faction="Alliance",family="forever-beta",epoch="beta-2026",@interface=16001,build="70374"}};
        string text=JsonSerializer.Serialize(MarketSync.BuildResearchFeedback(db,[batch],now));
        if(output!=null)System.IO.File.WriteAllText(output,text);
        Assert(!text.Contains("PRIVATE")&&!text.Contains("DO NOT SHARE")&&!text.Contains("balance")&&text.Contains("qualified"),"Only check allowlist is shared");
        Assert(MarketSync.BuildResearchFeedback(db,[batch,batch],now).Count==1,"Markets are deduplicated across item upload chunks");
        row["finishedAt"]=(double)now+1;Assert(MarketSync.BuildResearchFeedback(db,[batch],now).Count==0,"Future outcomes excluded");row["finishedAt"]=(double)now;
        row["itemKey"]="2589:0:0:0\"; evil()";Assert(MarketSync.BuildResearchFeedback(db,[batch],now).Count==0,"Malformed identities excluded");row["itemKey"]="2589:0:0:0";
        var pack=JsonSerializer.SerializeToElement(new{schema="foreverflip.market-pack",schemaVersion=1,packID="pack1",scope,createdAt=now,expiresAt=now+86400,recommendations=new[]{new{id="lead1",itemKey="2589:0:0:0",kind="research",priority=90,reasonCode="historical_discount",strategies=new[]{"resale"},expiresAt=now+86400,reason="Exact read nomination"}}});
        string lua=MarketSync.RenderPackEntry(pack);Assert(lua.Contains("strategies={\"resale\",")&&lua.Contains("reasonCode=\"historical_discount\""),"Research routing survives signed pack rendering");
        var bad=JsonNode.Parse(pack.GetRawText())!;bad["recommendations"]![0]!["strategies"]=new JsonArray("resale","resale");
        try{MarketSync.RenderPackEntry(JsonSerializer.SerializeToElement(bad));throw new Exception("Duplicate strategies accepted");}catch(System.IO.InvalidDataException){}
        var defaults=new Settings();Assert(defaults.ShareMarket&&!defaults.NoticeAccepted,"New installs show one checked sharing choice before uploads");
        var config=new Settings{ShareMarket=true,NoticeAccepted=true};
        string cache=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"ff-research-"+Guid.NewGuid().ToString("N"));
        var sync=new MarketSync(config,cache,null,new Handler(System.Net.HttpStatusCode.BadRequest));
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(MarketSync).GetField("researchFeedbackCapability",flags)!.SetValue(sync,true);
        var optional=typeof(MarketSync).GetMethod("SyncResearchFeedbackOptional",flags)!;
        bool sent=((Task<bool>)optional.Invoke(sync,new object[]{db,new List<object>{batch},"fixture-token","save-v1"})!).GetAwaiter().GetResult();
        Assert(!sent&&config.LastResearchSignature==""&&config.ResearchFingerprints.Count==0,"Failed feedback cannot acknowledge a save or check");
        Assert((DateTime)typeof(MarketSync).GetField("nextResearchFeedbackAt",flags)!.GetValue(sync)!>DateTime.UtcNow.AddMinutes(4),"Failed feedback retries with backoff");
        sync=new MarketSync(config,cache,null,new Handler(System.Net.HttpStatusCode.OK));
        typeof(MarketSync).GetField("researchFeedbackCapability",flags)!.SetValue(sync,true);
        string other="ff1-"+new string('b',32)+":other-check";config.ResearchFingerprints[other]="other-ack";
        sent=((Task<bool>)optional.Invoke(sync,new object[]{db,new List<object>{batch},"fixture-token","save-v1"})!).GetAwaiter().GetResult();
        Assert(sent&&config.LastResearchSignature=="save-v1"&&config.ResearchFingerprints[other]=="other-ack","Successful retry acknowledges only its markets and preserves the other faction");
        db["ledgers"]=new Dictionary<string,object?>{{"PRIVATE",new{postingOutcomes=new{sold=999}}}};
        Assert(!JsonSerializer.Serialize(MarketSync.BuildResearchFeedback(db,[batch],now)).Contains("postingOutcomes"),"Own posting outcomes remain private");
        var many=new Dictionary<string,object?>();
        for(int i=0;i<101;i++){var copy=new Dictionary<string,object?>(row);copy["id"]="epoch:"+i;many[i.ToString()]=copy;}
        var manyDB=new Dictionary<string,object?>{{"researchChecks",new Dictionary<string,object?>{{scope,many}}}};
        var interrupted=new Settings{ShareMarket=true,NoticeAccepted=true};interrupted.ResearchFingerprints[other]="keep";
        sync=new MarketSync(interrupted,cache,null,new Handler(System.Net.HttpStatusCode.OK,()=>interrupted.ShareMarket=false));
        typeof(MarketSync).GetField("researchFeedbackCapability",flags)!.SetValue(sync,true);
        sent=((Task<bool>)optional.Invoke(sync,new object[]{manyDB,new List<object>{batch},"fixture-token","interrupted-save"})!).GetAwaiter().GetResult();
        Assert(sent&&interrupted.LastResearchSignature==""&&interrupted.ResearchFingerprints.Count==101&&interrupted.ResearchFingerprints[other]=="keep","Sharing-off mid-report retains acknowledgements and leaves the whole save incomplete");
        Console.WriteLine("PASS: optional feedback errors are isolated, backed off and unacknowledged; successful retry and other-market cache retention");
        Console.WriteLine("PASS: research feedback privacy, identity, time, market deduplication and pack routing");
    }
}
