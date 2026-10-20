using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForeverFlip.Desktop;

// The companion source/package contains no addon source. An initial install or
// repair retrieves the separately distributed addon through its signed feed.
public static class SignedAddonDownload
{
    public static async Task<bool> EnsureAsync(string root,bool repair=false)
    {
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(2)};
        return await EnsureCore(root,repair,http,Path.Combine(AppContext.BaseDirectory,"Assets","release-public.pem"),ClientInstall.GameRunning,
            (r,bytes,version)=>ClientInstall.InstallArchive(r,bytes,version)).ConfigureAwait(false);
    }

    internal static async Task<bool> EnsureCore(string root,bool repair,HttpClient http,string publicKey,Func<bool> gameRunning,Action<string,byte[],Version> install)
    {
        if(!ClientInstall.SupportedRoot(root))throw new InvalidDataException("Select the supported WoW Forever client.");
        if(gameRunning())throw new IOException("Installation waits until WoW closes.");
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        byte[] raw=await Read(http,MarketSync.Service+"/releases/addon/latest.json",1024*1024,deadline.Token).ConfigureAwait(false);
        using var envelope=JsonDocument.Parse(raw);
        var manifest=MarketSync.Verify(envelope.RootElement,publicKey);
        if(manifest.GetProperty("schema").GetString()!="foreverflip.addon-release"||!Version.TryParse(manifest.GetProperty("version").GetString(),out var version))
            throw new InvalidDataException("Invalid signed addon manifest.");
        string path=manifest.GetProperty("path").GetString()??"";
        if(path!="/downloads/ForeverFlip-"+version+".zip")throw new InvalidDataException("Invalid signed addon path.");
        var local=ClientInstall.VersionAt(Path.Combine(root,"Interface","AddOns","ForeverFlip"));
        bool helper=File.Exists(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data","ForeverFlip_Data.toc"))
            &&File.Exists(Path.Combine(root,"Interface","AddOns","ForeverFlip_Data","Data.lua"));
        if(local>version)throw new InvalidDataException("Refusing to downgrade a newer installed addon.");
        if(!repair&&local==version&&helper)return false;
        byte[] bytes=await Read(http,MarketSync.Service+path,10*1024*1024,deadline.Token).ConfigureAwait(false);
        if(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()!=manifest.GetProperty("sha256").GetString())
            throw new InvalidDataException("Addon checksum mismatch.");
        if(gameRunning())throw new IOException("WoW started during download. Exit WoW and retry installation.");
        install(root,bytes,version);
        return true;
    }

    static async Task<byte[]> Read(HttpClient http,string url,int maximum,CancellationToken cancellation)
    {
        using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>maximum)throw new InvalidDataException("Addon download is too large.");
        using var source=await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var target=new MemoryStream();byte[] buffer=new byte[65536];int n;
        while((n=await source.ReadAsync(buffer.AsMemory(),cancellation).ConfigureAwait(false))>0){
            if(target.Length+n>maximum)throw new InvalidDataException("Addon download is too large.");
            target.Write(buffer,0,n);
        }
        return target.ToArray();
    }
}
