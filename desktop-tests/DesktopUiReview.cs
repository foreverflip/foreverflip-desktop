using ForeverFlip.Desktop;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class DesktopUiReview
{
 sealed class NoAccountNetwork:System.Net.Http.HttpMessageHandler
 {
  public int Calls;
  protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token){
   Calls++;return Task.FromException<System.Net.Http.HttpResponseMessage>(new InvalidOperationException("UI review must not use the network."));
  }
 }
 public static void Run(string output)
 {
  Exception? failure=null;string visualStatus="not checked";Directory.CreateDirectory(output);
  var thread=new Thread(()=>{try{
   RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
   Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
   var app=new Application();var report=new List<object>();
   // The UI fixture has no token and never calls Tick, Save, startup or installation actions.
   string fixture=Path.Combine(TestPaths.Root,"desktop-ui-fixture");Directory.CreateDirectory(Path.Combine(fixture,"Interface","AddOns","ForeverFlip"));File.WriteAllText(Path.Combine(fixture,"Interface","AddOns","ForeverFlip","ForeverFlip.toc"),"## Version: 0.12.78");
   var settings=new Settings{ClientRoot=fixture,NoticeAccepted=true,ShareMarket=true,LastUploaded="10/14/2026 9:31 AM",MarketLabel="Classic Beta PvE · Alliance",SavedItemCount=14870,SavedObservationCount=48442,LastPack="preview-pack"};
   var sync=new MarketSync(settings);
   var accountNetwork=new NoAccountNetwork();
   var previewAccount=new AccountSession(Path.Combine(output,"ui-account-"+Guid.NewGuid().ToString("N")+".json"),accountNetwork);
   sync.Account=previewAccount;
   void Set(string name,object value)=>typeof(MarketSync).GetProperty(name)!.SetValue(sync,value);
   Set(nameof(MarketSync.State),MarketSyncState.Synced);Set(nameof(MarketSync.Status),"Up to date. Waiting for the next game save.");Set(nameof(MarketSync.SavedStatus),"10/14/2026 9:30 AM");Set(nameof(MarketSync.PackStatus),"Downloaded; awaiting a saved game acknowledgement after reload/login.");
   var window=new MainWindow(true,sync);
   var content=(FrameworkElement)window.Content;
   var probeVisual=new DrawingVisual();using(var pen=probeVisual.RenderOpen())pen.DrawRectangle(Brushes.Red,null,new Rect(0,0,8,8));
   var probeImage=new RenderTargetBitmap(8,8,96,96,PixelFormats.Pbgra32);probeImage.Render(probeVisual);byte[] probePixels=new byte[8*8*4];probeImage.CopyPixels(probePixels,8*4,0);
   bool graphicsAvailable=probePixels.Where((_,i)=>i%4==3).Any(a=>a>0);visualStatus=graphicsAvailable?"nonblank screenshot validation passed":"visual rendering unavailable in this session (solid-color probe was transparent)";
   T Find<T>(string name) where T:FrameworkElement=>(T)window.FindName(name);
   void Assert(bool condition,string reason){if(!condition)throw new Exception(reason);}
   void Render(string name,int width,int height,double scale=1){
    content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
    var image=new RenderTargetBitmap((int)(width*scale),(int)(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);image.Render(content);
    int stride=image.PixelWidth*4;byte[] pixels=new byte[stride*image.PixelHeight];image.CopyPixels(pixels,stride,0);
    var colors=new HashSet<uint>();for(int i=0;i<pixels.Length;i+=64)colors.Add(BitConverter.ToUInt32(pixels,i));
    bool screenshotValid=colors.Count>16&&pixels.Where((_,i)=>i%4==3).Any(a=>a>0);
    Assert(!graphicsAvailable||screenshotValid,"UI screenshot is blank despite working drawing support: "+name);
    string screenshotPath=Path.Combine(output,name+".png");
    if(screenshotValid){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(screenshotPath);encoder.Save(file);}
    else if(File.Exists(screenshotPath))File.Move(screenshotPath,screenshotPath+".invalid",true);
    report.Add(new{name,width,height,scale,screenshotValid});
   }
   void Refresh()=>typeof(MainWindow).GetMethod("Render",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)!.Invoke(window,null);
   void Select(bool settingsPage)=>Find<Button>(settingsPage?"SettingsTab":"OverviewTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   Assert(window.Icon!=null&&Find<Image>("BrandImage").Source is BitmapSource brand&&brand.PixelWidth==256,"Packaged window/header branding resources must load");
   Render("home",820,620);
   Assert(Find<Button>("HomeSignInButton").Visibility==Visibility.Visible&&Find<TextBlock>("AccountHeroTitle").Text=="Sign in to unlock AI market analysis","Home needs a clear AI sign-in action");
   Assert(window.FindName("AccountEmail")==null,"Desktop should not duplicate the browser email form");
   Assert(window.FindName("ActivityPage")==null&&window.FindName("ActivityTab")==null,"Removed Activity navigation must stay absent");
   var updating=typeof(MainWindow).GetField("checkingUpdates",BindingFlags.Instance|BindingFlags.NonPublic)!;
   updating.SetValue(window,true);Refresh();
   Assert(!Find<Button>("SyncButton").IsEnabled&&!Find<Button>("ChooseFolderButton").IsEnabled&&!Find<Button>("RepairButton").IsEnabled&&!Find<Button>("SettingsUpdateButton").IsEnabled,"Update work disables conflicting sync, client selection and repair");
   var syncNow=(Task)typeof(MainWindow).GetMethod("SyncNow",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
   Assert(syncNow.IsCompleted&&sync.State==MarketSyncState.Synced,"Tray/manual/timer sync cannot enter Tick while an update owns the client");
   updating.SetValue(window,false);
   var syncGate=(SemaphoreSlim)typeof(MarketSync).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(sync)!;
   syncGate.Wait();try{
    Refresh();Assert(!Find<Button>("ChooseFolderButton").IsEnabled&&!Find<Button>("RepairButton").IsEnabled&&!Find<Button>("UpdateButton").IsEnabled,"Active sync disables conflicting client mutations");
    typeof(MainWindow).GetMethod("SelectClient",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,["DO_NOT_SELECT"]);
    Assert(settings.ClientRoot==fixture,"A direct selection callback cannot bypass the busy guard");
    var update=(Task)typeof(MainWindow).GetMethod("Updates",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[true])!;
    Assert(update.IsCompleted,"An update cannot start while sync owns the client");
   }finally{syncGate.Release();}Refresh();
   var manualAppCheck=(Task)typeof(MainWindow).GetMethod("Updates",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[true])!;
   Assert(manualAppCheck.IsCompleted&&Find<TextBlock>("updateStatus").Text=="Automatic app updates become available after installation.","An uninstalled app check completes without requesting an addon download first: "+Find<TextBlock>("updateStatus").Text+"; completed="+manualAppCheck.IsCompleted);
   typeof(MainWindow).GetMethod("UpdateMessage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[""]);
   Assert(Find<Button>("UpdateButton").Content.ToString()=="Update addon now","Overview needs an independent addon update action");
   settings.LoadedAddonVersion="0.12.77";Refresh();
   Assert(Find<TextBlock>("loadedAddonStatus").Text.Contains("Last game save reports addon 0.12.77"),"Saved loaded version must remain distinct from installed files");
   Assert(Find<TextBlock>("statusHeadline").Text=="Saved scans are up to date","Uploaded state must reflect real sync state");
   Assert(((System.Windows.Documents.Run)window.FindName("observationCount")).Text==48442.ToString("N0"),"Saved observation count must be rendered");
   Select(true);Assert(Find<ScrollViewer>("SettingsPage").Visibility==Visibility.Visible&&Find<Grid>("OverviewPage").Visibility==Visibility.Collapsed,"Settings navigation must switch pages");
   Render("settings",760,520);
   var scroll=Find<ScrollViewer>("SettingsPage");Assert(Find<TextBlock>("accountLine").TextWrapping==TextWrapping.Wrap,"Account instructions must wrap instead of clipping");
   Set(nameof(MarketSync.SupportStatus),"Logs sent · report a12f379b7e84 · 10/17/2026 1:54 PM");Refresh();Render("settings-report-sent",760,520);
   Assert(Find<TextBlock>("supportStatus").TextWrapping==TextWrapping.NoWrap,"Support receipt keeps a full tooltip");
   Render("settings-small",620,480);
   Assert(scroll.ScrollableHeight>0,"Small settings layout needs scrolling");scroll.ScrollToEnd();window.UpdateLayout();Render("settings-small-bottom",620,480);
   var startup=Find<CheckBox>("startup");var startupTop=startup.TransformToAncestor(scroll).Transform(new Point());Assert(startupTop.Y>=0&&startupTop.Y<scroll.ViewportHeight,"Startup option must remain reachable by scrolling");
   scroll.ScrollToTop();
   typeof(AccountSession).GetProperty("RegistrationOpen")!.SetValue(previewAccount,false);
   typeof(AccountSession).GetProperty("RegistrationStatus")!.SetValue(previewAccount,"Registration closed today · existing accounts can sign in · resets midnight Chicago time");
   Refresh();Select(false);Render("home-registration-closed",620,480);
   Assert(Find<Button>("HomeSignInButton").IsEnabled&&Find<TextBlock>("HomeRegistration").Text.Contains("existing accounts"),"Registration capacity must not block existing sign-in");
   Assert(previewAccount.RequestConnectAsync().GetAwaiter().GetResult(),"UI fixture could not prepare its local account handoff");Refresh();
   Render("home-browser-pending-small",620,480);Select(true);
   Render("account-browser-pending-small",620,480);
   var reopen=Find<Button>("ReopenSignInButton");var cancel=Find<Button>("CancelSignInButton");
   Assert(reopen.Visibility==Visibility.Visible&&cancel.Visibility==Visibility.Visible&&window.FindName("AccountEmail")==null,"Pending browser check must expose reopen/cancel without another email form");
   foreach(var button in new[]{reopen,cancel}){
    var bounds=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
    Assert(bounds.Width>0&&bounds.Height>=28&&bounds.Left>=0&&bounds.Right<=620,"Pending account action is clipped at the small size");
   }
   Render("account-browser-pending-scaled",760,520,1.5);
   previewAccount.CancelPendingAsync().GetAwaiter().GetResult();Refresh();
   Assert(reopen.Visibility==Visibility.Collapsed&&previewAccount.PendingVerificationUrl==null&&accountNetwork.Calls==0,"Cancelled preview retained an active browser check or made a network call");
   Select(false);Assert(Find<Grid>("OverviewPage").Visibility==Visibility.Visible,"Overview navigation must return");
   void CheckOverview(string name,int width,int height,double scale=1){
    Render(name,width,height,scale);
    var actions=new[]{"SyncButton","UpdateButton","SettingsTab","OverviewTab","MarketButton","AccountStatusButton"};
    var homeScroll=Find<ScrollViewer>("HomeScroll");
    foreach(string action in actions){
     var button=Find<Button>(action);if(homeScroll.IsAncestorOf(button)){button.BringIntoView();content.UpdateLayout();}
     var bounds=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
     Assert(bounds.Width>0&&bounds.Height>=28&&bounds.Left>=0&&bounds.Right<=width&&bounds.Top>=0&&bounds.Bottom<=height,name+": unreachable control "+action);
     if(homeScroll.IsAncestorOf(button)){
      var viewport=homeScroll.TransformToAncestor(content).TransformBounds(new Rect(homeScroll.RenderSize));
      Assert(bounds.Top>=viewport.Top-1&&bounds.Bottom<=viewport.Bottom+1,name+": Home action cannot scroll into view: "+action);
     }
    }
    var overview=Find<Grid>("OverviewPage");
    Assert(Find<ScrollViewer>("HomeScroll").HorizontalScrollBarVisibility==ScrollBarVisibility.Disabled,"Home must fit horizontally at small sizes");
    var research=Find<TextBlock>("researchIndicator");Assert(research.Text==sync.ResearchIndicator&&(Find<Button>("ResearchStatusButton").Visibility!=Visibility.Visible||research.ActualHeight>=16),name+": compact research status is missing or misleading");
    Assert(Find<TextBlock>("researchDot").ActualWidth<=30,name+": research indicator is not compact");
    var install=Find<TextBlock>("installationStatus");Assert(install.ActualWidth>=120,name+": update buttons crowd out installed status");
   }
   long profitNow=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
   Set(nameof(MarketSync.ProfitBooks),new List<ProfitBook>{new("Synthetic character / PvE",profitNow,
       new[]{new ProfitDay(profitNow/86400,"vendor",1000,1200,200,0,1),new ProfitDay(profitNow/86400,"resale",10000,20000,0,1)},
       new[]{new ProfitStock("vendor",12,900,0)})});
   Find<Button>("ProfitTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Render("profit",760,620);
   Assert(Find<Grid>("ProfitPage").Visibility==Visibility.Visible&&Find<Grid>("OverviewPage").Visibility==Visibility.Collapsed&&Find<ScrollViewer>("SettingsPage").Visibility==Visibility.Collapsed,"Profit must be an exclusive page");
   Assert(Find<DataGrid>("ProfitTotals").Items.Count==5&&Find<DataGrid>("ProfitDaily").Items.Count==2,"Profit strategy/day tables did not load native aggregates");
   Assert(Find<TextBlock>("ProfitStatus").Text.Contains("Includes manual adjustments")&&Find<DataGrid>("ProfitTotals").Items.OfType<ProfitDisplayRow>().Single(r=>r.Strategy=="Vendor").ProfitText.EndsWith(" *"),"Manual outcomes need visible provenance in the actual profit screen");
   Assert(Find<ComboBox>("ProfitCharacter").Items.Count==2&&Find<TextBlock>("ProfitStatus").Text.Contains("Nothing here is uploaded"),"Profit needs character scope and privacy/freshness labels");
   Render("profit-small",620,480);Assert(Find<DataGrid>("ProfitDaily").ActualHeight>=40,"Daily table must remain reachable at a small size");
   Assert(Find<DataGrid>("ProfitTotals").Columns.All(c=>c.ActualWidth>=60),"Profit columns must keep readable widths at the smallest size");
   Assert(Find<DataGrid>("ProfitTotals").Columns.Sum(c=>c.ActualWidth)<=Find<DataGrid>("ProfitTotals").ActualWidth+2,"Profit summary must fit without horizontal scrolling");
   Render("profit-150percent",900,640,1.5);
   Find<Button>("PatchNotesTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Render("patch-notes-small",620,480);
   Assert(Find<ScrollViewer>("PatchNotesPage").Visibility==Visibility.Visible&&Find<Grid>("OverviewPage").Visibility==Visibility.Collapsed&&Find<ScrollViewer>("SettingsPage").Visibility==Visibility.Collapsed,"Patch notes must be an exclusive page");
   var notes=Find<StackPanel>("PatchNotesContent").Children.OfType<TextBlock>().Select(t=>t.Text).ToArray();
   Assert(notes.Any(t=>t.Contains("0.1.33"))&&notes.Any(t=>t.Contains("0.13.90"))&&notes.Any(t=>t.Contains("Shift-click")),"Packaged release notes must identify current versions and linking behavior");
   Assert(Find<ScrollViewer>("PatchNotesPage").ScrollableHeight>0,"Older notes must remain scrollable");Select(false);
   CheckOverview("home-small",620,480);CheckOverview("home-laptop",900,640);CheckOverview("home-150percent",960,660,1.5);CheckOverview("home-200percent",800,480,2);
   var vault=typeof(AccountSession).GetField("vault",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(previewAccount)!;
   vault.GetType().GetProperty("Email")!.SetValue(vault,"preview@example.test");vault.GetType().GetProperty("RefreshToken")!.SetValue(vault,"SYNTHETIC_UI_REFRESH");
   typeof(AccountSession).GetProperty("Status")!.SetValue(previewAccount,"Signed in as preview@example.test.");Refresh();Render("home-signed-in",820,620);
   Assert(Find<Button>("HomeSignInButton").Visibility==Visibility.Collapsed&&Find<Button>("ResearchStatusButton").Visibility==Visibility.Visible,"Signed in Home must show analysis instead of a login prompt");
   Select(true);Render("settings-signed-in-small",620,480);Assert(Find<Button>("AccountMoreButton").Visibility==Visibility.Visible,"Secondary account actions belong in More");Select(false);
   string market="ff1-"+new string('a',32);
   var research=(System.Collections.IDictionary)typeof(MarketSync).GetField("researchPacks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(sync)!;
   long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();settings.MarketIDs.Add(market);
   research[market]=new ResearchPack(now-1,now+3600,true,"preview-pack","18:1:110:Classic Beta PvE:Alliance");settings.LoadedPack="";Refresh();
   Assert(Find<TextBlock>("researchIndicator").Text=="AI analysis ready · /reload in game","Staged research must show the next action");Render("home-research-ready",760,520);
   settings.LoadedPack="preview-pack";settings.LoadedPackScope="18:1:110:Classic Beta PvE:Alliance";Refresh();
   Assert(Find<TextBlock>("researchIndicator").Text=="AI analysis loaded (last game save)","Only acknowledged valid research may show as loaded");Render("home-research-loaded",760,520);
   research[market]=new ResearchPack(now-3600,now-1,true,"preview-pack","18:1:110:Classic Beta PvE:Alliance");Refresh();
   Assert(Find<TextBlock>("researchIndicator").Text=="AI analysis expired","Expired research must not show as loaded");Render("home-research-expired",760,520);
   research.Clear();settings.MarketIDs.Clear();settings.LoadedPack="";settings.LoadedPackScope="";Refresh();
   Set(nameof(MarketSync.PackStatus),string.Join("\n",Enumerable.Repeat("Classic Beta PvE · Alliance: 4 vendor, 0 resale, 1 craft, 1 research lead(s). Pack preview. Ready for /reload or login; game loading not yet confirmed.",8)));Refresh();
   typeof(MainWindow).GetMethod("UpdateMessage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,["Updates unavailable: "+new string('x',400)]);
   Set(nameof(MarketSync.Status),"Service unavailable: "+new string('x',400));Refresh();CheckOverview("home-long-status-small",620,480);CheckOverview("home-long-status-scaled",640,480,2);
   Assert(Find<TextBlock>("packStatus").TextWrapping==TextWrapping.Wrap,"Long/multiple-market pack summaries must wrap");
   Find<Button>("ResearchStatusButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Render("settings-details-small",620,480);
   Assert(Find<ScrollViewer>("SettingsPage").Visibility==Visibility.Visible&&Find<Expander>("TechnicalStatus").IsExpanded,"The small research link opens its full details on the existing Settings screen");
   Assert(Find<ScrollViewer>("SettingsPage").ScrollableHeight>0,"Full status details must remain reachable in Settings");
   Assert(Find<TextBlock>("packStatus").MaxHeight==double.PositiveInfinity&&Find<TextBlock>("status").MaxHeight==double.PositiveInfinity,"Technical status preserves full error and pack messages");Select(false);
   Set(nameof(MarketSync.State),MarketSyncState.Error);Set(nameof(MarketSync.Status),"Service unavailable. Saved data stays local; retrying automatically.");Refresh();Render("offline",840,600);
   Assert(Find<TextBlock>("statusHeadline").Text=="Sync needs attention","A failure must never show the uploaded headline");
   settings.ShareMarket=false;Set(nameof(MarketSync.State),MarketSyncState.SharingOff);Set(nameof(MarketSync.Status),"Market sharing is off. Shared data downloads remain available.");Refresh();Render("sharing-off",840,600);
   Assert(Find<TextBlock>("sharingStatus").Text.Contains("off",StringComparison.OrdinalIgnoreCase),"Disabled sharing must not claim automatic uploads");
   Assert(window.FindName("catalogSharing")==null,"Recipe sharing remains part of the one market-sharing choice, without a second checkbox");
   settings.SavedItemCount=null;settings.SavedObservationCount=null;settings.LastUploaded="";Set(nameof(MarketSync.State),MarketSyncState.NeedsSetup);Set(nameof(MarketSync.Status),"Choose your WoW Forever installation to begin.");Refresh();Render("setup",840,600);
   Assert(((System.Windows.Documents.Run)window.FindName("itemCount")).Text=="—","Unknown counts must not show fabricated data");
   settings.ClientRoot="";Refresh();Render("setup-no-client",620,480);
   Assert(Find<Button>("UpdateButton").Content.ToString()=="Choose WoW folder"&&!Find<Button>("SyncButton").IsEnabled,"Home exposes an actionable first-install path instead of ineffective sync/update buttons");
   Assert(((System.Windows.Documents.Run)window.FindName("addonVersion")).Text=="not installed","An absent addon must not duplicate the word Addon in the version summary");
   settings.NoticeAccepted=false;Set(nameof(MarketSync.Status),"Read the first-run sharing notice to continue.");Refresh();Render("setup-notice",620,480);
   Assert(Find<Button>("UpdateButton").Content.ToString()=="Review sharing notice"&&Find<Button>("UpdateButton").IsEnabled&&!Find<Button>("SyncButton").IsEnabled&&!Find<Button>("SettingsUpdateButton").IsEnabled,"Dismissing first-run consent leaves a way to reopen it while all sync/update work stays gated");
   File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new{rendered=report,graphicsAvailable,visualAcceptance=graphicsAvailable?"Validated nonblank renders":"Unavailable: even a solid-red WPF drawing renders transparent in this session; screenshots are invalid",homeSettingsAndPatchNotes=true,smallSettingsReachable=true,errorAndSharingStates=true,networkCalls=0,settingsWrites=0},new JsonSerializerOptions{WriteIndented=true}));window.Close();app.Shutdown();
  }catch(Exception e){failure=e;}});
  thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();Console.WriteLine("PASS: Home/Settings navigation, saved-data counts, compact research indicator, small-window scrolling, scaled layout, offline/sharing-off/setup and busy-operation states. "+visualStatus+". No uploads or settings writes.");
 }
}
