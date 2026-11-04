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
        Assert.Equal("https://example.test/bios.exe", Advisor.Advise(behind).Single(r => r.Id == "bios").LinkUrl);
    }

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
