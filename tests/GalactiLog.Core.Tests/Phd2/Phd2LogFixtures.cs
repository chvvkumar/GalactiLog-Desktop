using Xunit;

namespace GalactiLog.Core.Tests.Phd2;

// Fixture text for the PHD2 guide-log parser cases, taken verbatim from the web application's
// own backend/tests/test_phd2_parser.py, which took it verbatim from the reference corpus
// (PHD2_GuideLog_2026-01-19_172118.txt, PHD2_GuideLog_2026-07-14_201333.txt and an ASIAIR
// file). Nothing here is tidied: the leading space on the Camera line, the double space in the
// ASIAIR Mount line, the trailing comma after "parity = +/+" and the blank lines are all part
// of what is under test.
internal static class Phd2LogFixtures
{
    // Verbatim: PHD2_GuideLog_2026-01-19_172118.txt (whole file, 25 lines). Five stacked runs,
    // all data-free, all repeating the same "Log enabled at" while only "Log closed at" moves.
    internal const string EmptyStackedLog = """
PHD2 version 2.6.13dev8 [Windows], Log version 2.5. Log enabled at 2026-01-19 17:21:18
INFO: Guiding parameter change, MultiStar = true

Log Summary: calcnt:0 gcnt:0 gdur:0 gacnt:0
Log closed at 2026-01-19 17:21:51
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-01-19 17:21:18
INFO: Guiding parameter change, MultiStar = true

Log Summary: calcnt:0 gcnt:0 gdur:0 gacnt:0
Log closed at 2026-01-19 17:29:17
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-01-19 17:21:18
INFO: Guiding parameter change, MultiStar = true

Log Summary: calcnt:0 gcnt:0 gdur:0 gacnt:0
Log closed at 2026-01-19 18:07:05
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-01-19 17:21:18
INFO: Guiding parameter change, MultiStar = true

Log Summary: calcnt:0 gcnt:0 gdur:0 gacnt:0
Log closed at 2026-01-19 18:09:14
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-01-19 17:21:18
INFO: Guiding parameter change, MultiStar = true

Log Summary: calcnt:0 gcnt:0 gdur:0 gacnt:0
Log closed at 2026-01-19 18:21:50
""";

    // Verbatim: PHD2_GuideLog_2026-07-14_201333.txt lines 64 to 78, the guiding header block.
    // The Camera line keeps the leading space PHD2 emits.
    internal static readonly string[] GuidingHeaderLines =
    {
        "Equipment Profile = AM5n_OAG_ASI174M",
        "Dither = both axes, Dither scale = 1.000, Image noise reduction = none, Guide-frame time lapse = 0, Server enabled",
        "Pixel scale = 1.54 arc-sec/px, Binning = 1, Focal length = 784 mm",
        "Search region = 15 px, Star mass tolerance = 50.0%, Multi-star mode, list size = 12",
        " Camera = ZWO ASI174MM Mini, gain = 90, full size = 1936 x 1216, have dark, dark dur = 500, no defect map, pixel size = 5.9 um",
        "Exposure = 500 ms",
        "Mount = ASI Mount (ASCOM), connected, guiding enabled, xAngle = 87.9, xRate = 3.579, yAngle = -170.3, yRate = 4.437, parity = ?/?",
        "Norm rates RA = 7.0\"/s @ dec 0, Dec = 6.8\"/s; ortho.err. = 11.8 deg",
        "X guide algorithm = Hysteresis, Hysteresis = 0.100, Aggression = 0.700, Minimum move = 0.250",
        "Y guide algorithm = Resist Switch, Minimum move = 0.250 Aggression = 100% FastSwitch = enabled",
        "Backlash comp = disabled, pulse = 254 ms",
        "Max RA duration = 2500, Max DEC duration = 2500, DEC guide mode = Auto",
        "RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Cal Dec = 38.5, Last Cal Issue = None, Timestamp = 7/14/2026 9:42:27 PM",
        "RA = 20.22 hr, Dec = 38.5 deg, Hour angle = -4.02 hr, Pier side = West, Rotator pos = N/A, Alt = 43.7 deg, Az = 70.4 deg",
        "Lock position = 77.407, 204.420, Star position = 77.407, 204.420, HFD = 1.91 px",
    };

    // Verbatim: same file lines 105 to 114, the calibration header variant. Shorter Mount line,
    // short guide-speed line, and "Dec = -0.0 deg" on the pointing line.
    internal static readonly string[] CalHeaderLines =
    {
        "Equipment Profile = AM5n_OAG_ASI174M",
        "Pixel scale = 1.54 arc-sec/px, Binning = 1, Focal length = 784 mm",
        " Camera = ZWO ASI174MM Mini, gain = 90, full size = 1936 x 1216, have dark, dark dur = 500, no defect map, pixel size = 5.9 um",
        "Exposure = 500 ms",
        "Mount = ASI Mount (ASCOM), Calibration Step = 450 ms, Calibration Distance = 25 px, Assume orthogonal axes = no",
        "RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s",
        "RA = 16.53 hr, Dec = -0.0 deg, Hour angle = -0.33 hr, Pier side = West, Rotator pos = N/A, Alt = 51.0 deg, Az = 172.2 deg",
        "Lock position = 468.980, 290.926, Star position = 469.135, 291.637, HFD = 2.37 px",
    };

    // Verbatim pointing line from the ASIAIR file. No RA, no Alt, no Az.
    internal const string AsiairPointingLine =
        "Dec = 57.6 deg, Hour angle = -2.77 hr, Pier side = West, Rotator pos = N/A";

    // Verbatim pointing line from PHD2_GuideLog_2026-07-14_201333.txt: all seven fields, which
    // is what all but ten files in the 177-file corpus emit.
    internal const string DesktopPointingLine =
        "RA = 20.22 hr, Dec = 38.5 deg, Hour angle = -4.02 hr, Pier side = West, " +
        "Rotator pos = N/A, Alt = 43.7 deg, Az = 70.4 deg";

    // The ASIAIR's own Equipment Profile line, with the trailing space the file on disk carries.
    // ApplyHeaderLine trims before matching, so this form and the space-free form both leave the
    // field null; the case that drives this line pins that the two agree.
    internal const string AsiairEmptyProfileLine = "Equipment Profile = ";

    // Verbatim: PHD2_GuideLog_2026-07-14_201333.txt lines 1 to 100, trimmed to the first
    // calibration block and the first guiding section.
    internal const string FullSampleLog = """
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-07-14 20:13:33
INFO: Guiding parameter change, MultiStar = true

Calibration Begins at 2026-07-14 21:41:21
Equipment Profile = AM5n_OAG_ASI174M
Pixel scale = 1.54 arc-sec/px, Binning = 1, Focal length = 784 mm
 Camera = ZWO ASI174MM Mini, gain = 90, full size = 1936 x 1216, have dark, dark dur = 500, no defect map, pixel size = 5.9 um
Exposure = 500 ms
Mount = ASI Mount (ASCOM), Calibration Step = 450 ms, Calibration Distance = 25 px, Assume orthogonal axes = no
RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s
RA = 20.22 hr, Dec = 38.5 deg, Hour angle = -4.04 hr, Pier side = West, Rotator pos = N/A, Alt = 43.5 deg, Az = 70.3 deg
Lock position = 86.486, 202.175, Star position = 86.496, 202.920, HFD = 2.38 px
Direction,Step,dx,dy,x,y,Dist
West,0,0.000,0.000,86.217,202.308,0.000
West,1,0.175,1.175,86.042,201.133,1.188
West,2,0.078,3.452,86.139,198.856,3.453
West calibration complete. Angle = 87.9 deg, Rate = 3.579 px/sec, Parity = N/A
East,3,0.950,25.755,85.267,176.554,25.772
East,0,5.969,-2.244,80.248,204.552,6.377
Backlash,0,0.000,0.000,80.248,204.552,0.000
North,0,0.000,0.000,77.909,203.756,0.000
North,1,1.472,-0.332,76.437,204.089,1.509
North calibration complete. Angle = -170.3 deg, Rate = 4.437 px/sec, Parity = N/A
South,3,25.587,4.380,52.322,199.377,25.959
South,0,7.805,0.977,70.104,202.780,7.866
Calibration complete, mount = ASI Mount (ASCOM).

Guiding Begins at 2026-07-14 21:42:27
Equipment Profile = AM5n_OAG_ASI174M
Pixel scale = 1.54 arc-sec/px, Binning = 1, Focal length = 784 mm
 Camera = ZWO ASI174MM Mini, gain = 90, full size = 1936 x 1216, have dark, dark dur = 500, no defect map, pixel size = 5.9 um
Exposure = 500 ms
Mount = ASI Mount (ASCOM), connected, guiding enabled, xAngle = 87.9, xRate = 3.579, yAngle = -170.3, yRate = 4.437, parity = ?/?
Norm rates RA = 7.0"/s @ dec 0, Dec = 6.8"/s; ortho.err. = 11.8 deg
X guide algorithm = Hysteresis, Hysteresis = 0.100, Aggression = 0.700, Minimum move = 0.250
Y guide algorithm = Resist Switch, Minimum move = 0.250 Aggression = 100% FastSwitch = enabled
Backlash comp = disabled, pulse = 254 ms
Max RA duration = 2500, Max DEC duration = 2500, DEC guide mode = Auto
RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Cal Dec = 38.5, Last Cal Issue = None, Timestamp = 7/14/2026 9:42:27 PM
RA = 20.22 hr, Dec = 38.5 deg, Hour angle = -4.02 hr, Pier side = West, Rotator pos = N/A, Alt = 43.7 deg, Az = 70.4 deg
Lock position = 77.407, 204.420, Star position = 77.407, 204.420, HFD = 1.91 px
Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
INFO: SETTLING STATE CHANGE, Settling started
1,1.228,"Mount",0.330,0.544,0.556,-0.189,0.350,0.000,98,W,0,,,,1713,28.59,1
2,2.093,"Mount",0.524,0.132,0.151,-0.477,0.000,0.000,0,,0,,,,1702,28.36,0
3,3.149,"Mount",-0.139,-0.706,-0.710,-0.035,-0.448,0.000,125,E,0,,,,1888,29.97,1
INFO: SETTLING STATE CHANGE, Settling complete
7,7.381,"Mount",-1.050,0.681,0.642,1.183,0.388,1.183,108,W,267,S,,,1961,30.61,1
INFO: DITHER by 3.4, -1.2, new lock pos = 80.807, 203.220
9067,9716.891,"DROP",,,,,,,,,,,,,929,19.23,7,"Star lost - mass changed"
INFO: SET LOCK POSITION, new lock pos = 77.407, 204.420
Guiding Ends at 2026-07-14 21:42:46

Log Summary: calcnt:1 gcnt:1 gdur:19 gacnt:5
Log closed at 2026-07-14 21:43:00
""";

    // Guiding block killed mid-CSV: no "Guiding Ends", no "Log closed", and the final row is
    // cut off after the sixth field. Two corpus files end this way.
    internal const string TruncatedLog = """
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-07-14 20:13:33

Guiding Begins at 2026-07-14 21:42:27
Equipment Profile = AM5n_OAG_ASI174M
Pixel scale = 1.54 arc-sec/px, Binning = 1, Focal length = 784 mm
Exposure = 500 ms
Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
1,1.228,"Mount",0.330,0.544,0.556,-0.189,0.350,0.000,98,W,0,,,,1713,28.59,1
2,2.093,"Mount",0.524,0.132,0.151,-0.477,0.000,0.000,0,,0,,,,1702,28.36,0
3,3.149,"Mount",-0.139,-0.706,-0.7
""";

    // A "Guiding Ends" with no matching "Guiding Begins", which happens at run boundaries when
    // PHD2 was restarted mid-session.
    internal const string OrphanEndLog = """
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-07-14 20:13:33
Guiding Ends at 2026-07-14 20:14:00

Log Summary: calcnt:0 gcnt:0 gdur:0 gacnt:0
Log closed at 2026-07-14 20:15:00
""";

    // Calibration killed before the "Calibration complete" line (5 of 37 in the corpus), with a
    // STAR LOST info line inside the block.
    internal const string AbortedCalLog = """
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-07-14 20:13:33

Calibration Begins at 2026-07-14 21:41:21
Equipment Profile = ASI220mm_30F5_AM5
Pixel scale = 3.20 arc-sec/px, Binning = 1, Focal length = 150 mm
Direction,Step,dx,dy,x,y,Dist
West,0,0.000,0.000,86.217,202.308,0.000
West,1,0.175,1.175,86.042,201.133,1.188
INFO: STAR LOST during calibration, Mass= 0, SNR= 0.00, Error= 4, Status=Star lost - low HFD
Log closed at 2026-07-14 21:45:00
""";

    // One good row, one row cut to four fields, then two good rows, then a real "Guiding Ends".
    internal const string CorruptMidSection = """
PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-07-14 20:13:33

Guiding Begins at 2026-07-14 21:42:27
Equipment Profile = AM5n_OAG_ASI174M
Pixel scale = 1.54 arc-sec/px, Binning = 1, Focal length = 784 mm
Exposure = 500 ms
Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
1,1.228,"Mount",0.330,0.544,0.556,-0.189,0.350,0.000,98,W,0,,,,1713,28.59,1
2,2.093,"Mount",0.524
3,3.001,"Mount",0.524,0.132,0.151,-0.477,0.000,0.000,0,,0,,,,1702,28.36,0
4,4.001,"Mount",0.524,0.132,0.151,-0.477,0.000,0.000,0,,0,,,,1702,28.36,0
Guiding Ends at 2026-07-14 21:42:46

Log Summary: calcnt:0 gcnt:1 gdur:19 gacnt:4
Log closed at 2026-07-14 21:43:00
""";

    // Verbatim: an ASIAIR PHD2_GuideLog file, lines 1 to 30 and 32 to 52, joined. The blank line
    // between the two guiding sections is dropped so the second section ends mid-CSV, which is
    // how every one of the ten ASIAIR files in the corpus really ends: the ASIAIR kills PHD2
    // rather than closing the log.
    //
    // Three shapes here differ from every desktop build, and each one is a silent data loss
    // until the parser is taught about it: the banner carries no version and no platform tag,
    // the pointing line omits RA, Alt and Az, and the max-duration line is prefixed with
    // "Calibration step = ". The file's "Equipment Profile" and "Mount" lines both end in a
    // trailing space; the Equipment Profile one is kept here because the empty-string profile
    // it produces is what Task 3's profile map is keyed by.
    internal const string AsiairSampleLog = """
PHD2 version, Log version 2.5. Log enabled at 2024-09-16 20:12:29

Guiding Begins at 2024-09-16 20:12:31
Dither = both axes, Dither scale = 1.000, Image noise reduction = none, Guide-frame time lapse = 0, Server enabled
Pixel scale = 6.45 arc-sec/px, Binning = 1, Focal length = 120 mm
Search region = 50 px, Star mass tolerance = 50.0%
Equipment Profile =
Camera = ZWO ASI120MM Mini, gain = 48, full size = 1280 x 960, have dark, dark dur = 2000, no defect map, pixel size = 3.8 um
Exposure = 2000 ms
Mount = iOptron CEM26/GEM28/HEM27,  connected, guiding enabled, xAngle = -76.2, xRate = 0.693, yAngle = 13.3, yRate = 1.180, parity = +/+,
X guide algorithm = Hysteresis, Hysteresis = 0.100, Aggression = 0.700, Minimum move = 0.100
Y guide algorithm = Resist Switch, Minimum move = 0.100 Aggression = 100% FastSwitch = enabled
Backlash comp = disabled, pulse = 0 ms
Calibration step = phdlab_placeholder, Max RA duration = 2000, Max DEC duration = 2000, DEC guide mode = Auto
RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Cal Dec = 31.7, Last Cal Issue = None, Timestamp = Unknown
Dec = 57.6 deg, Hour angle = -2.77 hr, Pier side = West, Rotator pos = N/A
Lock position = 876.966, 96.832, Star position = 877.037, 96.694, HFD = 5.05 px
Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
INFO: SETTLING STATE CHANGE, Settling started
1,2.385,"Mount",0.186,-0.295,0.331,0.108,0.209,0.000,301,W,0,,,,2669,34.05,0
2,4.265,"Mount",0.142,-0.202,0.230,0.088,0.159,0.000,230,W,0,,,,2504,32.75,0
3,6.335,"Mount",0.135,-0.277,0.301,0.063,0.201,0.000,290,W,0,,,,2690,34.10,0
INFO: SETTLING STATE CHANGE, Settling complete
4,8.353,"Mount",0.166,-0.296,0.327,0.089,0.220,0.000,317,W,0,,,,2577,33.18,0
5,10.230,"Mount",0.311,-0.119,0.190,0.272,0.135,0.000,195,W,0,,,,2659,34.01,0
6,12.554,"Mount",0.263,-0.286,0.341,0.185,0.224,0.185,323,W,157,S,,,2669,33.96,0
7,14.578,"Mount",0.220,-0.366,0.408,0.123,0.273,0.123,393,W,105,S,,,2487,33.17,0
8,16.559,"Mount",0.245,-0.280,0.330,0.168,0.227,0.168,328,W,143,S,,,2647,33.92,0
9,18.593,"Mount",0.274,-0.370,0.425,0.174,0.283,0.174,409,W,148,S,,,2665,33.86,0
Guiding Ends at 2024-09-16 20:12:51
Guiding Begins at 2024-09-16 20:12:59
Dither = both axes, Dither scale = 1.000, Image noise reduction = none, Guide-frame time lapse = 0, Server enabled
Pixel scale = 6.45 arc-sec/px, Binning = 1, Focal length = 120 mm
Search region = 50 px, Star mass tolerance = 50.0%
Equipment Profile =
Camera = ZWO ASI120MM Mini, gain = 48, full size = 1280 x 960, have dark, dark dur = 2000, no defect map, pixel size = 3.8 um
Exposure = 2000 ms
Mount = iOptron CEM26/GEM28/HEM27,  connected, guiding enabled, xAngle = -76.2, xRate = 0.693, yAngle = 13.3, yRate = 1.180, parity = +/+,
X guide algorithm = Hysteresis, Hysteresis = 0.100, Aggression = 0.700, Minimum move = 0.100
Y guide algorithm = Resist Switch, Minimum move = 0.100 Aggression = 100% FastSwitch = enabled
Backlash comp = disabled, pulse = 0 ms
Calibration step = phdlab_placeholder, Max RA duration = 2000, Max DEC duration = 2000, DEC guide mode = Auto
RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Cal Dec = 31.7, Last Cal Issue = None, Timestamp = Unknown
Dec = 57.6 deg, Hour angle = -2.76 hr, Pier side = West, Rotator pos = N/A
Lock position = 877.595, 96.360, Star position = 877.644, 96.201, HFD = 4.61 px
Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
INFO: SETTLING STATE CHANGE, Settling started
1,2.297,"Mount",0.158,-0.216,0.248,0.100,0.156,0.000,225,W,0,,,,2831,35.11,0
2,4.409,"Mount",0.182,-0.376,0.408,0.084,0.268,0.000,387,W,0,,,,2672,34.14,0
3,6.243,"Mount",0.202,-0.159,0.203,0.156,0.147,0.000,212,W,0,,,,2771,34.70,0
INFO: SETTLING STATE CHANGE, Settling complete
""";

    // Both "Equipment Profile =" lines in the file on disk end in a trailing space. It is
    // restored here at run time rather than carried in the literal above, because every editor
    // and writing tool in this toolchain trims trailing whitespace off a source line. Both forms
    // exist in the corpus and a case drives each of them, to pin that they agree.
    internal static readonly string AsiairSampleLogWithProfileSpace =
        AsiairSampleLog.Replace("Equipment Profile =", "Equipment Profile = ", StringComparison.Ordinal);

    // A guide log whose banner never appeared: the head of the file was lost. It has guide-log
    // content but the parser has no run to hang it on.
    internal const string HeadlessLog = """
Guiding Begins at 2024-09-16 20:12:31
Pixel scale = 6.45 arc-sec/px, Binning = 1, Focal length = 120 mm
Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
1,2.385,"Mount",0.186,-0.295,0.331,0.108,0.209,0.000,301,W,0,,,,2669,34.05,0
Guiding Ends at 2024-09-16 20:12:51
""";

    // A banner shape no released PHD2 writes today. This is the class of defect the ASIAIR
    // banner was for a year: unmatched, zero runs, no complaint.
    internal const string UnknownBannerLog = """
PHD2 version 3.0.0 (Windows) :: Log version 3.0 :: enabled 2027-01-01 00:00:00
Guiding Begins at 2027-01-01 00:00:01
Guiding Ends at 2027-01-01 00:10:01
""";
}

// Shared double comparison for the two Phd2 case files. Every parsed double is compared to six
// decimals rather than exactly: exact equality on a parsed double is a flake waiting for a
// different JIT.
internal static class Phd2Assert
{
    internal static void Near(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, 6);
    }
}
