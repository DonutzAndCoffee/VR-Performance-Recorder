namespace XrPerf.Contracts
{
    /// <summary>
    /// Column layout of recorded CSV files. The first 7 columns are identical to OpenXR Toolkit stats files.
    /// </summary>
    public static class CsvSchema
    {
        public const string TimeFormat = "yyyy-MM-dd HH:mm:ss zzz";
        public const string CompanionSuffix = "_xrperf.json";

        public static readonly string[] OxrtkColumns =
        {
            "time", "FPS", "appCPU (us)", "renderCPU (us)", "appGPU (us)", "VRAM (MB)", "VRAM (%)",
        };

        public static readonly string[] ExtendedColumns =
        {
            "CPU (%)", "GPU (%)", "RAM (MB)", "appRAM (MB)",
            "frametime avg (ms)", "frametime p99 (ms)", "FPS 1% low", "lap",
        };

        public static string Header => string.Join(",", OxrtkColumns) + "," + string.Join(",", ExtendedColumns);
    }
}
