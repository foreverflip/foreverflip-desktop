using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ForeverFlip.Desktop;

// Only fixed actions are accepted. This is never a shell command, a file
// path or a browser destination supplied by a remote caller.
public sealed record DesktopActivation(string Action,string RequestID="",string Code="")
{
    public const string Scheme="us.blahh.foreverflip";
    static string PipeName=>"ForeverFlip.Activation."+WindowsIdentity.GetCurrent().User!.Value.Replace('-','_');
    public static DesktopActivation? Parse(string value)
    {
        if(value.Length>512||value.Any(char.IsControl)||!Uri.TryCreate(value,UriKind.Absolute,out var uri)
            ||uri.Scheme!=Scheme||uri.UserInfo!=""||!uri.IsDefaultPort||uri.Fragment!=""||uri.AbsolutePath is not ("" or "/"))return null;
        if(uri.Host is "sign-in" or "open"&&uri.Query=="")return new(uri.Host);
        if(uri.Host!="pair-done")return null;
        var match=Regex.Match(uri.Query,@"^\?requestID=([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})&code=([A-Za-z0-9_-]{43})$");
        return match.Success?new("pair-done",match.Groups[1].Value,match.Groups[2].Value):null;
    }
    internal static string Command(string executable)=>"\""+executable+"\" \"%1\"";
    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint eventId,uint flags,IntPtr item1,IntPtr item2);
    static void NotifyAssociations()=>SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);
    public static void Register(bool notify=false)
    {
        string executable=Environment.ProcessPath??throw new InvalidOperationException("App path is unavailable.");
        using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Classes\"+Scheme)){
            void Set(RegistryKey target,string name,string value){
                if(target.GetValue(name) is string current&&current==value&&target.GetValueKind(name)==RegistryValueKind.String)return;
                target.SetValue(name,value,RegistryValueKind.String);notify=true;
            }
            Set(key,"","URL:ForeverFlip sign-in");Set(key,"URL Protocol","");
            using(var icon=key.CreateSubKey("DefaultIcon"))Set(icon,"","\""+executable+"\",0");
            using(var command=key.CreateSubKey(@"shell\open\command"))Set(command,"",Command(executable));
        }
        // Browsers and the shell may have cached an absent handler before installation.
        // Install/update hooks force a notification even when the path is unchanged.
        if(notify)NotifyAssociations();
    }
    public static void Unregister()
    {
        bool owned;
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Classes\"+Scheme+@"\shell\open\command"))
            owned=key?.GetValue("") as string==Command(Environment.ProcessPath??"");
        if(owned){Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\"+Scheme,false);NotifyAssociations();}
    }
    public static bool Forward(string value,string? testPipe=null)
    {
        if(Parse(value)==null)return false;
        try{
            using var pipe=new NamedPipeClientStream(".",testPipe??PipeName,PipeDirection.Out,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            byte[] bytes=Encoding.UTF8.GetBytes(value+"\n");pipe.WriteAsync(bytes,timeout.Token).AsTask().GetAwaiter().GetResult();return true;
        }catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException){return false;}
    }
    public static async Task Listen(Action<DesktopActivation> receive,CancellationToken stop,string? testPipe=null)
    {
        while(!stop.IsCancellationRequested){
            try{
                using var pipe=new NamedPipeServerStream(testPipe??PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var bytes=new byte[513];int length=0;
                while(length<bytes.Length){int n=await pipe.ReadAsync(bytes.AsMemory(length,1),timeout.Token);if(n==0||bytes[length]=='\n')break;length++;}
                if(length<=512&&Parse(Encoding.UTF8.GetString(bytes,0,length)) is {} activation)receive(activation);
            }catch(Exception ex) when(ex is IOException or OperationCanceledException or UnauthorizedAccessException){if(stop.IsCancellationRequested)return;}
        }
    }
}
