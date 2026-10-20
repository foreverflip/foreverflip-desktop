using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

// Only flat, validated addon-code copies are owned here. No WTF or helper data.
public static class AddonBackups
{
    const int KeepVersions=3;
    static readonly Regex LegacyName=new(@"^\.foreverflip-backup-(?:local-)?[a-f0-9]{32}$",RegexOptions.CultureInvariant);
    static readonly Regex StoredName=new(@"^addon-(\d{19})-[a-f0-9]{32}$",RegexOptions.CultureInvariant);
    static readonly Regex CodeName=new(@"^[A-Za-z][A-Za-z0-9_.-]+\.(?:lua|xml|toc|txt|tga)$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
    public sealed record Result(int Migrated,int Removed,int Kept,int Skipped,long ReclaimedBytes);
    sealed record Copy(string Path,Version Version,long Ticks,Dictionary<string,string> Files,long Bytes);

    public static string DirectoryAt(string clientRoot)=>Path.Combine(Path.GetFullPath(clientRoot),".foreverflip","addon-backups");

    // Walk every ancestor inside the selected client before any move/delete.
    // In particular, never traverse a junction pretending to be a backup.
    internal static void CheckPath(string clientRoot,string path)
    {
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientRoot)),full=Path.GetFullPath(path);
        if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Backup path escaped the selected game folder.");
        for(string? at=full;at!=null;at=Path.GetDirectoryName(at)){
            if(File.Exists(at)||Directory.Exists(at))if((File.GetAttributes(at)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Backup paths cannot contain links or junctions.");
            if(string.Equals(at,root,StringComparison.OrdinalIgnoreCase))return;
        }
        throw new InvalidDataException("Backup path has no verified client root.");
    }
    internal static FileStream Acquire(string clientRoot)
    {
        string folder=DirectoryAt(clientRoot);CheckPath(clientRoot,folder);Directory.CreateDirectory(folder);CheckPath(clientRoot,folder);
        string path=Path.Combine(folder,"maintenance.lock");CheckPath(clientRoot,path);
        return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    internal static string NewPath(string clientRoot,long? ticks=null)
    {
        string folder=DirectoryAt(clientRoot);CheckPath(clientRoot,folder);
        return Path.Combine(folder,"addon-"+(ticks??DateTime.UtcNow.Ticks).ToString("D19",System.Globalization.CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N"));
    }
    static Copy? Inspect(string root,string path,bool legacy)
    {
        CheckPath(root,path);
        var match=StoredName.Match(Path.GetFileName(path));
        if(legacy?!LegacyName.IsMatch(Path.GetFileName(path)):!match.Success)return null;
        string parent=Path.GetFullPath(legacy?Path.Combine(root,"Interface","AddOns"):DirectoryAt(root));
        if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),parent,StringComparison.OrdinalIgnoreCase))return null;
        var entries=Directory.GetFileSystemEntries(path);
        if(entries.Length<1||entries.Length>100)return null;
        string toc=Path.Combine(path,"ForeverFlip.toc");CheckPath(root,toc);
        if(!File.Exists(toc)||new FileInfo(toc).Length>65536)return null;
        var owned=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"ForeverFlip.toc","Addon.lua","Bindings.xml","LibProfessionDB-LICENSE.txt","AdviceData.lua","BrandIcon.tga","SettingsGear.tga"};
        foreach(string line in File.ReadAllLines(toc)){
            string file=line.Trim();if(file.Length==0||file.StartsWith('#'))continue;
            if(!CodeName.IsMatch(file))return null;owned.Add(file);
        }
        var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(string entry in entries){
            CheckPath(root,entry);
            if(Directory.Exists(entry)||!CodeName.IsMatch(Path.GetFileName(entry))||!owned.Contains(Path.GetFileName(entry)))return null;
            var file=new FileInfo(entry);if(file.Length>2*1024*1024||(total+=file.Length)>10*1024*1024)return null;
            using var stream=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read);
            hashes.Add(Path.GetFileName(entry),Convert.ToHexString(SHA256.HashData(stream)));
        }
        var version=ClientInstall.VersionAt(path);if(version==null)return null;
        long ticks;
        if(legacy)ticks=Directory.GetCreationTimeUtc(path).Ticks;
        else if(!long.TryParse(match.Groups[1].Value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out ticks))return null;
        if(ticks<DateTime.MinValue.Ticks||ticks>DateTime.MaxValue.Ticks)return null;
        return new Copy(path,version,ticks,hashes,total);
    }
    static bool Same(Copy a,Copy b)=>a.Version==b.Version&&a.Files.Count==b.Files.Count&&a.Files.All(x=>b.Files.TryGetValue(x.Key,out string? hash)&&hash==x.Value);

    // Caller holds the per-client lock and has verified the installed target.
    // Errors preserve extra copies; cleanup must not invalidate a good install.
    internal static Result MaintainLocked(string root,string? protectedBackup=null)
    {
        string addons=Path.Combine(root,"Interface","AddOns"),store=DirectoryAt(root);
        CheckPath(root,addons);CheckPath(root,store);
        int migrated=0,removed=0,skipped=0;long bytes=0;
        foreach(string path in Directory.GetDirectories(addons,".foreverflip-backup-*")){
            try{
                var before=Inspect(root,path,true);if(before==null){skipped++;continue;}
                string destination=NewPath(root,before.Ticks);CheckPath(root,destination);
                Directory.Move(path,destination);
                var after=Inspect(root,destination,false);
                if(after==null||!Same(before,after)){
                    if(!Directory.Exists(path))Directory.Move(destination,path);
                    throw new IOException("Migrated addon backup did not match its original files.");
                }
                migrated++;
            }catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException){skipped++;Trace.TraceWarning("Addon backup migration skipped: "+e.Message);}
        }
        var copies=new List<Copy>();
        foreach(string path in Directory.GetDirectories(store,"addon-*")){
            try{var copy=Inspect(root,path,false);if(copy!=null)copies.Add(copy);else skipped++;}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException){skipped++;Trace.TraceWarning("Addon backup inspection skipped: "+e.Message);}
        }
        // Keep the immediate rollback even after a same-version repair. One
        // newest copy per version, at most three distinct rollback versions.
        var retained=copies.OrderByDescending(x=>string.Equals(x.Path,protectedBackup,StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x=>x.Ticks).ThenBy(x=>x.Path,StringComparer.OrdinalIgnoreCase)
            .GroupBy(x=>x.Version).Take(KeepVersions).Select(g=>g.First()).Select(x=>x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var copy in copies.Where(x=>!retained.Contains(x.Path))){
            try{
                var current=Inspect(root,copy.Path,false);if(current==null||!Same(copy,current)){skipped++;continue;}
                // All files are flat and were revalidated. No recursive delete,
                // wildcard deletion or traversal of unknown contents is used.
                foreach(string name in current.Files.Keys){string file=Path.Combine(current.Path,name);CheckPath(root,file);File.Delete(file);}
                CheckPath(root,current.Path);Directory.Delete(current.Path);removed++;bytes+=copy.Bytes;
            }catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException){skipped++;Trace.TraceWarning("Addon backup cleanup skipped: "+e.Message);}
        }
        return new Result(migrated,removed,copies.Count-removed,skipped,bytes);
    }
    public static Result Maintain(string clientRoot)
    {
        if(!ClientInstall.SupportedRoot(clientRoot))throw new InvalidDataException("Select the supported WoW Forever client.");
        using var guard=Acquire(clientRoot);
        // A missing/broken installation may need every recovery copy. It is
        // never a reason to prune or migrate the user's rollback folders.
        string target=Path.Combine(clientRoot,"Interface","AddOns","ForeverFlip");
        CheckPath(clientRoot,target);
        if(ClientInstall.VersionAt(target)==null||!ClientInstall.ReloadCompatible(target,target,Path.Combine(clientRoot,"Interface","AddOns","ForeverFlip_Data")))
            throw new IOException("A verified installed addon is required before backup cleanup.");
        return MaintainLocked(clientRoot);
    }
    internal static void AfterInstall(string root,string? previous)
    {
        try{MaintainLocked(root,previous);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException){Trace.TraceWarning("Addon installed; backup cleanup deferred: "+e.Message);}
    }
}
