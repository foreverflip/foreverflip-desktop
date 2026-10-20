using System.Text.Json;

namespace ForeverFlip.Desktop;

// Survives an updater failure/restart. A failed version may be retried explicitly,
// but must never repeatedly close the app on startup or on the background timer.
public sealed class DesktopUpdateAttempt(string path)
{
    public sealed record Attempt(string Version, DateTimeOffset StartedAt);
    public string PendingMessage(Version current)
    {
        if(!File.Exists(path))return "";
        try{
            var attempt=Read();
            if(current>=Version.Parse(attempt.Version)){File.Delete(path);return "";}
            return "Desktop update to "+attempt.Version+" did not finish. Automatic retries are paused. Close other copies, then use Check app updates in Settings to retry.";
        }catch{return "The last desktop update could not be verified. Automatic retries are paused. Use Check app updates in Settings to retry.";}
    }
    public bool CanApply(Version target,bool manual)
    {
        if(manual||!File.Exists(path))return true;
        try{return Version.Parse(Read().Version)<target;}
        catch{return false;}
    }
    public void Begin(Version target)
    {
        // If recording the attempt fails, the caller must not launch the updater.
        MarketSync.Atomic(path,JsonSerializer.Serialize(new Attempt(target.ToString(),DateTimeOffset.UtcNow)));
    }
    Attempt Read()
    {
        var result=JsonSerializer.Deserialize<Attempt>(File.ReadAllText(path))??throw new InvalidDataException("Missing update attempt.");
        if(!Version.TryParse(result.Version,out _))throw new InvalidDataException("Invalid update attempt version.");
        return result;
    }
}
