using System.Windows;
using System.Windows.Threading;

namespace HeavyRain;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!InstallationProbe.IsAdministrator())
        {
            MessageBox.Show("Heavy Rain requires administrator privileges. Launch HeavyRain.exe and approve the Windows elevation prompt.",
                "Administrator access required", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }
        var splash = new SplashWindow();
        MainWindow? window = null;
        var cancelled = false;
        var ready = false;
        splash.Closed += (_, _) =>
        {
            if (ready) return;
            cancelled = true;
            window?.Close();
            Shutdown();
        };
        try
        {
            splash.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (cancelled) return;
            using var startupSound = EmbeddedSound.TryPlay("HeavyRain.StartupChime.wav", !e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase));
            await splash.RunStartupAsync(async () =>
            {
                window = new MainWindow();
                await window.InitializeAsync(splash.ReportProgress);
            });
            if (cancelled) return;
            ready = true;
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window!.Show();
            splash.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (cancelled) return;
            ready = true;
            MessageBox.Show(splash, $"Heavy Rain could not start ({ex.GetType().Name}). Try launching the application again.",
                "Heavy Rain", MessageBoxButton.OK, MessageBoxImage.Error);
            window?.Close();
            splash.Close();
            Shutdown(1);
        }
    }
}
