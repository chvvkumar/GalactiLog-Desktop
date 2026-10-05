using GalactiLog.Core.Phd2;

namespace GalactiLog.Core.Tests.Phd2;

// The 200-row fixture of task3.md section 5, in closed form. Every expected figure in
// Phd2MetricsTests was computed by hand from these generating rules, never from an
// implementation's output.
//
// Frames are built with named arguments so the fixture does not depend on the declaration order of
// Phd2Frame's positional parameters, which Task 2 owns.
internal static class Phd2MetricsFixtures
{
    public const string ZoneId = "America/New_York";
    public const double ObserverLongitude = -80.0;

    public static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

    public static Phd2Header BaseHeader() => new()
    {
        EquipmentProfile = "Rig A",
        PixelScaleArcsec = 2.0,
        FocalLengthMm = 784.0,
        GuideCamera = "ZWO ASI174MM Mini",
        ExposureMs = 500.0,
        MountName = "ASI Mount (ASCOM)",
        DecGuideMode = "Auto",
        AlgoRa = "Hysteresis",
        AlgoDec = "Resist Switch",
        MinMoveRa = 0.25,
        MinMoveDec = 0.25,
        AggressionRa = 0.7,
        OrthoErrorDeg = 11.8,
        LastCalIssue = "None",
        PierSide = "West",
        AltDeg = 43.7,
        AzDeg = 70.4,
        DecDeg = 38.5,
        HourAngleHr = -4.02,
    };

    // RA alternates +1 and -1 by parity, Dec alternates +2 and -2 in pairs, so 200 frames give 100
    // values of each on each axis: mean zero, population sigma exactly 1.0 px and 2.0 px.
    public static Phd2Frame Frame(int i) => new(
        FrameIndex: i,
        TimeOffset: 0.5 * i,
        Dropped: false,
        Dx: i % 2 == 1 ? 1.0 : -1.0,
        Dy: i % 4 is 1 or 2 ? 2.0 : -2.0,
        RaRaw: i % 2 == 1 ? 1.0 : -1.0,
        DecRaw: i % 4 is 1 or 2 ? 2.0 : -2.0,
        RaGuide: 0.0,
        DecGuide: 0.0,
        RaDurationMs: i % 2 == 1 ? 100 : 0,
        RaDirection: i % 2 == 1 ? "W" : "",
        DecDurationMs: i % 4 == 0 ? 200 : 0,
        DecDirection: i % 4 == 0 ? "N" : "",
        StarMass: 1700.0,
        Snr: 30.0,
        ErrorCode: 0,
        DropReason: "");

    /// <summary>The base section: <paramref name="count"/> frames by the rule above, 100 seconds
    /// long, pixel scale 2.0, no events.</summary>
    public static Phd2Section Base(int count = 200)
    {
        var section = new Phd2Section
        {
            StartedAtLocal = new DateTime(2026, 3, 1, 21, 0, 0, DateTimeKind.Unspecified),
            EndedAtLocal = new DateTime(2026, 3, 1, 21, 1, 40, DateTimeKind.Unspecified),
            Header = BaseHeader(),
        };

        for (var i = 1; i <= count; i++)
        {
            section.Frames.Add(Frame(i));
        }

        return section;
    }

    /// <summary>Base200 with frame 200's RA distance moved from -1.0 to -21.0, and nothing else.
    /// </summary>
    public static Phd2Section Excursion()
    {
        var section = Base();
        section.Frames[199] = section.Frames[199] with { RaRaw = -21.0 };
        return section;
    }

    /// <summary>Base200 with one dither and settle window from t 50.0 to t 60.0, and the twenty
    /// frames inside it given a 30 pixel error on both axes.</summary>
    public static Phd2Section Windows()
    {
        var section = Base();
        section.Events.Add(new Phd2Event(Phd2EventTypes.Dither, 50.0, "0.42 pixels"));
        section.Events.Add(new Phd2Event(Phd2EventTypes.SettleStart, 50.0, "settling started"));
        // A lock_shift inside the window and a param_change after it. Neither opens nor closes a
        // window, so no figure this fixture pins moves; a rule that treated an unrecognised type
        // as an opener would add a second window from t 70.0 to the last frame and read the
        // session as better guided than it was.
        section.Events.Add(new Phd2Event(Phd2EventTypes.LockShift, 55.0, "150.0, 120.0"));
        section.Events.Add(new Phd2Event(Phd2EventTypes.SettleDone, 60.0, "settling complete"));
        section.Events.Add(new Phd2Event(Phd2EventTypes.ParamChange, 70.0, "RA aggression, 0.70"));

        for (var i = 101; i <= 120; i++)
        {
            section.Frames[i - 1] = section.Frames[i - 1] with { RaRaw = 30.0, DecRaw = 30.0 };
        }

        return section;
    }

    /// <summary>Base200 with frames 51, 52, 53 and 102 dropped.</summary>
    public static Phd2Section Drops()
    {
        var section = Base();
        (int Index, string Reason)[] dropped =
        [
            (51, "Star lost - mass changed"),
            (52, "Star lost - mass changed"),
            (53, "Star lost - mass changed"),
            (102, "Star lost - low SNR"),
        ];

        foreach (var (index, reason) in dropped)
        {
            section.Frames[index - 1] = section.Frames[index - 1] with
            {
                Dropped = true,
                RaRaw = null,
                DecRaw = null,
                Dx = null,
                Dy = null,
                Snr = 10.0,
                DropReason = reason,
            };
        }

        return section;
    }

    /// <summary>Five frames at t 1.0 through 5.0, pixel scale 1.0, with a window from t 2.0 to
    /// t 4.0. The RA distances 3, 7, 50, 9, 3 make each of the four possible bound choices give a
    /// different peak.</summary>
    public static Phd2Section Boundary()
    {
        var section = new Phd2Section
        {
            StartedAtLocal = new DateTime(2026, 3, 1, 21, 0, 0, DateTimeKind.Unspecified),
            EndedAtLocal = new DateTime(2026, 3, 1, 21, 0, 5, DateTimeKind.Unspecified),
            Header = new Phd2Header { PixelScaleArcsec = 1.0, EquipmentProfile = "Rig A" },
        };

        double[] raRaw = [3.0, 7.0, 50.0, 9.0, 3.0];
        for (var i = 1; i <= 5; i++)
        {
            section.Frames.Add(Frame(i) with
            {
                TimeOffset = i,
                RaRaw = raRaw[i - 1],
                DecRaw = 0.0,
                Dx = raRaw[i - 1],
                Dy = 0.0,
            });
        }

        section.Events.Add(new Phd2Event(Phd2EventTypes.Dither, 2.0, "0.42 pixels"));
        section.Events.Add(new Phd2Event(Phd2EventTypes.SettleDone, 4.0, "settling complete"));
        return section;
    }

    /// <summary>A bare section of <paramref name="count"/> frames at t 1.0, 2.0, ... with the given
    /// indexes dropped. Used by the leading and trailing drop-run cases.</summary>
    public static Phd2Section DropRun(int count, params int[] droppedFrames)
    {
        var section = new Phd2Section
        {
            StartedAtLocal = new DateTime(2026, 3, 1, 21, 0, 0, DateTimeKind.Unspecified),
            Header = BaseHeader(),
        };

        for (var i = 1; i <= count; i++)
        {
            var frame = Frame(i) with { TimeOffset = i };
            section.Frames.Add(droppedFrames.Contains(i)
                ? frame with { Dropped = true, RaRaw = null, DecRaw = null, DropReason = "Star lost" }
                : frame);
        }

        return section;
    }

    /// <summary>A section carrying only the given events, with no frames.</summary>
    public static Phd2Section EventsOnly(params (string Type, double TimeOffset)[] events)
    {
        var section = new Phd2Section
        {
            StartedAtLocal = new DateTime(2026, 3, 1, 21, 0, 0, DateTimeKind.Unspecified),
            Header = BaseHeader(),
        };

        foreach (var (type, offset) in events)
        {
            section.Events.Add(new Phd2Event(type, offset, ""));
        }

        return section;
    }
}
