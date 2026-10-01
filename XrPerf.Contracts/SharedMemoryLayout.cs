namespace XrPerf.Contracts
{
    /// <summary>
    /// Byte layout of the shared memory written by XrPerfLayer. Must match docs/XrPerf-Protocol.md and the C++ header.
    /// </summary>
    public static class SharedMemoryLayout
    {
        public const string MappingName = @"Local\XrPerf_Frames";
        public const uint Magic = 0x46505258; // "XRPF"
        public const uint Version = 1;
        public const int HeaderSize = 512;
        public const int SampleSize = 64;
        public const int Capacity = 4096;
        public const long TotalSize = HeaderSize + (long)SampleSize * Capacity;
        public const int StringSlotSize = 64;

        public static class Header
        {
            public const int Magic = 0;
            public const int Version = 4;
            public const int HeaderSize = 8;
            public const int SampleSize = 12;
            public const int Capacity = 16;
            public const int ProcessId = 20;
            public const int WriteIndex = 24;
            public const int QpcFrequency = 32;
            public const int SessionStartQpc = 40;
            public const int GraphicsApi = 48;
            public const int SwapchainWidth = 52;
            public const int SwapchainHeight = 56;
            public const int SwapchainSampleCount = 60;
            public const int SwapchainFormat = 64;
            public const int DisplayRefreshRate = 72;
            public const int ViewCount = 76;
            public const int FovLeft = 80;
            public const int FovRight = 96;
            public const int SessionState = 112;
            public const int AppName = 120;
            public const int EngineName = 184;
            public const int RuntimeName = 248;
            public const int SystemName = 312;
        }

        public static class Sample
        {
            public const int FrameIndex = 0;
            public const int WaitFrameQpc = 8;
            public const int BeginFrameQpc = 16;
            public const int EndFrameQpc = 24;
            public const int PredictedDisplayTimeNs = 32;
            public const int PredictedDisplayPeriodNs = 40;
            public const int AppCpuUs = 48;
            public const int RenderCpuUs = 52;
            public const int AppGpuUs = 56;
            public const int Flags = 60;
        }

        public static long SampleOffset(long index) => HeaderSize + (index % Capacity) * SampleSize;
    }

    public enum GraphicsApi : uint
    {
        Unknown = 0,
        D3D11 = 1,
        D3D12 = 2,
        Vulkan = 3,
        OpenGL = 4,
    }

    [System.Flags]
    public enum FrameFlags : uint
    {
        None = 0,
        GpuValid = 1,
        ShouldRenderFalse = 2,
    }
}
