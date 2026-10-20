using ForeverFlip.Desktop;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class RecommendationTextTests
{
    internal static void Run(string outputPath)
    {
        void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        void Reject(Action action,string message){try{action();}catch(InvalidDataException){return;}throw new Exception("Expected InvalidDataException: "+message);}
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string scope="18:1:110:Test Beta:Alliance";
        var legacy=JsonSerializer.SerializeToElement(new{schema="foreverflip.market-pack",schemaVersion=1,scope,packID="text-fixture",createdAt=now-1,expiresAt=now+3600,
            recommendations=new[]{new{id="vendor",itemKey="4234:0:0:0",kind="vendor_buy",priority=90,expiresAt=now+3600,reason="Observed below vendor; native payout check required.",maxBuyUnitCopper=95,maxQuantity=20}}});
        string legacyRendered=MarketSync.RenderPackEntry(legacy);
        Assert(!legacyRendered.Contains("profitPlan=")&&!legacyRendered.Contains("risks="),"Legacy pack remains optional-field free");
        var node=JsonNode.Parse(legacy.GetRawText())!;
        string plan="Check the whole stack, then sell to an NPC. \";error('never execute')-- \\ \n💰";
        string[] risks=["Current payout and stack cost must be checked.","A quoted ask is not a sale."];
        node["recommendations"]![0]!["profitPlan"]=plan;
        node["recommendations"]![0]!["risks"]=JsonSerializer.SerializeToNode(risks);
        var pack=JsonSerializer.SerializeToElement(node);
        string rendered=MarketSync.RenderPackEntry(pack);
        Assert(rendered.Contains(",profitPlan="+MarketSync.LuaString(plan)),"Profit plan survives serialization as an escaped string");
        foreach(string risk in risks)Assert(rendered.Contains(MarketSync.LuaString(risk)),"Every bounded risk survives serialization");
        var read=LuaReader.Read("ForeverFlipDB={ [\"plan\"]="+MarketSync.LuaString(plan)+" }");
        Assert((string)read["plan"]! == plan,"Quotes, backslashes, newlines and UTF-8 round-trip through the literal-only reader");
        foreach(string field in new[]{",maxBuyUnitCopper=95",",maxQuantity=20",",reason="+MarketSync.LuaString(legacy.GetProperty("recommendations")[0].GetProperty("reason").GetString()!)})
            Assert(rendered.Contains(field),"Existing financial amounts and reason stay unchanged");
        var boundary=node.DeepClone();
        boundary["recommendations"]![0]!["profitPlan"]=string.Concat(Enumerable.Repeat("💰",600));
        boundary["recommendations"]![0]!["risks"]=JsonSerializer.SerializeToNode(Enumerable.Repeat(string.Concat(Enumerable.Repeat("💰",240)),3));
        MarketSync.RenderPackEntry(JsonSerializer.SerializeToElement(boundary));
        boundary["recommendations"]![0]!["risks"]=new JsonArray();
        Assert(MarketSync.RenderPackEntry(JsonSerializer.SerializeToElement(boundary)).Contains(",risks={}"),"Zero risks are allowed");
        foreach(var invalid in new (string field,JsonNode? value)[]{
            ("profitPlan",JsonValue.Create("")),("profitPlan",JsonValue.Create(new string('x',601))),
            ("profitPlan",JsonValue.Create(1)),("profitPlan",null),("profitPlan",new JsonObject()),
            ("risks",JsonSerializer.SerializeToNode(new[]{"a","b","c","d"})),("risks",JsonSerializer.SerializeToNode(new[]{""})),
            ("risks",JsonSerializer.SerializeToNode(new[]{new string('x',241)})),("risks",JsonSerializer.SerializeToNode(new[]{1})),
            ("risks",JsonSerializer.SerializeToNode(new object?[]{null})),("risks",JsonValue.Create("text")),("risks",null)}){
            var bad=node.DeepClone();bad["recommendations"]![0]![invalid.field]=invalid.value?.DeepClone();
            Reject(()=>MarketSync.RenderPackEntry(JsonSerializer.SerializeToElement(bad)),"Malformed optional "+invalid.field);
        }
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] bytes=Encoding.UTF8.GetBytes(pack.GetRawText());
        var envelope=JsonSerializer.SerializeToElement(new{algorithm="ES256",payload=Convert.ToBase64String(bytes),signature=Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence))});
        string folder=Path.GetDirectoryName(Path.GetFullPath(outputPath))!;Directory.CreateDirectory(folder);
        string publicPath=Path.Combine(folder,"recommendation-test-public.pem");
        File.WriteAllText(publicPath,key.ExportSubjectPublicKeyInfoPem());
        var verified=MarketSync.Verify(envelope,publicPath);
        Assert(MarketSync.RenderPackEntry(verified)==rendered,"Verified signed advisory text reaches the same data-only renderer");
        File.WriteAllText(outputPath,"ForeverFlipShared={schema=1,markets={"+rendered+"}}");
        File.WriteAllText(Path.ChangeExtension(outputPath,"expected.json"),JsonSerializer.Serialize(new{scope,profitPlan=plan,risks}));
        Console.WriteLine("Recommendation explanations: legacy compatibility, Unicode boundaries, malformed input, signature and escaped Lua serialization passed.");
    }
}
