using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinHardwareMultitool.ViewModels;

namespace WinHardwareMultitool;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2; // Mica

    private readonly MainViewModel _viewModel;
    private readonly TrayIconService _trayIcon = new();
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        ApplySavedWindowBounds();

        // Seed both icons from the ViewModel's actual current state (defaults to Normal/green) -
        // OnViewModelPropertyChanged only fires on later *changes*, so without this they'd stay
        // on a hardcoded placeholder color for as long as the level never transitions.
        Icon = CreateDotIcon(_viewModel.SafetyLevelBrush.Color);
        _trayIcon.SetColor(_viewModel.SafetyLevelBrush.Color);

        _viewModel.EmergencyRaised += OnEmergencyRaised;
        _viewModel.ExportLogRequested += OnExportLogRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.LogEntries.CollectionChanged += (_, _) =>
        {
            // Deferred via BeginInvoke: calling ScrollIntoView synchronously inside a
            // CollectionChanged handler can run before the ItemsControl's container generator has
            // caught up with the new item, which WPF surfaces as "Items collection is inconsistent
            // with ItemsSource" - the same failure mode hit (and fixed) in the diagnostics panel.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogListBox.Items.Count > 0)
                    LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            }), System.Windows.Threading.DispatcherPriority.Background);
        };

        _trayIcon.ShowRequested += (_, _) => RestoreFromTray();
        _trayIcon.ExitRequested += (_, _) => Close();

        Loaded += (_, _) =>
        {
            _viewModel.Initialize();
            _trayIcon.Show();
        };
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyModernBackdrop();
    }

    /// <summary>Windows 11's Mica backdrop, applied via DWM. Silently does nothing on older
    /// Windows builds or if the API call fails - the solid dark background from XAML stays as-is.</summary>
    private void ApplyModernBackdrop()
    {
        if (Environment.OSVersion.Version.Build < 22000) return;

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;

            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

            int backdropType = DwmsbtMainWindow;
            int result = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            if (result == 0)
                Background = System.Windows.Media.Brushes.Transparent;
        }
        catch
        {
            // Mica unavailable - keep the fallback background from XAML.
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SafetyLevelBrush))
        {
            var color = _viewModel.SafetyLevelBrush.Color;
            _trayIcon.SetColor(color);
            Icon = CreateDotIcon(color);
        }
    }

    /// <summary>Renders a simple colored-dot icon in pure WPF (no static .ico asset needed) so the
    /// window/taskbar icon can reflect the current safety color, same as the tray icon.</summary>
    private static ImageSource CreateDotIcon(Color color)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(16, 16), 14, 14);
        }

        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            _trayIcon.ShowBalloon("Windows Hardware Multitool", "Uygulama sistem tepsisinde çalışmaya devam ediyor.");
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnEmergencyRaised(object? sender, EventArgs e)
    {
        RestoreFromTray();
        var dialog = new EmergencyDialog(_viewModel.EmergencyMessage) { Owner = this };
        dialog.ShowDialog();
    }

    private void OnExportLogRequested(object? sender, IReadOnlyList<string> lines)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"WinHardwareMultitool_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = "Metin Dosyası (*.txt)|*.txt|Tüm Dosyalar (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllLines(dialog.FileName, lines);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Günlük kaydedilemedi: {ex.Message}", "Hata",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Restores last run's window size/position if it's still a sane fit for the current
    /// display setup (e.g. a second monitor that provided the saved position may be gone now).</summary>
    private void ApplySavedWindowBounds()
    {
        var (width, height, left, top) = _viewModel.SavedWindowBounds;
        if (width is not { } w || height is not { } h || left is not { } l || top is not { } t)
            return;

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        if (!virtualScreen.IntersectsWith(new Rect(l, t, w, h))) return;

        Width = w;
        Height = h;
        Left = l;
        Top = t;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;

        e.Cancel = true;
        _isClosing = true;
        _trayIcon.Dispose();

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        _viewModel.SaveWindowBounds(bounds.Width, bounds.Height, bounds.X, bounds.Y);

        await _viewModel.ShutdownAsync();
        Close();
    }
}
