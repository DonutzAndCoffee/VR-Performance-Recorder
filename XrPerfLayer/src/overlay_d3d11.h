#pragma once

#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>

#include <openxr/openxr.h>

#include <cstdint>
#include <string>
#include <vector>

namespace xrperf {

// Overlay settings, read from HKCU\Software\XrPerf\Overlay (written by the openXRTK Graph app).
struct OverlayConfig {
	bool enabled = false;
	bool showFps = true;
	bool showFrameTime = true;
	bool showCpu = true;
	bool showGpu = true;
	bool showResolution = false;
	bool showApp = false;
	uint32_t position = 0; // 0 = top left, 1 = top right, 2 = bottom left, 3 = bottom right
	uint32_t scalePercent = 100;
};

enum class OverlayStatus : uint32_t {
	Disabled = 0,
	Active = 1,
	UnsupportedGraphicsApi = 2,
	NoSwapchainFormat = 3,
	SwapchainFailed = 4,
	SwapchainImagesFailed = 5,
	SpaceFailed = 6,
	GdiFailed = 7,
	NothingSelected = 8,
};

struct OverlayDispatch {
	PFN_xrEnumerateSwapchainFormats enumerateSwapchainFormats = nullptr;
	PFN_xrCreateSwapchain createSwapchain = nullptr;
	PFN_xrDestroySwapchain destroySwapchain = nullptr;
	PFN_xrEnumerateSwapchainImages enumerateSwapchainImages = nullptr;
	PFN_xrAcquireSwapchainImage acquireSwapchainImage = nullptr;
	PFN_xrWaitSwapchainImage waitSwapchainImage = nullptr;
	PFN_xrReleaseSwapchainImage releaseSwapchainImage = nullptr;
	PFN_xrCreateReferenceSpace createReferenceSpace = nullptr;
	PFN_xrDestroySpace destroySpace = nullptr;
};

struct OverlayFrameInfo {
	int64_t frameQpc = 0;
	int64_t qpcFrequency = 0;
	uint32_t appCpuUs = 0;
	uint32_t renderCpuUs = 0;
	uint32_t gpuUs = 0;
	bool gpuValid = false;
	float refreshRate = 0.0f;
	uint32_t width = 0;
	uint32_t height = 0;
	const char* appName = nullptr;
};

// Small head-locked HUD rendered as an additional quad composition layer (D3D11 sessions only).
class OverlayD3D11 {
public:
	~OverlayD3D11();

	void Initialize(ID3D11Device* device, XrSession session, const OverlayDispatch& dispatch);
	void Shutdown();

	// Feeds one frame of statistics; returns the quad layer to append or nullptr if the overlay is not shown.
	const XrCompositionLayerBaseHeader* OnFrame(const OverlayFrameInfo& info);

	OverlayStatus GetStatus() const { return m_status; }

private:
	static constexpr int kTexWidth = 512;
	static constexpr int kTexHeight = 320;
	static constexpr int kLineHeight = 40;

	bool EnsureResources();
	void DestroyResources();
	void ReloadConfig(int64_t now, int64_t freq);
	void Render(const OverlayFrameInfo& info);

	Microsoft::WRL::ComPtr<ID3D11Device> m_device;
	Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_context;
	XrSession m_session = XR_NULL_HANDLE;
	OverlayDispatch m_xr;
	OverlayConfig m_config;
	int64_t m_lastConfigQpc = 0;

	XrSwapchain m_swapchain = XR_NULL_HANDLE;
	XrSpace m_viewSpace = XR_NULL_HANDLE;
	std::vector<ID3D11Texture2D*> m_images;
	bool m_swapRedBlue = false;
	bool m_hasImage = false;
	bool m_failed = false;
	OverlayStatus m_status = OverlayStatus::Disabled;
	int m_usedHeight = kLineHeight;

	HDC m_dc = nullptr;
	HBITMAP m_bitmap = nullptr;
	HGDIOBJ m_oldBitmap = nullptr;
	HFONT m_font = nullptr;
	uint32_t* m_bits = nullptr;
	std::vector<uint32_t> m_upload;

	// Stats window.
	int64_t m_windowStartQpc = 0;
	uint32_t m_frames = 0;
	uint64_t m_sumAppCpu = 0;
	uint64_t m_sumRenderCpu = 0;
	uint64_t m_sumGpu = 0;
	uint32_t m_gpuFrames = 0;

	XrCompositionLayerQuad m_quad{XR_TYPE_COMPOSITION_LAYER_QUAD};
};

} // namespace xrperf
