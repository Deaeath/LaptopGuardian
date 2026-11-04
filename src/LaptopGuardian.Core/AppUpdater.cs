using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LaptopGuardian.Core;

public sealed record AppRelease(Version Version, string Tag, string ExeUrl, string? ShaUrl, string PageUrl, string Notes);

/// <summary>
/// Keeps LaptopGuardian up to date from its GitHub releases. A release carries LaptopGuardian.exe and
/// LaptopGuardian.exe.sha256; the download is checked against the hash before it replaces anything.
/// Replacing works because Windows lets a running .exe be renamed: the old one steps aside as
/// LaptopGuardian.exe.old, the new one takes its name, starts, and the old copy is deleted on the next start.
/// </summary>
public static class AppUpdater
{
    public const string Repo = "Deaeath/LaptopGuardian";
    public const string ExeName = "LaptopGuardian.exe";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"LaptopGuardian-updater/{Current}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>"v2.1.0" or "2.1" -> 2.1.0.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim().TrimStart('v', 'V');
        int dash = tag.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) tag = tag[..dash];
        if (!Version.TryParse(tag.Contains('.') ? tag : tag + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Only an installed copy updates itself; a development build never does.</summary>
    public static bool CanSelfUpdate(string? exePath) =>
        exePath != null &&
        !exePath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(exePath).Equals(ExeName, StringComparison.OrdinalIgnoreCase);

    public static async Task<AppRelease?> LatestAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag);
        if (version == null) return null;
        string? exe = null, sha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)) exe = url;
            else if (name.Equals(ExeName + ".sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (exe == null) return null;
        return new AppRelease(version, tag, exe, sha, root.GetProperty("html_url").GetString() ?? ReleasesPage,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
    }

    /// <summary>Downloads the release next to the running copy and checks its hash. Returns the staged path.</summary>
    public static async Task<string> DownloadAsync(AppRelease release, string exePath, CancellationToken ct = default)
    {
        if (release.ShaUrl == null) throw new InvalidOperationException("The release has no checksum, so it was not installed.");
        string expected = (await Http.GetStringAsync(release.ShaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
        if (expected.Length != 64) throw new InvalidOperationException("The release's checksum file is not a SHA-256 hash.");

        string staged = exePath + ".new";
        await using (var src = await Http.GetStreamAsync(release.ExeUrl, ct))
        await using (var dst = File.Create(staged))
            await src.CopyToAsync(dst, ct);

        string actual;
        await using (var f = File.OpenRead(staged))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(staged);
            throw new InvalidOperationException("The download did not match the release's checksum, so it was thrown away.");
        }
        return staged;
    }

    /// <summary>Swaps the staged copy in and starts it. The caller then exits.</summary>
    public static void ApplyAndRestart(string staged, string exePath, string fromVersion)
    {
        string old = exePath + ".old";
        try { if (File.Exists(old)) File.Delete(old); } catch { }
        File.Move(exePath, old);
        try { File.Move(staged, exePath); }
        catch { File.Move(old, exePath); throw; }
        Process.Start(new ProcessStartInfo(exePath, $"--background --updated-from {fromVersion}") { UseShellExecute = false });
    }

    /// <summary>Removes the copy an update replaced (it could not be deleted while it was running).</summary>
    public static void CleanUp(string? exePath)
    {
        if (exePath == null) return;
        foreach (var leftover in new[] { exePath + ".old", exePath + ".new" })
            for (int i = 0; i < 10 && File.Exists(leftover); i++)
            {
                try { File.Delete(leftover); } catch { Thread.Sleep(500); }
            }
    }
}
