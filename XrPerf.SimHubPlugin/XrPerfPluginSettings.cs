namespace XrPerf.SimHubPlugin
{
    public class XrPerfPluginSettings
    {
        public bool AutoStartOnSession { get; set; } = false;
        public bool AutoStopOnSessionEnd { get; set; } = true;
        public bool TrackLaps { get; set; } = true;
        public string LabelTemplate { get; set; } = "{Game} {Track} {SessionType}";
        public string Resolution { get; set; } = "";
        public string AntiAliasing { get; set; } = "";
        public string Notes { get; set; } = "";
        public int RowIntervalMs { get; set; } = 1000;
        public bool RawFrames { get; set; } = false;
    }
}
