using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace LaptopGuardian;

/// <summary>Light and dark palettes that follow Windows' app mode, including when it changes while running.</summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    static readonly Dictionary<string, (uint Light, uint Dark)> Palette = new()
    {
        ["Bg"] = (0xFFF3F4F6, 0xFF16171B),
        ["Nav"] = (0xFFEBEDF0, 0xFF1C1E23),
        ["Surface"] = (0xFFFFFFFF, 0xFF22242A),
        ["SurfaceHover"] = (0xFFF6F7F9, 0xFF2A2D34),
        ["SurfaceSunken"] = (0xFFF0F2F5, 0xFF1B1D21),
        ["Stroke"] = (0xFFE0E3E8, 0xFF31343C),
        ["Text"] = (0xFF1A1C20, 0xFFECEDEF),
        ["Muted"] = (0xFF5A606B, 0xFFA3A7B0),
        ["Faint"] = (0xFF8B919C, 0xFF6E7380),
        ["Accent"] = (0xFF0A66C8, 0xFF4CA2FF),
        ["AccentHover"] = (0xFF0957AA, 0xFF6BB3FF),
        ["OnAccent"] = (0xFFFFFFFF, 0xFF0B1220),
        ["Good"] = (0xFF1C8A4E, 0xFF3FCF85),
        ["Warn"] = (0xFFAD6E10, 0xFFF2B54A),
        ["Bad"] = (0xFFC62E2B, 0xFFF26663),
        ["Info"] = (0xFF2F68C9, 0xFF73A9FF),
        ["Track"] = (0xFFE3E6EA, 0xFF32353D),
    };

    static bool? _forced;

    /// <param name="forceDark">Pin light or dark (used by --screenshot); null follows Windows.</param>
    public static void Apply(bool? forceDark = null)
    {
        if (forceDark != null) _forced = forceDark;
        IsDark = _forced ?? ReadDark();
        // Keep WPF's own Fluent controls (combo boxes, scroll bars, menus) on the same side as the palette.
        try { Application.Current.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light; } catch { }
        var res = Application.Current.Resources;
        foreach (var (key, (light, dark)) in Palette)
        {
            var c = ToColor(IsDark ? dark : light);
            res[key] = Frozen(new SolidColorBrush(c));
            res[key + "Color"] = c;
        }
        // Soft tints for banners and badges.
        foreach (var key in new[] { "Good", "Warn", "Bad", "Info", "Accent" })
        {
            var c = ToColor(IsDark ? Palette[key].Dark : Palette[key].Light);
            res[key + "Tint"] = Frozen(new SolidColorBrush(Color.FromArgb(IsDark ? (byte)0x26 : (byte)0x1A, c.R, c.G, c.B)));
        }
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    public static void Watch()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && _forced == null && ReadDark() != IsDark)
                Application.Current.Dispatcher.BeginInvoke(() => Apply());
        };
    }

    public static Brush Brush(string key) => (Application.Current.Resources[key] as Brush) ?? Brushes.Gray;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark or light caption bar to match the window.</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    static bool ReadDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
