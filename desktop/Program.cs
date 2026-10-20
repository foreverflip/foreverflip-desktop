using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;
using Forms=System.Windows.Forms;

namespace ForeverFlip.Desktop;

public static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        // Apply only through our signed, persisted attempt path. The SDK's default
        // startup auto-apply bypasses failure backoff and can trap users in a loop.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var single=new Mutex(true,"Local\\ForeverFlipDesktop",out bool first);if(!first)return;
        if(args.Length>=2&&args[0]=="--inspect"){
            try{var batches=MarketSync.BuildBatches(args[1]);string output=args.Length>2?args[2]:Path.Combine(MarketSync.StateRoot,"inspection.json");MarketSync.Atomic(output,JsonSerializer.Serialize(batches));}catch(Exception e){MarketSync.Log(e.Message);Environment.ExitCode=1;}return;
        }
        Directory.CreateDirectory(MarketSync.StateRoot);
        Environment.CurrentDirectory=MarketSync.StateRoot;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var window=new MainWindow();app.Run(window);
    }
}

public sealed partial class MainWindow:System.Windows.Window
{
    readonly MarketSync sync;
    readonly Forms.NotifyIcon tray;
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(30)};
    readonly DesktopUpdateAttempt updateAttempt=new(Path.Combine(MarketSync.StateRoot,"desktop-update-attempt.json"));
    bool quitting,checkingUpdates;
    DateTime nextUpdate=DateTime.MinValue;
    public MainWindow(bool preview=false,MarketSync? syncOverride=null)
    {
        sync=syncOverride??new MarketSync();InitializeComponent();
        // Fit the initial window to the available desktop in WPF device-independent units.
        var workArea=SystemParameters.WorkArea;
        MinWidth=Math.Min(MinWidth,Math.Max(320,workArea.Width-24));
        MinHeight=Math.Min(MinHeight,Math.Max(400,workArea.Height-24));
        Width=Math.Min(Width,Math.Max(MinWidth,workArea.Width-24));
        Height=Math.Min(Height,Math.Max(MinHeight,workArea.Height-24));
        OverviewTab.Click+=(_,_)=>ShowPage("overview");SettingsTab.Click+=(_,_)=>ShowPage("settings");
        PatchNotesTab.Click+=(_,_)=>ShowPage("patch-notes");BuildPatchNotes();
        ProfitTab.Click+=(_,_)=>ShowPage("profit");
        ProfitPeriod.SelectionChanged+=(_,_)=>RenderProfit();ProfitCharacter.SelectionChanged+=(_,_)=>RenderProfit();
        SyncButton.Click+=async(_,_)=>await SyncNow();
        MarketButton.Click+=(_,_)=>Open(MarketSync.Service+"/market/");
        UpdateButton.Click+=async(_,_)=>{
            if(!sync.Config.NoticeAccepted){Notice();Render();if(sync.Config.NoticeAccepted)await SyncNow();}
            else if(string.IsNullOrEmpty(sync.Config.ClientRoot))ChooseClient();else await UpdateAddonNow();
        };
        SettingsUpdateButton.Click+=async(_,_)=>await Updates();
        ResearchStatusButton.Click+=(_,_)=>{ShowPage("settings");TechnicalStatus.IsExpanded=true;packStatus.BringIntoView();};
        ChooseFolderButton.Click+=(_,_)=>ChooseClient();RepairButton.Click+=(_,_)=>InstallAddon();
        MinimizeButton.Click+=(_,_)=>SystemCommands.MinimizeWindow(this);
        MaximizeButton.Click+=(_,_)=>{if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);};
        CloseButton.Click+=(_,_)=>Close();
        sharing.Foreground=Foreground;sharing.IsChecked=sync.Config.ShareMarket;
        sharing.Click+=(_,_)=>{sync.Config.ShareMarket=sharing.IsChecked==true;sync.Config.ShareCatalog=sync.Config.ShareMarket;sync.Config.CatalogNoticeAccepted=true;sync.Config.LastSignature="";sync.Save();_ = SyncNow();};
        startup.IsChecked=sync.Config.StartWithWindows;
        startup.Click+=(_,_)=>{
            try{using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if(startup.IsChecked==true)key.SetValue("ForeverFlip","\""+Environment.ProcessPath+"\" --tray");else key.DeleteValue("ForeverFlip",false);
                sync.Config.StartWithWindows=startup.IsChecked==true;sync.Save();}catch(Exception e){System.Windows.MessageBox.Show(e.Message);}
        };
        if(preview){tray=new Forms.NotifyIcon();Closed+=(_,_)=>tray.Dispose();Render();return;}
        tray=new Forms.NotifyIcon{Text="Forever Flip",Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),Visible=true};
        var menu=new Forms.ContextMenuStrip();menu.Items.Add("Open Forever Flip",null,(_,_)=>Dispatcher.Invoke(()=>{Show();Activate();}));
        menu.Items.Add("Sync now",null,async(_,_)=>await SyncNow());menu.Items.Add("Exit",null,(_,_)=>Dispatcher.Invoke(()=>{quitting=true;Close();}));tray.ContextMenuStrip=menu;
        tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(()=>{Show();Activate();});
        Closing+=(_,e)=>{if(!quitting){e.Cancel=true;Hide();}else{tray.Dispose();System.Windows.Application.Current.Shutdown();}};
        sync.Changed+=()=>Dispatcher.Invoke(Render);timer.Tick+=async(_,_)=>{await SyncNow();if(DateTime.UtcNow>=nextUpdate)await Updates(false);};
        Loaded+=async(_,_)=>{if(!sync.Config.NoticeAccepted)Notice();
            if(sync.Config.NoticeAccepted&&string.IsNullOrEmpty(sync.Config.ClientRoot)){
                var clients=await Task.Run(ClientInstall.Discover);
                if(clients.Count==1)SelectClient(clients[0]);
                else if(clients.Count>1){var choice=new System.Windows.Window{Owner=this,Title="Choose your WoW Forever client",Width=570,Height=350,MinWidth=460,MinHeight=280,Background=Background,Foreground=Foreground,FontFamily=FontFamily,Resources=Resources,WindowStartupLocation=WindowStartupLocation.CenterOwner};var options=new StackPanel{Margin=new Thickness(20)};foreach(string client in clients)options.Children.Add(Button(client,()=>{SelectClient(client);choice.Close();}));choice.Content=new ScrollViewer{Content=options,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};choice.ShowDialog();}
            }
            string previousUpdate=updateAttempt.PendingMessage(typeof(Program).Assembly.GetName().Version!);
            if(previousUpdate!="")UpdateMessage(previousUpdate);
            Render();timer.Start();if(Environment.GetCommandLineArgs().Contains("--tray")&&sync.Config.NoticeAccepted)Hide();await SyncNow();await Updates(false);};
    }
    static SolidColorBrush Brush(string hex)=>new((Color)ColorConverter.ConvertFromString(hex));
    Task SyncNow()=>checkingUpdates?Task.CompletedTask:sync.Tick();
    void ShowPage(string page)
    {
        OverviewPage.Visibility=page=="overview"?Visibility.Visible:Visibility.Collapsed;
        SettingsPage.Visibility=page=="settings"?Visibility.Visible:Visibility.Collapsed;
        PatchNotesPage.Visibility=page=="patch-notes"?Visibility.Visible:Visibility.Collapsed;
        ProfitPage.Visibility=page=="profit"?Visibility.Visible:Visibility.Collapsed;ProfitTab.Tag=page=="profit"?"selected":"";
        PatchNotesTab.Tag=page=="patch-notes"?"selected":"";
        OverviewTab.Tag=page=="overview"?"selected":"";
        SettingsTab.Tag=page=="settings"?"selected":"";
        if(page=="profit")RenderProfit();
    }
    IReadOnlyList<ProfitBook>? displayedBooks;
    void RenderProfit()
    {
        if(ProfitPage==null||ProfitPage.Visibility!=Visibility.Visible)return;
        if(!ReferenceEquals(displayedBooks,sync.ProfitBooks)){
            string? previous=ProfitCharacter.SelectedItem as string;displayedBooks=sync.ProfitBooks;
            ProfitCharacter.Items.Clear();ProfitCharacter.Items.Add("All saved characters");
            foreach(var b in displayedBooks)ProfitCharacter.Items.Add(b.Character);
            ProfitCharacter.SelectedItem=previous!=null&&ProfitCharacter.Items.Contains(previous)?previous:"All saved characters";
        }
        int days=ProfitPeriod.SelectedIndex switch{0=>1,2=>30,_=>7};
        var books=sync.ProfitBooks.Where(b=>ProfitCharacter.SelectedIndex<=0||b.Character==ProfitCharacter.SelectedItem as string).ToArray();
        ProfitTotals.ItemsSource=ProfitBook.Rows(books,days,false);ProfitDaily.ItemsSource=ProfitBook.Rows(books,days,true);
        string details=books.Length==0?"No accounting snapshot yet. Load this addon update, then /reload or log out. Trading totals stay private, including with market sharing off.":
            "Oldest selected snapshot: "+DateTimeOffset.FromUnixTimeSeconds(books.Min(b=>b.At)).ToLocalTime().ToString("g")+". UTC observed days; retained records. Spent includes known deposits; income includes proven refunds. Unconfirmed buys are excluded. AH income is gross until fees match; unpriced sales have no realized profit. Stock uses tracked cost; owned craft inputs use opportunity cost. Nothing here is uploaded."+
            (books.Any(b=>b.LegacyDates)?" Older merchant purchase dates use the batch date.":"")+(books.Any(b=>b.StockIncomplete)?" Some stock exits exceed retained tracking coverage.":"");
        ProfitStatus.Text=books.Length==0?details:"Saved "+DateTimeOffset.FromUnixTimeSeconds(books.Min(b=>b.At)).ToLocalTime().ToString("g")+" · retained UTC days. Unknown costs or fees stay unpriced. Nothing here is uploaded.";
        if(books.SelectMany(b=>b.Days).Any(r=>r.Manual>0)){
            ProfitStatus.Text+=" * Includes manual adjustments.";
            details+=" An asterisk marks totals with user-confirmed manual sales or used/lost stock. Original spending is unchanged; these are private bookkeeping, not observed market sales.";
        }
        ProfitStatus.ToolTip=details;
    }
    void BuildPatchNotes()
    {
        // Packaged plain text only. No remote HTML, script or private log access.
        var assembly=typeof(MainWindow).Assembly;
        string? resource=assembly.GetManifestResourceNames().SingleOrDefault(n=>n.EndsWith(".RELEASE-NOTES.md",StringComparison.Ordinal));
        using var stream=resource==null?null:assembly.GetManifestResourceStream(resource);
        string text=stream==null?"Release notes are unavailable in this package.":new StreamReader(stream).ReadToEnd();
        if(text.Length>131072)text=text[..131072];
        PatchNotesContent.Children.Add(Label("Installed app "+(assembly.GetName().Version?.ToString(3)??"unknown")+" · Bundled release notes. Addon updates can arrive separately.","#ADB5A1"));
        foreach(string raw in text.Replace("\r","").Split('\n')){
            string line=raw.Trim();if(line.Length==0)continue;
            bool heading=line.StartsWith("# ",StringComparison.Ordinal);
            var item=Label(heading?line[2..]:line.StartsWith("- ",StringComparison.Ordinal)?"• "+line[2..]:line,heading?"#E9D7AA":"#EEE7D8");
            item.FontSize=heading?18:13;item.Margin=new Thickness(0,heading?18:4,0,heading?8:4);
            if(heading)item.FontFamily=new FontFamily("Georgia");
            PatchNotesContent.Children.Add(item);
        }
    }
    void UpdateMessage(string message){updateStatus.Text=message;overviewUpdateStatus.Text=message;}
    static TextBlock Label(string text,string color)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Foreground=Brush(color),Margin=new Thickness(0,6,0,6)};
    static System.Windows.Controls.Button Button(string label,Action action){var b=new System.Windows.Controls.Button{Content=label,Padding=new Thickness(14,9,14,9),Margin=new Thickness(0,0,8,8),Background=Brush("#722019"),Foreground=Brush("#F2DDA9"),BorderBrush=Brush("#A98B56"),BorderThickness=new Thickness(1)};b.Click+=(_,_)=>action();return b;}
    static void Open(string target)=>Process.Start(ExternalLinks.StartInfo(target));
    void Render()
    {
        RenderProfit();
        pathStatus.Text=string.IsNullOrEmpty(sync.Config.ClientRoot)?"No WoW client selected":sync.Config.ClientRoot;
        string appVersion=typeof(Program).Assembly.GetName().Version?.ToString(3)??"Unknown";
        string addon=AddonVersion().Replace("## Version:","").Trim();
        desktopVersion.Text=appVersion;addonVersion.Text=addon=="Addon not installed"?"not installed":addon;
        loadedAddonStatus.Text=sync.Config.LoadedAddonVersion==""?"Loaded version: waiting for a game save":"Last game save reports addon "+sync.Config.LoadedAddonVersion+(sync.Config.LoadedAddonVersion!=addon?"; installed files are newer/different.":". This is a saved acknowledgement.");
        installationStatus.Text=sync.Config.PendingAddonInstall?"Installation queued until WoW closes":string.IsNullOrEmpty(sync.Config.ClientRoot)?"Choose your game installation":addon=="Addon not installed"?"Addon not installed":"WoW Forever beta selected";
        status.Text=sync.Status;saveStatus.Text=sync.SavedStatus;uploadStatus.Text=sync.Config.LastUploaded==""?"Not uploaded yet":sync.Config.LastUploaded;
        packStatus.Text=sync.PackStatus;
        supportStatus.Text=sync.SupportStatus;
        researchIndicator.Text=sync.ResearchIndicator;
        researchDot.Foreground=Brush(sync.PackLoadConfirmed?"#BED2A3":sync.ResearchPending?"#DEC182":"#ADB5A1");
        marketStatus.Text=sync.Config.MarketLabel==""?"WoW Forever beta":sync.Config.MarketLabel;
        itemCount.Text=sync.Config.SavedItemCount?.ToString("N0")??"—";
        observationCount.Text=sync.Config.SavedObservationCount?.ToString("N0")??"—";
        sharingStatus.Text=sync.Config.ShareMarket?"Market sharing enabled":"Market sharing off";
        sharingStatus.Foreground=Brush(sync.Config.ShareMarket?"#BED2A3":"#ADB5A1");
        statusHeadline.Text=sync.State switch{
            MarketSyncState.Synced=>"Saved scans are up to date",MarketSyncState.Reading=>"Reading your saved scans",
            MarketSyncState.Uploading=>"Uploading market observations",MarketSyncState.SharingOff=>"Market sharing is off",
            MarketSyncState.Waiting=>"Waiting for the game save",MarketSyncState.Error=>"Sync needs attention",_=>"Let’s connect your game"};
        overviewSyncStatus.Text=sync.State==MarketSyncState.Synced?"New scans sync automatically after /reload or logging out.":sync.Status;
        syncBadge.Text=sync.State==MarketSyncState.Synced?"✓":sync.State==MarketSyncState.Error?"!":"•";
        syncBadge.Foreground=Brush(sync.State==MarketSyncState.Error?"#E8BF77":"#BED2A3");
        packStatus.Foreground=Brush(sync.PackLoadConfirmed?"#BED2A3":"#DEC182");
        bool hasClient=!string.IsNullOrEmpty(sync.Config.ClientRoot);
        UpdateButton.Content=!sync.Config.NoticeAccepted?"Review sharing notice":!hasClient?"Choose WoW folder":addon=="Addon not installed"?"Install addon":"Update addon now";
        UpdateButton.ToolTip=!sync.Config.NoticeAccepted?"Read the data sharing notice and choose whether to share before syncing starts.":!hasClient?"Select your WoW Forever installation to install and manage the addon.":"Install the latest approved addon. Wait for Ready before using /reload.";
        SyncButton.IsEnabled=!checkingUpdates&&!sync.IsBusy&&sync.Config.NoticeAccepted&&hasClient;
        UpdateButton.IsEnabled=!checkingUpdates&&!sync.IsBusy;
        SettingsUpdateButton.IsEnabled=!checkingUpdates&&!sync.IsBusy&&sync.Config.NoticeAccepted;
        ChooseFolderButton.IsEnabled=!checkingUpdates&&!sync.IsBusy;
        RepairButton.IsEnabled=!checkingUpdates&&!sync.IsBusy;
        sharing.ToolTip="Share auction observations, recipe definitions, vendor supplies and anonymous native check outcomes. Personal transactions, names, bags and balances stay local.";
    }
    string AddonVersion(){try{return File.ReadLines(Path.Combine(sync.Config.ClientRoot,"Interface","AddOns","ForeverFlip","ForeverFlip.toc")).First(l=>l.StartsWith("## Version:"));}catch{return "Addon not installed";}}
    void Notice()
    {
        var notice=new System.Windows.Window{Owner=this,Title="Forever Flip — data sharing",Width=550,Height=480,MinWidth=460,MinHeight=350,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background,Foreground=Foreground,FontFamily=FontFamily,Resources=Resources};
        var stack=new StackPanel{Margin=new Thickness(24)};stack.Children.Add(new TextBlock{Text="Build a better market picture",FontSize=23,Foreground=Brush("#F4BD50")});
        stack.Children.Add(Label("After you continue, the app can upload auction prices, stock, timestamps, recipe definitions, pattern descriptions and vendor supply prices to forever.blahh.us.","#F7F4EE"));
        stack.Children.Add(Label("Anonymous check outcomes tell us whether researched and ordinary items reached Ready: buy, failed a check or could not be read. These reports improve research priorities. They are not purchases, sales or profit records.","#BBC0CA"));
        var choice=new CheckBox{Content="Share market data",Foreground=Foreground,IsChecked=sync.Config.ShareMarket,Margin=new Thickness(0,12,0,14)};stack.Children.Add(choice);
        stack.Children.Add(Label("Names, account paths, bag quantities, balances and personal transactions stay local. You can turn sharing off in Settings; downloads remain available. Report issue / feedback in the addon is a separate, explicitly submitted private support report.","#ADB5A1"));
        stack.Children.Add(Button("Continue",()=>{sync.Config.ShareMarket=choice.IsChecked==true;sync.Config.NoticeAccepted=true;sync.Config.ShareCatalog=sync.Config.ShareMarket;sync.Config.CatalogNoticeAccepted=true;sharing.IsChecked=sync.Config.ShareMarket;sync.Config.LastSignature="";sync.Save();notice.DialogResult=true;}));
        notice.Content=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};notice.ShowDialog();
    }
    void ChooseClient()
    {
        if(sync.IsBusy||checkingUpdates)return;
        var picker=new Microsoft.Win32.OpenFolderDialog{Title="Select the WoW client folder (_classic_beta_)",Multiselect=false};
        if(picker.ShowDialog()!=true)return;
        string? resolved=ClientInstall.ResolveRoot(picker.FolderName);
        if(resolved==null){System.Windows.MessageBox.Show("Select WoW Forever or its _classic_beta_ folder. Retail support is coming later.");return;}
        SelectClient(resolved);
    }
    void SelectClient(string root)
    {
        if(sync.IsBusy||checkingUpdates)return;
        string selectedSave="";var files=MarketSync.FindSavedFiles(root).ToArray();
        if(files.Length==1)selectedSave=files[0];
        else if(files.Length>1){var file=new Microsoft.Win32.OpenFileDialog{Title="Choose the account's ForeverFlip.lua",InitialDirectory=Path.Combine(root,"WTF","Account"),Filter="ForeverFlip database|ForeverFlip.lua"};if(file.ShowDialog()!=true)return;selectedSave=file.FileName;}
        if(sync.IsBusy||checkingUpdates){UpdateMessage("A sync or update started while choosing the account. Wait for it to finish, then choose the folder again.");return;}
        sync.SelectClient(root,selectedSave);sync.Save();Render();_ = SyncNow();
    }
    async void InstallAddon()
    {
        if(sync.IsBusy||checkingUpdates)return;
        if(string.IsNullOrEmpty(sync.Config.ClientRoot)){ChooseClient();return;}
        if(sync.GameRunning()){sync.Config.PendingAddonInstall=true;sync.Config.PendingAddonRepair=true;sync.Save();Render();UpdateMessage("Repair queued until WoW closes.");return;}
        checkingUpdates=true;Render();
        try{await SignedAddonDownload.EnsureAsync(sync.Config.ClientRoot,true);sync.Config.PendingAddonInstall=false;sync.Config.PendingAddonRepair=false;sync.Save();Render();UpdateMessage("Addon repaired. Your saved database was preserved.");}catch(Exception e){UpdateMessage(e.Message);}finally{checkingUpdates=false;Render();}
    }
    async Task Updates(bool manual=true)
    {
        if(checkingUpdates||sync.IsBusy||!sync.Config.NoticeAccepted)return;
        checkingUpdates=true;nextUpdate=DateTime.UtcNow.AddHours(1);
        Render();
        try{
            if(manual)UpdateMessage("Checking app updates…");
            else try{await AddonUpdate();}catch(Exception e){MarketSync.Log("Automatic addon update: "+e.Message);}
            var manager=new UpdateManager(new SignedUpdateSource());
            if(manager.IsInstalled){var update=await manager.CheckForUpdatesAsync();if(update!=null){
                var target=Version.Parse(update.TargetFullRelease.Version.ToString());
                if(!updateAttempt.CanApply(target,manual)){
                    UpdateMessage(updateAttempt.PendingMessage(typeof(Program).Assembly.GetName().Version!));return;
                }
                await manager.DownloadUpdatesAsync(update);
                sync.Save();updateAttempt.Begin(target);
                MarketSync.Log("Applying desktop update "+target+"; automatic retries of this version are disabled until a successful restart or explicit retry.");
                timer.Stop();tray.Visible=false;
                manager.ApplyUpdatesAndRestart(update);return;
            }
            if(manual)UpdateMessage("App is up to date ("+typeof(Program).Assembly.GetName().Version?.ToString(3)+").");
            }else if(manual)UpdateMessage("Automatic app updates become available after installation.");
        }catch(Exception e){MarketSync.Log("Update check: "+e.Message);if(manual)UpdateMessage("Updates unavailable: "+e.Message);}
        finally{checkingUpdates=false;timer.Start();tray.Visible=true;Render();}
    }
    async Task UpdateAddonNow()
    {
        if(checkingUpdates||sync.IsBusy||!sync.Config.NoticeAccepted)return;
        checkingUpdates=true;Render();
        try{UpdateMessage("Downloading the approved addon… Wait for Ready before using /reload.");UpdateMessage(await AddonUpdate(true));}
        catch(Exception e){MarketSync.Log("Addon update: "+e.Message);UpdateMessage("Addon update: "+e.Message);}
        finally{checkingUpdates=false;UpdateButton.IsEnabled=true;SettingsUpdateButton.IsEnabled=true;Render();}
    }
    async Task<string> AddonUpdate(bool force=false)
    {
        string clientRoot=sync.Config.ClientRoot;
        if(string.IsNullOrEmpty(clientRoot))return "Choose your WoW client first.";
        if(!force&&sync.GameRunning()){nextUpdate=DateTime.UtcNow.AddMinutes(1);return "WoW is running. Use Update addon now for a compatible /reload update, or exit WoW for automatic installation.";}
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(2)};
        using var response=await http.GetAsync(MarketSync.Service+"/releases/addon/latest.json");
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return "No newer approved addon release is published. "+AddonVersion();
        response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var manifest=MarketSync.Verify(doc.RootElement,Path.Combine(AppContext.BaseDirectory,"Assets","release-public.pem"));
        if(manifest.GetProperty("schema").GetString()!="foreverflip.addon-release"||!Version.TryParse(manifest.GetProperty("version").GetString(),out var remoteVersion))throw new InvalidDataException("Invalid addon release manifest.");
        if(ClientInstall.VersionAt(Path.Combine(clientRoot,"Interface","AddOns","ForeverFlip")) is Version localVersion&&(remoteVersion<localVersion||!force&&remoteVersion==localVersion))return "Up to date. "+AddonVersion();
        string relative=manifest.GetProperty("path").GetString()!;if(!System.Text.RegularExpressions.Regex.IsMatch(relative,@"^/downloads/ForeverFlip-[0-9.]+\.zip$"))throw new InvalidDataException("Invalid release path.");
        using var archive=await http.GetAsync(MarketSync.Service+relative,HttpCompletionOption.ResponseHeadersRead);archive.EnsureSuccessStatusCode();
        if(archive.Content.Headers.ContentLength>10*1024*1024)throw new InvalidDataException("Addon archive too large.");
        using var input=await archive.Content.ReadAsStreamAsync();using var buffer=new MemoryStream();byte[] chunk=new byte[65536];int read;
        while((read=await input.ReadAsync(chunk))>0){if(buffer.Length+read>10*1024*1024)throw new InvalidDataException("Addon archive too large.");await buffer.WriteAsync(chunk.AsMemory(0,read));}
        byte[] bytes=buffer.ToArray();if(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()!=manifest.GetProperty("sha256").GetString())throw new InvalidDataException("Addon checksum mismatch.");
        if(!string.Equals(sync.Config.ClientRoot,clientRoot,StringComparison.OrdinalIgnoreCase))throw new IOException("Selected game installation changed during download. Try Update addon now again.");
        ClientInstall.InstallArchive(clientRoot,bytes,remoteVersion,force);
        sync.Config.PendingAddonInstall=false;sync.Config.PendingAddonRepair=false;sync.Save();Render();
        return "Ready: addon "+remoteVersion+" installed. Use /reload after finishing any scan, purchase or posting. Your database is preserved; no game input was sent.";
    }
}
