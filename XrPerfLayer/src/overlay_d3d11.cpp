#include "overlay_d3d11.h"

#include <d3d12.h>
#include <openxr/openxr_platform.h>

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace xrperf {

namespace {

constexpr wchar_t kOverlayKey[] = L"Software\\XrPerf\\Overlay";
constexpr uint8_t kBackgroundAlpha = 150;

bool ReadDword(const wchar_t* name, uint32_t& value) {
	DWORD data = 0;
	DWORD size = sizeof(data);
	if (RegGetValueW(HKEY_CURRENT_USER, kOverlayKey, name, RRF_RT_REG_DWORD, nullptr, &data, &size) != ERROR_SUCCESS) {
		return false;
	}
	value = data;
	return true;
}

void ReadBool(const wchar_t* name, bool& value) {
	uint32_t data = 0;
	if (ReadDword(name, data)) {
		value = data != 0;
	}
}

} // namespace

OverlayD3D11::~OverlayD3D11() {
	Shutdown();
}

void OverlayD3D11::Initialize(ID3D11Device* device, XrSession session, const OverlayDispatch& dispatch) {
	Shutdown();
	if (!device) {
		return;
	}
	m_device = device;
	m_device->GetImmediateContext(&m_context);
	m_session = session;
	m_xr = dispatch;
	m_failed = false;
	m_lastConfigQpc = 0;
}

void OverlayD3D11::Shutdown() {
	DestroyResources();
	m_context.Reset();
	m_device.Reset();
	m_session = XR_NULL_HANDLE;
	m_windowStartQpc = 0;
	m_frames = 0;
	m_sumAppCpu = m_sumRenderCpu = m_sumGpu = 0;
	m_gpuFrames = 0;
}

void OverlayD3D11::DestroyResources() {
	if (m_swapchain != XR_NULL_HANDLE && m_xr.destroySwapchain) {
		m_xr.destroySwapchain(m_swapchain);
	}
	if (m_viewSpace != XR_NULL_HANDLE && m_xr.destroySpace) {
		m_xr.destroySpace(m_viewSpace);
	}
	m_swapchain = XR_NULL_HANDLE;
	m_viewSpace = XR_NULL_HANDLE;
	m_images.clear();
	m_hasImage = false;

	if (m_dc) {
		if (m_oldBitmap) {
			SelectObject(m_dc, m_oldBitmap);
		}
		DeleteDC(m_dc);
	}
	if (m_bitmap) {
		DeleteObject(m_bitmap);
	}
	if (m_font) {
		DeleteObject(m_font);
	}
	m_dc = nullptr;
	m_bitmap = nullptr;
	m_oldBitmap = nullptr;
	m_font = nullptr;
	m_bits = nullptr;
}

void OverlayD3D11::ReloadConfig(int64_t now, int64_t freq) {
	if (m_lastConfigQpc != 0 && now - m_lastConfigQpc < freq) {
		return;
	}
	m_lastConfigQpc = now;

	OverlayConfig config;
	ReadBool(L"Enabled", config.enabled);
	bool autoWithRecording = false;
	bool recording = false;
	ReadBool(L"AutoWithRecording", autoWithRecording);
	ReadBool(L"RecordingActive", recording);
	config.enabled = config.enabled || (autoWithRecording && recording);
	ReadBool(L"ShowFps", config.showFps);
	ReadBool(L"ShowFrameTime", config.showFrameTime);
	ReadBool(L"ShowCpu", config.showCpu);
	ReadBool(L"ShowGpu", config.showGpu);
	ReadBool(L"ShowResolution", config.showResolution);
	ReadBool(L"ShowApp", config.showApp);
	ReadDword(L"Position", config.position);
	ReadDword(L"Scale", config.scalePercent);
	config.position = std::min<uint32_t>(config.position, 3);
	config.scalePercent = std::clamp<uint32_t>(config.scalePercent, 50, 200);
	m_config = config;
}

bool OverlayD3D11::EnsureResources() {
	if (m_swapchain != XR_NULL_HANDLE) {
		return true;
	}
	if (m_failed || !m_device || m_session == XR_NULL_HANDLE || !m_xr.createSwapchain ||
		!m_xr.enumerateSwapchainFormats || !m_xr.enumerateSwapchainImages || !m_xr.createReferenceSpace) {
		return false;
	}
	m_failed = true; // cleared on success

	uint32_t formatCount = 0;
	if (XR_FAILED(m_xr.enumerateSwapchainFormats(m_session, 0, &formatCount, nullptr)) || formatCount == 0) {
		return false;
	}
	std::vector<int64_t> formats(formatCount);
	if (XR_FAILED(m_xr.enumerateSwapchainFormats(m_session, formatCount, &formatCount, formats.data()))) {
		return false;
	}

	const int64_t preferred[] = {DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
								 DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM};
	int64_t format = 0;
	for (int64_t candidate : preferred) {
		if (std::find(formats.begin(), formats.end(), candidate) != formats.end()) {
			format = candidate;
			break;
		}
	}
	if (format == 0) {
		m_status = OverlayStatus::NoSwapchainFormat;
		return false;
	}
	m_swapRedBlue = format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || format == DXGI_FORMAT_R8G8B8A8_UNORM;

	XrSwapchainCreateInfo createInfo{XR_TYPE_SWAPCHAIN_CREATE_INFO};
	createInfo.usageFlags = XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
	createInfo.format = format;
	createInfo.sampleCount = 1;
	createInfo.width = kTexWidth;
	createInfo.height = kTexHeight;
	createInfo.faceCount = 1;
	createInfo.arraySize = 1;
	createInfo.mipCount = 1;
	if (XR_FAILED(m_xr.createSwapchain(m_session, &createInfo, &m_swapchain))) {
		m_swapchain = XR_NULL_HANDLE;
		m_status = OverlayStatus::SwapchainFailed;
		return false;
	}

	uint32_t imageCount = 0;
	m_xr.enumerateSwapchainImages(m_swapchain, 0, &imageCount, nullptr);
	std::vector<XrSwapchainImageD3D11KHR> images(imageCount, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
	if (imageCount == 0 ||
		XR_FAILED(m_xr.enumerateSwapchainImages(m_swapchain, imageCount, &imageCount,
												reinterpret_cast<XrSwapchainImageBaseHeader*>(images.data())))) {
		DestroyResources();
		m_status = OverlayStatus::SwapchainImagesFailed;
		return false;
	}
	for (const auto& image : images) {
		m_images.push_back(image.texture);
	}

	XrReferenceSpaceCreateInfo spaceInfo{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
	spaceInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
	spaceInfo.poseInReferenceSpace.orientation.w = 1.0f;
	if (XR_FAILED(m_xr.createReferenceSpace(m_session, &spaceInfo, &m_viewSpace))) {
		m_viewSpace = XR_NULL_HANDLE;
		DestroyResources();
		m_status = OverlayStatus::SpaceFailed;
		return false;
	}

	BITMAPINFO bmi{};
	bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
	bmi.bmiHeader.biWidth = kTexWidth;
	bmi.bmiHeader.biHeight = -kTexHeight; // top-down
	bmi.bmiHeader.biPlanes = 1;
	bmi.bmiHeader.biBitCount = 32;
	bmi.bmiHeader.biCompression = BI_RGB;
	m_dc = CreateCompatibleDC(nullptr);
	void* bits = nullptr;
	m_bitmap = m_dc ? CreateDIBSection(m_dc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0) : nullptr;
	if (!m_bitmap) {
		DestroyResources();
		m_status = OverlayStatus::GdiFailed;
		return false;
	}
	m_bits = static_cast<uint32_t*>(bits);
	m_oldBitmap = SelectObject(m_dc, m_bitmap);
	m_font = CreateFontW(-(kLineHeight - 8), 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
						 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, FIXED_PITCH | FF_MODERN,
						 L"Consolas");
	if (m_font) {
		SelectObject(m_dc, m_font);
	}
	SetBkMode(m_dc, TRANSPARENT);
	m_upload.resize(static_cast<size_t>(kTexWidth) * kTexHeight);

	m_failed = false;
	return true;
}

void OverlayD3D11::Render(const OverlayFrameInfo& info) {
	const double seconds = static_cast<double>(info.frameQpc - m_windowStartQpc) / info.qpcFrequency;
	const double fps = seconds > 0 ? m_frames / seconds : 0.0;
	const double frameMs = m_frames > 0 ? seconds * 1000.0 / m_frames : 0.0;
	const double appCpuMs = m_frames > 0 ? m_sumAppCpu / 1000.0 / m_frames : 0.0;
	const double renderCpuMs = m_frames > 0 ? m_sumRenderCpu / 1000.0 / m_frames : 0.0;
	const double gpuMs = m_gpuFrames > 0 ? m_sumGpu / 1000.0 / m_gpuFrames : 0.0;

	struct Line {
		char text[64];
		COLORREF color;
	};
	std::vector<Line> lines;
	const COLORREF white = RGB(235, 235, 235);
	const COLORREF green = RGB(90, 230, 110);
	const COLORREF orange = RGB(255, 170, 40);
	const COLORREF red = RGB(255, 80, 70);
	const double budgetMs = info.refreshRate > 0 ? 1000.0 / info.refreshRate : 0.0;
	auto budgetColor = [&](double ms) {
		if (budgetMs <= 0) {
			return white;
		}
		return ms <= budgetMs * 0.85 ? green : (ms <= budgetMs ? orange : red);
	};

	if (m_config.showApp && info.appName && info.appName[0]) {
		Line line{{}, white};
		std::snprintf(line.text, sizeof(line.text), "%.24s", info.appName);
		lines.push_back(line);
	}
	if (m_config.showFps) {
		Line line{{}, white};
		if (info.refreshRate > 0) {
			std::snprintf(line.text, sizeof(line.text), "FPS   %5.1f / %.0f", fps, info.refreshRate);
			line.color = fps >= info.refreshRate * 0.97 ? green : (fps >= info.refreshRate * 0.85 ? orange : red);
		} else {
			std::snprintf(line.text, sizeof(line.text), "FPS   %5.1f", fps);
		}
		lines.push_back(line);
	}
	if (m_config.showFrameTime) {
		Line line{{}, white};
		std::snprintf(line.text, sizeof(line.text), "Frame %5.1f ms", frameMs);
		lines.push_back(line);
	}
	if (m_config.showCpu) {
		Line line{{}, budgetColor(appCpuMs)};
		std::snprintf(line.text, sizeof(line.text), "CPU   %5.1f ms (+%.1f)", appCpuMs, renderCpuMs);
		lines.push_back(line);
	}
	if (m_config.showGpu) {
		Line line{{}, white};
		if (m_gpuFrames > 0) {
			line.color = budgetColor(gpuMs);
			std::snprintf(line.text, sizeof(line.text), "GPU   %5.1f ms", gpuMs);
		} else {
			std::snprintf(line.text, sizeof(line.text), "GPU     n/a");
		}
		lines.push_back(line);
	}
	if (m_config.showResolution && info.width > 0) {
		Line line{{}, white};
		std::snprintf(line.text, sizeof(line.text), "Res   %ux%u", info.width, info.height);
		lines.push_back(line);
	}
	if (lines.empty()) {
		m_usedHeight = 0;
		return;
	}

	const int maxLines = (kTexHeight - 16) / kLineHeight;
	if (static_cast<int>(lines.size()) > maxLines) {
		lines.resize(maxLines);
	}
	m_usedHeight = static_cast<int>(lines.size()) * kLineHeight + 16;

	std::fill(m_bits, m_bits + static_cast<size_t>(kTexWidth) * kTexHeight, 0u);
	GdiFlush();
	int y = 8;
	for (const Line& line : lines) {
		SetTextColor(m_dc, line.color);
		TextOutA(m_dc, 14, y, line.text, static_cast<int>(std::strlen(line.text)));
		y += kLineHeight;
	}
	GdiFlush();

	// GDI renders onto black, so the colour is already premultiplied; derive alpha from coverage.
	const size_t count = static_cast<size_t>(kTexWidth) * kTexHeight;
	for (size_t i = 0; i < count; ++i) {
		const uint32_t px = m_bits[i];
		uint32_t b = px & 0xFF;
		const uint32_t g = (px >> 8) & 0xFF;
		uint32_t r = (px >> 16) & 0xFF;
		const uint32_t a = std::max<uint32_t>(kBackgroundAlpha, std::max({r, g, b}));
		if (m_swapRedBlue) {
			std::swap(r, b);
		}
		m_upload[i] = b | (g << 8) | (r << 16) | (a << 24);
	}

	uint32_t index = 0;
	XrSwapchainImageAcquireInfo acquireInfo{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
	if (XR_FAILED(m_xr.acquireSwapchainImage(m_swapchain, &acquireInfo, &index)) || index >= m_images.size()) {
		return;
	}
	XrSwapchainImageWaitInfo waitInfo{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
	waitInfo.timeout = 100000000; // 100 ms
	if (XR_SUCCEEDED(m_xr.waitSwapchainImage(m_swapchain, &waitInfo))) {
		m_context->UpdateSubresource(m_images[index], 0, nullptr, m_upload.data(), kTexWidth * 4, 0);
		XrSwapchainImageReleaseInfo releaseInfo{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
		if (XR_SUCCEEDED(m_xr.releaseSwapchainImage(m_swapchain, &releaseInfo))) {
			m_hasImage = true;
		}
	}
}

const XrCompositionLayerBaseHeader* OverlayD3D11::OnFrame(const OverlayFrameInfo& info) {
	if (info.qpcFrequency <= 0) {
		return nullptr;
	}

	ReloadConfig(info.frameQpc, info.qpcFrequency);
	if (!m_config.enabled) {
		if (m_swapchain != XR_NULL_HANDLE) {
			DestroyResources();
		}
		m_windowStartQpc = 0;
		m_status = OverlayStatus::Disabled;
		return nullptr;
	}
	if (!m_device) {
		m_status = OverlayStatus::UnsupportedGraphicsApi;
		return nullptr;
	}

	if (m_windowStartQpc == 0) {
		m_windowStartQpc = info.frameQpc;
		m_frames = 0;
		m_sumAppCpu = m_sumRenderCpu = m_sumGpu = 0;
		m_gpuFrames = 0;
	}
	m_frames++;
	m_sumAppCpu += info.appCpuUs;
	m_sumRenderCpu += info.renderCpuUs;
	if (info.gpuValid) {
		m_sumGpu += info.gpuUs;
		m_gpuFrames++;
	}

	if (!EnsureResources()) {
		return nullptr;
	}

	// Refresh the text four times per second.
	if (info.frameQpc - m_windowStartQpc >= info.qpcFrequency / 4 || !m_hasImage) {
		Render(info);
		m_windowStartQpc = info.frameQpc;
		m_frames = 0;
		m_sumAppCpu = m_sumRenderCpu = m_sumGpu = 0;
		m_gpuFrames = 0;
	}
	if (!m_hasImage || m_usedHeight <= 0) {
		m_status = m_usedHeight <= 0 ? OverlayStatus::NothingSelected : m_status;
		return nullptr;
	}
	m_status = OverlayStatus::Active;

	const float scale = m_config.scalePercent / 100.0f;
	const float width = 0.28f * scale;
	const float height = width * m_usedHeight / kTexWidth;
	const bool right = m_config.position == 1 || m_config.position == 3;
	const bool bottom = m_config.position >= 2;

	m_quad = XrCompositionLayerQuad{XR_TYPE_COMPOSITION_LAYER_QUAD};
	m_quad.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
	m_quad.space = m_viewSpace;
	m_quad.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
	m_quad.subImage.swapchain = m_swapchain;
	m_quad.subImage.imageRect.offset = {0, 0};
	m_quad.subImage.imageRect.extent = {kTexWidth, m_usedHeight};
	m_quad.subImage.imageArrayIndex = 0;
	m_quad.pose.orientation.w = 1.0f;
	m_quad.pose.position.x = (right ? 1.0f : -1.0f) * (0.22f + width * 0.5f - 0.14f);
	m_quad.pose.position.y = (bottom ? -1.0f : 1.0f) * (0.16f + height * 0.5f - 0.06f);
	m_quad.pose.position.z = -1.0f;
	m_quad.size = {width, height};
	return reinterpret_cast<const XrCompositionLayerBaseHeader*>(&m_quad);
}

} // namespace xrperf
