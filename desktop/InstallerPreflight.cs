using System;
using System.Threading;

namespace ForeverFlip.Desktop;

// The destructive installer is a callback so tests can prove it is never
// reached on a preservation failure, without running Setup or reading real state.
internal static class InstallerPreflight
{
    internal const string SetupMutex="Local\\ForeverFlipSetup",AppMutex="Local\\ForeverFlipDesktop";
    internal static bool InstallInProgress {
        get { if(!Mutex.TryOpenExisting(SetupMutex,out var mutex))return false;using(mutex)return true; }
    }
    internal static int Run(string installRoot,string dataRoot,Func<int> install)
    {
        DesktopState.PreserveForInstall(installRoot,dataRoot);
        return install();
    }
    internal static IDisposable Exclusive(string name)
    {
        var mutex=new Mutex(false,name);bool held;
        try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}
        if(!held){mutex.Dispose();throw new InvalidOperationException("Quit ForeverFlip from its system tray menu, then run Setup again. No installation files were changed.");}
        return new Lease(mutex);
    }
    sealed class Lease(Mutex mutex):IDisposable
    {
        public void Dispose(){mutex.ReleaseMutex();mutex.Dispose();}
    }
}
