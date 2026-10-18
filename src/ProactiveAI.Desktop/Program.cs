using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ProactiveAI.Desktop;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Held for the process lifetime so the installer and uninstaller can tell the app is running (Inno Setup AppMutex).
        using var running = new Mutex(false, "ProactiveAI.Running");
        // Single instance: a second launch activates the existing window instead of starting a second capture.
        var instance = AppInstance.FindOrRegisterForKey("ProactiveAI.Main");
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(activation).AsTask().Wait(TimeSpan.FromSeconds(5));
            return 0;
        }
        instance.Activated += (_, _) => App.Current?.OnRedirectedActivation();

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
