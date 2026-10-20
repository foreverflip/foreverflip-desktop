using System.IO;
using System.Security.Cryptography;
using System.Text;
using ForeverFlip.Desktop;

internal static class SavedGameFileTests
{
    internal static async Task Run(string folder)
    {
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        void Reject(Action action,string message){try{action();}catch(IOException){return;}catch(InvalidDataException){return;}throw new Exception(message);}
        var now=DateTime.UtcNow;
        foreach(var state in new[]{GameSaveState.Stopped,GameSaveState.Active}){
            Check(SavedGameFile.WaitReason(state,now.AddSeconds(-9),now)!=null,"A recent save must settle even when the game is stopped");
            Check(SavedGameFile.WaitReason(state,now.AddSeconds(-10),now)==null,"A settled save may sync while playing or after exit");
        }
        Check(SavedGameFile.WaitReason(GameSaveState.Windowless,now.AddHours(-1),now)!=null,"A windowless game must defer all sync, even with an old unchanged save");
        Check(SavedGameFile.WaitReason(GameSaveState.Unknown,now.AddHours(-1),now)!=null,"An uncertain process probe must not authorize file access");
        string file=Path.Combine(folder,"shared-save.lua"),old=Path.Combine(folder,"replaced-save.lua");
        File.WriteAllText(file,"ForeverFlipDB={ [\"markets\"]={},[\"label\"]=\"Cloth é\" }",new UTF8Encoding(true));
        var original=File.ReadAllBytes(file);var hash=SHA256.HashData(original);var mtime=File.GetLastWriteTimeUtc(file);
        // Reproduce the previous default sharing collision on an isolated file.
        using(var prior=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
            Reject(()=>{using var writer=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);},"Old sharing should reproduce a denied game writer");
            Reject(()=>File.Move(file,old),"Old sharing should reproduce a denied atomic save rename");
        }
        using(var shared=SavedGameFile.OpenRead(file)){
            using(var writer=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){
                Check(writer.CanWrite,"A concurrent game writer was blocked");
                Reject(()=>File.ReadAllText(file),"The original default reader must reproduce a sharing collision with an open writer");
                Check(SavedGameFile.Read(file).Text.Contains("Cloth é"),"Shared snapshot could not read alongside an unchanged open writer");
            }
            File.Move(file,old);File.WriteAllText(file,"Replacement save",Encoding.UTF8);
            using var reader=new StreamReader(shared,Encoding.UTF8,true,1024,true);
            Check(reader.ReadToEnd().Contains("Cloth é"),"Replacement must not redirect the already-open snapshot");
        }
        File.Move(old,file,true);
        var snapshot=SavedGameFile.Read(file);
        Check(snapshot.Text.Contains("Cloth é")&&snapshot.SavedAtUtc==mtime,"UTF-8 and actual save time must survive the shared reader");
        using(var exclusive=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check(exclusive.CanWrite,"Snapshot retained a handle after returning");
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file)))&&mtime==File.GetLastWriteTimeUtc(file),"Reading modified the source database");
        File.WriteAllText(file,"Changed length and metadata",Encoding.UTF8);
        Reject(()=>SavedGameFile.Read(file,snapshot.Signature),"A changed save must be rejected before parsing or upload");
        string huge=Path.Combine(folder,"oversize-save.lua");
        using(var stream=new FileStream(huge,FileMode.Create,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))stream.SetLength(64L*1024*1024+1);
        Reject(()=>SavedGameFile.Read(huge),"An oversized save must be bounded before reading");
        // Exercise the real Tick before any support parsing, market parsing or
        // network request. An exclusive test-file lock catches a misplaced gate.
        string client=Path.Combine(folder,"save-guard","_classic_beta_"),addons=Path.Combine(client,"Interface","AddOns");
        Directory.CreateDirectory(Path.Combine(addons,"ForeverFlip"));Directory.CreateDirectory(Path.Combine(addons,"ForeverFlip_Data"));
        Directory.CreateDirectory(Path.Combine(client,"WTF"));File.WriteAllText(Path.Combine(client,"WowB.exe"),"");
        File.WriteAllText(Path.Combine(addons,"ForeverFlip","ForeverFlip.toc"),"## Version: 999.0.0");
        File.WriteAllText(Path.Combine(addons,"ForeverFlip_Data","ForeverFlip_Data.toc"),"Data.lua");
        string selected=Path.Combine(client,"WTF","test-save.lua");File.WriteAllText(selected,"INVALID_LUA_MUST_NOT_BE_READ");
        File.SetLastWriteTimeUtc(selected,now.AddMinutes(-1));
        var config=new Settings{ClientRoot=client,SavedFile=selected,NoticeAccepted=true,ShareMarket=false};
        var sync=new MarketSync(config,Path.Combine(folder,"empty-pack-cache"));
        string untouchedSupport=sync.SupportStatus;
        using(var locked=new FileStream(selected,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
            foreach(var state in new[]{GameSaveState.Windowless,GameSaveState.Unknown}){
                sync.SaveProcessProbe=_=>state;await sync.Tick();
                Check(sync.State==MarketSyncState.Waiting&&!sync.IsBusy&&config.LastLocalRead=="", "Closing/unknown Tick reached the locked save or retained its operation gate");
                Check(sync.SupportStatus==untouchedSupport,"Support parsing ran despite the shared shutdown gate");
            }
        }
        File.SetLastWriteTimeUtc(selected,DateTime.UtcNow.AddMinutes(1));sync.SaveProcessProbe=_=>GameSaveState.Active;
        await sync.Tick();Check(sync.State==MarketSyncState.Waiting&&sync.Status.Contains("10 seconds"),"Real Tick did not defer a changing/recent save during play");
        Check(config.ProtectedToken==""&&config.ProtectedDiagnosticsToken==""&&config.LastLocalRead=="", "Waiting changed consent, credentials or the read acknowledgement");
        Console.WriteLine("PASS: old writer/rename collision reproduced; shared reads permit game writes/replacement, release handles, preserve data/timestamps, bound input and defer recent/windowless/unknown sessions");
    }
}
