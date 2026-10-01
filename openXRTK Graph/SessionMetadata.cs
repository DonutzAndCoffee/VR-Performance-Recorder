using openXRTK_Graph.XrPerf;

namespace openXRTK_Graph;

public class SessionMetadata
{
    public string RunningApp { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }

    // From _openxrtk.json
    public int? TargetRate { get; set; }
    public int? Scaling { get; set; }
    public int? Sharpness { get; set; }
    public int? ScalingType { get; set; }
    public int? ResolutionWidth { get; set; }
    public int? ResolutionHeight { get; set; }
    public int? HmdResX { get; set; }
    public int? HmdResY { get; set; }

    // From renderer JSON (e.g. iRacing rendererDX11OpenXR.ini)
    public string? GameSettingsFile { get; set; }
    public Dictionary<string, Dictionary<string, string>> GameSettings { get; set; } = [];

    // From _xrperf.json (XrPerf recorder: label, SimHub context, layer info, system info, laps)
    public SessionFile? XrPerfSession { get; set; }

    public string? GetGameSetting(string section, string key)
    {
        if (GameSettings.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var val))
            return val;
        return null;
    }

    public string ScalingTypeName => ScalingType switch
    {
        0 => "NIS",
        1 => "FSR",
        2 => "CAS",
        _ => "?"
    };
}
