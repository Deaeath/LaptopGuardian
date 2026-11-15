using LaptopGuardian.Core;

namespace LaptopGuardian.Tests;

/// <summary>The charger cutting out, planted from what Windows logged on the laptop it happened to (Kernel-Power event 105).</summary>
public class ChargerTests
{
    static readonly TimeSpan Est = TimeSpan.FromHours(-5);
    static DateTimeOffset T(int month, int day, int h, int m, double s) => new DateTimeOffset(2026, month, day, h, m, 0, Est).AddSeconds(s);
    static PowerSourceChange Off(DateTimeOffset t) => new(t, false);
    static PowerSourceChange On(DateTimeOffset t) => new(t, true);

    // 15 November 2026, 5:14 to 5:30 AM. The laptop reset itself at 5:43.
    static readonly PowerSourceChange[] Nov15 =
    {
        Off(T(11, 15, 5, 14, 41.094)), On(T(11, 15, 5, 18, 22.765)),
        Off(T(11, 15, 5, 18, 44.759)), On(T(11, 15, 5, 18, 45.752)),
        Off(T(11, 15, 5, 18, 47.895)), On(T(11, 15, 5, 19, 38.353)),
        Off(T(11, 15, 5, 20, 36.743)), On(T(11, 15, 5, 23, 37.611)),
        Off(T(11, 15, 5, 23, 39.674)), Off(T(11, 15, 5, 27, 38.147)), Off(T(11, 15, 5, 29, 58.077)),
    };

    // 4 November 2026, 4:06 to 4:09 PM: six drops of a second or two, three minutes before a sudden reset.
    static readonly PowerSourceChange[] Nov4 =
    {
        Off(T(11, 4, 16, 6, 31.537)), On(T(11, 4, 16, 6, 33.476)),
        Off(T(11, 4, 16, 6, 36.822)), On(T(11, 4, 16, 6, 38.760)),
        Off(T(11, 4, 16, 7, 8.776)), On(T(11, 4, 16, 7, 10.740)),
        Off(T(11, 4, 16, 7, 33.017)), On(T(11, 4, 16, 7, 34.998)),
        Off(T(11, 4, 16, 7, 40.012)), On(T(11, 4, 16, 7, 41.016)),
        Off(T(11, 4, 16, 9, 21.765)), On(T(11, 4, 16, 9, 23.637)),
    };

    [Fact]
    public void The_charger_cut_out_five_times_before_the_5_43_reset_and_nothing_said_so()
    {
        var ep = Assert.Single(Charger.Episodes(Nov15));
        Assert.True(ep.Unstable);
        Assert.Equal(5, ep.Disconnects); // the "off" Windows logged again around two restarts is not a disconnect
        Assert.Equal(1, ep.QuickDrops);
        var inc = Charger.ToIncident(ep);
        Assert.Equal(IncidentKind.ChargerUnstable, inc.Kind);
        Assert.Equal(Severity.Warning, inc.Severity);
        Assert.Contains("5 times", inc.Summary);

        // The reset 20 minutes later says so.
        var lastActive = T(11, 15, 5, 43, 7);
        Assert.Equal(5, Charger.DisconnectsBefore(Nov15, lastActive, Charger.CrashWindow));
        var v = CrashClassifier.Classify(new CrashFacts { RestartedAt = lastActive.AddSeconds(27), LastActivity = lastActive, ChargerDropsBefore = 5 });
        Assert.Equal(CrashCause.SuddenReset, v.Cause);
        Assert.Contains("charger cut out 5 times in the hour before", v.Summary);
    }

    [Fact]
    public void Six_two_second_drops_on_4_November()
    {
        var ep = Assert.Single(Charger.Episodes(Nov4));
        Assert.True(ep.Unstable);
        Assert.Equal(6, ep.Disconnects);
        Assert.Equal(6, ep.QuickDrops);
        Assert.Contains("each time for only a few seconds", Charger.ToIncident(ep).Summary);
    }

    [Fact]
    public void Unplugging_it_is_not_a_fault()
    {
        // Unplugged for 40 minutes, then once more to move rooms: normal use, nothing recorded.
        var t = T(11, 10, 12, 0, 0);
        var changes = new[] { Off(t), On(t.AddMinutes(40)), Off(t.AddMinutes(41)), On(t.AddMinutes(45)) };
        Assert.DoesNotContain(Charger.Episodes(changes), e => e.Notable);
    }

    [Fact]
    public void A_one_second_blip_is_recorded_quietly()
    {
        var t = T(11, 3, 20, 4, 44.205);
        var ep = Assert.Single(Charger.Episodes(new[] { Off(t), On(t.AddSeconds(1.06)) }));
        Assert.True(ep.Notable);
        Assert.False(ep.Unstable);
        Assert.Equal(Severity.Info, Charger.ToIncident(ep).Severity);
    }

    [Fact]
    public void Drops_more_than_ten_minutes_apart_are_separate_episodes()
    {
        // 25 October 2026: two blips 13 minutes apart.
        var a = T(10, 25, 9, 7, 34.897);
        var b = T(10, 25, 9, 20, 18.974);
        var eps = Charger.Episodes(new[] { Off(a), On(a.AddSeconds(1.95)), Off(b), On(b.AddSeconds(0.94)) });
        Assert.Equal(2, eps.Count);
        Assert.All(eps, e => Assert.Equal(1, e.QuickDrops));
    }

    [Fact]
    public void A_crash_after_the_charger_cut_out_puts_the_charger_first()
    {
        var drop = Charger.ToIncident(Charger.Episodes(Nov15).Single());
        var crash = new Incident
        {
            Id = "crash:1", Kind = IncidentKind.UnexpectedShutdown, Time = T(11, 15, 5, 43, 7), Severity = Severity.Critical,
            Title = "Reset itself without warning", Crash = new CrashInfo { Cause = CrashCause.SuddenReset, ChargerDropsBefore = 5 },
        };
        var f = new Findings
        {
            Now = T(11, 15, 6, 0, 0), Incidents = new[] { crash, drop },
            Battery = new BatteryReport { DesignMWh = 90005, FullMWh = 61854 },
        };
        var first = Advisor.Advise(f).First();
        Assert.Equal("charger", first.Id);
        Assert.Equal(Severity.Critical, first.Severity);
        Assert.Contains("before a crash", first.Title);
        Assert.Contains("69%", first.Body); // the worn battery is part of the story
    }

    [Fact]
    public void Charger_trouble_with_no_crash_is_a_warning()
    {
        var drop = Charger.ToIncident(Charger.Episodes(Nov4).Single());
        var rec = Advisor.Advise(new Findings { Now = T(11, 5, 12, 0, 0), Incidents = new[] { drop } }).Single(r => r.Id == "charger");
        Assert.Equal(Severity.Warning, rec.Severity);
        Assert.Equal("The charger keeps cutting out", rec.Title);
    }

    [Fact]
    public void One_blip_alone_gives_no_advice_but_three_in_two_weeks_do()
    {
        var now = T(11, 5, 12, 0, 0);
        Incident Blip(int daysAgo)
        {
            var t = now.AddDays(-daysAgo);
            return Charger.ToIncident(Charger.Episodes(new[] { Off(t), On(t.AddSeconds(2)) }).Single());
        }
        Assert.DoesNotContain(Advisor.Advise(new Findings { Now = now, Incidents = new[] { Blip(1) } }), r => r.Id == "charger");
        Assert.Contains(Advisor.Advise(new Findings { Now = now, Incidents = new[] { Blip(1), Blip(4), Blip(9) } }), r => r.Id == "charger");
    }
}

/// <summary>Dismissing alerts and hiding suggestions.</summary>
public class DismissTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 15, 6, 0, 0, TimeSpan.FromHours(-5));

    static Incident Reset(DateTimeOffset at) => new()
    {
        Id = "crash:" + at.UtcTicks, Kind = IncidentKind.UnexpectedShutdown, Time = at, Severity = Severity.Critical, Title = "Reset itself without warning",
        Crash = new CrashInfo { Cause = CrashCause.SuddenReset, RestartedAt = at.AddSeconds(30) },
    };

    [Fact]
    public void Hiding_the_reset_advice_hid_it_through_four_more_resets()
    {
        // 15 November 2026: the sudden-reset advice had been hidden, and stayed hidden through resets at 7:12 PM, 2:24, 5:08 and 5:43.
        var hidden = new HiddenSuggestion { At = Now.AddHours(-12), Title = "Your laptop reset itself 8 times in two weeks" };
        var before = new Findings { Now = Now, Incidents = new[] { Reset(Now.AddHours(-13)) } };
        Assert.False(Advisor.ComesBack(Advisor.Advise(before).Single(r => r.Id == "sudden-resets"), hidden, before));

        var after = new Findings { Now = Now, Incidents = new[] { Reset(Now.AddHours(-13)), Reset(Now.AddMinutes(-17)) } };
        Assert.True(Advisor.ComesBack(Advisor.Advise(after).Single(r => r.Id == "sudden-resets"), hidden, after));
    }

    [Fact]
    public void A_hidden_driver_update_comes_back_only_for_a_newer_version()
    {
        var hidden = new HiddenSuggestion { At = Now.AddDays(-1), Title = "AMD driver 26.10.2 is available" };
        var f = new Findings { Now = Now };
        Assert.False(Advisor.ComesBack(new Recommendation("amd-driver", Severity.Info, "AMD driver 26.10.2 is available", ""), hidden, f));
        Assert.True(Advisor.ComesBack(new Recommendation("amd-driver", Severity.Info, "AMD driver 26.11.1 is available", ""), hidden, f));
        // Hidden by version 2.2, which kept no title: stays hidden.
        Assert.False(Advisor.ComesBack(new Recommendation("amd-driver", Severity.Info, "AMD driver 26.11.1 is available", ""), new HiddenSuggestion { At = Now }, f));
    }

    [Fact]
    public void The_version_two_hidden_list_carries_over_once()
    {
        var s = new AppState { DismissedRecommendations = { "sleep-deaths", "amd-driver" } };
        Assert.True(s.MigrateHidden(Now));
        Assert.Empty(s.DismissedRecommendations);
        Assert.Equal(Now, s.HiddenSuggestions["amd-driver"].At);
        Assert.False(s.MigrateHidden(Now));
    }

    [Fact]
    public void Dismissed_alerts_calm_the_status_but_keep_the_facts()
    {
        var a = Reset(Now.AddHours(-3));
        var b = Reset(Now.AddHours(-1));
        var f = new Findings { Now = Now, Incidents = new[] { a, b } };
        Assert.Equal(Severity.Critical, Advisor.Status(f, Array.Empty<Recommendation>()).Level);

        a.DismissedAt = Now;
        Assert.Equal(Severity.Critical, Advisor.Status(f, Array.Empty<Recommendation>()).Level); // one is still open

        b.DismissedAt = Now;
        var (level, _, summary) = Advisor.Status(f, Array.Empty<Recommendation>());
        Assert.Equal(Severity.Warning, level);
        Assert.Contains("2 unexpected shutdowns this week", summary);
        Assert.Contains("dismissed", summary);
    }

    [Fact]
    public void A_dismissal_survives_a_restart_and_a_rescan_and_can_be_undone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lg-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "journal.json");
        try
        {
            var crash = Reset(Now);
            var j = new Journal(path);
            j.Add(crash);
            Assert.Equal(1, j.SetDismissed(new[] { crash.Id }, true, Now));
            Assert.Equal(0, j.SetDismissed(new[] { crash.Id }, true, Now));

            // Working the crash out again (as a new version of the reasoning does) keeps the dismissal.
            j.Upsert(Reset(Now));
            j.Save();
            var again = new Journal(path);
            Assert.Equal(Now, again.Find(crash.Id)!.DismissedAt);

            again.SetDismissed(new[] { crash.Id }, false);
            Assert.False(new Journal(path).Find(crash.Id)!.Dismissed);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
