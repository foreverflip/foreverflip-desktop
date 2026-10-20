using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

public static class ClientInstall
{
    public static bool ValidRoot(string root)=>Directory.Exists(Path.Combine(root,"Interface"))&&Directory.Exists(Path.Combine(root,"WTF"))
        && Directory.EnumerateFiles(root,"*.exe").Any(p=>Path.GetFileName(p).StartsWith("Wow",StringComparison.OrdinalIgnoreCase));
    public static bool SupportedRoot(string root)
    {
        if(!ValidRoot(root))return false;
        return new DirectoryInfo(root).Name.Equals("_classic_beta_",StringComparison.OrdinalIgnoreCase)
            || Directory.EnumerateFiles(root,"Wow*.exe").Any(p=>FileVersionInfo.GetVersionInfo(p).FileVersion?.StartsWith("1.60.")==true);
    }
    public static string? ResolveRoot(string selected)
    {
        foreach(string suffix in new[]{"","_classic_beta_"})try{string root=Path.GetFullPath(Path.Combine(selected,suffix));if(SupportedRoot(root))return root;}catch{}
        return null;
    }
    public static bool GameRunning()=>Process.GetProcesses().Any(p=>{try{return p.ProcessName.StartsWith("Wow",StringComparison.OrdinalIgnoreCase);}catch{return false;}finally{p.Dispose();}});
    public static List<string> Discover()
    {
        var candidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path){if(!string.IsNullOrWhiteSpace(path))candidates.Add(path.Trim('"'));}
        foreach(var p in Process.GetProcesses())try{if(p.ProcessName.StartsWith("Wow",StringComparison.OrdinalIgnoreCase))Add(Path.GetDirectoryName(p.MainModule?.FileName));}catch{}finally{p.Dispose();}
        foreach(var hive in new[]{Microsoft.Win32.Registry.CurrentUser,Microsoft.Win32.Registry.LocalMachine}){
            foreach(string keyName in new[]{@"SOFTWARE\Blizzard Entertainment\World of Warcraft",@"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft"})
                try{using var key=hive.OpenSubKey(keyName);Add(key?.GetValue("InstallPath") as string);}catch{}
            foreach(string branch in new[]{@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"})
                try{using var uninstall=hive.OpenSubKey(branch);if(uninstall==null)continue;foreach(var name in uninstall.GetSubKeyNames()){
                    using var key=uninstall.OpenSubKey(name);string display=key?.GetValue("DisplayName") as string??"";
                    if(display.Contains("Warcraft",StringComparison.OrdinalIgnoreCase)||display.Contains("WoW Forever",StringComparison.OrdinalIgnoreCase))Add(key?.GetValue("InstallLocation") as string);
                }}catch{}
        }
        foreach(var drive in DriveInfo.GetDrives())try{if(drive.DriveType!=DriveType.Fixed||!drive.IsReady)continue;
            foreach(string basePath in new[]{"","Games","Program Files","Program Files (x86)","Battle.net"})
                foreach(string game in new[]{"World of Warcraft","WoW Forever","WoWForever"})Add(Path.Combine(drive.RootDirectory.FullName,basePath,game));
        }catch{}
        var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string candidate in candidates)foreach(string suffix in new[]{"","_classic_beta_"})try{
            string root=Path.GetFullPath(Path.Combine(candidate,suffix));
            if(SupportedRoot(root))result.Add(root);
        }catch{}
        return result.OrderBy(x=>x).ToList();
    }
    public static Version? VersionAt(string addon)
    {
        try{string line=File.ReadLines(Path.Combine(addon,"ForeverFlip.toc")).First(x=>x.StartsWith("## Version:"));return Version.Parse(line.Split(':',2)[1].Trim());}catch{return null;}
    }
    // Compatibility name retained; this desktop-only edition has no bundled addon.
    public static bool NeedsBundled(string root)=>VersionAt(Path.Combine(root,"Interface","AddOns","ForeverFlip"))==null
        ||!File.Exists(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data","ForeverFlip_Data.toc"))
        ||!File.Exists(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data","Data.lua"));
    internal static void CheckInstallPaths(string root)
    {
        string addons=Path.Combine(root,"Interface","AddOns"),helper=Path.Combine(addons,"ForeverFlip_Data");
        foreach(string path in new[]{addons,Path.Combine(addons,"ForeverFlip"),helper,Path.Combine(helper,"Data.lua"),Path.Combine(helper,"ForeverFlip_Data.toc")})
            AddonBackups.CheckPath(root,path);
    }
    static void CheckManifestFiles(string folder)
    {
        foreach(string line in File.ReadAllLines(Path.Combine(folder,"ForeverFlip.toc"))){
            string file=line.Trim();if(file.Length==0||file.StartsWith('#'))continue;
            if(!Regex.IsMatch(file,@"^[A-Za-z][A-Za-z0-9_.-]+$")||!File.Exists(Path.Combine(folder,file)))
                throw new InvalidDataException("Addon archive is missing a declared load file or has an unsafe manifest.");
        }
    }
    public static bool EnsureBundled(string clientRoot,bool repair=false)
        =>SignedAddonDownload.EnsureAsync(clientRoot,repair).GetAwaiter().GetResult();
    public static bool ReloadCompatible(string stage,string target,string helper)
    {
        if(!Directory.Exists(target)||!File.Exists(Path.Combine(helper,"ForeverFlip_Data.toc"))||!File.Exists(Path.Combine(helper,"Data.lua")))return false;
        string Manifest(string folder)=>string.Join("\n",File.ReadAllLines(Path.Combine(folder,"ForeverFlip.toc")).Where(l=>!l.StartsWith("## Version:",StringComparison.OrdinalIgnoreCase)).Select(l=>l.TrimEnd()));
        try{
            if(Manifest(stage)!=Manifest(target))return false;
            foreach(string line in File.ReadAllLines(Path.Combine(stage,"ForeverFlip.toc"))){
                string file=line.Trim();if(file==""||file.StartsWith('#'))continue;
                if(!Regex.IsMatch(file,@"^[A-Za-z][A-Za-z0-9_.-]+$")||!File.Exists(Path.Combine(stage,file)))return false;
            }
            string bindings="Bindings.xml";
            return File.Exists(Path.Combine(stage,bindings))==File.Exists(Path.Combine(target,bindings))
                &&(!File.Exists(Path.Combine(stage,bindings))||File.ReadAllText(Path.Combine(stage,bindings))==File.ReadAllText(Path.Combine(target,bindings)));
        }catch{return false;}
    }
    public static void InstallArchive(string clientRoot,byte[] bytes,Version expected,bool allowWhileRunning=false)
        =>InstallArchiveCore(clientRoot,bytes,expected,allowWhileRunning,GameRunning);
    internal static void InstallArchiveCore(string clientRoot,byte[] bytes,Version expected,bool allowWhileRunning,Func<bool> gameRunning,Action<string,string>? moveDirectory=null)
    {
        if(!SupportedRoot(clientRoot))throw new InvalidDataException("Unsupported WoW client.");
        CheckInstallPaths(clientRoot);
        using var installLock=AddonBackups.Acquire(clientRoot);
        string addons=Path.GetFullPath(Path.Combine(clientRoot,"Interface","AddOns"));Directory.CreateDirectory(addons);
        string stage=Path.Combine(addons,".foreverflip-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        string target=Path.Combine(addons,"ForeverFlip"),backup=AddonBackups.NewPath(clientRoot);
        try{
            using var zip=new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
            if(zip.Entries.Count>100)throw new InvalidDataException("Unexpected addon contents.");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
            foreach(var entry in zip.Entries){
                if(entry.FullName=="ForeverFlip/")continue;
                string[] parts=entry.FullName.Split('/');
                if(parts.Length!=2||parts[0]!="ForeverFlip"||!Regex.IsMatch(parts[1],@"^[A-Za-z][A-Za-z0-9_.-]+$")||!names.Add(parts[1])||entry.Length>2*1024*1024||(total+=entry.Length)>10*1024*1024)
                    throw new InvalidDataException("Unsafe addon archive contents.");
                using var input=entry.Open();using var output=File.Create(Path.Combine(stage,parts[1]));input.CopyTo(output);
            }
            if(VersionAt(stage)!=expected)throw new InvalidDataException("Archive version does not match the signed release.");
            CheckManifestFiles(stage);
            bool running=gameRunning();
            if(running&&(!allowWhileRunning||!ReloadCompatible(stage,target,Path.Combine(addons,"ForeverFlip_Data"))))
                throw new IOException("This update needs WoW closed: initial installation, addon load files or dependencies changed. Exit WoW and try Update addon now again.");
            if(VersionAt(target) is Version installed&&installed>expected)throw new InvalidDataException("Refusing to downgrade a newer installed addon.");
            AddonBackups.CheckPath(clientRoot,target);AddonBackups.CheckPath(clientRoot,stage);AddonBackups.CheckPath(clientRoot,backup);
            var move=moveDirectory??Directory.Move;
            if(Directory.Exists(target))move(target,backup);
            try{move(stage,target);}catch{if(Directory.Exists(backup)&&!Directory.Exists(target))Directory.Move(backup,target);throw;}
            if(!running)EnsureHelper(clientRoot);
            if(VersionAt(target)!=expected)throw new IOException("Installed addon version failed verification; rollback retained.");
            AddonBackups.AfterInstall(clientRoot,backup);
        }finally{
            if(!Path.GetFullPath(stage).StartsWith(addons+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid stage path.");
            if(Directory.Exists(stage))Directory.Delete(stage,true);
        }
    }
    static void EnsureHelper(string root)
    {
        CheckInstallPaths(root);
        string helper=Path.Combine(root,"Interface","AddOns","ForeverFlip_Data");Directory.CreateDirectory(helper);
        string data=Path.Combine(helper,"Data.lua");if(!File.Exists(data))MarketSync.Atomic(data,"ForeverFlipShared={schema=1,markets={}}\n");
        MarketSync.Atomic(Path.Combine(helper,"ForeverFlip_Data.toc"),"## Interface: 16001\n## Title: Forever Flip Data\n## Version: 0.1.0\nData.lua\n");
    }
}
