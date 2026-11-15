using LaptopGuardian.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace LaptopGuardian;

/// <summary>Windows toast notifications with a "Show details" button that opens the right page.</summary>
internal static class Notifier
{
    /// <summary>Called (on a background thread) with the page and incident a toast was about.</summary>
    public static Action<string, string?>? Activated;
    /// <summary>Called (on a background thread) when the toast's Dismiss button is pressed, with the incident it was about.</summary>
    public static Action<string>? DismissRequested;

    public static void Init()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                if (args.TryGetValue("action", out string? action) && action == "dismiss")
                {
                    if (args.TryGetValue("id", out string? dismissId) && !string.IsNullOrEmpty(dismissId)) DismissRequested?.Invoke(dismissId);
                    return;
                }
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
                .AddButton(new ToastButton().SetContent("Show details").AddArgument("page", alert.Page).AddArgument("id", alert.IncidentId ?? ""));
            // About something in the history: Dismiss there too, quietly. Otherwise it just closes the toast.
            if (!string.IsNullOrEmpty(alert.IncidentId))
                builder.AddButton(new ToastButton().SetContent("Dismiss").AddArgument("action", "dismiss").AddArgument("id", alert.IncidentId).SetBackgroundActivation());
            else
                builder.AddButton(new ToastButtonDismiss("Dismiss"));
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
