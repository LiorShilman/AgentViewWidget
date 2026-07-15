using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace AgentLiveWidget;

/// <summary>
/// System-tray icon whose dot color mirrors the agent status.
/// Uses the built-in WinForms NotifyIcon — no external packages.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly WF.NotifyIcon _notifyIcon;
    private Icon? _currentIcon;

    public TrayIconService(Action toggleVisibility, Action exit)
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => toggleVisibility());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _notifyIcon = new WF.NotifyIcon
        {
            Text = "Agent Live Widget",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => toggleVisibility();

        SetStatusColor(Color.FromArgb(248, 113, 113)); // offline red
    }

    public void SetStatusColor(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var glow = new SolidBrush(Color.FromArgb(70, color));
            using var core = new SolidBrush(color);
            g.FillEllipse(glow, 0, 0, 16, 16);
            g.FillEllipse(core, 3, 3, 10, 10);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var handleIcon = Icon.FromHandle(hIcon);
            var newIcon = (Icon)handleIcon.Clone();
            _notifyIcon.Icon = newIcon;
            _currentIcon?.Dispose();
            _currentIcon = newIcon;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}
