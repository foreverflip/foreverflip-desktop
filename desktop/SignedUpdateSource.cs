using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace ForeverFlip.Desktop;

// The existing release signer, not the public server or AI worker, authorizes
// executable updates. Verify both the feed's assets and downloaded bytes.
public sealed class SignedUpdateSource:IUpdateSource
{
    readonly SimpleWebSource inner=new(MarketSync.Service+"/releases/desktop/");
    readonly Dictionary<string,string> allowed=[];
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger,string? appId,string channel,Guid? stagingId=null,VelopackAsset? latestLocalRelease=null)
    {
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};
        using var response=await http.GetAsync(MarketSync.Service+"/releases/desktop/latest.json");
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return new VelopackAssetFeed{Assets=[]};
        response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var manifest=MarketSync.Verify(doc.RootElement,Path.Combine(AppContext.BaseDirectory,"Assets","release-public.pem"));
        if(manifest.GetProperty("schema").GetString()!="foreverflip.desktop-release")throw new InvalidDataException("Invalid desktop release manifest.");
        string approvedVersion=manifest.GetProperty("version").GetString()??throw new InvalidDataException("Missing approved version.");
        allowed.Clear();foreach(var entry in manifest.GetProperty("assets").EnumerateArray()){
            string name=entry.GetProperty("file").GetString()!,hash=entry.GetProperty("sha256").GetString()!;
            if(allowed.Count>=20||Path.GetFileName(name)!=name||name.Contains('\\')||name.Contains(':')||!System.Text.RegularExpressions.Regex.IsMatch(hash,@"^[a-f0-9]{64}$"))throw new InvalidDataException("Invalid signed update asset.");allowed.Add(name,hash);
        }
        var feed=await inner.GetReleaseFeed(logger,appId,channel,stagingId,latestLocalRelease);
        return new VelopackAssetFeed{Assets=feed.Assets.Where(a=>a.PackageId=="ForeverFlip"&&a.Version.ToString()==approvedVersion&&allowed.TryGetValue(a.FileName,out var hash)&&hash.Equals(a.SHA256,StringComparison.OrdinalIgnoreCase)).ToArray()};
    }
    public async Task DownloadReleaseEntry(IVelopackLogger logger,VelopackAsset releaseEntry,string localFile,Action<int> progress,CancellationToken cancelToken)
    {
        if(!allowed.TryGetValue(releaseEntry.FileName,out var expected)||releaseEntry.Size>250*1024*1024)throw new InvalidDataException("Update package was not approved by the release signer.");
        await inner.DownloadReleaseEntry(logger,releaseEntry,localFile,progress,cancelToken);
        using var stream=File.OpenRead(localFile);if(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()!=expected)throw new InvalidDataException("Signed update checksum mismatch.");
    }
}
