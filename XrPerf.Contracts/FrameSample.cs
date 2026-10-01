namespace XrPerf.Contracts
{
    public struct FrameSample
    {
        public long FrameIndex;
        public long WaitFrameQpc;
        public long BeginFrameQpc;
        public long EndFrameQpc;
        public long PredictedDisplayTimeNs;
        public long PredictedDisplayPeriodNs;
        public uint AppCpuUs;
        public uint RenderCpuUs;
        public uint AppGpuUs;
        public FrameFlags Flags;
    }

    public sealed class LayerSessionInfo
    {
        public int ProcessId { get; set; }
        public long QpcFrequency { get; set; }
        public long SessionStartQpc { get; set; }
        public GraphicsApi GraphicsApi { get; set; }
        public uint SwapchainWidth { get; set; }
        public uint SwapchainHeight { get; set; }
        public uint SwapchainSampleCount { get; set; }
        public long SwapchainFormat { get; set; }
        public float DisplayRefreshRate { get; set; }
        public uint ViewCount { get; set; }
        public float[] FovLeft { get; set; } = new float[4];
        public float[] FovRight { get; set; } = new float[4];
        public uint SessionState { get; set; }
        public string AppName { get; set; } = string.Empty;
        public string EngineName { get; set; } = string.Empty;
        public string RuntimeName { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
    }
}
