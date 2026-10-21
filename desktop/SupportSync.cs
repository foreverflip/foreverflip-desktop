using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForeverFlip.Desktop;

public sealed partial class MarketSync
{
    string supportReadSignature="";
    Dictionary<string,object?>? supportDB;
    async Task SyncSupport(string signature)
    {
        try{
            if(supportReadSignature!=signature||supportDB==null){
                var db=await Task.Run(()=>LuaReader.Read(SavedGameFile.Read(Config.SavedFile,signature).Text));
                var after=new FileInfo(Config.SavedFile);
                if(signature!=after.Length+":"+after.LastWriteTimeUtc.Ticks)throw new IOException("Game save changed while preparing logs.");
                // Local accounting is useful even when sharing is disabled or
                // a later network operation fails. Reuse this stable local read.
                ProfitBooks=ProfitBook.Read(db);
                // Retain just the request/redaction inputs, never a second full
                // in-memory database between 30-second polling ticks.
                supportDB=new(){["supportRequest"]=Sub(db,"supportRequest"),["ledgers"]=Sub(db,"ledgers").Keys.ToDictionary(k=>k,_=>(object?)true)};
                supportReadSignature=signature;
            }
            string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Config.SavedFile).ToUpperInvariant()))).ToLowerInvariant()[..24];
            var queue=new SupportOutbox(Path.Combine(StateRoot,"support",key));
            // The in-game request remains the only consent. Signing in neither
            // creates nor sends a report; a signed-out report waits on disk.
            string? blocked=SignedIn?null:"Report saved on this PC and waiting. Sign in under Settings → Account to send it; signing in does not turn on market sharing.";
            await queue.Process(supportDB,typeof(Program).Assembly.GetName().Version!.ToString(3),async raw=>{
                string token=(Account==null?null:await Account.AccessTokenAsync())??throw new MarketServiceException("Sign in under Settings → Account to send the saved report.");
                using var body=JsonDocument.Parse(raw);return await Post("/api/v1/support/reports",body.RootElement,token);
            },blocked:blocked);
            SupportStatus=queue.Status;
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException or FormatException){
            SupportStatus="Report could not be prepared. Keep the desktop app open and make a new game save.";
        }
    }
}
