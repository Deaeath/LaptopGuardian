using LaptopGuardian.Core;

namespace LaptopGuardian.Tests;

/// <summary>Heat, planted from the flight recorder of the laptop it happened to (15 November 2026).</summary>
public class HeatTests
{
    static readonly TimeSpan Est = TimeSpan.FromHours(-5);
    static DateTimeOffset T(int h, int m, int s) => new(2026, 11, 15, h, m, s, Est);
    static FlightSample S(int h, int m, int s, double zone, double busy, double speed, string? mode = null) =>
        new() { At = T(h, m, s), ZoneC = zone, CpuBusyPct = busy, CpuSpeedPct = speed, OnAc = true, PowerMode = mode };

    // 7:42-7:45 AM on "Best performance": the processor 2-6% busy, held at ~96% of its top speed, the sensor at 85-94°C.
    static readonly FlightSample[] BestPerformanceIdle =
    {
        S(7,42,3, 94, 4, 92), S(7,42,13, 93, 4, 94), S(7,42,24, 88, 3, 98), S(7,42,34, 90, 3, 98), S(7,42,44, 92, 6, 98), S(7,42,54, 84, 3, 95),
        S(7,43,3, 88, 3, 97), S(7,43,14, 94, 6, 96), S(7,43,24, 87, 2, 97), S(7,43,33, 87, 3, 95), S(7,43,44, 88, 3, 97), S(7,43,54, 95, 10, 90),
        S(7,44,4, 91, 4, 98), S(7,44,13, 83, 4, 98), S(7,44,24, 77, 2, 97), S(7,44,34, 87, 2, 94), S(7,44,44, 88, 6, 98), S(7,44,54, 84, 2, 98),
        S(7,45,4, 85, 4, 89), S(7,45,14, 88, 5, 98), S(7,45,24, 88, 2, 97), S(7,45,34, 88, 4, 99), S(7,45,44, 83, 3, 98), S(7,45,54, 75, 1, 98),
    };

    // 7:55 AM, minutes after switching to "Best power efficiency".
    static readonly FlightSample[] EfficiencyIdle =
    {
        S(7,55,4, 61, 3, 75), S(7,55,14, 58, 3, 79), S(7,55,24, 68, 5, 83), S(7,55,34, 71, 8, 85), S(7,55,44, 97, 25, 37),
    };

    [Fact]
    public void Power_modes_are_read_from_windows_overlay_ids()
    {
        Assert.Equal(WindowsPowerMode.BestPowerEfficiency, PowerMode.FromGuid(new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")));
        Assert.Equal(WindowsPowerMode.Balanced, PowerMode.FromGuid(Guid.Empty));
        Assert.Equal(WindowsPowerMode.BestPerformance, PowerMode.FromGuid(new Guid("ded574b5-45a0-4f42-8737-46345c09c238")));
        Assert.Equal(WindowsPowerMode.Unknown, PowerMode.FromGuid(Guid.NewGuid()));
        foreach (var m in new[] { WindowsPowerMode.BestPowerEfficiency, WindowsPowerMode.Balanced, WindowsPowerMode.BestPerformance })
        {
            Assert.Equal(m, PowerMode.FromGuid(PowerMode.ToGuid(m)));
            Assert.Equal(m, PowerMode.FromName(PowerMode.Name(m)));
        }
    }

    [Fact]
    public void Best_performance_kept_the_laptop_at_90_degrees_doing_nothing()
    {
        var spell = Assert.Single(IdleHeat.Spells(BestPerformanceIdle));
        Assert.Equal(T(7, 42, 0), spell.Start);
        Assert.Equal(T(7, 45, 0), spell.End); // 7:45 averaged 84.5°C, just under the line
        Assert.Equal(3, spell.Minutes);
        Assert.InRange(spell.ZoneC, 85, 95);
        Assert.Empty(IdleHeat.Spells(EfficiencyIdle));
    }

    [Fact]
    public void Busy_and_hot_is_not_hot_while_idle()
    {
        var gaming = Enumerable.Range(0, 30).Select(i => S(2, 20 + i / 6, i % 6 * 10, 96, 38, 100)).ToArray();
        Assert.Empty(IdleHeat.Spells(gaming));
    }

    [Fact]
    public void A_performance_mode_with_idle_heat_gets_the_one_click_switch_first()
    {
        var spell = IdleHeat.Spells(BestPerformanceIdle).Single();
        var f = new Findings { Now = T(7, 50, 0), PowerMode = WindowsPowerMode.BestPerformance, IdleHeat = new[] { spell } };
        var rec = Advisor.Advise(f).First();
        Assert.Equal("power-mode", rec.Id);
        Assert.Equal(Advisor.ActionPowerEfficiency, rec.ActionId);
        // Most laptops have no Armoury Crate: it is mentioned only where it is installed.
        Assert.DoesNotContain("Armoury", rec.Body);
        var withArmoury = f with { Drivers = new DriverReport { Apps = new() { new InstalledApp("Armoury Crate", "asus.armourycrate") } } };
        Assert.Contains("Power mode synchronization", Advisor.Advise(withArmoury).First().Body);

        // Already efficient, and the stretch was from before the switch: nothing to say.
        Assert.DoesNotContain(Advisor.Advise(f with { PowerMode = WindowsPowerMode.BestPowerEfficiency }), r => r.Id is "power-mode" or "idle-heat");
        // Hot while idle even though the processor could slow down: the cooling.
        var calm = spell with { Mode = "Best power efficiency" };
        var cooling = Advisor.Advise(f with { PowerMode = WindowsPowerMode.BestPowerEfficiency, IdleHeat = new[] { calm } });
        Assert.Contains(cooling, r => r.Id == "idle-heat");
        // No idle heat: a performance mode alone is not a problem.
        Assert.DoesNotContain(Advisor.Advise(f with { IdleHeat = Array.Empty<HeatSpell>() }), r => r.Id == "power-mode");
    }

    [Fact]
    public void The_8_13_shutdown_at_102_degrees_was_overheating_not_a_mystery()
    {
        var last = S(8, 13, 21, 102, 64, 37);
        var v = CrashClassifier.Classify(new CrashFacts { RestartedAt = T(8, 13, 43), LastActivity = last.At, LastReading = last });
        Assert.Equal(CrashCause.Overheated, v.Cause);
        Assert.Contains("102°C", v.Summary);
        Assert.Contains("37%", v.Summary);
        Assert.Contains("heat governor", v.Detail);
    }

    [Fact]
    public void The_7_46_stop_at_78_degrees_was_not_heat()
    {
        var last = S(7, 46, 34, 78, 2, 98);
        var v = CrashClassifier.Classify(new CrashFacts { RestartedAt = T(7, 49, 12), LastActivity = last.At, LastReading = last });
        Assert.NotEqual(CrashCause.Overheated, v.Cause);
    }

    [Fact]
    public void A_hot_reading_long_before_the_stop_does_not_count()
    {
        var old = S(8, 0, 0, 99, 50, 90);
        var v = CrashClassifier.Classify(new CrashFacts { RestartedAt = T(8, 10, 0), LastActivity = T(8, 5, 0), LastReading = old });
        Assert.NotEqual(CrashCause.Overheated, v.Cause);
    }

    [Fact]
    public void Overheating_shutdowns_offer_the_governor_first_and_say_so_in_the_status()
    {
        Incident Hot(int h, int m, double zone) => new()
        {
            Id = $"crash:{h}{m}", Kind = IncidentKind.UnexpectedShutdown, Time = T(h, m, 0), Severity = Severity.Critical, Title = "Overheated and switched off",
            Crash = new CrashInfo { Cause = CrashCause.Overheated, RestartedAt = T(h, m, 30), LastReading = S(h, m, 0, zone, 40, 50) },
        };
        var f = new Findings { Now = T(8, 20, 0), Incidents = new[] { Hot(7, 55, 97), Hot(8, 7, 106), Hot(8, 13, 102) } };
        var rec = Advisor.Advise(f).First();
        Assert.Equal("overheated", rec.Id);
        Assert.Contains("3 times", rec.Title);
        Assert.Contains("106°C", rec.Body);
        Assert.Equal(Advisor.ActionGovernorOn, rec.ActionId);
        Assert.Equal(Advisor.ActionShowHeat, Advisor.Advise(f with { GovernorOn = true }).First().ActionId);
        Assert.DoesNotContain("Armoury", rec.Body); // not an ASUS laptop: no maker-specific steps
        Assert.DoesNotContain(Advisor.Advise(f), r => r.Id == "sudden-resets");
        Assert.Contains("all from overheating", Advisor.Status(f, Array.Empty<Recommendation>()).Summary);
    }
}

/// <summary>When the heat governor caps and lets go.</summary>
public class GovernorTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 15, 8, 30, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void It_caps_as_soon_as_it_is_too_hot()
    {
        var p = new GovernorPolicy();
        Assert.Equal(GovernorPolicy.Step.None, p.Next(T0, 80, 30));
        Assert.Equal(GovernorPolicy.Step.Cap, p.Next(T0.AddSeconds(1), 88, 30));
        Assert.True(p.Capped);
    }

    [Fact]
    public void Full_load_at_87_degrees_is_capped_before_the_spike_to_98()
    {
        // The stress test: 100% busy held 86-87°C for 35 seconds, then jumped to 98°C within two seconds.
        var p = new GovernorPolicy();
        var steps = Enumerable.Range(0, 20).Select(i => p.Next(T0.AddSeconds(i), 87, 100)).ToList();
        Assert.Equal(GovernorPolicy.Step.Cap, steps[15]);
        Assert.All(steps.Take(15), s => Assert.Equal(GovernorPolicy.Step.None, s));
    }

    [Fact]
    public void It_holds_the_cap_at_least_two_minutes_and_lets_go_once_cool()
    {
        var p = new GovernorPolicy();
        Assert.Equal(GovernorPolicy.Step.Cap, p.Next(T0, 90, 100));
        // Cools down at once, but the cap stays for two minutes.
        for (int s = 1; s < 120; s++) Assert.NotEqual(GovernorPolicy.Step.Release, p.Next(T0.AddSeconds(s), 70, 30));
        Assert.Equal(GovernorPolicy.Step.Release, p.Next(T0.AddSeconds(120), 70, 30));
        Assert.False(p.Capped);
    }

    [Fact]
    public void Warm_or_busy_keeps_the_cap()
    {
        var p = new GovernorPolicy();
        p.Next(T0, 90, 100);
        for (int s = 1; s < 300; s++) Assert.NotEqual(GovernorPolicy.Step.Release, p.Next(T0.AddSeconds(s), 84, 40));
        var q = new GovernorPolicy();
        q.Next(T0, 90, 100);
        for (int s = 1; s < 300; s++) Assert.NotEqual(GovernorPolicy.Step.Release, q.Next(T0.AddSeconds(s), 70, 85));
    }

    [Fact]
    public void Still_hot_while_capped_caps_one_more_app_every_five_seconds()
    {
        var p = new GovernorPolicy();
        p.Next(T0, 92, 100);
        var more = Enumerable.Range(1, 20).Select(s => p.Next(T0.AddSeconds(s), 92, 100)).ToList();
        Assert.Equal(4, more.Count(s => s == GovernorPolicy.Step.Cap)); // at 5, 10, 15 and 20 s
    }

    [Fact]
    public void Your_own_caps_go_first_and_never_means_never()
    {
        var use = new[] { new ProcessUse(100, "Game", 60), new ProcessUse(200, "chrome", 20), new ProcessUse(300, "Discord", 3), new ProcessUse(400, "dwm", 30), new ProcessUse(500, "build", 40) };
        var rules = new[] { new AppCapRule { App = "Game", Never = true }, new AppCapRule { App = "Discord", CapPercent = 15 } };
        var picked = HeatGovernor.PickTargets(use, rules, _ => false, limit: 3, selfPid: 1, defaultPercent: 40);
        Assert.Equal(new[] { "Discord", "build", "chrome" }, picked.Select(p => p.Name));
        // Half of what each is using, never more than its own cap or the default, never under 5%.
        Assert.Equal(new[] { 5, 20, 10 }, picked.Select(p => p.Percent));
        // Windows' own processes (dwm) and "never" apps are never picked; nor is anything already capped, or past the limit.
        var again = HeatGovernor.PickTargets(use, rules, pid => pid == 500, limit: 1, selfPid: 1, defaultPercent: 40);
        Assert.Equal("Discord", Assert.Single(again).Name);
    }

    [Fact]
    public void A_flat_40_percent_cap_held_nothing_back_at_9_01()
    {
        // 15 November 2026, 9:01 AM: 3DXChat at 25%, Chrome at 12%, Discord at 4%, all "capped" at 40%, and the laptop
        // sat at 97-110°C and cut out at 9:04. Every cap must now be below what the app is using.
        var use = new[] { new ProcessUse(10, "3DXChat", 25), new ProcessUse(20, "chrome", 12), new ProcessUse(30, "Discord", 4) };
        var picked = HeatGovernor.PickTargets(use, Array.Empty<AppCapRule>(), _ => false, limit: 3, selfPid: 1, defaultPercent: 40);
        Assert.Equal(3, picked.Count);
        foreach (var p in picked)
            Assert.True(p.Percent < use.Single(u => u.Name == p.Name).Share || p.Percent == HeatGovernor.MinPercent, $"{p.Name} capped at {p.Percent}%");
    }

    [Fact]
    public void An_always_cap_shares_a_few_cores_across_the_whole_app()
    {
        // 32 threads: 5% is 2 of them, 1% still gets one; taken from the top.
        Assert.Equal(0b11L << 30, HeatGovernor.AlwaysMask(32, 5));
        Assert.Equal(1L << 31, HeatGovernor.AlwaysMask(32, 1));
        Assert.Equal(0b1111_0000L, HeatGovernor.AlwaysMask(8, 50));
        Assert.Equal((1L << 32) - 1, HeatGovernor.AlwaysMask(32, 100));
    }

    [Fact]
    public void App_names_are_kept_without_exe()
    {
        Assert.Equal("chrome", AppCapRule.Normalize("chrome.exe"));
        Assert.Equal("Discord", AppCapRule.Normalize(" Discord "));
    }

    [Fact]
    public void Without_a_temperature_reading_load_alone_decides()
    {
        var p = new GovernorPolicy();
        for (int s = 0; s <= 15; s++) p.Next(T0.AddSeconds(s), null, 100);
        Assert.True(p.Capped);
        for (int s = 16; s < 200; s++) p.Next(T0.AddSeconds(s), null, 20);
        Assert.False(p.Capped);
    }
}
