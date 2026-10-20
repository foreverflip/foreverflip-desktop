using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ForeverFlip.Desktop;

internal enum GameSaveState { Stopped, Active, Windowless, Unknown }
internal sealed record GameSaveSnapshot(string Text,string Signature,DateTime SavedAtUtc);

internal static class SavedGameFile
{
    const long MaxBytes=64L*1024*1024;
    internal static string Signature(FileInfo info)=>info.Length+":"+info.LastWriteTimeUtc.Ticks;
    internal static string? WaitReason(GameSaveState state,DateTime savedAt,DateTime now)
    {
        if(state==GameSaveState.Windowless)return "Waiting for WoW to finish starting or closing before syncing.";
        if(state==GameSaveState.Unknown)return "Waiting to confirm the WoW process state before syncing.";
        return now-savedAt<TimeSpan.FromSeconds(10)?"Waiting for the game save to settle (10 seconds).":null;
    }
    internal static GameSaveState Probe(string clientRoot)
    {
        string root=Path.GetFullPath(clientRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        bool found=false,windowless=false,unknown=false;
        Process[] processes;
        try{processes=Process.GetProcesses();}catch{return GameSaveState.Unknown;}
        foreach(var process in processes)try{
            if(!Regex.IsMatch(process.ProcessName,@"^Wow(?:Classic)?(?:B|T)?(?:-64)?$",RegexOptions.IgnoreCase))continue;
            string? exe;
            try{exe=process.MainModule?.FileName;}catch{unknown=true;continue;}
            if(exe==null){unknown=true;continue;}
            if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(exe)),root,StringComparison.OrdinalIgnoreCase))continue;
            found=true;
            if(process.MainWindowHandle==IntPtr.Zero)windowless=true;
        }catch{unknown=true;}finally{process.Dispose();}
        if(unknown)return GameSaveState.Unknown;
        return !found?GameSaveState.Stopped:windowless?GameSaveState.Windowless:GameSaveState.Active;
    }
    // Never deny the game a concurrent write, rename or atomic replacement.
    // The input handle is disposed before Lua parsing or any network request.
    internal static FileStream OpenRead(string path)=>new(path,FileMode.Open,FileAccess.Read,
        FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan);
    internal static GameSaveSnapshot Read(string path,string? expected=null)
    {
        var before=new FileInfo(path);string signature=Signature(before);
        if(expected!=null&&signature!=expected)throw new IOException("Game save changed before reading; retrying later.");
        if(before.Length>MaxBytes)throw new InvalidDataException("Game save exceeds the 64 MiB reader limit.");
        string text;
        using(var stream=OpenRead(path))
        using(var reader=new StreamReader(stream,Encoding.UTF8,true,65536)){
            if(stream.Length!=before.Length)throw new IOException("Game save changed while opening; retrying later.");
            var buffer=new char[65536];var result=new StringBuilder();int read;
            while((read=reader.Read(buffer,0,buffer.Length))!=0){
                if(result.Length+read>MaxBytes)throw new InvalidDataException("Game save exceeds the reader limit.");
                result.Append(buffer,0,read);
            }
            text=result.ToString();
        }
        if(Signature(new FileInfo(path))!=signature)throw new IOException("Game save changed during reading; retrying later.");
        return new(text,signature,before.LastWriteTimeUtc);
    }
}
