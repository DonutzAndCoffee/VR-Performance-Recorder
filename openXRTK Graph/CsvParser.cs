using System.Globalization;
using System.IO;

namespace openXRTK_Graph;

public static class CsvParser
{
    public static List<CsvDataPoint> Parse(string filePath)
    {
        var result = new List<CsvDataPoint>();
        var lines = File.ReadAllLines(filePath);

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
                    "yyyy-MM-dd HH:mm:ss zzz",
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
                    VramPercent = ParseDouble(parts[6])
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
}
