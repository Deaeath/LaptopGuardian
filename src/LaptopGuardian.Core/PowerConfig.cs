using System.Runtime.InteropServices;

namespace LaptopGuardian.Core;

public sealed record PowerSettingsInfo
{
    public string SchemeName { get; init; } = "";
    public Guid Scheme { get; init; }
    public uint? SleepAfterAc { get; init; }
    public uint? SleepAfterDc { get; init; }
    public uint? HibernateAfterAc { get; init; }
    public uint? HibernateAfterDc { get; init; }
    public uint? LidAc { get; init; }
    public uint? LidDc { get; init; }
    public uint? PowerButtonAc { get; init; }
    public uint? PowerButtonDc { get; init; }
    public bool ModernStandby { get; init; }
    public bool HibernateSupported { get; init; }
    public bool HiberFilePresent { get; init; }
    public bool LidPresent { get; init; }
    public bool HasBattery { get; init; }

    public bool HibernateAvailable => HibernateSupported && HiberFilePresent;
}

/// <summary>Reads and changes sleep and lid settings through the Windows power API (no localized text to parse).</summary>
public static class PowerConfig
{
    static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    static readonly Guid HibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364");
    static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    static readonly Guid LidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    static readonly Guid PowerButtonAction = new("7648efa3-dd9c-4e3e-b566-50f929386280");

    public const uint ActionNothing = 0, ActionSleep = 1, ActionHibernate = 2, ActionShutDown = 3, ActionDisplayOff = 4;

    public static string ActionName(uint? a) => a switch
    {
        null => "Unknown",
        ActionNothing => "Do nothing",
        ActionSleep => "Sleep",
        ActionHibernate => "Hibernate",
        ActionShutDown => "Shut down",
        ActionDisplayOff => "Turn off the display",
        _ => $"Option {a}",
    };

    public static PowerSettingsInfo Read()
    {
        var caps = Capabilities();
        Guid scheme = Guid.Empty;
        string name = "";
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
        {
            scheme = Marshal.PtrToStructure<Guid>(p);
            LocalFree(p);
            name = FriendlyName(scheme) ?? "";
        }
        uint? Ac(Guid sub, Guid setting) => scheme == Guid.Empty ? null : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v) == 0 ? v : null;
        uint? Dc(Guid sub, Guid setting) => scheme == Guid.Empty ? null : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v) == 0 ? v : null;

        return new PowerSettingsInfo
        {
            SchemeName = name,
            Scheme = scheme,
            SleepAfterAc = Ac(SubSleep, StandbyIdle),
            SleepAfterDc = Dc(SubSleep, StandbyIdle),
            HibernateAfterAc = Ac(SubSleep, HibernateIdle),
            HibernateAfterDc = Dc(SubSleep, HibernateIdle),
            LidAc = Ac(SubButtons, LidAction),
            LidDc = Dc(SubButtons, LidAction),
            PowerButtonAc = Ac(SubButtons, PowerButtonAction),
            PowerButtonDc = Dc(SubButtons, PowerButtonAction),
            ModernStandby = caps.AoAc,
            HibernateSupported = caps.S4,
            HiberFilePresent = caps.HiberFile,
            LidPresent = caps.Lid,
            HasBattery = caps.Battery,
        };
    }

    /// <summary>Sleep turns into hibernation after <paramref name="seconds"/> asleep, plugged in and on battery.</summary>
    public static uint SetHibernateAfter(uint seconds, bool bestEffort = false) =>
        Write(SubSleep, HibernateIdle, seconds, seconds, bestEffort);

    /// <summary>Closing the lid and pressing the power button hibernate instead of sleeping.</summary>
    public static uint SetLidAndButtonToHibernate(bool bestEffort = false)
    {
        uint a = Write(SubButtons, LidAction, ActionHibernate, ActionHibernate, bestEffort);
        uint b = Write(SubButtons, PowerButtonAction, ActionHibernate, ActionHibernate, bestEffort);
        return a != 0 ? a : b;
    }

    /// <summary>Every power plan on this PC.</summary>
    public static List<Guid> Schemes()
    {
        const uint AccessScheme = 16;
        var list = new List<Guid>();
        for (uint i = 0; i < 64; i++)
        {
            var buffer = new byte[16];
            uint size = 16;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref size) != 0) break;
            list.Add(new Guid(buffer));
        }
        return list;
    }

    /// <summary>
    /// Writes to every power plan, not only the active one: laptop utilities such as Armoury Crate switch
    /// plans with their performance modes, and a fix made to one plan would silently stop applying.
    /// With <paramref name="bestEffort"/> only the active plan has to succeed (the elevated helper's last resort).
    /// </summary>
    static uint Write(Guid sub, Guid setting, uint ac, uint dc, bool bestEffort)
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return 1;
        var active = Marshal.PtrToStructure<Guid>(p);
        LocalFree(p);

        uint One(Guid scheme)
        {
            uint r = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, ac);
            return r != 0 ? r : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, dc);
        }

        uint activeResult = One(active);
        if (activeResult != 0) return activeResult;
        uint others = 0;
        foreach (var scheme in Schemes().Where(s => s != active))
        {
            uint r = One(scheme);
            if (r != 0 && others == 0) others = r;
        }
        uint applied = PowerSetActiveScheme(IntPtr.Zero, ref active); // applies the change now
        if (applied != 0) return applied;
        return bestEffort ? 0 : others;
    }

    public const uint ErrorAccessDenied = 5;

    static string? FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    internal readonly record struct Caps(bool Lid, bool S3, bool S4, bool HiberFile, bool AoAc, bool Battery);

    /// <summary>SYSTEM_POWER_CAPABILITIES, read as bytes (all BOOLEANs up to offset 31).</summary>
    internal static Caps Capabilities()
    {
        var b = new byte[128];
        if (!GetPwrCapabilities(b)) return default;
        return new Caps(Lid: b[2] != 0, S3: b[5] != 0, S4: b[6] != 0, HiberFile: b[8] != 0, AoAc: b[20] != 0, Battery: b[30] != 0);
    }

    [DllImport("powrprof.dll")] static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint accessFlags, uint index, [Out] byte[] buffer, ref uint size);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, IntPtr buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool GetPwrCapabilities([Out] byte[] caps);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
}
