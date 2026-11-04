using System.Xml.Linq;
using LaptopGuardian.Core;

namespace LaptopGuardian.Tests;

public class WerReportsTests
{
    static WerEntry Entry(string id, string code, DateTimeOffset t, string dump) =>
        new(t, "LiveKernelEvent", code, id, "2051", $"\\\\?\\C:\\WINDOWS\\LiveKernelReports\\WATCHDOG\\{dump}\n\\\\?\\C:\\WINDOWS\\SystemTemp\\x.xml",
            $"\\\\?\\C:\\ProgramData\\Microsoft\\Windows\\WER\\ReportQueue\\Kernel_{code}_abc_{id}");

    [Fact]
    public void Thousands_of_retries_are_four_reports()
    {
        // As seen on 4 November 2026: four reports from September, each re-logged every 4 hours for six weeks.
        var start = new DateTimeOffset(2026, 9, 22, 2, 42, 0, TimeSpan.FromHours(-4));
        var entries = new List<WerEntry>();
        string[] ids = { "97495469", "6788a5dc", "3bab8715", "c6bf1327" };
        string[] codes = { "141", "193", "193", "193" };
        for (int r = 0; r < 4; r++)
            for (int i = 0; i < 250; i++)
                entries.Add(Entry(ids[r], codes[r], start.AddDays(r * 3).AddHours(4 * i), $"WATCHDOG-202609{21 + r}-1718.dmp"));

        var reports = WerReports.Group(entries);
        Assert.Equal(4, reports.Count);
        Assert.All(reports, r => Assert.True(r.Stuck));
        Assert.All(reports, r => Assert.Equal(250, r.TimesLogged));
        var tdr = reports.Single(r => r.Code == "141");
        Assert.Equal("VIDEO_ENGINE_TIMEOUT_DETECTED", tdr.CodeName);
        Assert.Equal(new DateTime(2026, 9, 21, 17, 18, 0), tdr.CapturedAt!.Value.DateTime);
        Assert.StartsWith(@"C:\ProgramData\", tdr.QueueFolder);
        Assert.Single(tdr.DumpFiles);
    }

    [Fact]
    public void A_report_logged_once_is_not_stuck()
    {
        var reports = WerReports.Group(new[] { Entry("a", "141", DateTimeOffset.Now, "WATCHDOG-20261104-0300.dmp") });
        Assert.False(reports.Single().Stuck);
    }

    [Fact]
    public void Clear_never_touches_paths_outside_windows_report_folders()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "lg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string victim = Path.Combine(tmp, "keep.dmp");
        File.WriteAllText(victim, "x");
        try
        {
            var r = new KernelReport { ReportId = "x", QueueFolder = tmp, DumpFiles = new() { victim } };
            var (n, files, _, _) = WerReports.Clear(new[] { r });
            Assert.Equal(0, n);
            Assert.Equal(0, files);
            Assert.True(File.Exists(victim));
        }
        finally { Directory.Delete(tmp, true); }
    }
}

public class BatteryReportTests
{
    const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <SystemInformation><ConnectedStandby>1</ConnectedStandby></SystemInformation>
          <Batteries><Battery><Id>TEST</Id><Manufacturer>TEST</Manufacturer><Chemistry>LION</Chemistry>
            <DesignCapacity>90005</DesignCapacity><FullChargeCapacity>61854</FullChargeCapacity><CycleCount>0</CycleCount></Battery></Batteries>
          <RecentUsage>
            <UsageEntry Timestamp="2026-11-04T02:31:09Z" LocalTimestamp="2026-11-03T21:31:09" Ac="1" EntryType="Active" ChargeCapacity="53813" FullChargeCapacity="61854" />
            <UsageEntry Timestamp="2026-11-04T01:39:00Z" LocalTimestamp="2026-11-03T20:39:00" Ac="1" EntryType="Suspend" ChargeCapacity="53813" FullChargeCapacity="61854" />
            <UsageEntry Timestamp="2026-11-03T22:00:00Z" LocalTimestamp="2026-11-03T17:00:00" Ac="0" EntryType="Active" ChargeCapacity="60000" FullChargeCapacity="61854" />
          </RecentUsage>
        </BatteryReport>
        """;

    static BatteryReport Report() => BatteryReport.Parse(XDocument.Parse(Xml));

    [Fact]
    public void Reads_wear_and_ignores_an_unreported_cycle_count()
    {
        var b = Report();
        Assert.Equal(68.7, b.HealthPct);
        Assert.Null(b.Cycles);
        Assert.True(b.ModernStandby);
    }

    [Fact]
    public void Knows_the_laptop_was_asleep_when_it_died()
    {
        var died = new DateTimeOffset(2026, 11, 4, 2, 24, 57, TimeSpan.Zero);
        var s = Report().StateAt(died);
        Assert.NotNull(s);
        Assert.True(s!.IsAsleep);
        Assert.True(s.Ac);
        Assert.Equal(87, s.Percent);
    }

    [Fact]
    public void Says_nothing_about_times_before_the_report() =>
        Assert.Null(Report().StateAt(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));
}

public class GraphicsEventTests
{
    [Fact]
    public void Nvidia_153_reads_the_message_not_the_device_path()
    {
        var d = GraphicsEvents.Decode("nvlddmkm", 153, 2, new[] { @"\Device\Video3", "Error occurred on GPUID: 100", "0", "0" });
        Assert.Equal("Graphics card reset after an error", d.Title);
        Assert.Equal("Error occurred on GPUID: 100", d.Data);
        Assert.Equal(IncidentKind.GraphicsError, d.Kind);
    }

    [Fact]
    public void Display_4101_names_the_driver()
    {
        var d = GraphicsEvents.Decode("Display", 4101, 3, new[] { "nvlddmkm", "" });
        Assert.Contains("nvlddmkm", d.Summary);
    }

    [Fact]
    public void Corrected_hardware_errors_are_information_only() =>
        Assert.Equal(Severity.Info, GraphicsEvents.Decode(GraphicsEvents.WheaProvider, 17, 3, Array.Empty<string>()).Severity);

    [Fact]
    public void The_query_selects_every_graphics_provider_and_errors_only()
    {
        var x = GraphicsEvents.XPath(TimeSpan.FromDays(1));
        foreach (var p in GraphicsEvents.GraphicsProviders) Assert.Contains($"@Name='{p}'", x);
        Assert.Contains("Level=2", x);
        Assert.Contains("timediff", x);
    }

    [Theory]
    [InlineData("32.0.16.1656", "616.56")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("garbage", null)]
    public void Converts_windows_driver_versions_to_nvidia_versions(string windows, string? nvidia) =>
        Assert.Equal(nvidia, SystemInfo.NvidiaVersion(windows));
}
