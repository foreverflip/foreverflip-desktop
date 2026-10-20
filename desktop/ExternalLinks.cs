using System.Diagnostics;

namespace ForeverFlip.Desktop;

public static class ExternalLinks
{
    public static ProcessStartInfo StartInfo(string url)=>new(url){
        UseShellExecute=true,
        // Browsers can retain this directory for their entire lifetime. Never
        // inherit Velopack's replaceable 'current' folder when launching them.
        WorkingDirectory=MarketSync.StateRoot
    };
}
