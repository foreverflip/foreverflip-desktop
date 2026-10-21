using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;

namespace ForeverFlip.Desktop;

// The downloadable Setup is this outer executable. The embedded Velopack
// installer is never started until an independent durable snapshot exists.
public static class InstallerProgram
{
    const string Resource="ForeverFlip.NativeSetup";
    static Stream Payload()=>Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
        ??throw new InvalidDataException("This Setup is missing its installer payload.");
    static string ExpectedDigest()=>Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a=>a.Key=="InstallerPayloadSHA256").Value??"";
    static bool ValidDigest(string digest)=>digest.Length==64&&digest.All(Uri.IsHexDigit);
    internal static void VerifyPayload(Stream input,string expected,Stream? output=null)
    {
        if(!ValidDigest(expected))throw new InvalidDataException("This Setup is missing its integrity record.");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer=new byte[81920];int count;
        while((count=input.Read(buffer,0,buffer.Length))>0){hash.AppendData(buffer,0,count);output?.Write(buffer,0,count);}
        if(!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),Convert.FromHexString(expected)))
            throw new InvalidDataException("The installer payload is damaged. Download Setup again.");
    }
    [STAThread] public static int Main(string[] args)
    {
        bool verify=args.Length==1&&args[0]=="--verify-payload",silent=args.Length==1&&args[0]=="--silent";
        string? staging=null;int result=1;
        try{
            if(args.Length>0&&!verify&&!silent)throw new ArgumentException("Setup accepts --silent or --verify-payload only.");
            if(verify){using var payload=Payload();VerifyPayload(payload,ExpectedDigest());return 0;}
            using(InstallerPreflight.Exclusive(InstallerPreflight.SetupMutex))
            using(InstallerPreflight.Exclusive(InstallerPreflight.AppMutex)){
                result=InstallerPreflight.Run(DesktopState.LegacyRoot,DesktopState.DefaultRoot,()=>{
                    staging=Directory.CreateTempSubdirectory("ForeverFlip-Setup-").FullName;
                    string inner=Path.Combine(staging,"Install.exe");
                    using(var input=Payload())
                    using(var output=new FileStream(inner,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                        VerifyPayload(input,ExpectedDigest(),output);output.Flush(true);
                    }
                    var start=new ProcessStartInfo(inner){UseShellExecute=false,WorkingDirectory=DesktopState.DefaultRoot};
                    // Keep native progress/overwrite confirmation for normal installs.
                    // Its automatic app launch exits quietly while our lock exists.
                    if(silent)start.ArgumentList.Add("--silent");
                    using var process=Process.Start(start)??throw new IOException("The installer could not start.");
                    process.WaitForExit();return process.ExitCode;
                });
            }
            if(result==0&&!silent){
                string exe=Path.Combine(DesktopState.LegacyRoot,"current","ForeverFlip.exe");
                var expected=Assembly.GetExecutingAssembly().GetName().Version!;
                if(File.Exists(exe)&&Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion,out var actual)
                    &&actual.Major==expected.Major&&actual.Minor==expected.Minor&&actual.Build==expected.Build)
                    Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=DesktopState.DefaultRoot});
            }
            return result;
        }catch(Exception e){
            if(!silent&&!verify)MessageBox.Show("Setup stopped safely. "+e.Message+"\nYour saved settings and recovery files were retained.","ForeverFlip Setup",MessageBoxButton.OK,MessageBoxImage.Warning);
            return 1;
        }finally{
            // Delete only our own payload, never recurse through a temp directory.
            if(staging!=null){try{File.Delete(Path.Combine(staging,"Install.exe"));Directory.Delete(staging,false);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
    }
}
