using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinHardwareMultitool;

/// <summary>Wraps a WinForms NotifyIcon so the app can live in the system tray with an icon
/// color-coded to the current safety level - lets a user glance at the taskbar corner instead
/// of keeping the dashboard window open. Owned and disposed by MainWindow.</summary>
public sealed class TrayIconService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _notifyIcon;

    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Göster").Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add("Çıkış").Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _notifyIcon = new NotifyIcon
        {
            Text = "Windows Hardware Multitool & Safety Guard",
            Visible = false,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        SetColor(System.Windows.Media.Colors.Gray);
    }

    public void Show() => _notifyIcon.Visible = true;

    public void Hide() => _notifyIcon.Visible = false;

    public void ShowBalloon(string title, string text) =>
        _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    /// <summary>Redraws the tray icon as a solid dot in the given color (green/yellow/red safety states).</summary>
    public void SetColor(System.Windows.Media.Color color)
    {
        var gdiColor = Color.FromArgb(color.A, color.R, color.G, color.B);

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(gdiColor);
            g.FillEllipse(brush, 3, 3, 26, 26);
        }

        IntPtr hIcon = bitmap.GetHicon();
        Icon clonedIcon;
        using (var wrapped = Icon.FromHandle(hIcon))
        {
            clonedIcon = (Icon)wrapped.Clone();
        }
        DestroyIcon(hIcon);

        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = clonedIcon;
        oldIcon?.Dispose();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
