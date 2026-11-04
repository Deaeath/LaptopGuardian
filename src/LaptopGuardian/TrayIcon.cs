using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using LaptopGuardian.Core;
using WinForms = System.Windows.Forms;

namespace LaptopGuardian;

/// <summary>The notification-area icon. Its colour is the laptop's state; left-click opens the window, right-click the menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon = new();
    readonly ContextMenu _menu;
    System.Drawing.Icon? _current;
    Severity? _level;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    public TrayIcon(Action open, ContextMenu menu)
    {
        _menu = menu;
        _menu.Placement = PlacementMode.MousePoint;
        // A context menu opened from the tray only closes on an outside click if its window is in the foreground.
        _menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(_menu) is HwndSource src) SetForegroundWindow(src.Handle);
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) open();
            else if (e.Button == WinForms.MouseButtons.Right) _menu.IsOpen = true;
        };
        _icon.BalloonTipClicked += (_, _) => open();
        SetStatus(Severity.Info, "LaptopGuardian");
        _icon.Visible = true;
    }

    public void SetStatus(Severity level, string tooltip)
    {
        tooltip = tooltip.Length > 127 ? tooltip[..124] + "..." : tooltip;
        if (_icon.Text != tooltip) _icon.Text = tooltip;
        if (_level == level) return;
        _level = level;
        var old = _current;
        _current = IconFactory.TrayIcon(IconFactory.For(level));
        _icon.Icon = _current;
        old?.Dispose();
    }

    /// <summary>Fallback when Windows toast notifications are unavailable.</summary>
    public void Balloon(Alert a) =>
        _icon.ShowBalloonTip(8000, a.Title, a.Body, a.Severity >= Severity.Warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
