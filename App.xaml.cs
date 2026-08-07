using System.Windows;

namespace WinHardwareMultitool;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Gives the tray icon's WinForms ContextMenuStrip modern (COMCTL32 v6) visual styles
        // instead of the classic flat look, even though this is otherwise a pure WPF app.
        System.Windows.Forms.Application.EnableVisualStyles();
    }
}
