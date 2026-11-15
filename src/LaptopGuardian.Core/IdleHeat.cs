namespace LaptopGuardian.Core;

/// <summary>Minutes in a row when the laptop was hot while doing next to nothing.</summary>
public sealed record HeatSpell(DateTimeOffset Start, DateTimeOffset End, double ZoneC, double BusyPct, double SpeedPct, string? Mode)
{
    public int Minutes => Math.Max(1, (int)Math.Round((End - Start).TotalMinutes));

    public string Describe() =>
        $"{Fmt.When(Start)}, {Fmt.Plural(Minutes, "minute", "minutes")} at {ZoneC:0}°C with the processor only {BusyPct:0}% busy but at {SpeedPct:0}% of its top speed";
}

/// <summary>
/// Finds the laptop running hot with nothing to do: the processor nearly idle yet held near full speed, and the
/// thermal sensor high. That is heat made for nothing, usually by a performance power mode; if it happens in an
/// efficient mode, the cooling is not keeping up.
/// </summary>
public static class IdleHeat
{
    public const double HotC = 85, IdleBusyPct = 10, FullSpeedPct = 90;
    public const int MinMinutes = 3;

    public static List<HeatSpell> Spells(IEnumerable<FlightSample> samples)
    {
        var minutes = samples
            .Where(s => s.State == "awake" && s.ZoneC != null && s.CpuBusyPct != null && s.CpuSpeedPct != null)
            .GroupBy(s => s.At.AddTicks(-(s.At.Ticks % TimeSpan.TicksPerMinute)))
            .Where(g => g.Count() >= 3)
            .Select(g => new Minute(g.Key, g.Average(s => s.ZoneC!.Value), g.Average(s => s.CpuBusyPct!.Value), g.Average(s => s.CpuSpeedPct!.Value),
                g.Select(s => s.PowerMode).LastOrDefault(m => m != null)))
            .OrderBy(m => m.At)
            .ToList();

        var spells = new List<HeatSpell>();
        var run = new List<Minute>();
        void Close()
        {
            if (run.Count >= MinMinutes)
                spells.Add(new HeatSpell(run[0].At, run[^1].At.AddMinutes(1), run.Average(m => m.Zone), run.Average(m => m.Busy), run.Average(m => m.Speed),
                    run.Select(m => m.Mode).LastOrDefault(m => m != null)));
            run.Clear();
        }
        foreach (var m in minutes)
        {
            bool hot = m.Zone >= HotC && m.Busy <= IdleBusyPct && m.Speed >= FullSpeedPct;
            if (run.Count > 0 && (!hot || m.At != run[^1].At.AddMinutes(1))) Close();
            if (hot) run.Add(m);
        }
        Close();
        return spells;
    }

    readonly record struct Minute(DateTimeOffset At, double Zone, double Busy, double Speed, string? Mode);
}
