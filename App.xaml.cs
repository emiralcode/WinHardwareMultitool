using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WinHardwareMultitool;

public partial class App : System.Windows.Application
{
    private static readonly string CrashLogPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "WinHardwareMultitool", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Gives the tray icon's WinForms ContextMenuStrip modern (COMCTL32 v6) visual styles
        // instead of the classic flat look, even though this is otherwise a pure WPF app.
        System.Windows.Forms.Application.EnableVisualStyles();

        // Without this, any unhandled exception on the UI thread silently kills the whole
        // process - the stress test engine (especially the GPU render thread) is novel enough
        // code that we want a diagnosable message instead of a mystery disappearance.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI thread", e.Exception);
        System.Windows.MessageBox.Show(
            $"Beklenmeyen bir hata oluştu ve günlüğe kaydedildi:\n\n{e.Exception.Message}\n\nUygulama kapanmayacak.",
            "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogCrash("AppDomain (kurtarılamaz)", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Arka plan görevi", e.Exception);
        e.SetObserved();
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(CrashLogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {ex}\n\n");
        }
        catch
        {
            // best effort - don't let logging itself crash the app
        }
    }
}
