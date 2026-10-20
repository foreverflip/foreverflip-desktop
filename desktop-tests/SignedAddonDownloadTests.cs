using ForeverFlip.Desktop;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

static class SignedAddonDownloadTests
{
    sealed class Fake(Func<string,byte[]> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            =>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(reply(request.RequestUri!.AbsolutePath))});
    }
    public static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);string root=Path.Combine(folder,"_classic_beta_");
        Directory.CreateDirectory(Path.Combine(root,"Interface"));Directory.CreateDirectory(Path.Combine(root,"WTF"));File.WriteAllText(Path.Combine(root,"Wow.exe"),"");
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);string pem=Path.Combine(folder,"public.pem");File.WriteAllText(pem,key.ExportSubjectPublicKeyInfoPem());
        byte[] archive=[1,2,3];long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();bool badHash=false,badPath=false,future=false,expired=false,badSignature=false,oversized=false;int calls=0,installed=0;
        byte[] Reply(string path)
        {
            calls++;
            if(!path.EndsWith("latest.json"))return oversized?new byte[10*1024*1024+1]:archive;
            var payload=JsonSerializer.SerializeToUtf8Bytes(new{schema="foreverflip.addon-release",version="1.0.0",createdAt=future?now+3600:now-60,expiresAt=expired?now-1:now+3600,path=badPath?"https://other.invalid/evil.zip":"/downloads/ForeverFlip-1.0.0.zip",sha256=badHash?new string('0',64):Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()});
            byte[] signature=key.SignData(payload,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence);if(badSignature)signature[0]^=1;
            return JsonSerializer.SerializeToUtf8Bytes(new{algorithm="ES256",payload=Convert.ToBase64String(payload),signature=Convert.ToBase64String(signature)});
        }
        using var http=new HttpClient(new Fake(Reply));
        Action<string,byte[],Version> install=(r,b,v)=>{if(r!=root||!b.SequenceEqual(archive)||v!=new Version("1.0.0"))throw new Exception("Wrong verified installation");installed++;};
        await SignedAddonDownload.EnsureCore(root,false,http,pem,()=>false,install);if(installed!=1||calls!=2)throw new Exception("Initial signed download failed");
        foreach(string mode in new[]{"hash","path","future","expired","signature","size","running","started"}){
            badHash=mode=="hash";badPath=mode=="path";future=mode=="future";expired=mode=="expired";badSignature=mode=="signature";oversized=mode=="size";int probes=0;bool rejected=false;
            try{await SignedAddonDownload.EnsureCore(root,true,http,pem,()=>mode=="running"||mode=="started"&&++probes>1,install);}catch{rejected=true;}
            if(!rejected||installed!=1)throw new Exception("Accepted unsafe addon download: "+mode);
        }
        badHash=badPath=future=expired=badSignature=oversized=false;
        string addon=Path.Combine(root,"Interface","AddOns","ForeverFlip"),helper=Path.Combine(root,"Interface","AddOns","ForeverFlip_Data");Directory.CreateDirectory(addon);Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(addon,"ForeverFlip.toc"),"## Version: 1.0.0");File.WriteAllText(Path.Combine(helper,"ForeverFlip_Data.toc"),"Data.lua");File.WriteAllText(Path.Combine(helper,"Data.lua"),"KEEP");
        calls=0;if(await SignedAddonDownload.EnsureCore(root,false,http,pem,()=>false,install)||calls!=1)throw new Exception("Equal current version downloaded twice");
        File.WriteAllText(Path.Combine(addon,"ForeverFlip.toc"),"## Version: 2.0.0");bool downgrade=false;try{await SignedAddonDownload.EnsureCore(root,true,http,pem,()=>false,install);}catch(InvalidDataException){downgrade=true;}if(!downgrade||installed!=1)throw new Exception("Downloaded downgrade accepted");
        Console.WriteLine("PASS: separate addon download verifies signature/time/path/hash/size, checks game state twice and refuses downgrade; no addon source bundled");
    }
}
