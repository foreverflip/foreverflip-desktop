using ForeverFlip.Desktop;
using System.Text.Json;
using System.Net.Http;
using System.IO;
using System.Text;

static class SupportTests
{
    public static async Task Run(string dir)
    {
        void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string id=$"fflog-{now}-1-17";
        var db=LuaReader.Read("""
ForeverFlipDB={["markets"]={["PRIVATE_MARKET"]={["secret"]="NO_FULL_DB"}},["ledgers"]={ ["18:1:110:Classic Beta PvP:Alliance:Otherchar"]={["money"]=99999}},
 ["supportRequest"]={["schemaVersion"]=1,["consent"]="support-2026-10-17",["requestID"]="REQUEST_ID",["requestedAt"]=CLOCK_NOW,["addonVersion"]="0.13.58",["build"]="70450",["debugEnabled"]=true,
 ["scope"]="18:1:110:Classic Beta PvP:Alliance",["redactions"]={"Mychar"},["settings"]={ ["trade.vendorMinProfit"]=10,["character.name"]="PRIVATE" },
 ["events"]={{["t"]=CLOCK_NOW,["kind"]="vendor_live_read",["data"]={["rows"]=12,["key"]="123:0:0:0",["seller"]="SELLER_SECRET",["message"]="Mychar Otherchar Player-4618-AABB token=SECRET"}}},
 ["errors"]={{["message"]="Interface/AddOns/ForeverFlip/Display.lua:239: failure",["stack"]="C:/Users/Secret/WTF/private.lua",["firstAt"]=CLOCK_NOW,["lastAt"]=CLOCK_NOW,["addon"]="0.13.57",["build"]="70450",["count"]=1}}}}
""".Replace("REQUEST_ID",id).Replace("CLOCK_NOW",now.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var nativeData=MarketSync.Sub(MarketSync.Table(MarketSync.Sub(MarketSync.Sub(db,"supportRequest"),"events")["1"]),"data");
        foreach(var key in new[]{"recipes","attachments","waiting","held","id","processingSeconds","worstBasket","guardedBudget","excluded category"})nativeData[key]=471d;
        nativeData["route"]="vendor";nativeData["queryKey"]="2589:0:0:0";nativeData["sortProfile"]="commodity price";nativeData["keySource"]="server key";
        nativeData["realmID"]="PRIVATE_COUNTER_TEXT";nativeData["cacheSource"]="PRIVATE_ENUM_TEXT";
        nativeData["gross"]=100d;nativeData["fee"]=5d;nativeData["refund"]=1d;nativeData["deposit"]=10d;nativeData["proof"]="unique_seller_invoice";nativeData["cancelled"]=true;
        string raw=SupportLogs.Build(db,"0.1.21",now)!;
        using var report=JsonDocument.Parse(raw);var root=report.RootElement;
        Check(root.GetProperty("events").GetArrayLength()==1&&root.GetProperty("settings").GetProperty("trade.vendorMinProfit").GetInt32()==10,"Typed diagnostics preserved");
        foreach(string secret in new[]{"Mychar","Otherchar","AABB","SECRET","Secret","NO_FULL_DB","SELLER_SECRET","PRIVATE_MARKET","character.name"})Check(!raw.Contains(secret),"Support privacy leak: "+secret);
        var counters=root.GetProperty("events")[0].GetProperty("data");
        Check(counters.GetProperty("gross").GetInt32()==100&&counters.GetProperty("fee").GetInt32()==5&&counters.GetProperty("refund").GetInt32()==1&&counters.GetProperty("deposit").GetInt32()==10&&counters.GetProperty("cancelled").GetBoolean()&&counters.GetProperty("proof").GetString()=="unique_seller_invoice","Financial receipt fields survive the desktop contract");
        nativeData["gross"]=true;nativeData["fee"]=-1d;nativeData["refund"]=.5d;nativeData["deposit"]="SECRET";nativeData["proof"]="SECRET";nativeData["cancelled"]=1d;
        using(var invalid=JsonDocument.Parse(SupportLogs.Build(db,"0.1.36",now)!))foreach(string field in new[]{"gross","fee","refund","deposit","proof","cancelled"})Check(!invalid.RootElement.GetProperty("events")[0].GetProperty("data").TryGetProperty(field,out _),"Invalid new diagnostic field excluded: "+field);
        Check(counters.GetProperty("recipes").GetDouble()==471&&counters.GetProperty("attachments").GetDouble()==471&&counters.GetProperty("held").GetDouble()==471&&counters.GetProperty("queryKey").GetString()=="2589:0:0:0"&&counters.GetProperty("route").GetString()=="vendor","Recipe/mail/vendor/search diagnostics retained");
        Check(!raw.Contains("PRIVATE_COUNTER_TEXT")&&!raw.Contains("PRIVATE_ENUM_TEXT"),"New numeric and enum fields must not carry arbitrary private text");
        Check(SupportLogs.Build(new(),"0.1.21",now)==null,"No upload without explicit request");
        var request=MarketSync.Sub(db,"supportRequest");request["consent"]="unknown";Check(SupportLogs.Build(db,"0.1.21",now)==null,"Consent required");request["consent"]="support-2026-10-17";
        Check(SupportLogs.Build(db,"0.1.21",now+8*86400)==null,"Old requests not submitted");
        string folder=Path.Combine(dir,"support-outbox");int calls=0;string first="";var queue=new SupportOutbox(folder);
        await queue.Process(db,"0.1.21",body=>{calls++;first=body;throw new HttpRequestException("lost response");},now);
        Check(calls==1&&Directory.GetFiles(folder,"*.report.json").Length==1&&queue.Status.Contains("waiting"),"Failure retained locally");
        queue=new SupportOutbox(folder);await queue.Process(db,"0.1.21",_=>throw new Exception("Backoff ignored"),now+1);
        // A restart/version change must resend the frozen bytes, not rebuild a
        // different report with the same id after an uncertain network result.
        await queue.Process(db,"0.1.22",body=>{calls++;Check(body==first,"Retry changed report content");return Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new{reportID=new string('a',64),requestID=id,receivedAt=now})));},now+61);
        Check(calls==2&&queue.Status.Contains("Report sent")&&Directory.GetFiles(folder,"*.report.json").Length==0,"Receipt removes pending payload");
        await new SupportOutbox(folder).Process(db,"0.1.22",_=>throw new Exception("Already sent report repeated"),now+62);
        // Wrong receipt cannot falsely mark a report delivered.
        string wrong=Path.Combine(dir,"support-wrong-receipt");var bad=new SupportOutbox(wrong);
        await bad.Process(db,"0.1.21",_=>Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new{reportID=new string('a',64),requestID="fflog-1-1-1",receivedAt=now}))),now);
        Check(Directory.GetFiles(wrong,"*.report.json").Length==1&&!bad.Status.StartsWith("Report sent"),"Receipt binds request");
        string corrupt=Path.Combine(dir,"support-corrupt");Directory.CreateDirectory(corrupt);File.WriteAllText(Path.Combine(corrupt,"state.json"),"broken json");File.WriteAllText(Path.Combine(corrupt,"bad.report.json"),"broken packet");
        var recovered=new SupportOutbox(corrupt);
        await recovered.Process(new(),"0.1.21",_=>throw new Exception("Corrupted report transmitted"),now);
        Check(recovered.Status.Contains("Damaged")&&Directory.GetFiles(corrupt,"*.report.json").Length==0&&Directory.GetFiles(corrupt,"*.invalid-*").Length==2,"Corrupt state and payload preserved, not a permanent queue block");
        // Size bound is enforced after escaping/UTF-8 serialization.
        var many=new Dictionary<string,object?>();
        for(int i=1;i<=600;i++)many[i.ToString()]=new Dictionary<string,object?>{{"t",(double)now},{"kind","test"},{"data",new Dictionary<string,object?>{{"message",new string('界',320)},{"reason",new string('界',320)},{"name",new string('界',320)}}}};
        request["events"]=many;string limited=SupportLogs.Build(db,"0.1.21",now)!;
        Check(System.Text.Encoding.UTF8.GetByteCount(limited)<=SupportLogs.MaxBytes,"Serialized support size bound");
        // Export a safe synthetic packet for the independent Python contract/API test.
        File.WriteAllText(Path.Combine(dir,"support-contract.json"),raw);
        Check(!report.RootElement.TryGetProperty("description",out _)&&!report.RootElement.TryGetProperty("reportType",out _),"Legacy reports retain their original payload identity");
        request["schemaVersion"]=2d;Check(SupportLogs.Build(db,"0.1.31",now)==null,"New saved request cannot lose its required context");
        request["reportType"]="feedback";request["description"]="Mychar Otherchar: crafting steps are unclear. token=SECRET\n界🙂";
        using(var feedback=JsonDocument.Parse(SupportLogs.Build(db,"0.1.31",now)!)){
            string message=feedback.RootElement.GetProperty("description").GetString()!;
            Check(feedback.RootElement.GetProperty("reportType").GetString()=="feedback"&&message.Contains("界🙂")&&!message.Contains("SECRET")&&!message.Contains("Mychar")&&!message.Contains("Otherchar"),"Feedback context retained and scrubbed");
            Check(feedback.RootElement.GetProperty("events").GetArrayLength()>0,"Feedback must still include diagnostics");
            Check(feedback.RootElement.GetProperty("schemaVersion").GetInt32()==1,"Saved-request version must not change the HTTPS wire contract");
            File.WriteAllText(Path.Combine(dir,"support-feedback-contract.json"),feedback.RootElement.GetRawText());
        }
        request["description"]=string.Concat(Enumerable.Repeat("🙂",1100));
        using(var bounded=JsonDocument.Parse(SupportLogs.Build(db,"0.1.31",now)!))Check(bounded.RootElement.GetProperty("description").GetString()!.EnumerateRunes().Count()==1000,"Unicode context limit is in whole code points");
        request["description"]="  ";Check(SupportLogs.Build(db,"0.1.31",now)==null,"Empty new context rejected");
        request["reportType"]="other";request["description"]="context";Check(SupportLogs.Build(db,"0.1.31",now)==null,"Unknown feedback type rejected");
        var guarded=LuaReader.Read("""
ForeverFlipDB={["markets"]={},["supportRequest"]={["schemaVersion"]=2,["consent"]="support-2026-10-17",["requestID"]="REQUEST_ID",["requestedAt"]=CLOCK_NOW,
 ["addonVersion"]="0.13.77",["build"]="70450",["scope"]="18:1:110:Classic Beta PvP:Alliance",["reportType"]="issue",["description"]="Material shopping stopped.",
 ["events"]={{["t"]=CLOCK_NOW,["kind"]="craft_guard_rejected",["data"]={["recipeID"]=3845,["route"]="auction",["crafts"]=1,["itemID"]=2318,["needed"]=6,["available"]=3,
 ["maxUnit"]=100,["cost"]=600,["profit"]=500,["floor"]=500,["minROI"]=0.25,["budget"]=1000,["limit"]=100000,["reason"]="material_price",["message"]="Need 6 Light Leather; only 3 within 1s per item."}}},["errors"]={},["settings"]={},["debugEnabled"]=false}}
""".Replace("REQUEST_ID",$"fflog-{now}-2-17").Replace("CLOCK_NOW",now.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        string guardRaw=SupportLogs.Build(guarded,"0.1.32",now)!;
        using(var check=JsonDocument.Parse(guardRaw)){
            var e=check.RootElement.GetProperty("events")[0];var data=e.GetProperty("data");
            Check(e.GetProperty("kind").GetString()=="craft_guard_rejected"&&data.GetProperty("needed").GetInt32()==6&&data.GetProperty("available").GetInt32()==3&&data.GetProperty("maxUnit").GetInt32()==100&&data.GetProperty("minROI").GetDouble()==.25&&data.GetProperty("reason").GetString()=="material_price","Craft blocker survives the existing desktop contract with verbose debug off");
        }
        File.WriteAllText(Path.Combine(dir,"support-craft-guard.json"),guardRaw);
        // A valid600-event addon snapshot can exceed the wire byte limit.
        // Its older failures/outcomes must survive that second trimming stage.
        var pressureRequest=new Dictionary<string,object?>(MarketSync.Sub(guarded,"supportRequest"));
        var pressure=new Dictionary<string,object?>{["supportRequest"]=pressureRequest};
        var pressureEvents=new Dictionary<string,object?>();
        Dictionary<string,object?> Row(long at,string kind,Dictionary<string,object?> data)=>new(){["t"]=(double)at,["kind"]=kind,["data"]=data};
        pressureEvents["1"]=Row(now-1000,"transaction_unconfirmed",new(){["total"]=120d});
        pressureEvents["2"]=Row(now-999,"craft_vendor_sale",new(){["proceeds"]=200d});
        pressureEvents["3"]=Row(now-1002,"startup",new(){["addon"]="0.13.95"});
        var large=new Dictionary<string,object?>();foreach(string field in new[]{"message","name","reason","source","status","mode","state","kind"})large[field]=new string('界',320);
        for(int i=4;i<=600;i++)pressureEvents[i.ToString()]=Row(now-600+i,"queue_status",new(large));
        pressureRequest["events"]=pressureEvents;
        pressureRequest["errors"]=new Dictionary<string,object?>{["1"]=new Dictionary<string,object?>{["message"]="older captured error",["stack"]=new string('界',8000),["addon"]="0.13.55",["build"]="70450",["firstAt"]=(double)now-2000,["lastAt"]=(double)now-2000,["count"]=1d}};
        string unchanged=JsonSerializer.Serialize(pressureRequest);
        string prioritized=SupportLogs.Build(pressure,"0.1.36",now)!;
        Check(Encoding.UTF8.GetByteCount(prioritized)<=SupportLogs.MaxBytes,"Priority trimming exceeded wire size");
        using(var parsed=JsonDocument.Parse(prioritized)){
            var kept=parsed.RootElement.GetProperty("events").EnumerateArray().ToArray();
            Check(kept.Any(e=>e.GetProperty("kind").GetString()=="transaction_unconfirmed")&&kept.Any(e=>e.GetProperty("kind").GetString()=="craft_vendor_sale")&&kept.Any(e=>e.GetProperty("kind").GetString()=="startup"),"Byte trimming lost critical outcomes/context before newer routine traffic");
            Check(kept.Length<600&&kept.Select(e=>e.GetProperty("at").GetInt64()).SequenceEqual(kept.Select(e=>e.GetProperty("at").GetInt64()).Order()),"Byte trimming lost chronology or did not trim the flood");
            Check(!kept.Any(e=>e.TryGetProperty("priority",out _)),"Internal ranks leaked onto wire");
        }
        Check(unchanged==JsonSerializer.Serialize(pressureRequest),"Trimming mutated the immutable saved request");
        // Among only critical records, prefer recent evidence over old errors.
        for(int i=1;i<=600;i++)pressureEvents[i.ToString()]=Row(now-600+i,"api_error",new(large));
        using(var parsed=JsonDocument.Parse(SupportLogs.Build(pressure,"0.1.36",now)!)){
            Check(parsed.RootElement.GetProperty("errors").GetArrayLength()==0,"Old Lua errors displaced newer critical evidence under byte pressure");
            Check(parsed.RootElement.GetProperty("events").EnumerateArray().Any(e=>e.GetProperty("at").GetInt64()==now),"Newest critical record was lost");
            Check(Encoding.UTF8.GetByteCount(parsed.RootElement.GetRawText())<=SupportLogs.MaxBytes,"Critical-only report exceeded byte limit");
        }
        Check(SupportLogs.Priority("pricing_error_decision",[])==1&&SupportLogs.Priority("transaction_unconfirmed",[])==3&&SupportLogs.Priority("startup",[])==4,"Diagnostic priority categories changed");
        Check(SupportLogs.Priority("craft_guard_rejected",[])==3&&SupportLogs.Priority("craft_merchant_blocked",[])==3,"Action blockers must survive routine traces");
        Check(SupportLogs.Priority("api_throttle_event",new(){["event"]="AUCTION_HOUSE_THROTTLED_MESSAGE_DROPPED"})==3&&SupportLogs.Priority("api_throttle_event",new(){["event"]="AUCTION_HOUSE_THROTTLED_SYSTEM_READY"})==0,"Throttle failures need distinct priority");
        File.WriteAllText(Path.Combine(dir,"support-priority-contract.json"),prioritized);
        Console.WriteLine("PASS support privacy, consent, retry/receipt/size guards and compatible detailed crafting failures");
    }
}
