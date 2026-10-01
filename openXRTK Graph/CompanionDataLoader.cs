using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace openXRTK_Graph;

public static class CompanionDataLoader
{
    /// <summary>
    /// Tries to find and parse _openxrtk.json and _renderer*.json sidecar files
    /// next to the given CSV path. Returns null if neither file is found.
    /// </summary>
    public static SessionMetadata? TryLoad(string csvPath)
    {
        string dir = Path.GetDirectoryName(csvPath)!;
        string baseName = Path.GetFileNameWithoutExtension(csvPath); // e.g. iRacingSim64DX11_20260327_154624

        // Locate sidecar files next to the CSV
        string oxrPath = Path.Combine(dir, baseName + "_openxrtk.json");
        string? rendererPath = Directory.GetFiles(dir, baseName + "_renderer*.json").FirstOrDefault();

        if (!File.Exists(oxrPath) && rendererPath is null)
            return null;

        var meta = new SessionMetadata();

        if (File.Exists(oxrPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(oxrPath));
                meta.RunningApp = root?["RunningApp"]?.GetValue<string>() ?? "";
                if (DateTimeOffset.TryParse(root?["RecordedAt"]?.GetValue<string>(), out var dt))
                    meta.RecordedAt = dt;

                var s = root?["OpenXRToolkitSettings"];
                if (s is not null)
                {
                    meta.TargetRate      = s["target_rate"]?.GetValue<int>();
                    meta.Scaling         = s["scaling"]?.GetValue<int>();
                    meta.Sharpness       = s["sharpness"]?.GetValue<int>();
                    meta.ScalingType     = s["scaling_type"]?.GetValue<int>();
                    meta.ResolutionWidth  = s["resolution_width"]?.GetValue<int>();
                    meta.ResolutionHeight = s["resolution_height"]?.GetValue<int>();
                    meta.HmdResX         = s["HMD_res_x"]?.GetValue<int>();
                    meta.HmdResY         = s["HMD_res_y"]?.GetValue<int>();
                }
            }
            catch { }
        }

        if (rendererPath is not null && File.Exists(rendererPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(rendererPath));
                if (string.IsNullOrEmpty(meta.RunningApp))
                    meta.RunningApp = root?["RunningApp"]?.GetValue<string>() ?? "";

                var gameSettings = root?["GameSettings"];
                meta.GameSettingsFile = gameSettings?["SettingsFile"]?.GetValue<string>();

                var values = gameSettings?["Values"]?.AsObject();
                if (values is not null)
                {
                    foreach (var section in values)
                    {
                        var sectionDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (section.Value is JsonObject sectionObj)
                        {
                            foreach (var kv in sectionObj)
                                sectionDict[kv.Key] = kv.Value?.GetValue<string>() ?? "";
                        }
                        meta.GameSettings[section.Key] = sectionDict;
                    }
                }
            }
            catch { }
        }

        return meta;
    }
}
