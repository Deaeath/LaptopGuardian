using System.IO;
using System.Reflection;
using System.Text.Json;
using LaptopGuardian.Core;

namespace LaptopGuardian;

/// <summary>Command-line modes: a full diagnosis without the window, and writing the app icon.</summary>
internal static class Cli
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "2.0";

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// <c>LaptopGuardian.exe --diagnose [--out report.md]</c>: scans everything (in memory, without touching the
    /// running app's journal) and writes the Markdown report plus a JSON copy of the findings next to it.
    /// </summary>
    public static int Diagnose(string[] args)
    {
        string outPath = Arg(args, "--out") ?? Path.Combine(Guardian.DefaultDataDir, "diagnosis.md");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var g = new Guardian(dataDir: null);
            long setup = sw.ElapsedMilliseconds; sw.Restart();
            g.Gpu.SampleOnce();
            long firstSample = sw.ElapsedMilliseconds;
            Thread.Sleep(1200);
            sw.Restart();
            g.Gpu.SampleOnce();
            long secondSample = sw.ElapsedMilliseconds; sw.Restart();
            g.Scan();
            g.CheckDriversAsync().GetAwaiter().GetResult();
            var f = g.Findings();
            var timings = new Dictionary<string, long>(g.ScanTimings)
            {
                ["setup"] = setup, ["first gpu sample"] = firstSample, ["second gpu sample"] = secondSample, ["scan total"] = sw.ElapsedMilliseconds,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, ReportBuilder.Markdown(f, g.Recommendations, Version));
            var json = new
            {
                timings,
                status = Advisor.Status(f, g.Recommendations),
                f.System,
                f.Power,
                battery = f.Battery == null ? null : new { f.Battery.HealthPct, f.Battery.DesignMWh, f.Battery.FullMWh, f.Battery.Cycles, usage = f.Battery.Usage },
                f.Gpu,
                recommendations = g.Recommendations,
                incidents = f.Incidents,
                kernelReports = f.KernelReports,
            };
            File.WriteAllText(Path.ChangeExtension(outPath, ".json"), JsonSerializer.Serialize(json, new JsonSerializerOptions(JsonFile.Options) { IncludeFields = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, "Diagnosis failed: " + ex);
            return 1;
        }
    }

    public static int WriteIcon(string[] args)
    {
        string path = Arg(args, "--write-icon") ?? "LaptopGuardian.ico";
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var png = IconFactory.Render(128, IconFactory.Calm);
            png.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }
        File.WriteAllBytes(path, IconFactory.Ico(IconFactory.Calm, 16, 20, 24, 32, 40, 48, 64, 128, 256));
        return 0;
    }
}
