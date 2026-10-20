using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

public sealed partial class MarketSync
{
    bool patternCapability,marketIdentityCapability,depthCapability,recipeIdentityCapability,catalogReadRequired=true;
    DateTime nextCatalogCapability;
    public static bool SupportsPatternDiscovery(JsonElement source)=>source.ValueKind==JsonValueKind.Object&&source.TryGetProperty("capabilities",out var caps)&&caps.ValueKind==JsonValueKind.Object&&
        caps.TryGetProperty("patternDiscovery",out var flag)&&flag.ValueKind==JsonValueKind.Number&&flag.TryGetInt32(out int version)&&version==1;
    public static bool SupportsMarketIdentity(JsonElement source)=>source.ValueKind==JsonValueKind.Object&&source.TryGetProperty("capabilities",out var caps)&&caps.ValueKind==JsonValueKind.Object&&
        caps.TryGetProperty("marketIdentity",out var flag)&&flag.ValueKind==JsonValueKind.Number&&flag.TryGetInt32(out int version)&&version==1;
    public static bool SupportsCapability(JsonElement source,string name)=>source.ValueKind==JsonValueKind.Object&&source.TryGetProperty("capabilities",out var caps)&&caps.ValueKind==JsonValueKind.Object&&
        caps.TryGetProperty(name,out var flag)&&flag.ValueKind==JsonValueKind.Number&&flag.TryGetInt32(out int version)&&version==1;
    async Task RefreshCatalogCapability()
    {
        if(!Config.ShareMarket||!Config.NoticeAccepted||DateTime.UtcNow<nextCatalogCapability)return;
        nextCatalogCapability=DateTime.UtcNow.AddMinutes(5);
        try{
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response=await http.GetAsync(Service+"/api/v1/catalog/sources",timeout.Token);
            if(!response.IsSuccessStatusCode)return;
            using var source=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            bool supported=SupportsPatternDiscovery(source.RootElement);
            marketIdentityCapability=SupportsMarketIdentity(source.RootElement);
            depthCapability=SupportsCapability(source.RootElement,"commodityDepth");
            recipeIdentityCapability=SupportsCapability(source.RootElement,"recipeIdentity");
            bool feedback=SupportsCapability(source.RootElement,"researchFeedback");
            if(feedback&&!researchFeedbackCapability)Config.LastSignature="";
            researchFeedbackCapability=feedback;
            if(supported&&!patternCapability)catalogReadRequired=true;
            patternCapability=supported;
            if(supported)nextCatalogCapability=DateTime.UtcNow.AddHours(1);
        }catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException){
            // Optional discovery must not block ordinary market/recipe synchronization.
        }
    }
    public static int CountSharedPatternsForBuild(IReadOnlyDictionary<string,string> fingerprints,string build)=>
        string.IsNullOrEmpty(build)?0:fingerprints.Keys.Count(k=>k.StartsWith(build+":pattern:",StringComparison.Ordinal));
    public static int CountSharedRecipesForBuild(IReadOnlyDictionary<string,string> fingerprints,string build)
    {
        if(string.IsNullOrEmpty(build))return 0;
        string prefix=build+":recipe:";
        return fingerprints.Keys.Count(k=>k.StartsWith(prefix,StringComparison.Ordinal));
    }
    static string Clean(string value,int limit,string fallback)
    {
        string text=new(value.Where(c=>!char.IsControl(c)).ToArray());return text.Length==0?fallback:text.Length>limit?string.Concat(text.EnumerateRunes().Take(limit).Select(r=>r.ToString())):text;
    }
    public static List<object> BuildCatalogBatches(Dictionary<string,object?> db,IEnumerable<object> marketBatches,long savedAt,bool includePatterns=true,bool includeIdentity=false)
    {
        var result=new List<object>();var client=Sub(db,"client");if(Number(client,"interface")!=16001)return result;
        var crafting=Sub(db,"crafting");var catalogue=Sub(Sub(crafting,"catalog"),"18:"+Text(client,"build"));
        JsonElement market=default;
        foreach(var batch in marketBatches){using var doc=JsonDocument.Parse(JsonSerializer.Serialize(batch));market=doc.RootElement.GetProperty("market").Clone();break;}
        if(market.ValueKind==JsonValueKind.Undefined&&crafting.ContainsKey("market")){
            var raw=Sub(crafting,"market");
            market=JsonSerializer.SerializeToElement(new{project=18,major=(int)Number(raw,"major"),region=(int)Number(raw,"region"),realm=Text(raw,"realm"),faction=Text(raw,"faction"),family="forever-beta",epoch="beta-2026",@interface=16001,build=Text(client,"build")});
        }
        if(market.ValueKind==JsonValueKind.Undefined)return result;
        if(market.GetProperty("project").GetInt32()!=18||market.GetProperty("interface").GetInt32()!=16001||market.GetProperty("build").GetString()!=Text(client,"build"))return result;
        var recipes=new List<object>();
        foreach(var value in catalogue.Values.Take(10000)){
            var r=Table(value);var id=Integer(r,"recipeID",1,10000000);var output=Integer(r,"outputItemID",1,10000000);var min=Integer(r,"outputMin",1,100000);var max=Integer(r,"outputMax",1,100000);var seen=Integer(r,"observedAt",1,4102444800);
            if(!id.HasValue||!output.HasValue||!min.HasValue||!max.HasValue||max<min||!seen.HasValue||seen<DateTimeOffset.UtcNow.ToUnixTimeSeconds()-30*86400||seen>DateTimeOffset.UtcNow.ToUnixTimeSeconds()+300)continue;
            var mats=new List<(long itemID,long quantity,string? itemKey)>();var positions=new Dictionary<long,(int index,string key)>();bool complete=r.GetValueOrDefault("complete") is true;
            var rawReagents=Sub(r,"reagents");if(rawReagents.Count>32)continue;
            foreach(var m in rawReagents.Values){
                var mat=Table(m);var item=Integer(mat,"itemID",1,10000000);var quantity=Integer(mat,"quantity",1,100000);
                if(!item.HasValue||!quantity.HasValue){complete=false;continue;}
                string key=Text(mat,"itemKey");
                if(key!=""&&!Regex.IsMatch(key,@"^"+item.Value+@":\d+:-?\d+:0$")){complete=false;continue;}
                string identity=key==""?$"{item.Value}:0:0:0":key;
                if(positions.TryGetValue(item.Value,out var prior)){
                    if(prior.key!=identity||mats[prior.index].quantity>100000-quantity.Value){complete=false;continue;}
                    var old=mats[prior.index];mats[prior.index]=(old.itemID,old.quantity+quantity.Value,old.itemKey);
                }else{positions[item.Value]=(mats.Count,identity);mats.Add((item.Value,quantity.Value,key==""?null:key));}
            }
            if(mats.Count==0||mats.Count>32||!complete)continue;
            string outputKey=Text(r,"outputKey");
            if(outputKey!=""&&!Regex.IsMatch(outputKey,@"^"+output.Value+@":\d+:-?\d+:0$"))continue;
            string binding=Text(r,"outputBinding");if(binding is not ("tradeable" or "bop"))binding="unknown";
            var sharedRecipe=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new{recipeID=id.Value,name=Clean(Text(r,"name"),160,id.Value.ToString()),profession=Clean(Text(r,"profession"),80,"Unknown"),category=Clean(Text(r,"category"),80,"Other"),outputItemID=output.Value,outputKey=outputKey==""?null:outputKey,outputMin=min.Value,outputMax=max.Value,reagents=mats.Select(m=>new{m.itemID,m.quantity,m.itemKey}).ToArray(),complete,outputBinding=binding,vendorCopper=Integer(r,"vendorCopper",0,100000000000),cooldownSeconds=Integer(r,"cooldownSeconds",0,31536000),observedAt=seen.Value}))!;
            if(includeIdentity&&Text(r,"identityProof")=="item_link"&&outputKey!="")sharedRecipe["identityProof"]="item_link";
            recipes.Add(sharedRecipe);
        }
        var vendors=new List<object>();
        foreach(var value in Sub(Sub(crafting,"vendors"),"18:"+Text(client,"build")).Values){
            var v=Table(value);var id=Integer(v,"itemID",1,10000000);var price=Integer(v,"bundleCopper",1,100000000000);var quantity=Integer(v,"bundleQuantity",1,100000);var at=Integer(v,"observedAt",1,4102444800);
            if(id.HasValue&&price.HasValue&&quantity.HasValue&&at.HasValue&&at<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+300)vendors.Add(new{itemID=id.Value,bundleCopper=price.Value,bundleQuantity=quantity.Value,unlimited=v.GetValueOrDefault("unlimited") is true,observedAt=at.Value});
        }
        var patterns=new List<object>();long clock=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if(includePatterns)foreach(var value in Sub(Sub(crafting,"patternItems"),"18:"+Text(client,"build")).Values.Take(5000)){
            var p=Table(value);var id=Integer(p,"itemID",1,10000000);var seen=Integer(p,"observedAt",1,4102444800);var subclass=Integer(p,"subclassID",0,255);
            if(!id.HasValue||Integer(p,"classID",9,9)!=9||!seen.HasValue||seen<clock-30*86400||seen>clock+300||Text(p,"name")=="")continue;
            if(p.ContainsKey("subclassID")&&!subclass.HasValue)continue;
            patterns.Add(new{itemID=id.Value,name=Clean(Text(p,"name"),160,id.Value.ToString()),classID=9,subclassID=subclass,
                profession=Clean(Text(p,"profession"),80,"Unclassified"),observedAt=seen.Value});
        }
        int total=Math.Max(Math.Max(recipes.Count,patterns.Count),(vendors.Count+1)/2);
        for(int offset=0;offset<total;offset+=100){
            var batch=new Dictionary<string,object>{["schemaVersion"]=1,["market"]=market,["savedAt"]=savedAt,["source"]="client_observed",
                ["recipes"]=recipes.Skip(offset).Take(100).ToArray(),["vendorMaterials"]=vendors.Skip(offset*2).Take(200).ToArray()};
            if(includePatterns)batch["patternItems"]=patterns.Skip(offset).Take(100).ToArray();result.Add(batch);
        }
        return result;
    }
    async Task<bool> SyncCatalog(Dictionary<string,object?> db,List<object> marketBatches,string token,long savedAt)
    {
        if(!Config.ShareCatalog||!Config.CatalogNoticeAccepted)return false;
        bool sent=false;string currentBuild=Text(Sub(db,"client"),"build");
        foreach(var batch in BuildCatalogBatches(db,marketBatches,savedAt,patternCapability,recipeIdentityCapability)){
            if(!Config.ShareMarket||!Config.ShareCatalog)return sent;
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(batch));var root=doc.RootElement;
            var recipes=root.GetProperty("recipes").EnumerateArray().ToArray();var vendors=root.GetProperty("vendorMaterials").EnumerateArray().ToArray();var updates=new Dictionary<string,string>();
            var patterns=root.TryGetProperty("patternItems",out var patternArray)?patternArray.EnumerateArray().ToArray():Array.Empty<JsonElement>();
            string client=root.GetProperty("market").GetProperty("build").GetString()!;
            JsonElement[] Changed(JsonElement[] records,string kind,string identity)=>records.Where(r=>{
                string key=client+":"+kind+":"+r.GetProperty(identity).GetInt64();string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.GetRawText())));
                if(Config.CatalogFingerprints.GetValueOrDefault(key)==hash)return false;updates[key]=hash;return true;
            }).Select(r=>r.Clone()).ToArray();
            var deltaRecipes=Changed(recipes,"recipe","recipeID");var deltaVendors=Changed(vendors,"vendor","itemID");var deltaPatterns=Changed(patterns,"pattern","itemID");if(updates.Count==0)continue;
            var payload=new Dictionary<string,object>{["schemaVersion"]=1,["market"]=root.GetProperty("market").Clone(),["savedAt"]=root.GetProperty("savedAt").GetInt64(),
                ["source"]="client_observed",["recipes"]=deltaRecipes,["vendorMaterials"]=deltaVendors};
            if(patternCapability)payload["patternItems"]=deltaPatterns;
            using var response=await Post("/api/v1/catalog/recipes",payload,token);
            sent=true;
            foreach(var pair in updates)Config.CatalogFingerprints[pair.Key]=pair.Value;
            Config.RecipesSynced=CountSharedRecipesForBuild(Config.CatalogFingerprints,currentBuild);Save();
        }
        int currentCount=CountSharedRecipesForBuild(Config.CatalogFingerprints,currentBuild);
        if(Config.RecipesSynced!=currentCount){Config.RecipesSynced=currentCount;Save();}
        Config.PatternsSynced=CountSharedPatternsForBuild(Config.CatalogFingerprints,currentBuild);Save();catalogReadRequired=false;
        return sent;
    }
    static string DataLiteral(JsonElement value,int depth,ref int nodes)
    {
        if(depth>20||++nodes>1000000)throw new InvalidDataException("Crafting data exceeds complexity limits.");
        var text=new StringBuilder();
        switch(value.ValueKind){
            case JsonValueKind.Object:
                text.Append('{');foreach(var field in value.EnumerateObject())text.Append('[').Append(LuaString(field.Name)).Append("]=").Append(DataLiteral(field.Value,depth+1,ref nodes)).Append(',');return text.Append('}').ToString();
            case JsonValueKind.Array:
                text.Append('{');foreach(var item in value.EnumerateArray())text.Append(DataLiteral(item,depth+1,ref nodes)).Append(',');return text.Append('}').ToString();
            case JsonValueKind.String:return LuaString(value.GetString()!);
            case JsonValueKind.Number:
                if(!value.TryGetInt64(out long number)||Math.Abs((decimal)number)>9007199254740991)throw new InvalidDataException("Unsafe crafting data number.");return number.ToString(CultureInfo.InvariantCulture);
            case JsonValueKind.True:return "true";case JsonValueKind.False:return "false";case JsonValueKind.Null:return "nil";
            default:throw new InvalidDataException("Unsupported crafting data.");
        }
    }
}
