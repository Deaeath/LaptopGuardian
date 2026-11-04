namespace LaptopGuardian.Core;

public sealed record DecodedEvent(IncidentKind Kind, string Title, string Summary, string Detail, Severity Severity, string? Data);

/// <summary>
/// Turns graphics-driver and hardware-error events into plain English. Drivers log these with a
/// message file Windows often cannot find, which is why Event Viewer says "The description for
/// Event ID 153 ... cannot be found". The useful part is the insertion strings, read here directly.
/// </summary>
public static class GraphicsEvents
{
    public static readonly string[] GraphicsProviders = { "nvlddmkm", "amdkmdag", "amdwddmg", "Display", "igfx", "igfxn" };
    public const string WheaProvider = "Microsoft-Windows-WHEA-Logger";

    /// <summary>XPath for graphics-driver and hardware-error warnings and errors in the System log.</summary>
    public static string XPath(TimeSpan? since = null)
    {
        var names = string.Join(" or ", GraphicsProviders.Append(WheaProvider).Select(p => $"@Name='{p}'"));
        var time = since is TimeSpan s ? " and " + EventLogX.Since(s) : "";
        return $"*[System[Provider[{names}] and (Level=1 or Level=2 or Level=3){time}]]";
    }

    public static bool IsGraphics(string provider) =>
        GraphicsProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

    public static DecodedEvent Decode(string provider, int id, int level, IReadOnlyList<string> props)
    {
        if (string.Equals(provider, WheaProvider, StringComparison.OrdinalIgnoreCase))
            return DecodeWhea(id, level);

        string? data = FirstMeaningful(props);
        var severity = level <= 1 ? Severity.Critical : Severity.Warning;
        string p = provider.ToLowerInvariant();

        if (p == "nvlddmkm")
        {
            switch (id)
            {
                case 153:
                    return new DecodedEvent(IncidentKind.GraphicsError, "Graphics card reset after an error",
                        "The NVIDIA driver caught the graphics card in a bad state and reset it.",
                        "The screen may have flickered or gone black for a moment, and a game or app using the graphics card may have frozen or closed. " +
                        "One reset now and then is not a concern. Repeats point to heat, an overclock or undervolt, a driver problem, or (rarely) failing hardware.",
                        severity, data);
                case 13:
                case 14:
                    return new DecodedEvent(IncidentKind.GraphicsError, "Graphics engine fault",
                        "The graphics card's engine hit an internal fault while running work from an app.",
                        "A program sent the graphics card work it could not finish (an \"engine exception\"). This is usually the program or the driver rather than the hardware. " +
                        "If it keeps happening with one game or app, update that app and the graphics driver.",
                        severity, data);
                default:
                    return new DecodedEvent(IncidentKind.GraphicsError, "NVIDIA driver problem",
                        $"The NVIDIA driver reported a problem (event {id}).",
                        "The driver logged an error without a description Windows can read. If these repeat, a clean reinstall of the graphics driver is the first thing to try.",
                        severity, data);
            }
        }

        if (p == "display" && id == 4101)
        {
            string driver = string.IsNullOrWhiteSpace(data) ? "The display driver" : $"The display driver ({data})";
            return new DecodedEvent(IncidentKind.GraphicsError, "Display driver stopped responding",
                $"{driver} stopped responding and Windows restarted it.",
                "Windows gives the graphics driver about two seconds to answer. When it does not, Windows resets it (a \"TDR\"). " +
                "The screen goes black briefly and comes back. Repeats point to heat, an unstable overclock, or a driver problem.",
                severity, data);
        }

        if (p.StartsWith("amd"))
            return new DecodedEvent(IncidentKind.GraphicsError, "AMD graphics driver problem",
                $"The AMD graphics driver reported a problem (event {id}).",
                "If these repeat, update or cleanly reinstall the AMD graphics driver from the laptop maker or AMD.",
                severity, data);

        if (p.StartsWith("igfx"))
            return new DecodedEvent(IncidentKind.GraphicsError, "Intel graphics driver problem",
                $"The Intel graphics driver reported a problem (event {id}).",
                "If these repeat, update the Intel graphics driver from the laptop maker or Intel.",
                severity, data);

        return new DecodedEvent(IncidentKind.GraphicsError, "Graphics driver problem",
            $"{provider} reported a problem (event {id}).", "", severity, data);
    }

    static DecodedEvent DecodeWhea(int id, int level)
    {
        bool corrected = id is 17 or 19 or 47 || level >= 3;
        if (corrected)
            return new DecodedEvent(IncidentKind.HardwareError, "Corrected hardware error",
                "The processor or a device reported an error that the hardware fixed by itself.",
                "Corrected errors are handled automatically. A handful is harmless; many per day, or ones that come with crashes, can point to a failing device, memory, or an unstable overclock.",
                Severity.Info, null);
        return new DecodedEvent(IncidentKind.HardwareError, "Hardware error",
            "The processor or a device reported an error it could not correct.",
            "Uncorrected hardware errors usually stop Windows with a blue screen. If this repeats, check for a BIOS update, remove any overclock, and test the memory.",
            Severity.Critical, null);
    }

    static string? FirstMeaningful(IReadOnlyList<string> props)
    {
        // nvlddmkm logs "\Device\VideoN" first and the message second; Display 4101 logs the driver name first.
        foreach (var s in props)
        {
            var t = s.Trim();
            if (t.Length == 0 || t.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (long.TryParse(t, out _)) continue;
            return t;
        }
        return null;
    }
}

/// <summary>Names and plain-English meanings for Windows stop codes and kernel live dumps.</summary>
public static class StopCodes
{
    static readonly Dictionary<uint, (string Name, string Meaning)> Known = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "A driver touched memory it should not have."),
        [0x19] = ("BAD_POOL_HEADER", "Kernel memory was corrupted, usually by a driver."),
        [0x1A] = ("MEMORY_MANAGEMENT", "Memory was corrupted; can be a driver or failing RAM."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "A driver crashed."),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "A driver or system component crashed."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "A driver read memory that was not there."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A driver crashed."),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "The processor hit an unexpected fault; can be hardware."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "A driver failed to go to sleep or wake up."),
        [0xA0] = ("INTERNAL_POWER_ERROR", "Windows' power management hit a fatal error."),
        [0xC2] = ("BAD_POOL_CALLER", "A driver misused kernel memory."),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver touched memory it should not have."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "A process Windows cannot run without stopped."),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "A process Windows cannot run without stopped."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "A processor core stopped responding."),
        [0x116] = ("VIDEO_TDR_FAILURE", "The graphics driver stopped responding and could not be reset."),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "The display driver stopped responding and Windows reset it."),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "The graphics scheduler hit a fatal error."),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "The hardware reported an error it could not correct."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "A driver held the processor for too long."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "Kernel data was corrupted, usually by a driver."),
        [0x141] = ("VIDEO_ENGINE_TIMEOUT_DETECTED", "Part of the graphics card stopped responding and Windows reset it."),
        [0x144] = ("BUGCODE_USB3_DRIVER", "A USB controller problem."),
        [0x14F] = ("PDC_WATCHDOG_TIMEOUT", "Something did not respond in time while the laptop was waking from Modern Standby sleep."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "The memory store hit an error; can be the disk."),
        [0x15F] = ("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", "Something kept the laptop from sleeping properly."),
        [0x193] = ("VIDEO_DXGKRNL_LIVEDUMP", "Windows saved a diagnostic snapshot of the graphics system."),
        [0x1A1] = ("WIN32K_CALLOUT_WATCHDOG_LIVEDUMP", "Part of the Windows graphics subsystem stopped responding."),
        [0x1C8] = ("MANUALLY_INITIATED_POWER_BUTTON_HOLD", "The power button was held because the laptop had stopped responding."),
    };

    public static (string Name, string Meaning) Describe(uint code) =>
        Known.TryGetValue(code, out var v) ? v : ($"0x{code:X}", "Windows stopped with this stop code.");

    public static bool TryParseHex(string? s, out uint code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out code);
    }
}
