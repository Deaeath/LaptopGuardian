using System.Xml.Linq;
using LaptopGuardian.Core;

namespace LaptopGuardian.Tests;

public class DriverTests
{
    [Theory]
    [InlineData("621.14", "616.56", true)]
    [InlineData("616.56", "616.56", false)]
    [InlineData("600.10", "616.56", false)]
    [InlineData("621.14", null, true)]
    [InlineData("garbage", "616.56", false)]
    public void Compares_driver_versions(string available, string? installed, bool newer) =>
        Assert.Equal(newer, DriverChecks.IsNewer(available, installed));

    [Fact]
    public void Finds_the_card_in_nvidias_product_list_with_or_without_the_nvidia_prefix()
    {
        var list = XDocument.Parse("""
            <LookupValueSearch><LookupValues>
              <LookupValue ParentID="129"><Name>GeForce RTX 4070 Laptop GPU</Name><Value>1006</Value></LookupValue>
              <LookupValue ParentID="133"><Name>NVIDIA GeForce RTX 5070 Laptop GPU</Name><Value>1077</Value></LookupValue>
            </LookupValues></LookupValueSearch>
            """);
        Assert.Equal(("1006", "129"), DriverChecks.FindNvidiaProduct(list, "NVIDIA GeForce RTX 4070 Laptop GPU"));
        Assert.Equal(("1077", "133"), DriverChecks.FindNvidiaProduct(list, "GeForce RTX 5070 Laptop GPU"));
        Assert.Null(DriverChecks.FindNvidiaProduct(list, "GeForce RTX 9999"));
    }

    [Theory]
    [InlineData("ROG Strix G713PI_G713PI", "G713PI")]
    [InlineData("ASUS TUF Gaming A15 FA507NV", "FA507NV")]
    public void Reads_the_asus_model(string product, string model) => Assert.Equal(model, DriverChecks.AsusModel(product));

    [Fact]
    public void Reads_the_asus_bios_number() => Assert.Equal("336", DriverChecks.AsusBiosNumber("G713PI.336"));

    [Fact]
    public void The_bios_update_advice_was_given_when_the_newest_bios_was_already_installed()
    {
        // 4 November 2026: BIOS 336 was the newest ASUS published, but the advice went by its age alone.
        var now = new DateTimeOffset(2026, 11, 4, 5, 0, 0, TimeSpan.FromHours(-5));
        var sys = new SystemSnapshot { Manufacturer = "ASUSTeK COMPUTER INC.", Model = "ROG Strix G713PI_G713PI", BiosVersion = "G713PI.336", BiosDate = new DateTime(2025, 10, 1) };
        var crash = new Incident { Id = "c", Kind = IncidentKind.UnexpectedShutdown, Time = now.AddDays(-1), Severity = Severity.Critical, Crash = new CrashInfo { Cause = CrashCause.Unknown } };

        var current = new Findings { Now = now, System = sys, Incidents = new[] { crash }, Drivers = new DriverReport { LatestBios = "336", BiosIsLatest = true } };
        Assert.DoesNotContain(Advisor.Advise(current), r => r.Id == "bios");

        var update = new DriverUpdate("bios:340", "ASUS", "BIOS update", "336", "340", null, "https://example.test/bios.exe", null, Severity.Warning);
        var behind = current with { Drivers = new DriverReport { LatestBios = "340", Updates = new() { update } } };
        Assert.Equal(Advisor.ActionShowDrivers, Advisor.Advise(behind).Single(r => r.Id == "bios").ActionId);
    }

    [Theory]
    [InlineData("AMD Radeon(TM) 610M", "AMD Ryzen 9 7945HX with Radeon Graphics", "/processors/ryzen/ryzen-7000-series/amd-ryzen-9-7945hx.html")]
    [InlineData("AMD Radeon RX 7900 XTX", "Intel(R) Core(TM) i9", "/graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7900-xtx.html")]
    [InlineData("AMD Radeon RX 7600", "AMD Ryzen 7 7800X3D", "/graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7600.html")]
    public void Works_out_amds_page_for_a_product(string gpu, string cpu, string tail) =>
        Assert.Equal(DriverPages.AmdRoot + tail, DriverChecks.AmdProductPage(gpu, cpu));

    [Fact]
    public void An_unknown_amd_product_has_no_page_of_its_own() =>
        Assert.Null(DriverChecks.AmdProductPage("AMD FirePro W9100", "Intel(R) Core(TM) i7"));

    [Fact]
    public void Reads_the_newest_version_from_amds_page() =>
        Assert.Equal("26.10.2", DriverChecks.LatestAdrenalin(
            "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.1-win11-a.exe x whql-amd-software-adrenalin-edition-26.10.2-win11-a.exe"));

    static SystemSnapshot Laptop(string maker = "ASUSTeK COMPUTER INC.") => new()
    {
        Manufacturer = maker, Model = "Some Laptop_X1", BiosVersion = "X1.336",
        Adapters = new()
        {
            new GraphicsAdapter("NVIDIA GeForce RTX 4070 Laptop GPU", "32.0.16.1656", "616.56", null, 8, "NVIDIA"),
            new GraphicsAdapter("AMD Radeon(TM) 610M", "32.0.21045.16010", "26.10.1", null, 0.5, "AMD"),
        },
    };

    [Fact]
    public void An_nvidia_update_opens_the_app_when_it_is_installed_and_links_the_file_either_way()
    {
        var update = new DriverUpdate("nvidia:621.14", "NVIDIA", "NVIDIA Game Ready driver", "616.56", "621.14", null, "https://example.test/details", null, Severity.Warning, "https://example.test/621.14.exe");
        var withApp = UpdateGuides.Build(Laptop(), new DriverReport { Updates = new() { update }, Apps = new() { new InstalledApp("NVIDIA App", "com.nvidia.nvapp") } }).Single(g => g.Id == "nvidia");
        Assert.True(withApp.UpdateAvailable);
        Assert.Equal(new GuideAction("Open NVIDIA App", GuideAction.App, "com.nvidia.nvapp", true), withApp.Actions[0]);
        Assert.Contains(withApp.Actions, a => a.Target == "https://example.test/621.14.exe");
        Assert.Contains(withApp.Steps, s => s.Contains("Open NVIDIA App"));

        var noApp = UpdateGuides.Build(Laptop(), new DriverReport { Updates = new() { update } }).Single(g => g.Id == "nvidia");
        Assert.True(noApp.Actions.Single(a => a.Target == "https://example.test/621.14.exe").Primary);
        Assert.DoesNotContain(noApp.Steps, s => s.Contains("Open NVIDIA App"));
    }

    [Fact]
    public void An_amd_update_opens_amd_software_and_its_exact_page()
    {
        var update = new DriverUpdate("amd:26.10.2", "AMD", "AMD Software driver", "26.10.1", "26.10.2", null, "https://example.test/amd", null, Severity.Warning);
        var report = new DriverReport { Updates = new() { update }, AmdLatest = "26.10.2", AmdPageUrl = "https://example.test/amd", Apps = new() { new InstalledApp("AMD Software", "Amd.App!Id") } };
        var guide = UpdateGuides.Build(Laptop(), report).Single(g => g.Id == "amd");
        Assert.Contains("26.10.1", guide.Status);
        Assert.Contains("26.10.2", guide.Status);
        Assert.Equal("Amd.App!Id", guide.Actions[0].Target);
        Assert.True(guide.Actions[0].Primary);
        Assert.Contains(guide.Actions, a => a.Target == "https://example.test/amd");
    }

    [Fact]
    public void A_bios_update_warns_about_bitlocker_before_anything_else_is_run()
    {
        var update = new DriverUpdate("bios:340", "ASUS", "BIOS update", "336", "340", null, "https://example.test/bios.exe", null, Severity.Warning);
        var guide = UpdateGuides.Build(Laptop(), new DriverReport { Updates = new() { update } }).Single(g => g.Id == "bios");
        Assert.Equal("https://example.test/bios.exe", guide.Actions.First(a => a.Primary).Target);
        int bitlocker = guide.Steps.ToList().FindIndex(s => s.Contains("BitLocker"));
        int run = guide.Steps.ToList().FindIndex(s => s.Contains("Download BIOS"));
        Assert.InRange(bitlocker, 0, run - 1);
        Assert.Contains(guide.Actions, a => a.Target == DriverPages.RecoveryKey);
    }

    [Fact]
    public void The_newest_bios_needs_no_steps() =>
        Assert.Empty(UpdateGuides.Build(Laptop(), new DriverReport { BiosIsLatest = true, LatestBios = "336" }).Single(g => g.Id == "bios").Steps);

    [Fact]
    public void Another_makers_laptop_is_sent_to_that_makers_support_page()
    {
        var guide = UpdateGuides.Build(Laptop("HP Inc."), new DriverReport()).Single(g => g.Id == "bios");
        Assert.Contains(guide.Actions, a => a.Target.StartsWith("https://support.hp.com"));
        Assert.Contains("HP", guide.Status);
    }

    [Fact]
    public void Update_app_names_match_on_whole_words() =>
        Assert.Null(InstalledApps.First(new[] { new InstalledApp("AMD Software Removal Tool Helperish", "x"), new InstalledApp("NVIDIA Control Panel", "y") }, "NVIDIA App"));

    [Fact]
    public void A_missing_driver_is_explained() => Assert.Contains("No driver", DriverChecks.ProblemMeaning(28));
}

public class AppUpdaterTests
{
    [Theory]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("2.1", "2.1.0")]
    [InlineData("V3.0.1-beta", "3.0.1")]
    public void Reads_release_tags(string tag, string version) => Assert.Equal(Version.Parse(version), AppUpdater.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void Ignores_tags_that_are_not_versions(string? tag) => Assert.Null(AppUpdater.ParseTag(tag));

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\LaptopGuardian\LaptopGuardian.exe", true)]
    [InlineData(@"C:\_LaptopGuardian\src\LaptopGuardian\bin\Debug\LaptopGuardian.exe", false)]
    [InlineData(@"C:\Downloads\LaptopGuardian (1).exe", false)]
    [InlineData(null, false)]
    public void Only_an_installed_copy_updates_itself(string? path, bool can) => Assert.Equal(can, AppUpdater.CanSelfUpdate(path));
}
