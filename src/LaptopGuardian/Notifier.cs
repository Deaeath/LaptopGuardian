using LaptopGuardian.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace LaptopGuardian;

/// <summary>Windows toast notifications with a "Show details" button that opens the right page.</summary>
internal static class Notifier
{
    /// <summary>Called (on a background thread) with the page and incident a toast was about.</summary>
    public static Action<string, string?>? Activated;

    public static void Init()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                args.TryGetValue("page", out string? page);
                args.TryGetValue("id", out string? id);
                Activated?.Invoke(string.IsNullOrEmpty(page) ? "overview" : page, string.IsNullOrEmpty(id) ? null : id);
            };
        }
        catch { }
    }

    public static bool Show(Alert alert)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument("page", alert.Page)
                .AddArgument("id", alert.IncidentId ?? "")
                .AddText(alert.Title)
                .AddText(alert.Body)
                .AddButton(new ToastButton().SetContent("Show details").AddArgument("page", alert.Page).AddArgument("id", alert.IncidentId ?? ""))
                .AddButton(new ToastButtonDismiss("Dismiss"));
            builder.Show(t =>
            {
                t.Group = "laptopguardian";
                t.Tag = alert.Kind.ToString(); // a newer alert of the same kind replaces the older one
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
