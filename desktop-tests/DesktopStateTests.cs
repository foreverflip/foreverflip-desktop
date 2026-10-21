using ForeverFlip.Desktop;
using System.Diagnostics;
using System.IO;

// Synthetic folders only; never reads the real %LocalAppData% state.
internal static class DesktopStateTests
{
    const string Pack="ff1-0123456789abcdef0123456789abcdef.json",Queue="0123456789abcdef01234567";
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Write(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);}
    static string Read(string path)=>File.ReadAllText(path);
    static void Throws(Action act,string message){try{act();}catch(DesktopStateMigrationException){return;}throw new Exception(message);}
    static bool Temps(string root)=>Directory.EnumerateFiles(root,"*.ff-migrate-*.tmp",SearchOption.AllDirectories).Any();
    static void Junction(string link,string target)
    {
        using var p=Process.Start(new ProcessStartInfo("cmd.exe"){ArgumentList={"/d","/c","mklink","/J",link,target},UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
        p.WaitForExit();Check(p.ExitCode==0&&Directory.Exists(link),"Test junction could not be created.");
    }
    static string Legacy(string folder)
    {
        string root=Path.Combine(folder,"ForeverFlip");
        Write(Path.Combine(root,"settings.json"),"{\"SavedFile\":\"synthetic\"}");
        Write(Path.Combine(root,"account.json"),"{\"schemaVersion\":1,\"protected\":\"U1lOVEhFVElD\"}");
        Write(Path.Combine(root,"desktop-update-attempt.json"),"{\"version\":\"0.1.45\"}");
        Write(Path.Combine(root,"desktop.log"),"log");Write(Path.Combine(root,"desktop.log.previous"),"older log");
        Write(Path.Combine(root,"packs",Pack),"{\"pack\":1}");
        Write(Path.Combine(root,"packs","not-a-pack.json"),"x");Write(Path.Combine(root,"packs","nested",Pack),"x");
        Write(Path.Combine(root,"support",Queue,"state.json"),"{\"Failures\":1}");
        Write(Path.Combine(root,"support",Queue,"report.txt"),"x");Write(Path.Combine(root,"support","other","state.json"),"x");
        // Install-root contents that must never move.
        Write(Path.Combine(root,"current","ForeverFlip.exe"),"binary");Write(Path.Combine(root,"packages","ForeverFlip-full.nupkg"),"package");
        Write(Path.Combine(root,"Update.exe"),"binary");Write(Path.Combine(root,"inspection.json"),"x");
        return root;
    }
    public static void Run()
    {
        Check(DesktopState.DefaultRoot.EndsWith("ForeverFlipData",StringComparison.OrdinalIgnoreCase)
            &&!string.Equals(DesktopState.DefaultRoot,DesktopState.LegacyRoot,StringComparison.OrdinalIgnoreCase),"App data must leave the install root.");
        string folder=Path.GetFullPath(Path.Combine(TestPaths.Root,"foreverflip-state-test-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        try{
            var fast=TimeSpan.FromMilliseconds(20);

            // Known state moves byte-for-byte; binaries and unknown files stay behind.
            string legacy=Legacy(Path.Combine(folder,"a")),data=Path.Combine(folder,"a","ForeverFlipData");
            var first=DesktopState.Migrate(legacy,data,retryDelay:fast);
            Check(first.Copied==7&&first.Preserved==0&&!first.AlreadyComplete,"Known state files were not all migrated.");
            foreach(string name in new[]{"settings.json","account.json","desktop-update-attempt.json","desktop.log","desktop.log.previous",Path.Combine("packs",Pack),Path.Combine("support",Queue,"state.json")})
                Check(Read(Path.Combine(data,name))==Read(Path.Combine(legacy,name)),"Migrated content differs: "+name);
            foreach(string name in new[]{"current","packages","Update.exe","inspection.json",Path.Combine("packs","not-a-pack.json"),Path.Combine("packs","nested"),Path.Combine("support",Queue,"report.txt"),Path.Combine("support","other")})
                Check(!File.Exists(Path.Combine(data,name))&&!Directory.Exists(Path.Combine(data,name)),"Unexpected file migrated: "+name);
            Check(File.Exists(Path.Combine(legacy,"settings.json"))&&File.Exists(Path.Combine(legacy,"current","ForeverFlip.exe")),"Legacy state was removed.");
            Check(File.Exists(Path.Combine(data,DesktopState.Marker))&&!Temps(data),"Migration marker missing or temporary files left.");

            // After completion, later sign-out/edits are never undone.
            File.Delete(Path.Combine(data,"account.json"));Write(Path.Combine(data,"settings.json"),"{\"changed\":true}");
            var again=DesktopState.Migrate(legacy,data,retryDelay:fast);
            Check(again.AlreadyComplete&&!File.Exists(Path.Combine(data,"account.json"))&&Read(Path.Combine(data,"settings.json"))=="{\"changed\":true}","Completed migration resurrected or overwrote state.");

            // Existing destination wins; an interrupted run resumes and cleans its temp files.
            legacy=Legacy(Path.Combine(folder,"b"));data=Path.Combine(folder,"b","ForeverFlipData");
            Write(Path.Combine(data,"settings.json"),"{\"restored\":true}");Write(Path.Combine(data,"packs",Pack),"{\"newer\":1}");
            Write(Path.Combine(data,"account.json.ff-migrate-"+Guid.NewGuid().ToString("N")+".tmp"),"partial");
            var resumed=DesktopState.Migrate(legacy,data,retryDelay:fast);
            Check(resumed.Copied==5&&resumed.Preserved==2,"Existing destination files were not preserved.");
            Check(Read(Path.Combine(data,"settings.json"))=="{\"restored\":true}"&&Read(Path.Combine(data,"packs",Pack))=="{\"newer\":1}","Existing destination was overwritten.");
            Check(Read(Path.Combine(data,"account.json"))==Read(Path.Combine(legacy,"account.json"))&&!Temps(data),"Interrupted migration did not resume cleanly.");

            // A briefly locked file is retried; a persistent lock fails visibly without a marker.
            legacy=Legacy(Path.Combine(folder,"c"));data=Path.Combine(folder,"c","ForeverFlipData");
            var held=new FileStream(Path.Combine(legacy,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.None);
            var release=Task.Run(async()=>{await Task.Delay(150);held.Dispose();});
            var retried=DesktopState.Migrate(legacy,data,attempts:40,retryDelay:fast);release.Wait();
            Check(retried.Copied==7&&Read(Path.Combine(data,"settings.json"))==Read(Path.Combine(legacy,"settings.json")),"Locked file was not retried.");
            legacy=Legacy(Path.Combine(folder,"d"));data=Path.Combine(folder,"d","ForeverFlipData");
            using(new FileStream(Path.Combine(legacy,"account.json"),FileMode.Open,FileAccess.Read,FileShare.None))
                Throws(()=>DesktopState.Migrate(legacy,data,attempts:2,retryDelay:fast),"Persistent lock did not surface a failure.");
            Check(!File.Exists(Path.Combine(data,DesktopState.Marker))&&!File.Exists(Path.Combine(data,"account.json"))&&!Temps(data),"Failed migration looked complete.");
            Check(DesktopState.Migrate(legacy,data,retryDelay:fast).Copied==6,"Migration did not recover after the lock cleared.");

            // Junctions are rejected on either side, before any marker is written.
            legacy=Legacy(Path.Combine(folder,"e"));data=Path.Combine(folder,"e","ForeverFlipData");
            string elsewhere=Path.Combine(folder,"e","elsewhere");Write(Path.Combine(elsewhere,Pack),"{\"foreign\":1}");
            Directory.Delete(Path.Combine(legacy,"packs"),true);Junction(Path.Combine(legacy,"packs"),elsewhere);
            Throws(()=>DesktopState.Migrate(legacy,data,retryDelay:fast),"Linked legacy pack folder was followed.");
            Check(!File.Exists(Path.Combine(data,DesktopState.Marker))&&!Directory.Exists(Path.Combine(data,"packs")),"Linked folder produced migrated state.");
            legacy=Legacy(Path.Combine(folder,"f"));string target=Path.Combine(folder,"f","target");Directory.CreateDirectory(target);
            data=Path.Combine(folder,"f","ForeverFlipData");Junction(data,target);
            Throws(()=>DesktopState.Migrate(legacy,data,retryDelay:fast),"Linked destination root was used.");
            Check(!Directory.EnumerateFileSystemEntries(target).Any(),"Linked destination received files.");

            // Oversized state, overlapping roots and a missing legacy folder.
            legacy=Legacy(Path.Combine(folder,"g"));data=Path.Combine(folder,"g","ForeverFlipData");
            using(var big=new FileStream(Path.Combine(legacy,"desktop.log"),FileMode.Open,FileAccess.Write))big.SetLength(DesktopState.MaxFileBytes+1);
            Throws(()=>DesktopState.Migrate(legacy,data,retryDelay:fast),"Oversized state file was copied.");
            Check(!File.Exists(Path.Combine(data,DesktopState.Marker)),"Oversized failure looked complete.");
            Throws(()=>DesktopState.Migrate(legacy,Path.Combine(legacy,"data"),retryDelay:fast),"Nested destination was accepted.");
            data=Path.Combine(folder,"h","ForeverFlipData");
            var empty=DesktopState.Migrate(Path.Combine(folder,"h","ForeverFlip"),data,retryDelay:fast);
            Check(empty.Copied==0&&File.Exists(Path.Combine(data,DesktopState.Marker)),"Fresh install did not complete migration.");

            // Setup has already renamed the install when its hook runs.
            legacy=Legacy(Path.Combine(folder,"install"));data=Path.Combine(folder,"install","ForeverFlipData");
            Write(Path.Combine(legacy,"current","sq.version"),"<package><metadata><id>ForeverFlip</id><mainExe>ForeverFlip.exe</mainExe></metadata></package>");
            string rollback=legacy+".abcdefghijklmnop";Directory.Move(legacy,rollback);Directory.CreateDirectory(legacy);
            Write(Path.Combine(rollback,"support",Queue,new string('a',64)+".report.json"),"{\"consented\":true}");
            var installed=DesktopState.MigrateInstall(legacy,data);
            Check(installed.Copied==8&&Read(Path.Combine(data,"settings.json"))==Read(Path.Combine(rollback,"settings.json")),"Setup rollback state was not preserved.");
            Check(Read(Path.Combine(data,"support",Queue,new string('a',64)+".report.json"))=="{\"consented\":true}","Queued consented report was lost.");
            Check(!Directory.Exists(Path.Combine(data,"current"))&&File.Exists(Path.Combine(rollback,"account.json")),"Installer migration copied binaries or removed credentials.");
            File.Delete(Path.Combine(data,"account.json"));
            Check(DesktopState.MigrateInstall(legacy,data).AlreadyComplete&&!File.Exists(Path.Combine(data,"account.json")),"Reinstall resurrected a signed-out account.");
        }finally{
            // Recursive delete removes junctions without following them.
            Check(folder.StartsWith(Path.GetFullPath(TestPaths.Root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Cleanup escaped test workspace.");
            try{Directory.Delete(folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
