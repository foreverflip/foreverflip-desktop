using ForeverFlip.Desktop;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

static class AddonBackupTests
{
    public static void Run(string folder)
    {
        folder=Path.GetFullPath(folder);
        string root=Path.Combine(folder,"_classic_beta_"),addons=Path.Combine(root,"Interface","AddOns"),target=Path.Combine(addons,"ForeverFlip");
        Directory.CreateDirectory(addons);Directory.CreateDirectory(Path.Combine(root,"WTF"));File.WriteAllText(Path.Combine(root,"Wow.exe"),"");
        string personal=Path.Combine(root,"WTF","personal.lua");File.WriteAllText(personal,"PERSONAL_DATABASE");
        void Check(bool value,string why){if(!value)throw new Exception(why);}
        string Toc(string v)=>"## Interface: 16001\n## Title: ForeverFlip\n## Version: "+v+"\n## SavedVariables: ForeverFlipDB\nAddon.lua\n";
        byte[] Package(string v,string body){using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){
            foreach(var item in new[]{("ForeverFlip.toc",Toc(v)),("Addon.lua",body),("BrandIcon.tga","KNOWN_BRAND_TEXTURE"),("SettingsGear.tga","KNOWN_SETTINGS_TEXTURE")}){using var w=new StreamWriter(zip.CreateEntry("ForeverFlip/"+item.Item1).Open());w.Write(item.Item2);}}
            return memory.ToArray();}
        void Install(string version,string body,Action<string,string>? move=null)=>ClientInstall.InstallArchiveCore(root,Package(version,body),Version.Parse(version),false,()=>false,move);
        string[] Stored()=>Directory.GetDirectories(AddonBackups.DirectoryAt(root),"addon-*");
        for(int i=1;i<=6;i++)Install("0.1."+i,"CODE"+i);
        Check(Stored().Length==3,"Keep exactly three rollback versions after six installs");
        Check(Stored().Select(ClientInstall.VersionAt).OrderBy(x=>x).SequenceEqual(new[]{new Version("0.1.3"),new Version("0.1.4"),new Version("0.1.5")}),"Newest three previous versions retained");
        Check(Stored().All(p=>File.ReadAllText(Path.Combine(p,"BrandIcon.tga"))=="KNOWN_BRAND_TEXTURE"),"Known branding textures participate in safe backup retention");
        Check(Stored().All(p=>File.ReadAllText(Path.Combine(p,"SettingsGear.tga"))=="KNOWN_SETTINGS_TEXTURE"),"Known settings texture participates in safe backup retention");
        Check(!Directory.GetDirectories(addons,".foreverflip-backup-*").Any(),"New backups live outside AddOns");
        string helper=Path.Combine(addons,"ForeverFlip_Data","Data.lua");File.WriteAllText(helper,"SHARED_HELPER");
        Install("0.1.6","REPAIR_ONE");Install("0.1.6","REPAIR_TWO");
        Check(Stored().Length==3&&Stored().Select(ClientInstall.VersionAt).Distinct().Count()==3,"Repeated same-version repairs deduplicate rollback versions");
        string immediate=Stored().Single(p=>ClientInstall.VersionAt(p)==new Version("0.1.6"));
        Check(File.ReadAllText(Path.Combine(immediate,"Addon.lua"))=="REPAIR_ONE","Immediate pre-repair code retained");
        string[] before=Stored().Order().ToArray();int moves=0;bool rejected=false;
        try{Install("0.1.7","NEW",(a,b)=>{if(++moves==2)throw new IOException("Injected stage rename failure");Directory.Move(a,b);});}catch(IOException){rejected=true;}
        Check(rejected&&File.ReadAllText(Path.Combine(target,"Addon.lua"))=="REPAIR_TWO","Failed stage rename restores installed addon");
        Check(before.SequenceEqual(Stored().Order())&&!Directory.GetDirectories(addons,".foreverflip-stage-*").Any(),"Failed update never prunes rollback copies and stage is removed");
        Check(File.ReadAllText(personal)=="PERSONAL_DATABASE"&&File.ReadAllText(helper)=="SHARED_HELPER","Backups and rollback preserve personal/shared data");

        string Legacy(string v,int age){string path=Path.Combine(addons,".foreverflip-backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path,"ForeverFlip.toc"),Toc(v));File.WriteAllText(Path.Combine(path,"Addon.lua"),"LEGACY_"+v);
            Directory.SetCreationTimeUtc(path,DateTime.UtcNow.AddMinutes(-age));return path;}
        Legacy("0.1.0",60);Legacy("0.1.4",55);Legacy("0.1.5",50);
        string foreign=Legacy("0.1.9",1);File.WriteAllText(Path.Combine(foreign,"Personal.lua"),"UNKNOWN_CONTENT");
        string nested=Legacy("0.1.9",1);Directory.CreateDirectory(Path.Combine(nested,"unknown"));
        string similar=Path.Combine(addons,".foreverflip-backup-not-ours");Directory.CreateDirectory(similar);
        File.WriteAllText(Path.Combine(similar,"KEEP.txt"),"KEEP");
        var migrated=AddonBackups.Maintain(root);
        Check(migrated.Migrated==3&&Stored().Length==3,"Valid legacy backups migrate then prune to three versions");
        Check(Directory.Exists(foreign)&&Directory.Exists(nested)&&File.ReadAllText(Path.Combine(similar,"KEEP.txt"))=="KEEP","Unknown code, nested contents and unrelated names are preserved");
        string lockedLegacy=Legacy("0.1.1",80);
        using(var held=File.Open(Path.Combine(lockedLegacy,"Addon.lua"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
            var lockedResult=AddonBackups.Maintain(root);
            Check(lockedResult.Skipped>=3&&Directory.Exists(lockedLegacy)&&File.Exists(Path.Combine(lockedLegacy,"ForeverFlip.toc")),"Locked legacy copy remains intact for later maintenance");
        }
        Check(AddonBackups.Maintain(root).Migrated==1&&!Directory.Exists(lockedLegacy)&&Stored().Length==3,"Locked copy migrates safely on a later attempt");
        string malformed=Path.Combine(AddonBackups.DirectoryAt(root),"addon-9999999999999999999-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(malformed);File.WriteAllText(Path.Combine(malformed,"ForeverFlip.toc"),Toc("0.1.1"));File.WriteAllText(Path.Combine(malformed,"Addon.lua"),"PRESERVE_BAD_METADATA");
        AddonBackups.Maintain(root);Check(File.Exists(Path.Combine(malformed,"Addon.lua")),"Invalid stored metadata remains untouched");
        string external=Path.Combine(folder,"outside-backups");Directory.CreateDirectory(external);File.WriteAllText(Path.Combine(external,"KEEP.txt"),"EXTERNAL");
        string junction=Path.Combine(addons,".foreverflip-backup-"+Guid.NewGuid().ToString("N"));
        var process=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"/c","mklink","/J",junction,external})process.ArgumentList.Add(arg);
        using(var p=Process.Start(process)!){string output=p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd();p.WaitForExit();Check(p.ExitCode==0,"Junction fixture could not be created: "+output);}
        try{var guarded=AddonBackups.Maintain(root);Check(guarded.Skipped>=3&&Directory.Exists(junction)&&File.ReadAllText(Path.Combine(external,"KEEP.txt"))=="EXTERNAL","Junction backup is not followed or removed");}
        finally{Directory.Delete(junction);}
        using(var locked=AddonBackups.Acquire(root)){
            bool blocked=false;try{AddonBackups.Maintain(root);}catch(IOException){blocked=true;}Check(blocked,"Concurrent maintenance is serialized");
        }
        before=Stored().Order().ToArray();File.Delete(Path.Combine(target,"Addon.lua"));
        rejected=false;try{AddonBackups.Maintain(root);}catch(IOException){rejected=true;}
        Check(rejected&&before.SequenceEqual(Stored().Order()),"Broken live install cannot trigger cleanup");
        Check(File.ReadAllText(personal)=="PERSONAL_DATABASE"&&File.ReadAllText(helper)=="SHARED_HELPER","Migration/junction/lock checks preserve saved data");
        Console.WriteLine("PASS: outside-AddOns backups; newest3 distinct versions; repeated repairs; stage-failure rollback without pruning; legacy migration; locked-copy retry; invalid metadata/unknown contents/junction preservation; concurrency lock; broken-live refusal; personal/helper preservation");
    }
}
