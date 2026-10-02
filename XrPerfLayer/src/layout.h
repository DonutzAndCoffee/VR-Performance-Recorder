#pragma once

// Mirrors docs/XrPerf-Protocol.md and XrPerf.Contracts/SharedMemoryLayout.cs. Keep in sync.

#include <cstddef>
#include <cstdint>

namespace xrperf {

constexpr wchar_t kMappingName[] = L"Local\\XrPerf_Frames";
constexpr uint32_t kMagic = 0x46505258; // "XRPF"
constexpr uint32_t kVersion = 1;
constexpr uint32_t kCapacity = 4096;

enum class GraphicsApi : uint32_t { Unknown = 0, D3D11 = 1, D3D12 = 2, Vulkan = 3, OpenGL = 4 };

enum FrameFlags : uint32_t { FlagNone = 0, FlagGpuValid = 1, FlagShouldRenderFalse = 2 };

#pragma pack(push, 1)
struct Header {
	uint32_t magic;
	uint32_t version;
	uint32_t headerSize;
	uint32_t sampleSize;
	uint32_t capacity;
	uint32_t processId;
	int64_t writeIndex;
	int64_t qpcFrequency;
	int64_t sessionStartQpc;
	uint32_t graphicsApi;
	uint32_t swapchainWidth;
	uint32_t swapchainHeight;
	uint32_t swapchainSampleCount;
	int64_t swapchainFormat;
	float displayRefreshRate;
	uint32_t viewCount;
	float fovLeft[4];
	float fovRight[4];
	uint32_t sessionState;
	uint32_t overlayStatus;
	char appName[64];
	char engineName[64];
	char runtimeName[64];
	char systemName[64];
	char reserved[136];
};

struct Sample {
	int64_t frameIndex;
	int64_t waitFrameQpc;
	int64_t beginFrameQpc;
	int64_t endFrameQpc;
	int64_t predictedDisplayTimeNs;
	int64_t predictedDisplayPeriodNs;
	uint32_t appCpuUs;
	uint32_t renderCpuUs;
	uint32_t appGpuUs;
	uint32_t flags;
};
#pragma pack(pop)

static_assert(sizeof(Header) == 512, "Header must be 512 bytes");
static_assert(sizeof(Sample) == 64, "Sample must be 64 bytes");
static_assert(offsetof(Header, writeIndex) == 24);
static_assert(offsetof(Header, swapchainFormat) == 64);
static_assert(offsetof(Header, fovLeft) == 80);
static_assert(offsetof(Header, sessionState) == 112);
static_assert(offsetof(Header, appName) == 120);
static_assert(offsetof(Header, systemName) == 312);
static_assert(offsetof(Sample, appCpuUs) == 48);

constexpr size_t kTotalSize = sizeof(Header) + sizeof(Sample) * kCapacity;

} // namespace xrperf
