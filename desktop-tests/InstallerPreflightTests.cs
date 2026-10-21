using ForeverFlip.Desktop;
using System.IO;
using System.Security.Cryptography;

// Only synthetic state and a fake destructive installer; never runs real Setup.
internal static class InstallerPreflightTests
{
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
 static void Write(string p,string s){Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,s);}
 static void Refuse(Action action){try{action();}catch(DesktopStateMigrationException){return;}throw new Exception("Expected preservation refusal");}
 public static void Run()
 {
  string testRoot=Path.GetFullPath(Path.Combine(TestPaths.Root,"installer-test-"+Guid.NewGuid().ToString("N")));
  Directory.CreateDirectory(testRoot);
  try{
   string legacy=Path.Combine(testRoot,"ForeverFlip"),data=Path.Combine(testRoot,"ForeverFlipData");
   Write(Path.Combine(legacy,"settings.json"),"SYNTHETIC SETTINGS");Write(Path.Combine(legacy,"account.json"),"SYNTHETIC PROTECTED ACCOUNT");
   int starts=0;
   using(new FileStream(Path.Combine(legacy,"account.json"),FileMode.Open,FileAccess.Read,FileShare.None))
    Refuse(()=>InstallerPreflight.Run(legacy,data,()=>{starts++;return 0;}));
   Check(starts==0&&File.Exists(Path.Combine(legacy,"account.json")),"Locked preservation reached destructive installer");
   Check(!File.Exists(Path.Combine(data,DesktopState.Marker))&&!File.Exists(Path.Combine(data,DesktopState.InstallPointer)),"Failure looked migrated/prepared");
   // Cancellation creates a recovery copy, not a completed migration. A later
   // old-app edit must be captured by the next installer attempt.
   Check(InstallerPreflight.Run(legacy,data,()=>{starts++;return 7;})==7,"Installer failure status was hidden");
   Check(!File.Exists(Path.Combine(data,DesktopState.Marker)),"Cancelled Setup completed migration");
   Write(Path.Combine(legacy,"settings.json"),"LATEST SYNTHETIC SETTINGS");
   // Simulates the upstream success/cleanup path, including a failed fast hook:
   // it deletes the original. Startup must recover solely from the preflight.
   InstallerPreflight.Run(legacy,data,()=>{
    starts++;Check(File.Exists(Path.Combine(data,DesktopState.InstallPointer)),"Installer started before durable preservation");
    Directory.Delete(legacy,true);Directory.CreateDirectory(legacy);
    Write(Path.Combine(legacy,"current","sq.version"),"<package />");return 0;
   });
   // An update after an interrupted first launch must not hide the Setup snapshot.
   DesktopState.RecordUpdate(data);DesktopState.MigrateInstall(legacy,data);
   Check(File.ReadAllText(Path.Combine(data,"settings.json"))=="LATEST SYNTHETIC SETTINGS","Startup did not recover latest pre-install state");
   Check(File.ReadAllText(Path.Combine(data,"account.json"))=="SYNTHETIC PROTECTED ACCOUNT","Installer cleanup lost protected account");
   File.Delete(Path.Combine(data,"account.json"));DesktopState.MigrateInstall(legacy,data);
   Check(!File.Exists(Path.Combine(data,"account.json")),"Recovery resurrected sign-out");
   // An empty new install root alone cannot certify a failed migration complete.
   string unknown=Path.Combine(testRoot,"unknown"),unknownData=Path.Combine(testRoot,"unknown-data");
   Write(Path.Combine(unknown,"current","sq.version"),"<package />");
   Refuse(()=>DesktopState.MigrateInstall(unknown,unknownData));
   Check(!File.Exists(Path.Combine(unknownData,DesktopState.Marker)),"Missing recovery wrote false marker");
   string fresh=Path.Combine(testRoot,"fresh"),freshData=Path.Combine(testRoot,"fresh-data");
   InstallerPreflight.Run(fresh,freshData,()=>{Directory.CreateDirectory(fresh);return 0;});
   DesktopState.MigrateInstall(fresh,freshData);
   Check(File.Exists(Path.Combine(freshData,DesktopState.Marker)),"Protected fresh installation could not start");
   // Cancel Setup, keep using .44, sign out, then update IN APP (not Setup).
   string cancelled=Path.Combine(testRoot,"cancel-update"),cancelData=Path.Combine(testRoot,"cancel-data");
   Write(Path.Combine(cancelled,"settings.json"),"BEFORE CANCEL");Write(Path.Combine(cancelled,"account.json"),"OLD SIGN-IN");
   InstallerPreflight.Run(cancelled,cancelData,()=>0); // native cancel can return success
   Write(Path.Combine(cancelled,"settings.json"),"AFTER CANCEL");File.Delete(Path.Combine(cancelled,"account.json"));
   DesktopState.RecordUpdate(cancelData);DesktopState.MigrateInstall(cancelled,cancelData);
   Check(File.ReadAllText(Path.Combine(cancelData,"settings.json"))=="AFTER CANCEL"&&!File.Exists(Path.Combine(cancelData,"account.json")),"Cancelled Setup snapshot undid edits/sign-out after in-app update");
   string emptyUpdate=Path.Combine(testRoot,"empty-update"),emptyData=Path.Combine(testRoot,"empty-update-data");
   Write(Path.Combine(emptyUpdate,"current","sq.version"),"<package />");DesktopState.RecordUpdate(emptyData);
   DesktopState.MigrateInstall(emptyUpdate,emptyData);
   Check(File.Exists(Path.Combine(emptyData,DesktopState.Marker)),"An unconfigured in-app update could not start");
   string large=Path.Combine(testRoot,"large"),largeData=Path.Combine(testRoot,"large-data");
   Write(Path.Combine(large,"settings.json"),"x");using(var f=File.OpenWrite(Path.Combine(large,"settings.json")))f.SetLength(DesktopState.MaxFileBytes+1);
   Refuse(()=>InstallerPreflight.Run(large,largeData,()=>{throw new Exception("Oversized state reached installer");}));
   string corrupt=Path.Combine(testRoot,"corrupt");Write(Path.Combine(corrupt,DesktopState.InstallPointer),"{\"schemaVersion\":1,\"snapshot\":\"../../elsewhere\"}");
   Refuse(()=>DesktopState.MigrateInstall(unknown,corrupt));
   string badMarker=Path.Combine(testRoot,"bad-marker");Write(Path.Combine(badMarker,DesktopState.Marker),"{}");
   Refuse(()=>InstallerPreflight.Run(unknown,badMarker,()=>throw new Exception("Corrupt marker reached installer")));
   byte[] payload="SYNTHETIC SETUP"u8.ToArray();string digest=Convert.ToHexString(SHA256.HashData(payload));
   using var output=new MemoryStream();InstallerProgram.VerifyPayload(new MemoryStream(payload),digest,output);
   Check(output.ToArray().SequenceEqual(payload),"Embedded payload changed");
   try{InstallerProgram.VerifyPayload(new MemoryStream("BAD"u8.ToArray()),digest);throw new Exception("Bad payload accepted");}catch(InvalidDataException){}
   Console.WriteLine("PASS: Setup never runs before durable preservation; locks/oversize fail closed; cancellation/retry, destructive cleanup, fresh install, marker recovery, sign-out and payload integrity.");
  }finally{
   Check(testRoot.StartsWith(Path.GetFullPath(TestPaths.Root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Cleanup escaped workspace");
   Directory.Delete(testRoot,true);
  }
 }
}
