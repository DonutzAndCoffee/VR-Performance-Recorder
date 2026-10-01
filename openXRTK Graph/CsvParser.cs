using System.Globalization;
using System.IO;

namespace openXRTK_Graph;

public static class CsvParser
{
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss.fff zzz"];

    public static List<CsvDataPoint> Parse(string filePath)
    {
        var result = new List<CsvDataPoint>();
        var lines = File.ReadAllLines(filePath);
        if (lines.Length == 0) return result;

        // Extended XrPerf columns are located by header name; OXRTK files simply don't have them.
        var columns = lines[0].Split(',').Select(h => h.Trim()).ToList();
        int Col(string name) => columns.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        int cpuCol = Col("CPU (%)");
        int gpuCol = Col("GPU (%)");
        int ramCol = Col("RAM (MB)");
        int appRamCol = Col("appRAM (MB)");
        int ftAvgCol = Col("frametime avg (ms)");
        int ftP99Col = Col("frametime p99 (ms)");
        int lowCol = Col("FPS 1% low");
        int lapCol = Col("lap");

        // Skip header line
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Split carefully: the timestamp has a space inside it, so there are two leading comma-separated tokens
            // Format: "2026-04-11 16:14:31 +0200,66.2,14719,6963,13045,4829,31"
            // The datetime portion is "2026-04-11 16:14:31 +0200" (no comma inside it)
            var parts = line.Split(',');
            if (parts.Length < 7) continue;

            try
            {
                // The timestamp is in parts[0] exactly
                string timestampStr = parts[0].Trim();
                if (!DateTimeOffset.TryParseExact(timestampStr,
                    TimestampFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTimeOffset dto))
                    continue;

                var point = new CsvDataPoint
                {
                    Time = dto.LocalDateTime,
                    Fps = ParseDouble(parts[1]),
                    AppCpuUs = ParseDouble(parts[2]),
                    RenderCpuUs = ParseDouble(parts[3]),
                    AppGpuUs = ParseDouble(parts[4]),
                    VramMb = ParseDouble(parts[5]),
                    VramPercent = ParseDouble(parts[6]),
                    CpuPercent = ParseOptional(parts, cpuCol),
                    GpuPercent = ParseOptional(parts, gpuCol),
                    RamMb = ParseOptional(parts, ramCol),
                    AppRamMb = ParseOptional(parts, appRamCol),
                    FrameTimeAvgMs = ParseOptional(parts, ftAvgCol),
                    FrameTimeP99Ms = ParseOptional(parts, ftP99Col),
                    Fps1PercentLow = ParseOptional(parts, lowCol),
                    Lap = ParseOptional(parts, lapCol) is double lap ? (int)lap : null,
                };
                result.Add(point);
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return result;
    }

    private static double ParseDouble(string s)
    {
        return double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private static double? ParseOptional(string[] parts, int index)
    {
        if (index < 0 || index >= parts.Length) return null;
        return double.TryParse(parts[index].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
