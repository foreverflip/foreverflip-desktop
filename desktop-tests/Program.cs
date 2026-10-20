using ForeverFlip.Desktop;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Every test mode keeps application state away from the real tray app.
MarketSync.TestStateRoot=Path.Combine(TestPaths.Root,"desktop-test-state-"+Guid.NewGuid().ToString("N"));
if(args.Length==3&&args[0]=="--render-pack"){
 using var fixture=JsonDocument.Parse(File.ReadAllText(args[1]));
 File.WriteAllText(args[2],"ForeverFlipShared={schema=1,markets={"+MarketSync.RenderPackEntry(fixture.RootElement)+"}}",new UTF8Encoding(false));return;
}
ProfitTests.Run();
ResearchTests.Run();
if(args.Length==2&&args[0]=="--research-contract"){ResearchTests.Run(args[1]);return;}

if(args.Length==2&&args[0]=="--followup-tests"){
 Directory.CreateDirectory(args[1]);await FollowupTests.Run(args[1]);return;
}

if(args.Length==2&&args[0]=="--recommendation-text"){
 RecommendationTextTests.Run(args[1]);return;
}
if(args.Length==2&&args[0]=="--support-logs"){
 Directory.CreateDirectory(args[1]);await SupportTests.Run(args[1]);return;
}
if(args.Length==2&&args[0]=="--backup-tests"){
 Directory.CreateDirectory(args[1]);AddonBackupTests.Run(args[1]);return;
}

if(args.Length==1&&args[0]=="--pack-status"){
 var sync=new MarketSync();Console.WriteLine(JsonSerializer.Serialize(new{status=sync.PackStatus,loadedConfirmed=sync.PackLoadConfirmed,lastDownloaded=sync.Config.LastPack,lastSavedLoaded=sync.Config.LoadedPack}));return;
}
if(args.Length==1&&args[0]=="--verify-live-updates"){
 string folder=Path.Combine(TestPaths.Root,"live-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
 try{
  var logger=new Velopack.Logging.NullVelopackLogger();var updates=new SignedUpdateSource();
  var feed=await updates.GetReleaseFeed(logger,"ForeverFlip","win");
  var asset=feed.Assets.Single(a=>a.Type==Velopack.VelopackAssetType.Full);
  string package=Path.Combine(folder,"desktop.nupkg");await updates.DownloadReleaseEntry(logger,asset,package,_=>{},CancellationToken.None);
  using var http=new System.Net.Http.HttpClient();using var envelope=JsonDocument.Parse(await http.GetStringAsync(MarketSync.Service+"/releases/addon/latest.json"));
  var manifest=MarketSync.Verify(envelope.RootElement,Path.Combine(AppContext.BaseDirectory,"Assets","release-public.pem"));
  byte[] bytes=await http.GetByteArrayAsync(MarketSync.Service+manifest.GetProperty("path").GetString());
  if(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()!=manifest.GetProperty("sha256").GetString())throw new Exception("Published addon checksum mismatch");
  string client=Path.Combine(folder,"_classic_beta_");Directory.CreateDirectory(Path.Combine(client,"Interface","AddOns","ForeverFlip"));Directory.CreateDirectory(Path.Combine(client,"WTF"));File.WriteAllText(Path.Combine(client,"Wow.exe"),"");
  string personal=Path.Combine(client,"WTF","preserved.lua");File.WriteAllText(personal,"PERSONAL_DATABASE");File.WriteAllText(Path.Combine(client,"Interface","AddOns","ForeverFlip","ForeverFlip.toc"),"## Version: 0.12.71");
  File.WriteAllText(Path.Combine(client,"Interface","AddOns","ForeverFlip","AdviceData.lua"),"OLD_LEGACY_FILE");
  bool installed=true;
  // This is an isolated fake client under the verified test folder. Its
  // process probe is simulated; the real running game is never installed to.
  if(installed){ClientInstall.InstallArchiveCore(client,bytes,Version.Parse(manifest.GetProperty("version").GetString()!),false,()=>false);
   if(File.ReadAllText(personal)!="PERSONAL_DATABASE"||File.Exists(Path.Combine(client,"Interface","AddOns","ForeverFlip","AdviceData.lua")))throw new Exception("Published install did not preserve data/remove legacy file");}
  Console.WriteLine(JsonSerializer.Serialize(new{desktopVersion=asset.Version.ToString(),desktopBytes=new FileInfo(package).Length,addonVersion=manifest.GetProperty("version").GetString(),addonBytes=bytes.Length,signatureAndHashVerified=true,mockInstallVerified=installed}));
 }finally{if(!Path.GetFullPath(folder).StartsWith(Path.GetFullPath(TestPaths.Root)+Path.DirectorySeparatorChar+"live-update-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe cleanup");Directory.Delete(folder,true);}return;
}
if(args.Length==2&&args[0]=="--install-addon"){
 string root=ClientInstall.ResolveRoot(args[1])??throw new InvalidDataException("Unsupported WoW client.");
 Console.WriteLine(ClientInstall.EnsureBundled(root)?"Bundled addon installed; personal SavedVariables preserved.":"Addon already current.");return;
}
if(args.Length==3&&args[0]=="--pack"){
 using var doc=JsonDocument.Parse(File.ReadAllText(args[1]));
 var pack=MarketSync.Verify(doc.RootElement,Path.Combine(AppContext.BaseDirectory,"Assets","pack-public.pem"));
 File.WriteAllText(args[2],"ForeverFlipShared={schema=1,markets={"+MarketSync.RenderPackEntry(pack)+"}}");
 Console.WriteLine("Verified live pack and rendered data-only Lua for "+pack.GetProperty("recommendations").GetArrayLength()+" recommendations.");return;
}
if(args.Length==2&&args[0]=="--preview"){
 Exception? failed=null;
 var thread=new Thread(()=>{try{
  var app=new System.Windows.Application();var window=new MainWindow(true);
  var content=(System.Windows.FrameworkElement)window.Content;
  content.Measure(new System.Windows.Size(760,620));content.Arrange(new System.Windows.Rect(0,0,760,620));content.UpdateLayout();
  var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(760,620,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(content);
  byte[] probe=new byte[760*620*4];bitmap.CopyPixels(probe,760*4,0);if(!probe.Where((_,i)=>i%4==3).Any(a=>a>0))throw new InvalidOperationException("WPF preview rendering is unavailable in this session; no valid screenshot was produced.");
  var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var output=File.Create(args[1]);png.Save(output);
  app.Shutdown();
 }catch(Exception e){failed=e;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failed!=null)throw failed;Console.WriteLine("Rendered desktop preview.");return;
}
if(args.Length==2&&args[0]=="--ui-review"){
 DesktopUiReview.Run(args[1]);return;
}
if(args.Contains("--updater-interface")){
 foreach(var method in typeof(Velopack.Sources.IUpdateSource).GetMethods())Console.WriteLine(method.ToString());
 foreach(var prop in typeof(Velopack.VelopackAsset).GetProperties())Console.WriteLine(prop.Name+" "+prop.PropertyType);
 foreach(var t in typeof(Velopack.Logging.IVelopackLogger).Assembly.GetTypes().Where(t=>t.Name.Contains("Logger"))){Console.WriteLine(t.FullName);foreach(var ctor in t.GetConstructors())Console.WriteLine(ctor.ToString());}
 foreach(var m in typeof(Velopack.Logging.IVelopackLogger).GetMethods())Console.WriteLine(m.ToString());
 return;
}
void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
void Reject(Action action,string label){try{action();}catch{return;}throw new Exception("Expected rejection: "+label);}
Assert(MarketSync.RequestFailure(System.Net.HttpStatusCode.Unauthorized).Contains("authorization")&&MarketSync.RequestFailure(System.Net.HttpStatusCode.BadRequest,"Save timestamp is stale").Contains("timestamp is stale")&&MarketSync.RequestFailure(System.Net.HttpStatusCode.UnprocessableEntity).Contains("format"),"Validation/authorization failures must remain actionable instead of masquerading as outages");
foreach(var attack in new[]{"ForeverFlipDB={ [\"markets\"]=(function() return {} end)() }","ForeverFlipDB={ [\"markets\"]={},[\"markets\"]={} }","ForeverFlipDB={ [\"markets\"]={},[\"x\"]=1e999 }"})Reject(()=>LuaReader.Read(attack),"Lua input");
var parsed=LuaReader.Read("ForeverFlipDB={ [\"markets\"]={},[\"s\"]=\"a\\034b\\092c\",[\"n\"]=-1.25e2 }");
Assert((string)parsed["s"]! == "a\"b\\c","Lua string escapes");Assert((double)parsed["n"]! == -125,"numeric reader");
Assert((string)LuaReader.Read("ForeverFlipDB={ [\"s\"]=\"\\195\\169\" }")["s"]! == "é","UTF-8 byte escapes preserve names");
Reject(()=>LuaReader.Read("ForeverFlipDB={} evil()"),"trailing executable text");
Assert(LuaReader.Read("ForeverFlipDB={} ForeverFlipExport=\"PRIVATE_EXPORT\"").Count==0,"Optional saved export is validated and excluded");
Reject(()=>LuaReader.Read("ForeverFlipDB={} ForeverFlipExport=(function() end)()"),"executable export");
string dir=Path.Combine(TestPaths.Root, "forever-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
try{
 RecommendationTextTests.Run(Path.Combine(dir,"recommendation-text.lua"));
 await SavedGameFileTests.Run(dir);
 await SignedAddonDownloadTests.Run(Path.Combine(dir,"signed-addon-download"));
 await SupportTests.Run(dir);
 await FollowupTests.Run(Path.Combine(dir,"followup-tests"));
 string attemptPath=Path.Combine(dir,"update-attempt.json");var updateAttempt=new DesktopUpdateAttempt(attemptPath);
 var browserStart=ExternalLinks.StartInfo("https://forever.blahh.us/market/");
 Assert(browserStart.UseShellExecute&&browserStart.WorkingDirectory==MarketSync.StateRoot,"Browser launch explicitly uses stable data directory, not replaceable current folder");
 Assert(updateAttempt.CanApply(new Version("0.1.15"),false),"First update is allowed");
 updateAttempt.Begin(new Version("0.1.15"));updateAttempt=new DesktopUpdateAttempt(attemptPath);
 Assert(!updateAttempt.CanApply(new Version("0.1.15"),false),"Failed update cannot repeat after app restart");
 Assert(updateAttempt.CanApply(new Version("0.1.15"),true),"Explicit user retry is allowed");
 Assert(updateAttempt.CanApply(new Version("0.1.16"),false),"A newer fix can replace a failed version");
 Assert(updateAttempt.PendingMessage(new Version("0.1.14")).Contains("did not finish"),"Failure status stays visible on the old version");
 Assert(updateAttempt.PendingMessage(new Version("0.1.15.0"))==""&&!File.Exists(attemptPath),"Successful version clears failure state");
 File.WriteAllText(attemptPath,"broken");Assert(!updateAttempt.CanApply(new Version("0.1.15"),false),"Unreadable attempt cannot trigger an automatic restart loop");
 updateAttempt.Begin(new Version("0.1.15"));Assert(updateAttempt.PendingMessage(new Version("0.1.16"))=="","Newer installed version clears old attempt");
 string file=Path.Combine(dir,"ForeverFlip.lua");long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
 string sample="ForeverFlipDB={ [\"client\"]={ [\"build\"]=\"70374\",[\"interface\"]=16001,[\"addon\"]=\"0.12.77\" },[\"ledgers\"]={ [\"SECRET_CHARACTER\"]={} },[\"markets\"]={ [\"18:1:110:Test Beta:Alliance\"]={ [\"4234:0:0:0\"]={ [\"name\"]=\"Leather\",[\"history\"]={ { [\"p\"]=90,[\"q\"]=20,[\"t\"]="+now+" } } } } },[\"itemCatalog\"]={ [\"18:70374\"]={ [\"4234\"]={ [\"vendorPrice\"]=100,[\"checkedAt\"]="+now+",[\"stack\"]=20 } } } }";
 sample=sample.Replace("[\"ledgers\"]={","[\"luaErrors\"]={{[\"message\"]=\"PRIVATE_LUA_ERROR\",[\"stack\"]=\"PRIVATE_LUA_STACK\"}},[\"ledgers\"]={");
 File.WriteAllText(file,sample);var batches=MarketSync.BuildBatches(file);string json=JsonSerializer.Serialize(batches);
 Assert(!json.Contains("PRIVATE_LUA_ERROR")&&!json.Contains("PRIVATE_LUA_STACK")&&!json.Contains("luaErrors"),"Local Lua errors are excluded from community market uploads");
 var summary=MarketSync.SummarizeBatches(batches);
 Assert(summary.Items==1&&summary.Observations==1&&summary.Label=="Test Beta · Alliance","Dashboard counts actual sanitized saved records");
 Assert(MarketSync.SummarizeBatches([]).Items==0,"Empty dataset has zero saved items");

 string hordeRow="[\"18:1:110:Test Beta:Horde\"]={[\"4234:0:0:0\"]={[\"name\"]=\"Leather\",[\"history\"]={{[\"p\"]=900,[\"q\"]=7,[\"t\"]="+now+"}}}},";
 string multiFaction=sample.Replace("[\"markets\"]={","[\"markets\"]={"+hordeRow);
 File.WriteAllText(file,multiFaction);
 using(var two=JsonDocument.Parse(JsonSerializer.Serialize(MarketSync.BuildBatches(file)))){
  Assert(two.RootElement.GetArrayLength()==2,"Both factions in one save create separate upload batches");
  foreach(var b in two.RootElement.EnumerateArray()){
   bool horde=b.GetProperty("market").GetProperty("faction").GetString()=="Horde";
   var point=b.GetProperty("items")[0].GetProperty("points")[0];
   Assert(point.GetProperty("price").GetDouble()==(horde?900:90)&&point.GetProperty("stock").GetInt32()==(horde?7:20),"Same item keeps each faction's exact price and quantity");
  }
 }
 if(args.Length==2&&args[0]=="--export-factions"){
  File.WriteAllText(file,multiFaction.Replace("Test Beta","Classic Beta PvE"));
  File.WriteAllText(args[1],JsonSerializer.Serialize(MarketSync.BuildBatches(file)));
 }

 string withIdentity=sample.Replace("[\"ledgers\"]={", "[\"marketIdentities\"]={[\"18:1:110:Test Beta:Alliance\"]={[\"scope\"]=\"18:1:110:Test Beta:Alliance\",[\"project\"]=18,[\"build\"]=\"70374\",[\"realmID\"]=4618,[\"observedAt\"]="+now+",[\"PRIVATE_CHARACTER\"]=\"do not share\"}},[\"ledgers\"]={");
 File.WriteAllText(file,withIdentity);
 Assert(!JsonSerializer.Serialize(MarketSync.BuildBatches(file)).Contains("realmID"),"Old service capability keeps the legacy upload schema");
 using(var identityDoc=JsonDocument.Parse(JsonSerializer.Serialize(MarketSync.BuildBatches(file,includeRealmIdentity:true)))){
  var m=identityDoc.RootElement[0].GetProperty("market");Assert(m.GetProperty("realmID").GetInt64()==4618&&m.GetProperty("realmObservedAt").GetInt64()==now,"Only exact-scope observed server identity is shared");
  Assert(!identityDoc.RootElement.ToString().Contains("PRIVATE_CHARACTER"),"Identity evidence excludes character data");
 }
 File.WriteAllText(file,withIdentity.Replace("[\"scope\"]=\"18:1:110:Test Beta:Alliance\"","[\"scope\"]=\"18:1:110:Other:Alliance\""));
 Assert(!JsonSerializer.Serialize(MarketSync.BuildBatches(file,includeRealmIdentity:true)).Contains("realmID"),"Last-character realm evidence never labels a different retained market");
 File.WriteAllText(file,withIdentity.Replace("[\"realmID\"]=4618","[\"conflict\"]=true,[\"realmID\"]=4618"));
 Assert(!JsonSerializer.Serialize(MarketSync.BuildBatches(file,includeRealmIdentity:true)).Contains("realmID"),"Conflicting realm evidence is held for diagnosis");
 File.WriteAllText(file,sample);
 using(var source=JsonDocument.Parse("{\"capabilities\":{\"marketIdentity\":1}}"))Assert(MarketSync.SupportsMarketIdentity(source.RootElement),"Versioned capability enables optional realm evidence");
 using(var source=JsonDocument.Parse("{}"))Assert(!MarketSync.SupportsMarketIdentity(source.RootElement),"Old service cannot receive new realm evidence fields");
 string craftSample="ForeverFlipDB={client={interface=16001,build=\"70374\"},crafting={catalog={[\"18:70374\"]={[100]={recipeID=100,name=\"Example\",profession=\"Blacksmithing\",category=\"Armor\",outputItemID=100,outputMin=1,outputMax=1,reagents={{itemID=4234,quantity=2}},complete=true,learned=true,character=\"PRIVATE_CRAFTER\",vendorCopper=1000,cooldownSeconds=0,observedAt="+now+"}}},vendors={}}}";
 string depthSample="ForeverFlipDB={client={interface=16001,build=\"70374\"},markets={[\"18:1:110:Test Beta:Alliance\"]={[\"1:0:0:0\"]={name=\"Cloth\",commodity=true,history={{t="+(now-1800)+",p=1,q=3}},tooltipQuote={price=1,quantity=3,seen="+now+",source=\"exact\",unitKnown=true},depthQuote={schemaVersion=1,at="+now+",commodity=true,complete=true,tiers={{unitCopper=1,quantity=1},{unitCopper=100,quantity=2}}}}}}}";
 string SavedLiteral(string raw)=>"ForeverFlipDB="+System.Text.RegularExpressions.Regex.Replace(raw["ForeverFlipDB=".Length..],@"\b([A-Za-z_]\w*)=","[\"$1\"]=");
 File.WriteAllText(file,SavedLiteral(depthSample));
 Assert(!JsonSerializer.Serialize(MarketSync.BuildBatches(file)).Contains("\"depth\""),"Legacy service does not receive optional depth");
 using(var depthDoc=JsonDocument.Parse(JsonSerializer.Serialize(MarketSync.BuildBatches(file,includeDepth:true))))Assert(depthDoc.RootElement[0].GetProperty("items")[0].GetProperty("depth").GetProperty("tiers").GetArrayLength()==2,"Owned-excluded exact commodity depth is transported without inventing minimum-price stock");
 using(var wrongCaps=JsonDocument.Parse("{\"capabilities\":{\"commodityDepth\":\"1\"}}"))Assert(!MarketSync.SupportsCapability(wrongCaps.RootElement,"commodityDepth"),"Malformed optional capability is safely unsupported");
 var histories=new StringBuilder();for(int h=0;h<336;h++)histories.Append("{t=").Append(now-h*1800).Append(",p=100000000000,q=100000000},");
 var dense=new StringBuilder("ForeverFlipDB={client={interface=16001,build=\"70374\"},markets={[\"18:1:110:Test Beta:Alliance\"]={");
 for(int k=1;k<=120;k++)dense.Append("[\"").Append(k).Append(":0:0:0\"]={name=\"Dense\",history={").Append(histories).Append("}},");dense.Append("}}}");File.WriteAllText(file,SavedLiteral(dense.ToString()));
 var denseBatches=MarketSync.BuildBatches(file);Assert(denseBatches.All(b=>JsonSerializer.SerializeToUtf8Bytes(b).Length<4*1024*1024),"Dense histories stay below the server body limit");
 using(var denseDoc=JsonDocument.Parse(JsonSerializer.Serialize(denseBatches)))Assert(denseDoc.RootElement.EnumerateArray().Sum(b=>b.GetProperty("items").GetArrayLength())==120,"Byte-bounded batching loses no items");
 string repeatedSample=craftSample.Replace("reagents={{itemID=4234,quantity=2}}","reagents={{itemID=4234,quantity=1},{itemID=4234,quantity=1}}");
 craftSample=System.Text.RegularExpressions.Regex.Replace(craftSample,@"(?<=[{,])([A-Za-z_][A-Za-z_0-9]*)=","[\"$1\"]=");
 var recipeBatches=MarketSync.BuildCatalogBatches(LuaReader.Read(craftSample),batches,now);string recipeJson=JsonSerializer.Serialize(recipeBatches);
 var vendorDB=LuaReader.Read(craftSample);
 ((Dictionary<string,object?>)vendorDB["crafting"]!)["vendors"]=new Dictionary<string,object?>{
  ["18:70374"]=new Dictionary<string,object?>{["2320"]=new Dictionary<string,object?>{
   ["itemID"]=2320d,["bundleCopper"]=10d,["bundleQuantity"]=1d,["unlimited"]=true,["observedAt"]=(double)(now-90*86400),["characterKey"]="PRIVATE_VENDOR_CHARACTER"},
   ["999"]=new Dictionary<string,object?>{["itemID"]=999d,["bundleCopper"]=1d,["bundleQuantity"]=1d,["unlimited"]=true,["observedAt"]=(double)(now+1000)}},
  ["18:OTHER_BUILD"]=new Dictionary<string,object?>{["777"]=new Dictionary<string,object?>{["itemID"]=777d,["bundleCopper"]=1d,["bundleQuantity"]=1d,["unlimited"]=true,["observedAt"]=(double)now}}};
 using(var vendorDoc=JsonDocument.Parse(JsonSerializer.Serialize(MarketSync.BuildCatalogBatches(vendorDB,batches,now)))){
  var vendors=vendorDoc.RootElement[0].GetProperty("vendorMaterials");
  Assert(vendors.GetArrayLength()==1&&vendors[0].GetProperty("itemID").GetInt64()==2320&&vendors[0].GetProperty("observedAt").GetInt64()==now-90*86400,
      "Old vendor prices sync with original timestamps, while future and other-build quotes stay excluded");
  Assert(!vendorDoc.RootElement.ToString().Contains("PRIVATE_VENDOR_CHARACTER"),"Merchant sharing excludes private character identity");
 }
 Assert(MarketSync.CountSharedRecipesForBuild(new Dictionary<string,string>{["70374:recipe:100"]="old",["70425:recipe:100"]="new",["70425:recipe:101"]="new",["70425:vendor:1"]="supply"},"70425")==2,
     "Recipe count reports the current client build, not retained fingerprints from older beta builds");
 Assert(recipeBatches.Count==1&&!recipeJson.Contains("PRIVATE_CRAFTER")&&!recipeJson.Contains("learned"),"Recipe uploads exclude character/learned state and unrelated data");
 var patternDB=LuaReader.Read("ForeverFlipDB={ [\"client\"]={[\"interface\"]=16001,[\"build\"]=\"70374\"},[\"crafting\"]={[\"patternItems\"]={[\"18:70374\"]={[500]={[\"itemID\"]=500,[\"classID\"]=9,[\"subclassID\"]=2,[\"name\"]=\"Pattern: Test\",[\"profession\"]=\"Tailoring\",[\"observedAt\"]="+now+",[\"sources\"]={[\"bag\"]=true},[\"quantity\"]=5,[\"learned\"]=true,[\"character\"]=\"PRIVATE_FINDER\"}}}}}");
 var patternBatches=MarketSync.BuildCatalogBatches(patternDB,batches,now);string patternJSON=JsonSerializer.Serialize(patternBatches);
 using(var patternDoc=JsonDocument.Parse(patternJSON)){
  var pattern=patternDoc.RootElement[0].GetProperty("patternItems")[0];
  Assert(pattern.GetProperty("itemID").GetInt64()==500&&patternDoc.RootElement[0].GetProperty("recipes").GetArrayLength()==0,"Pattern-only discovery uploads without inventing a recipe or material list");
  Assert(!patternJSON.Contains("PRIVATE_FINDER")&&!patternJSON.Contains("sources")&&!patternJSON.Contains("bag")&&!patternJSON.Contains("quantity")&&!patternJSON.Contains("learned"),"Pattern sharing strips inventory, origins and character access");
  var patternPack=JsonSerializer.SerializeToElement(new{schema="foreverflip.market-pack",schemaVersion=1,scope="18:1:110:Test Beta:Alliance",packID="pattern-fixture",createdAt=now-1,expiresAt=now+3600,recommendations=Array.Empty<object>(),crafting=new{schema=1,build="70374",catalogue=Array.Empty<object>(),plans=Array.Empty<object>(),patternItems=new[]{pattern.Clone()}}});
  Assert(MarketSync.RenderPackEntry(patternPack).Contains("patternItems"),"Signed metadata-only patterns reach the data addon without becoming plans");
 }
 Assert(MarketSync.BuildCatalogBatches(patternDB,batches,now,false).Count==0,"An older service receives no pattern-only requests");
 Assert(!JsonSerializer.Serialize(MarketSync.BuildCatalogBatches(LuaReader.Read(craftSample),batches,now,false)).Contains("patternItems"),"An older strict service receives its unchanged recipe schema");
 using(var legacySource=JsonDocument.Parse("{}"))Assert(!MarketSync.SupportsPatternDiscovery(legacySource.RootElement),"Missing service capability leaves patterns pending");
 using(var supportedSource=JsonDocument.Parse("{\"capabilities\":{\"patternDiscovery\":1}}"))Assert(MarketSync.SupportsPatternDiscovery(supportedSource.RootElement),"Versioned capability enables pattern sync");
 using(var badSource=JsonDocument.Parse("{\"capabilities\":{\"patternDiscovery\":\"yes\"}}"))Assert(!MarketSync.SupportsPatternDiscovery(badSource.RootElement),"Malformed service capability does not break normal synchronization");
 Assert(MarketSync.CountSharedPatternsForBuild(new Dictionary<string,string>{{"70374:pattern:500","x"},{"70374:recipe:100","x"},{"70400:pattern:501","x"}},"70374")==1,"Pattern counts stay separate from verified recipes and old builds");
 var patternTable=(Dictionary<string,object?>)((Dictionary<string,object?>)((Dictionary<string,object?>)patternDB["crafting"]!)["patternItems"]!)["18:70374"]!;
 ((Dictionary<string,object?>)patternTable["500"]!)["classID"]=7d;
 Assert(MarketSync.BuildCatalogBatches(patternDB,batches,now).Count==0,"A nonrecipe item cannot enter pattern sharing");
 repeatedSample=System.Text.RegularExpressions.Regex.Replace(repeatedSample,@"(?<=[{,])([A-Za-z_][A-Za-z_0-9]*)=","[\"$1\"]=");
 var repeatedBatches=MarketSync.BuildCatalogBatches(LuaReader.Read(repeatedSample),batches,now);
 using(var repeatedDoc=JsonDocument.Parse(JsonSerializer.Serialize(repeatedBatches))){
  var mats=repeatedDoc.RootElement[0].GetProperty("recipes")[0].GetProperty("reagents");
  Assert(mats.GetArrayLength()==1&&mats[0].GetProperty("quantity").GetInt64()==2,"Legacy repeated reagent slots upload as one complete material, without exposing character state");
 }
 string badReagent=craftSample.Replace("[\"quantity\"]=2","[\"quantity\"]=2,[\"itemKey\"]=\"999:0:0:0\"");
 Assert(MarketSync.BuildCatalogBatches(LuaReader.Read(badReagent),batches,now).Count==0,"A conflicting reagent variant cannot silently become the base item");
 string badOutput=craftSample.Replace("[\"outputItemID\"]=100","[\"outputItemID\"]=100,[\"outputKey\"]=\"999:0:0:0\"");
 Assert(MarketSync.BuildCatalogBatches(LuaReader.Read(badOutput),batches,now).Count==0,"A conflicting output variant cannot silently become the base item");
 using(var recipeDoc=JsonDocument.Parse(recipeJson))Assert(recipeDoc.RootElement[0].GetProperty("savedAt").GetInt64()==now,"Recipe upload preserves actual game save time");
 using(var recipeDoc=JsonDocument.Parse(recipeJson)){
  var pack=JsonSerializer.SerializeToElement(new{schema="foreverflip.market-pack",schemaVersion=1,scope="18:1:110:Test Beta:Alliance",packID="crafting-fixture",createdAt=now-1,expiresAt=now+3600,recommendations=Array.Empty<object>(),crafting=new{schema=1,build="70374",catalogue=new[]{recipeDoc.RootElement[0].GetProperty("recipes")[0].Clone()},plans=new[]{new{recipeID=100,route="vendor",reason="\";error('must not execute')--",expiresAt=now+3600}}}});
  string rendered=MarketSync.RenderPackEntry(pack);Assert(rendered.Contains("crafting=")&&rendered.Contains("\\034;error"),"Crafting pack is escaped data-only Lua");
  File.WriteAllText(Path.Combine(TestPaths.Root,"crafting-roundtrip-fixture.lua"),"ForeverFlipShared={schema=1,markets={"+rendered+"}}");
 }
 Assert(batches.Count==1&&!json.Contains("SECRET_CHARACTER")&&!json.Contains("ledgers")&&!json.Contains(dir),"Whitelisted upload excludes private identity/path");
 using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);string publicKey=Path.Combine(dir,"public.pem");File.WriteAllText(publicKey,key.ExportSubjectPublicKeyInfoPem());
 byte[] payload=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{createdAt=now,expiresAt=now+100,schema="example"}));
 var envelope=new{algorithm="ES256",payload=Convert.ToBase64String(payload),signature=Convert.ToBase64String(key.SignData(payload,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence))};
 using var good=JsonDocument.Parse(JsonSerializer.Serialize(envelope));Assert(MarketSync.Verify(good.RootElement,publicKey).GetProperty("schema").GetString()=="example","ES256 verifies");
 using var bad=JsonDocument.Parse(JsonSerializer.Serialize(new{envelope.algorithm,payload=Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")),envelope.signature}));Reject(()=>MarketSync.Verify(bad.RootElement,publicKey),"forged pack");
 string testMarket="ff1-"+new string('a',32),cacheRoot=Path.Combine(dir,"pack-cache"),packClient=Path.Combine(dir,"pack-client");Directory.CreateDirectory(cacheRoot);
 Directory.CreateDirectory(Path.Combine(packClient,"Interface","AddOns","ForeverFlip"));
 var advice=JsonSerializer.SerializeToElement(new{schema="foreverflip.market-pack",schemaVersion=1,marketID=testMarket,scope="18:1:110:Test Beta:Alliance",packID="advice-fixture",createdAt=now-1,expiresAt=now+3600,
  recommendations=new[]{new{id="vendor",itemKey="4234:0:0:0",kind="vendor_buy",priority=90,expiresAt=now+3600,reason="Vendor test",maxBuyUnitCopper=95,maxQuantity=20}},
  crafting=new{schema=1,build="70374",catalogue=Array.Empty<object>(),plans=Array.Empty<object>()}});
 byte[] signedBytes=Encoding.UTF8.GetBytes(advice.GetRawText());var signedAdvice=JsonSerializer.SerializeToElement(new{algorithm="ES256",payload=Convert.ToBase64String(signedBytes),signature=Convert.ToBase64String(key.SignData(signedBytes,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence))});
 File.WriteAllText(Path.Combine(cacheRoot,testMarket+".json"),signedAdvice.GetRawText());
 var packConfig=new Settings{ClientRoot=packClient,LastPack="advice-fixture",LoadedPack="previous-fixture",LoadedPackScope="18:1:110:Test Beta:Alliance",MarketIDs=[testMarket],MarketPackIDs=new(){{testMarket,"advice-fixture"}}};
 Assert(MarketSync.ClassifyResearch(now-1,now+3600,false,false,now)==ResearchAvailability.Downloaded&&
        MarketSync.ClassifyResearch(now-1,now+3600,true,false,now)==ResearchAvailability.Ready&&
        MarketSync.ClassifyResearch(now-1,now+3600,true,true,now)==ResearchAvailability.Loaded&&
        MarketSync.ClassifyResearch(now-1,now,true,true,now)==ResearchAvailability.Expired&&
        MarketSync.ClassifyResearch(now+1,now+3600,true,true,now)==ResearchAvailability.Scheduled&&
        MarketSync.ClassifyResearch(now,now,true,true,now)==ResearchAvailability.None,
        "Research status distinguishes downloaded, staged, loaded and expired packs");
 var unstaged=new MarketSync(packConfig,cacheRoot,publicKey);Assert(unstaged.PackStatus.Contains("1 vendor, 0 resale, 0 craft, 0 research")&&unstaged.PackStatus.Contains("needs staging")&&unstaged.ResearchIndicator=="Research downloaded; syncing"&&!unstaged.PackLoadConfirmed,"Startup restores actual counts without treating a remembered ID as staged");
 var stage=typeof(MarketSync).GetMethod("StagePackCore",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
 Assert(!(bool)stage.Invoke(unstaged,[advice,signedAdvice,(Func<bool>)(()=>true)])!&&!Directory.Exists(Path.Combine(packClient,"Interface","AddOns","ForeverFlip_Data")),"First-time helper creation waits for game exit, while verified pack remains cached");
 stage.Invoke(unstaged,[advice,signedAdvice,(Func<bool>)(()=>false)]);
 var ready=new MarketSync(packConfig,cacheRoot,publicKey);Assert(ready.PackStatus.Contains("Ready for /reload")&&ready.ResearchIndicator=="Research ready after /reload"&&!ready.PackLoadConfirmed,"Cached valid helper is detected before any network tick");
 File.WriteAllText(Path.Combine(packClient,"Interface","AddOns","ForeverFlip_Data","Data.lua"),"CORRUPTED");
 Assert(new MarketSync(packConfig,cacheRoot,publicKey).PackStatus.Contains("needs staging"),"Matching cached pack ID cannot hide damaged addon data");
 File.WriteAllText(Path.Combine(cacheRoot,"ff1-"+new string('b',32)+".json"),"MALFORMED");stage.Invoke(unstaged,[advice,signedAdvice,(Func<bool>)(()=>false)]);
 packConfig.LoadedPack="advice-fixture";var loaded=new MarketSync(packConfig,cacheRoot,publicKey);Assert(loaded.PackStatus.Contains("Confirmed loaded")&&loaded.PackLoadConfirmed&&loaded.ResearchIndicator=="Research loaded (last game save)","Saved matching acknowledgement is reported distinctly");
 Assert(MarketSync.DescribePack(advice,true,"advice-fixture",now+3600).Contains("expired"),"Expired advice is not described as current");
 var expiredNode=System.Text.Json.Nodes.JsonNode.Parse(advice.GetRawText())!;expiredNode["createdAt"]=now-7200;expiredNode["expiresAt"]=now-1;
 byte[] expiredBytes=Encoding.UTF8.GetBytes(expiredNode.ToJsonString());
 var expiredEnvelope=JsonSerializer.SerializeToElement(new{algorithm="ES256",payload=Convert.ToBase64String(expiredBytes),signature=Convert.ToBase64String(key.SignData(expiredBytes,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence))});
 File.WriteAllText(Path.Combine(cacheRoot,testMarket+".json"),expiredEnvelope.GetRawText());
 var expired=new MarketSync(packConfig,cacheRoot,publicKey);
 Assert(expired.ResearchIndicator=="Research expired"&&!expired.PackLoadConfirmed&&!expired.ResearchPending,"A real signed expired cache retains its status across app restart without appearing active");
 Reject(()=>MarketSync.Verify(expiredEnvelope,publicKey),"Expired cached advice remains rejected by the activation/download verifier");
 File.WriteAllText(Path.Combine(cacheRoot,testMarket+".json"),signedAdvice.GetRawText());
 packConfig.MarketIDs.Add("ff1-"+new string('c',32));var mixedSync=new MarketSync(packConfig,cacheRoot,publicKey);string mixed=mixedSync.PackStatus;
 Assert(mixed.Contains("1 vendor")&&mixed.Contains("No valid cached"),"Missing second-market pack cannot overwrite valid first-market status");
 Assert(mixedSync.PackLoadConfirmed,"Other account markets cannot negate the last character's valid scoped loaded acknowledgement");
 packConfig.LoadedPackScope="18:1:110:Other Beta:Alliance";
 Assert(!new MarketSync(packConfig,cacheRoot,publicKey).PackLoadConfirmed,"A pack ID cannot claim loaded confirmation for another market scope");
 mixedSync.RememberAcknowledgement(LuaReader.Read("ForeverFlipDB={ [\"client\"]={ [\"addon\"]=\"0.13.26\" },[\"sharedData\"]={ [\"packID\"]=\"advice-fixture\",[\"scope\"]=\"18:1:110:Test Beta:Alliance\" } }"));
 Assert(packConfig.LoadedPackScope=="18:1:110:Test Beta:Alliance"&&packConfig.LoadedPack=="advice-fixture","Saved acknowledgement captures its exact scope");
 mixedSync.RememberAcknowledgement(LuaReader.Read("ForeverFlipDB={ [\"client\"]={ [\"addon\"]=\"0.13.26\" } }"));
 Assert(packConfig.LoadedPack==""&&packConfig.LoadedPackScope=="","Missing acknowledgement in a later save clears stale desktop confirmation");
 var changedClient=new MarketSync(packConfig,cacheRoot,publicKey);packConfig.LastUploaded="Previous upload";packConfig.LoadedAddonVersion="0.12.99";packConfig.SavedItemCount=123;packConfig.RecipesSynced=42;packConfig.PendingAddonRepair=true;
 packConfig.ProtectedToken="UNCHANGED_PROTECTED_TEST_TOKEN";packConfig.ShareMarket=false;packConfig.CatalogNoticeAccepted=true;
 changedClient.SelectClient(Path.Combine(dir,"other-client"),"other-save.lua");
 Assert(packConfig.LastPack==""&&packConfig.LoadedPack==""&&packConfig.LoadedPackScope==""&&packConfig.LastUploaded==""&&packConfig.LoadedAddonVersion==""&&packConfig.MarketIDs.Count==0&&packConfig.MarketPackIDs.Count==0&&packConfig.SavedItemCount==null&&packConfig.RecipesSynced==0,"Changing client clears only the prior client dataset, upload and loaded acknowledgements");
 Assert(!packConfig.ShareMarket&&packConfig.CatalogNoticeAccepted&&packConfig.ProtectedToken=="UNCHANGED_PROTECTED_TEST_TOKEN"&&!packConfig.PendingAddonRepair&&packConfig.PendingAddonInstall,"Changing client preserves sharing/credential preferences and does not carry a force-repair intent");
 Assert(!changedClient.PackLoadConfirmed&&changedClient.SavedStatus=="Not read yet","New client starts unconfirmed rather than borrowing another client's state");
 string badSettings=Path.Combine(dir,"corrupt-settings.json");
 File.WriteAllText(badSettings,"{\"MarketIDs\":null,\"LastPack\":null,\"ItemFingerprints\":null,\"ShareMarket\":false,\"ProtectedToken\":\"PRIVATE_TEST\",\"SavedItemCount\":\"bad\"}");
 var recovered=MarketSync.LoadSettings(badSettings);_ = new MarketSync(recovered,cacheRoot,publicKey);
 Assert(recovered.MarketIDs.Count==0&&recovered.ItemFingerprints.Count==0&&recovered.LastPack==""&&!recovered.ShareMarket&&recovered.ProtectedToken=="PRIVATE_TEST"&&recovered.SavedItemCount==null,"Invalid fields recover individually without losing credentials/preferences");
 Assert(Directory.EnumerateFiles(dir,"corrupt-settings.json.invalid-*").Any(),"Original invalid settings are retained before replacement");
 var manyVendors=new Dictionary<string,object?>();for(int i=1;i<=450;i++)manyVendors[i.ToString()]=new Dictionary<string,object?>{{"itemID",(double)i},{"bundleCopper",10d},{"bundleQuantity",1d},{"unlimited",true},{"observedAt",(double)now}};
 var catalogDB=LuaReader.Read(craftSample);MarketSync.Sub(MarketSync.Sub(catalogDB,"crafting"),"vendors")["18:70374"]=manyVendors;
 var vendorBatches=MarketSync.BuildCatalogBatches(catalogDB,batches,now);int sentVendors=0;
 foreach(var b in vendorBatches){using var vendorParsed=JsonDocument.Parse(JsonSerializer.Serialize(b));int size=vendorParsed.RootElement.GetProperty("vendorMaterials").GetArrayLength();Assert(size<=200,"Vendor request bound");sentVendors+=size;}
 Assert(sentVendors==450,"All retained merchant quotes survive per-request batching");
 Console.WriteLine("PASS: settings corruption recovery, original backup, complete vendor catalogue batching, helper lifecycle, verified cached pack startup, expiry and acknowledgement");
 string escaped=MarketSync.LuaString("\";evil()--\n|cffff0000é");Assert(!escaped.Contains('\n')&&escaped.StartsWith("\"\\034"),"Data strings are escaped Lua literals");
 string parent=Path.Combine(dir,"World of Warcraft"),client=Path.Combine(parent,"_classic_beta_");
 Directory.CreateDirectory(Path.Combine(client,"Interface","AddOns"));Directory.CreateDirectory(Path.Combine(client,"WTF","Account","test","SavedVariables"));
 File.WriteAllText(Path.Combine(client,"Wow.exe"),"");string personal=Path.Combine(client,"WTF","Account","test","SavedVariables","ForeverFlip.lua");File.WriteAllText(personal,"PERSONAL_DATABASE");
 Assert(ClientInstall.ResolveRoot(parent)==Path.GetFullPath(client),"Parent game folder resolves to beta client");
 byte[] Zip(params (string name,string body)[] entries){using var memory=new MemoryStream();using(var zip=new System.IO.Compression.ZipArchive(memory,System.IO.Compression.ZipArchiveMode.Create,true))foreach(var entry in entries){using var writer=new StreamWriter(zip.CreateEntry(entry.name).Open());writer.Write(entry.body);}return memory.ToArray();}
 if(!ClientInstall.GameRunning()){
  ClientInstall.InstallArchive(client,Zip(("ForeverFlip/ForeverFlip.toc","## Version: 0.1.0"),("ForeverFlip/Addon.lua","first")),new Version("0.1.0"));
  string helper=Path.Combine(client,"Interface","AddOns","ForeverFlip_Data","Data.lua");File.WriteAllText(helper,"KEEP_SHARED_DATA");
  ClientInstall.InstallArchive(client,Zip(("ForeverFlip/ForeverFlip.toc","## Version: 0.2.0"),("ForeverFlip/Addon.lua","second")),new Version("0.2.0"));
  Assert(ClientInstall.VersionAt(Path.Combine(client,"Interface","AddOns","ForeverFlip"))==new Version("0.2.0"),"Install updates addon version");
  Assert(File.ReadAllText(personal)=="PERSONAL_DATABASE"&&File.ReadAllText(helper)=="KEEP_SHARED_DATA","Updates preserve saved database and shared data");
  Assert(Directory.EnumerateDirectories(AddonBackups.DirectoryAt(client),"addon-*").Any()&&!Directory.EnumerateDirectories(Path.Combine(client,"Interface","AddOns"),".foreverflip-backup-*").Any(),"Prior addon is backed up outside AddOns");
 }
 Reject(()=>ClientInstall.InstallArchive(client,Zip(("ForeverFlip/../escape.lua","bad")),new Version("0.3.0")),"zip traversal");
 Reject(()=>ClientInstall.InstallArchive(client,Zip(("ForeverFlip/Addon.lua","one"),("ForeverFlip/addon.lua","two")),new Version("0.3.0")),"duplicate zip entries");
 Reject(()=>ClientInstall.InstallArchive(client,Zip(("ForeverFlip/ForeverFlip.toc","## Version: 0.1.0")),new Version("0.3.0")),"release version mismatch");
 string Manifest(string version)=>"## Interface: 16001\n## SavedVariables: ForeverFlipDB\n## Version: "+version+"\nAddon.lua\n";
 ClientInstall.InstallArchiveCore(client,Zip(("ForeverFlip/ForeverFlip.toc",Manifest("0.3.0")),("ForeverFlip/Addon.lua","initial")),new Version("0.3.0"),false,()=>false);
 string liveHelper=Path.Combine(client,"Interface","AddOns","ForeverFlip_Data","Data.lua");File.WriteAllText(liveHelper,"PRESERVE_ADVICE");
 ClientInstall.InstallArchiveCore(client,Zip(("ForeverFlip/ForeverFlip.toc",Manifest("0.4.0")),("ForeverFlip/Addon.lua","reload update")),new Version("0.4.0"),true,()=>true);
 string liveTarget=Path.Combine(client,"Interface","AddOns","ForeverFlip");
 Assert(ClientInstall.VersionAt(liveTarget)==new Version("0.4.0")&&File.ReadAllText(liveHelper)=="PRESERVE_ADVICE"&&File.ReadAllText(personal)=="PERSONAL_DATABASE","Explicit compatible update while game runs preserves personal save and helper");
 ClientInstall.InstallArchiveCore(client,Zip(("ForeverFlip/ForeverFlip.toc",Manifest("0.4.0")),("ForeverFlip/Addon.lua","same-version repair")),new Version("0.4.0"),true,()=>true);
 Assert(File.ReadAllText(Path.Combine(liveTarget,"Addon.lua"))=="same-version repair","Explicit addon action repairs equal approved version");
 Reject(()=>ClientInstall.InstallArchiveCore(client,Zip(("ForeverFlip/ForeverFlip.toc",Manifest("0.5.0")),("ForeverFlip/Addon.lua","blocked")),new Version("0.5.0"),false,()=>true),"Automatic updates wait while game runs");
 Reject(()=>ClientInstall.InstallArchiveCore(client,Zip(("ForeverFlip/ForeverFlip.toc",Manifest("0.5.0")+"Extra.lua\n"),("ForeverFlip/Addon.lua","blocked"),("ForeverFlip/Extra.lua","new")),new Version("0.5.0"),true,()=>true),"Changed load files require restart");
 Reject(()=>ClientInstall.InstallArchiveCore(client,Zip(("ForeverFlip/ForeverFlip.toc",Manifest("0.5.0").Replace("ForeverFlipDB","OtherDB")),("ForeverFlip/Addon.lua","blocked")),new Version("0.5.0"),true,()=>true),"Changed saved-variable declaration requires restart");
 Reject(()=>ClientInstall.InstallArchiveCore(client,Zip(("ForeverFlip/ForeverFlip.toc",Manifest("0.5.0"))),new Version("0.5.0"),true,()=>true),"Missing declared Lua file cannot be staged for reload");
 Reject(()=>ClientInstall.InstallArchiveCore(client,Zip(("ForeverFlip/ForeverFlip.toc",Manifest("0.3.0")),("ForeverFlip/Addon.lua","old")),new Version("0.3.0"),true,()=>true),"Force never downgrades");
 Assert(ClientInstall.VersionAt(liveTarget)==new Version("0.4.0")&&File.ReadAllText(Path.Combine(liveTarget,"Addon.lua"))=="same-version repair","Rejected updates preserve installed code");
 AddonBackupTests.Run(Path.Combine(dir,"backup-tests"));
 Console.WriteLine("PASS: explicit reload-compatible update, equal-version repair, automatic deferral, structure/manifest checks, no downgrade and personal/helper preservation");
 if(args.Length>0&&File.Exists(args[0])){
  var started=System.Diagnostics.Stopwatch.StartNew();var real=MarketSync.BuildBatches(args[0]);
  if(args.Length>1)File.WriteAllText(args[1],JsonSerializer.Serialize(real));
  Console.WriteLine($"Real save: {real.Count} sanitized batches in {started.Elapsed.TotalSeconds:F2}s");
 }
 Console.WriteLine("PASS: literal parser, upload privacy, ES256/forgery rejection, client resolution, archive path/duplicate/version guards. Install/update/database-preservation checks "+(ClientInstall.GameRunning()?"skipped while WoW is running.":"passed."));
}finally{
 if(!Path.GetFullPath(dir).StartsWith(Path.GetFullPath(TestPaths.Root)+Path.DirectorySeparatorChar+"forever-test-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe test cleanup");
 Directory.Delete(dir,true);
}

