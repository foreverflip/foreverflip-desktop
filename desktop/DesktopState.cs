using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ForeverFlip.Desktop;

public sealed class DesktopStateMigrationException(string message,Exception? inner=null):Exception(message,inner);

// App state lives outside the Velopack install root, which Setup may rename or
// replace. Migration copies only known state files from the old install-root
// location, never overwrites, never follows links and never deletes the source.
public static class DesktopState
{
    static readonly string Local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string LegacyRoot=Path.Combine(Local,"ForeverFlip");
    public static readonly string DefaultRoot=Path.Combine(Local,"ForeverFlipData");
    internal const string Marker="state-migration.json";
    internal const string InstallPointer="install-preservation.json";
    internal const string UpdateReceipt="update-preservation.json";
    static string RecoveryRoot(string dataRoot)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot))+".install-recovery";
    internal const long MaxFileBytes=32L*1024*1024;
    internal const int MaxPacks=512,MaxSupportQueues=64;
    const string TempTag=".ff-migrate-";
    static readonly string[] RootFiles=["settings.json","account.json","desktop-update-attempt.json","desktop.log","desktop.log.previous"];
    static readonly Regex PackFile=new(@"^ff1-[0-9a-f]{32}\.json$"),SupportQueue=new(@"^[0-9a-f]{24}$");

    public sealed record Result(int Copied,int Preserved,bool AlreadyComplete);
    enum Outcome{Missing,Copied,Preserved}

    public static Result MigrateDefault()=>MigrateInstall(LegacyRoot,DefaultRoot);

    // In-app updates retain install-root state, even for an unconfigured app.
    // This tiny receipt is not a migration-completed marker.
    public static void RecordUpdate()=>RecordUpdate(DefaultRoot);
    internal static void RecordUpdate(string dataRoot)
    {
        Folder(dataRoot,create:true);
        string receipt=Path.Combine(dataRoot,UpdateReceipt);if(Present(receipt))Regular(receipt);
        WriteDurable(receipt,"{\"schemaVersion\":1}",replace:true);
    }

    // Called by the OUTER Setup, before Velopack can rename/delete anything.
    // Snapshot separately: cancelling Setup must not mark the old app migrated.
    internal static void PreserveForInstall(string installRoot,string dataRoot)
    {
        Folder(dataRoot,create:true);
        if(Completed(dataRoot))return;
        string recovery=RecoveryRoot(dataRoot);Folder(recovery,create:true);
        string id=Guid.NewGuid().ToString("N"),snapshot=Path.Combine(recovery,id);
        Migrate(InstallSource(installRoot),snapshot);
        string receipt=Path.Combine(dataRoot,UpdateReceipt);
        if(Present(receipt)){Regular(receipt);File.Delete(receipt);}
        string pointer=Path.Combine(dataRoot,InstallPointer);
        if(Present(pointer))Regular(pointer);
        WriteDurable(pointer,JsonSerializer.Serialize(new{schemaVersion=1,snapshot=id}),replace:true);
    }

    // Normal startup has no fast-hook deadline. Prefer the pre-Setup snapshot;
    // an older interrupted install can still recover its renamed directory.
    public static Result MigrateInstall()=>MigrateInstall(LegacyRoot,DefaultRoot);
    internal static Result MigrateInstall(string installRoot,string dataRoot)
    {
        Folder(dataRoot,create:true);
        if(Completed(dataRoot))return Migrate(installRoot,dataRoot);
        string update=Path.Combine(dataRoot,UpdateReceipt);
        bool retainedUpdate=false;
        if(Present(update)){
            Regular(update);
            try{
                if(new FileInfo(update).Length>4096)throw new DesktopStateMigrationException("The update recovery record is invalid.");
                using var receipt=JsonDocument.Parse(File.ReadAllText(update));
                retainedUpdate=receipt.RootElement.GetProperty("schemaVersion").GetInt32()==1;
                if(!retainedUpdate)throw new DesktopStateMigrationException("The update recovery record is invalid.");
            }catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException){
                throw new DesktopStateMigrationException("The update recovery record could not be read.",e);
            }
        }
        // A cancelled Setup leaves the original state intact. Prefer later edits
        // and sign-out there over the cancelled install's older snapshot.
        if(RootFiles.Any(f=>Present(Path.Combine(installRoot,f))))
            return Migrate(InstallSource(installRoot),dataRoot);
        string pointer=Path.Combine(dataRoot,InstallPointer);
        if(Present(pointer)){
            Regular(pointer);
            if(new FileInfo(pointer).Length>4096)throw new DesktopStateMigrationException("The saved install recovery record is invalid.");
            try{
                using var record=JsonDocument.Parse(File.ReadAllText(pointer));
                string id=record.RootElement.GetProperty("snapshot").GetString()??"";
                if(record.RootElement.GetProperty("schemaVersion").GetInt32()!=1||!Regex.IsMatch(id,@"^[a-f0-9]{32}$"))
                    throw new DesktopStateMigrationException("The saved install recovery record is invalid.");
                string recovery=RecoveryRoot(dataRoot);Folder(recovery,create:false);
                string snapshot=Path.Combine(recovery,id);Folder(snapshot,create:false);
                if(!Completed(snapshot))throw new DesktopStateMigrationException("The saved install recovery snapshot is incomplete.");
                return Migrate(snapshot,dataRoot);
            }catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException){
                throw new DesktopStateMigrationException("The saved install recovery record could not be read. Keep the recovery folder and contact support.",e);
            }
        }
        string source=InstallSource(installRoot);
        // Do not record an empty successful migration after a failed old hook.
        // A supported fresh Setup also writes an explicit (possibly empty) snapshot.
        if(!retainedUpdate&&source==installRoot&&Present(Path.Combine(installRoot,"current","sq.version"))&&!RootFiles.Any(f=>Present(Path.Combine(source,f))))
            throw new DesktopStateMigrationException("Install recovery is missing. Run the protected ForeverFlip Setup again; keep any previous install folders.");
        return Migrate(source,dataRoot);
    }

    static string InstallSource(string installRoot)
    {
        string parent=Path.GetDirectoryName(Path.GetFullPath(installRoot))!;
        if(!Directory.Exists(parent))return installRoot;
        string name=Path.GetFileName(installRoot);
        var candidates=new List<string>();
        var siblings=Directory.EnumerateDirectories(parent,name+".*").Take(65).ToArray();
        if(siblings.Length>64)throw new DesktopStateMigrationException("Too many previous install folders; recovery needs review.");
        foreach(string folder in siblings){
            if(!Regex.IsMatch(Path.GetFileName(folder),"^"+Regex.Escape(name)+@"\.[A-Za-z0-9]{12,32}$"))continue;
            Folder(folder,create:false);
            string manifest=Path.Combine(folder,"current","sq.version");
            if(!Present(manifest)||!RootFiles.Any(f=>Present(Path.Combine(folder,f))))continue;
            Folder(Path.Combine(folder,"current"),create:false);Regular(manifest);
            using var reader=XmlReader.Create(manifest,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});
            var document=XDocument.Load(reader);
            string? Value(string element)=>document.Descendants().FirstOrDefault(e=>e.Name.LocalName==element)?.Value;
            if(Value("id")=="ForeverFlip"&&Value("mainExe")=="ForeverFlip.exe")candidates.Add(folder);
        }
        if(candidates.Count>1)throw new DesktopStateMigrationException("More than one previous install contains saved state; recovery needs review.");
        if(candidates.Count==1&&RootFiles.Any(f=>Present(Path.Combine(installRoot,f))))
            throw new DesktopStateMigrationException("Both the current and previous install contain saved state; recovery needs review.");
        return candidates.Count==1?candidates[0]:installRoot;
    }

    // A completion marker stops later runs from resurrecting state the user
    // has since removed (for example account.json after sign-out). Until it is
    // written, a restart resumes and skips files already present.
    internal static Result Migrate(string legacyRoot,string newRoot,int attempts=5,TimeSpan? retryDelay=null)
    {
        legacyRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));newRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot));
        if(Within(legacyRoot,newRoot)||Within(newRoot,legacyRoot))throw new DesktopStateMigrationException("ForeverFlip data folder overlaps the install folder.");
        var delay=retryDelay??TimeSpan.FromMilliseconds(250);
        try{
            Folder(newRoot,create:true);
            string marker=Path.Combine(newRoot,Marker);
            if(Completed(newRoot))return new(0,0,true);
            int copied=0,preserved=0;
            void Take(string source,string dest){
                switch(Retry(()=>CopyOnce(source,dest),attempts,delay)){case Outcome.Copied:copied++;break;case Outcome.Preserved:preserved++;break;}
            }
            if(Present(legacyRoot)){
                Folder(legacyRoot,create:false);
                foreach(string name in RootFiles)Take(Path.Combine(legacyRoot,name),Path.Combine(newRoot,name));
                string packs=Path.Combine(legacyRoot,"packs");
                if(Present(packs)){
                    Folder(packs,create:false);
                    var names=Directory.EnumerateFiles(packs).Select(p=>Path.GetFileName(p)).Where(n=>PackFile.IsMatch(n)).Take(MaxPacks+1).ToList();
                    if(names.Count>MaxPacks)throw new DesktopStateMigrationException("Too many cached AI packs to migrate safely.");
                    if(names.Count>0)Folder(Path.Combine(newRoot,"packs"),create:true);
                    foreach(string name in names)Take(Path.Combine(packs,name),Path.Combine(newRoot,"packs",name));
                }
                string support=Path.Combine(legacyRoot,"support");
                if(Present(support)){
                    Folder(support,create:false);
                    var keys=Directory.EnumerateDirectories(support).Select(p=>Path.GetFileName(p)).Where(n=>SupportQueue.IsMatch(n)).Take(MaxSupportQueues+1).ToList();
                    if(keys.Count>MaxSupportQueues)throw new DesktopStateMigrationException("Too many support queues to migrate safely.");
                    foreach(string key in keys){
                        string from=Path.Combine(support,key);Folder(from,create:false);
                        if(!Present(Path.Combine(from,"state.json")))continue;
                        Folder(Path.Combine(newRoot,"support"),create:true);Folder(Path.Combine(newRoot,"support",key),create:true);
                        Take(Path.Combine(from,"state.json"),Path.Combine(newRoot,"support",key,"state.json"));
                        var reports=Directory.EnumerateFiles(from).Where(p=>Regex.IsMatch(Path.GetFileName(p),@"^[a-f0-9]{64}\.report\.json$")).Take(4).ToArray();
                        if(reports.Length>3)throw new DesktopStateMigrationException("Too many queued support reports to migrate safely.");
                        foreach(string report in reports)Take(report,Path.Combine(newRoot,"support",key,Path.GetFileName(report)));
                    }
                }
            }
            WriteDurable(marker,JsonSerializer.Serialize(new{schemaVersion=1,from=legacyRoot,copied,preserved,completedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()}));
            return new(copied,preserved,false);
        }catch(DesktopStateMigrationException){throw;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){
            throw new DesktopStateMigrationException("ForeverFlip could not move its settings to "+newRoot+". Close other ForeverFlip windows and retry; nothing was deleted.",e);
        }
    }

    static Outcome Retry(Func<Outcome> copy,int attempts,TimeSpan delay)
    {
        for(int attempt=1;;attempt++){
            try{return copy();}
            catch(IOException) when(attempt<attempts){Thread.Sleep(delay);}
        }
    }

    static Outcome CopyOnce(string source,string dest)
    {
        if(!Present(source))return Outcome.Missing;
        Regular(source);
        if(Present(dest)){Regular(dest);return Outcome.Preserved;}
        if(new FileInfo(source).Length>MaxFileBytes)throw new DesktopStateMigrationException("A saved state file is too large to migrate: "+Path.GetFileName(source));
        string temp=dest+TempTag+Guid.NewGuid().ToString("N")+".tmp";
        try{
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,81920,FileOptions.SequentialScan))
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                if(input.Length>MaxFileBytes)throw new DesktopStateMigrationException("A saved state file is too large to migrate: "+Path.GetFileName(source));
                input.CopyTo(output);output.Flush(true);
            }
            try{File.Move(temp,dest,false);}
            catch(IOException) when(Present(dest)){return Outcome.Preserved;}
            return Outcome.Copied;
        }finally{
            try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    static void WriteDurable(string path,string text,bool replace=false)
    {
        string temp=path+TempTag+Guid.NewGuid().ToString("N")+".tmp";
        try{
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                byte[] bytes=new UTF8Encoding(false).GetBytes(text);output.Write(bytes);output.Flush(true);
            }
            File.Move(temp,path,replace);
        }finally{
            try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    // Rejects links/junctions and files posing as folders. Destination folders
    // also lose stale temporary files left by an interrupted earlier run.
    static void Folder(string path,bool create)
    {
        var info=new DirectoryInfo(path);
        if(Present(path)){
            if(!info.Exists||info.Attributes.HasFlag(FileAttributes.ReparsePoint)||info.LinkTarget!=null)throw new DesktopStateMigrationException("Refusing linked or unexpected state folder: "+path);
        }else if(create)Directory.CreateDirectory(path);
        else return;
        if(!create)return;
        foreach(string stale in Directory.EnumerateFiles(path,"*"+TempTag+"*.tmp")){
            var file=new FileInfo(stale);
            if(!file.Attributes.HasFlag(FileAttributes.ReparsePoint))file.Delete();
        }
    }

    static void Regular(string path)
    {
        var info=new FileInfo(path);
        if(!info.Exists||info.Attributes.HasFlag(FileAttributes.ReparsePoint)||info.Attributes.HasFlag(FileAttributes.Directory)||info.LinkTarget!=null)
            throw new DesktopStateMigrationException("Refusing linked or unexpected state file: "+path);
    }

    static bool Completed(string root)
    {
        string path=Path.Combine(root,Marker);if(!Present(path))return false;
        Regular(path);
        if(new FileInfo(path).Length>4096)throw new DesktopStateMigrationException("The migration record is invalid; keep the recovery folders.");
        try{
            using var record=JsonDocument.Parse(File.ReadAllText(path));var value=record.RootElement;
            if(value.GetProperty("schemaVersion").GetInt32()!=1||value.GetProperty("copied").GetInt32()<0||value.GetProperty("preserved").GetInt32()<0)
                throw new DesktopStateMigrationException("The migration record is invalid; keep the recovery folders.");
            return true;
        }catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){
            throw new DesktopStateMigrationException("The migration record could not be read; keep the recovery folders.",e);
        }
    }

    static bool Present(string path)=>File.Exists(path)||Directory.Exists(path)||new FileInfo(path).LinkTarget!=null;

    static bool Within(string parent,string child)=>string.Equals(parent,child,StringComparison.OrdinalIgnoreCase)
        ||child.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
}
