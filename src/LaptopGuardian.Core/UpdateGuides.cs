using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LaptopGuardian.Core;

public sealed record InstalledApp(string Name, string AppId);

/// <summary>Finds and opens the apps people update drivers with, by asking Windows what is in the Start menu.</summary>
public static class InstalledApps
{
    /// <summary>Everything Windows lists under Start: Store apps and ordinary programs alike.</summary>
    public static List<InstalledApp> Read()
    {
        var list = new List<InstalledApp>();
        // The shell's folder objects expect a single-threaded apartment.
        var t = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic items = shell.NameSpace("shell:AppsFolder").Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string name = item.Name;
                    string id = item.Path;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) list.Add(new InstalledApp(name, id));
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        })
        { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(TimeSpan.FromSeconds(15));
        return list;
    }

    public static InstalledApp? First(IEnumerable<InstalledApp> apps, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = apps.FirstOrDefault(a => Regex.IsMatch(a.Name, $@"^{Regex.Escape(name)}\b", RegexOptions.IgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Opens an app by the id Windows gave it.</summary>
    public static void Open(string appId) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = false });
}

/// <summary>Addresses checked against the live sites when written.</summary>
public static class DriverPages
{
    public const string Nvidia = "https://www.nvidia.com/en-us/drivers/";
    public const string NvidiaApp = "https://www.nvidia.com/en-us/software/nvidia-app/";
    public const string Amd = "https://www.amd.com/en/support/download/drivers.html";
    public const string AmdRoot = "https://www.amd.com/en/support/downloads/drivers.html";
    public const string Intel = "https://www.intel.com/content/www/us/en/support/detect.html";
    public const string RecoveryKey = "https://account.microsoft.com/devices/recoverykey";
    public const string OptionalUpdates = "ms-settings:windowsupdate-optionalupdates";
}

public sealed record GuideAction(string Label, string Kind, string Target, bool Primary = false)
{
    public const string App = "app", Link = "link", Apply = "apply";
}

/// <summary>One thing that can be updated: where it stands, what to do, and buttons that do as much of it as possible.</summary>
public sealed record UpdateGuide(string Id, string Title, string Status, bool UpdateAvailable, IReadOnlyList<string> Steps, IReadOnlyList<GuideAction> Actions);

public static class UpdateGuides
{
    sealed record Maker(string Match, string[] Apps, string AppSteps, string SupportUrl);

    static readonly Maker[] Makers =
    {
        new("ASUS", new[] { "MyASUS", "Armoury Crate" }, "Look for the update section (Customer Support > LiveUpdate in MyASUS, Update Center in Armoury Crate) and install the BIOS update it offers.", "https://www.asus.com/support/download-center/"),
        new("HP", new[] { "HP Support Assistant" }, "Choose Updates, check for updates, and install the BIOS update it offers.", "https://support.hp.com/us-en/drivers"),
        new("Dell", new[] { "Dell Command | Update", "Dell Update", "SupportAssist" }, "Check for updates and install the BIOS update it offers.", "https://www.dell.com/support/home"),
        new("Lenovo", new[] { "Lenovo Vantage", "Lenovo Commercial Vantage", "System Update" }, "Open System Update, check for updates, and install the BIOS update it offers.", "https://pcsupport.lenovo.com/"),
        new("Acer", new[] { "Acer Care Center", "AcerCare" }, "Check for updates and install the BIOS update it offers.", "https://www.acer.com/us-en/support/drivers-and-manuals"),
        new("Micro-Star", new[] { "MSI Center", "Dragon Center" }, "Check for updates and install the BIOS update it offers.", "https://www.msi.com/support/download"),
        new("MSI", new[] { "MSI Center", "Dragon Center" }, "Check for updates and install the BIOS update it offers.", "https://www.msi.com/support/download"),
    };

    public static List<UpdateGuide> Build(SystemSnapshot sys, DriverReport? d)
    {
        var guides = new List<UpdateGuide>();
        if (d == null) return guides;
        var nvidia = sys.Adapters.FirstOrDefault(a => a.Vendor == "NVIDIA");
        var amd = sys.Adapters.FirstOrDefault(a => a.Vendor == "AMD");
        var intel = sys.Adapters.FirstOrDefault(a => a.Vendor == "Intel");
        if (nvidia != null) guides.Add(Nvidia(nvidia, d));
        if (amd != null) guides.Add(Amd(amd, d));
        if (intel != null) guides.Add(Intel(intel, d));
        guides.Add(Bios(sys, d));
        guides.Add(WindowsUpdate(d));
        return guides.OrderByDescending(g => g.UpdateAvailable).ToList();
    }

    static UpdateGuide Nvidia(GraphicsAdapter gpu, DriverReport d)
    {
        var app = InstalledApps.First(d.Apps, "NVIDIA App", "GeForce Experience");
        var update = d.Updates.FirstOrDefault(u => u.Source == "NVIDIA");
        var actions = new List<GuideAction>();
        if (app != null) actions.Add(new($"Open {app.Name}", GuideAction.App, app.AppId, Primary: update != null));
        if (update != null)
        {
            string file = update.DownloadUrl ?? update.Url ?? DriverPages.Nvidia;
            actions.Add(new($"Download {update.Available}", GuideAction.Link, file, Primary: app == null));
            if (update.Url != null) actions.Add(new("What's new", GuideAction.Link, update.Url));
            if (app == null) actions.Add(new("Get the NVIDIA App", GuideAction.Link, DriverPages.NvidiaApp));
            var steps = new List<string> { "Close any games first." };
            if (app != null)
            {
                steps.Add($"Click Open {app.Name}, then go to Drivers.");
                steps.Add($"Click Download next to {update.Available}. When it is ready, click Install and choose Express.");
            }
            else
            {
                steps.Add($"Click Download {update.Available}. It is a large file, and it goes to your Downloads folder.");
                steps.Add("Run the file and choose Express.");
            }
            steps.Add("If you have had graphics errors, choose Custom instead and tick Perform a clean installation.");
            steps.Add("The screen may flicker or go black for a moment during the install. That is normal.");
            return new UpdateGuide("nvidia", $"{gpu.Name} driver", $"You have {update.Installed}. NVIDIA has {update.Available}{(update.Released is DateTime r ? $", released {r:MMM d, yyyy}" : "")}.", true, steps, actions);
        }
        string status = d.NvidiaLatest != null ? $"You have {gpu.FriendlyVersion}, the newest NVIDIA lists." : $"You have {gpu.FriendlyVersion}. NVIDIA's list could not be read just now.";
        if (d.NvidiaLatest == null) actions.Add(new("NVIDIA's driver page", GuideAction.Link, DriverPages.Nvidia));
        return new UpdateGuide("nvidia", $"{gpu.Name} driver", status, false, Array.Empty<string>(), actions);
    }

    static UpdateGuide Amd(GraphicsAdapter gpu, DriverReport d)
    {
        var app = InstalledApps.First(d.Apps, "AMD Software");
        var update = d.Updates.FirstOrDefault(u => u.Source == "AMD");
        string page = d.AmdPageUrl ?? DriverPages.Amd;
        var actions = new List<GuideAction>();
        if (app != null) actions.Add(new($"Open {app.Name}", GuideAction.App, app.AppId, Primary: update != null));
        actions.Add(new("AMD's driver page", GuideAction.Link, page, Primary: update != null && app == null));

        string title = $"{gpu.Name} driver";
        if (update != null)
        {
            var steps = new List<string> { "Close any games first." };
            if (app != null)
            {
                steps.Add($"Click Open {app.Name}.");
                steps.Add("Find Check for Updates (on the Home page, under Drivers & Software, or in Settings > System) and let it check.");
                steps.Add($"Download {update.Available} and choose Express Install. Restart if it asks.");
            }
            else
            {
                steps.Add("Click AMD's driver page and download AMD Software for your product.");
                steps.Add("Run the file and choose Express Install. Restart if it asks.");
            }
            steps.Add("The screen may flicker or go black for a moment during the install. That is normal.");
            steps.Add("If the installer says your system is not supported, your laptop maker customised this driver: get it from the maker's support page instead.");
            return new UpdateGuide("amd", title, $"You have AMD Software {update.Installed}. AMD lists {update.Available}.", true, steps, actions);
        }
        string status = gpu.FriendlyVersion == null
            ? $"Driver {gpu.DriverVersion} came from your laptop's maker, so there is no AMD Software version to compare."
            : d.AmdLatest != null ? $"You have AMD Software {gpu.FriendlyVersion}, the newest AMD lists."
            : $"You have AMD Software {gpu.FriendlyVersion}. AMD's site could not be read just now.";
        return new UpdateGuide("amd", title, status, false, Array.Empty<string>(), actions);
    }

    static UpdateGuide Intel(GraphicsAdapter gpu, DriverReport d)
    {
        var app = InstalledApps.First(d.Apps, "Intel Driver & Support Assistant", "Intel® Driver & Support Assistant");
        var actions = new List<GuideAction>();
        if (app != null) actions.Add(new($"Open {app.Name}", GuideAction.App, app.AppId, Primary: true));
        actions.Add(new("Intel's driver checker", GuideAction.Link, DriverPages.Intel, Primary: app == null));
        return new UpdateGuide("intel", $"{gpu.Name} driver",
            $"You have {gpu.DriverVersion}. Intel's checker compares it for you, and laptop makers sometimes customise this driver.", false,
            Array.Empty<string>(), actions);
    }

    static UpdateGuide Bios(SystemSnapshot sys, DriverReport d)
    {
        var maker = Makers.FirstOrDefault(m => sys.Manufacturer.Contains(m.Match, StringComparison.OrdinalIgnoreCase));
        var app = maker == null ? null : InstalledApps.First(d.Apps, maker.Apps);
        var update = d.Updates.FirstOrDefault(u => u.Key.StartsWith("bios:", StringComparison.Ordinal));
        string name = sys.Maker.Length > 0 ? sys.Maker : "your laptop's maker";

        if (d.BiosIsLatest)
            return new UpdateGuide("bios", "BIOS", $"You have {sys.BiosVersion}, the newest {name} lists for this model.", false, Array.Empty<string>(), Array.Empty<GuideAction>());

        var actions = new List<GuideAction>();
        if (update?.Url != null) actions.Add(new($"Download BIOS {update.Available}", GuideAction.Link, update.Url, Primary: true));
        if (app != null) actions.Add(new($"Open {app.Name}", GuideAction.App, app.AppId, Primary: update == null));
        string search = maker?.SupportUrl ?? "https://www.bing.com/search?q=" + Uri.EscapeDataString($"{sys.Manufacturer} {sys.ShortModel} BIOS download");
        actions.Add(new($"{name} support", GuideAction.Link, search, Primary: update == null && app == null));

        var steps = new List<string>
        {
            "Plug in the charger and close your apps.",
            "If this PC uses BitLocker, make sure you can get your recovery key first: a BIOS update can make Windows ask for it.",
        };
        if (update != null) steps.Add($"Click Download BIOS {update.Available} and run the file.");
        else if (app != null) { steps.Add($"Click Open {app.Name}."); steps.Add(maker!.AppSteps); }
        else steps.Add($"Click {name} support, find your model ({sys.ShortModel}) and download its newest BIOS.");
        steps.Add("The laptop restarts during the update, and the screen or fans can act oddly for a few minutes. Do not turn it off.");
        actions.Add(new("Find my BitLocker key", GuideAction.Link, DriverPages.RecoveryKey));

        string status = update != null
            ? $"You have {update.Installed}. {name} has {update.Available}{(update.Released is DateTime r ? $", released {r:MMM d, yyyy}" : "")}."
            : $"You have {sys.BiosVersion}{(sys.BiosDate is DateTime b ? $" from {b:MMMM yyyy}" : "")}. LaptopGuardian cannot read {name}'s list, so check there.";
        return new UpdateGuide("bios", "BIOS", status, update != null, steps, actions);
    }

    static UpdateGuide WindowsUpdate(DriverReport d)
    {
        var wu = d.Updates.Where(u => u.Source == "Windows Update").ToList();
        var page = new GuideAction("Open Windows Update", GuideAction.Link, DriverPages.OptionalUpdates);
        if (wu.Count == 0)
            return new UpdateGuide("windows-update", "Windows Update drivers", "No driver updates are waiting.", false, Array.Empty<string>(), new[] { page });
        return new UpdateGuide("windows-update", "Windows Update drivers", $"{Fmt.Plural(wu.Count, "update is", "updates are")} waiting: {string.Join("; ", wu.Take(3).Select(u => u.Title))}{(wu.Count > 3 ? "; and more" : "")}.", true,
            new[] { "Click Install all. Windows asks for permission, then downloads and installs them.", "Restart if it asks. To choose them one at a time, use Open Windows Update." },
            new[] { new GuideAction("Install all", GuideAction.Apply, DriverChecks.ActionInstallWindowsUpdateDrivers, Primary: true), page });
    }
}
