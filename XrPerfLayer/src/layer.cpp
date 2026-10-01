// XrPerfLayer - runtime independent OpenXR performance capture layer.
// Hooks the frame loop and publishes per-frame timings via shared memory (see docs/XrPerf-Protocol.md).

#include "gpu_timer_d3d11.h"
#include "shared_memory.h"

#include <d3d11.h>
#include <d3d12.h>

#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <openxr/openxr_loader_negotiation.h>

#include <algorithm>
#include <cstring>
#include <mutex>

#define LAYER_NAME "XR_APILAYER_DONUTZ_xrperf"

namespace {

using namespace xrperf;

struct LayerState {
	std::mutex mutex;
	XrInstance instance = XR_NULL_HANDLE;
	XrSession session = XR_NULL_HANDLE;
	SharedMemoryWriter shm;
	GpuTimerD3D11 gpuTimerD3D11;
	uint32_t lastGpuUs = 0;
	bool lastGpuValid = false;

	char appName[64] = {};
	char engineName[64] = {};
	char runtimeName[64] = {};

	PFN_xrGetInstanceProcAddr nextGetInstanceProcAddr = nullptr;
	PFN_xrDestroyInstance nextDestroyInstance = nullptr;
	PFN_xrCreateSession nextCreateSession = nullptr;
	PFN_xrDestroySession nextDestroySession = nullptr;
	PFN_xrCreateSwapchain nextCreateSwapchain = nullptr;
	PFN_xrLocateViews nextLocateViews = nullptr;
	PFN_xrWaitFrame nextWaitFrame = nullptr;
	PFN_xrBeginFrame nextBeginFrame = nullptr;
	PFN_xrEndFrame nextEndFrame = nullptr;
	PFN_xrGetSystemProperties nextGetSystemProperties = nullptr;
	PFN_xrGetInstanceProperties nextGetInstanceProperties = nullptr;

	// Frame timing state.
	int64_t lastWaitQpc = 0;
	int64_t lastBeginQpc = 0;
	XrTime predictedDisplayTime = 0;
	XrDuration predictedDisplayPeriod = 0;
	bool shouldRender = true;
};

LayerState g_state;

int64_t Now() {
	LARGE_INTEGER value{};
	QueryPerformanceCounter(&value);
	return value.QuadPart;
}

uint32_t QpcToUs(int64_t delta) {
	if (delta <= 0 || !g_state.shm.IsOpen()) {
		return 0;
	}
	const int64_t freq = g_state.shm.GetHeader()->qpcFrequency;
	return static_cast<uint32_t>((delta * 1000000) / freq);
}

template <typename T>
void Resolve(XrInstance instance, const char* name, T& target) {
	g_state.nextGetInstanceProcAddr(instance, name, reinterpret_cast<PFN_xrVoidFunction*>(&target));
}

GraphicsApi DetectGraphicsApi(const void* next) {
	auto header = static_cast<const XrBaseInStructure*>(next);
	while (header) {
		switch (header->type) {
		case XR_TYPE_GRAPHICS_BINDING_D3D11_KHR:
			return GraphicsApi::D3D11;
		case XR_TYPE_GRAPHICS_BINDING_D3D12_KHR:
			return GraphicsApi::D3D12;
		case XR_TYPE_GRAPHICS_BINDING_VULKAN_KHR:
			return GraphicsApi::Vulkan;
		case XR_TYPE_GRAPHICS_BINDING_OPENGL_WIN32_KHR:
			return GraphicsApi::OpenGL;
		default:
			break;
		}
		header = header->next;
	}
	return GraphicsApi::Unknown;
}

const XrGraphicsBindingD3D11KHR* FindD3D11Binding(const void* next) {
    auto header = static_cast<const XrBaseInStructure*>(next);
    while (header) {
        if (header->type == XR_TYPE_GRAPHICS_BINDING_D3D11_KHR) {
            return reinterpret_cast<const XrGraphicsBindingD3D11KHR*>(header);
        }
        header = header->next;
    }
    return nullptr;
}

// ---------------------------------------------------------------------------------------------
// Hooked functions
// ---------------------------------------------------------------------------------------------

XRAPI_ATTR XrResult XRAPI_CALL Hook_xrCreateSession(XrInstance instance, const XrSessionCreateInfo* createInfo,
													XrSession* session) {
	const XrResult result = g_state.nextCreateSession(instance, createInfo, session);
	if (XR_FAILED(result)) {
		return result;
	}

	std::lock_guard lock(g_state.mutex);
	g_state.session = *session;
	g_state.lastWaitQpc = 0;
	g_state.lastBeginQpc = 0;
	g_state.lastGpuUs = 0;
	g_state.lastGpuValid = false;

	if (const XrGraphicsBindingD3D11KHR* d3d11 = FindD3D11Binding(createInfo->next)) {
		g_state.gpuTimerD3D11.Initialize(d3d11->device);
	}

	if (!g_state.shm.Open()) {
		return result;
	}
	g_state.shm.Reset();

	Header* header = g_state.shm.GetHeader();
	header->graphicsApi = static_cast<uint32_t>(DetectGraphicsApi(createInfo->next));
	CopyString(header->appName, g_state.appName);
	CopyString(header->engineName, g_state.engineName);
	CopyString(header->runtimeName, g_state.runtimeName);

	if (g_state.nextGetSystemProperties) {
		XrSystemProperties props{XR_TYPE_SYSTEM_PROPERTIES};
		if (XR_SUCCEEDED(g_state.nextGetSystemProperties(instance, createInfo->systemId, &props))) {
			CopyString(header->systemName, props.systemName);
		}
	}

	return result;
}

XRAPI_ATTR XrResult XRAPI_CALL Hook_xrDestroySession(XrSession session) {
	const XrResult result = g_state.nextDestroySession(session);

	std::lock_guard lock(g_state.mutex);
	if (session == g_state.session) {
		g_state.session = XR_NULL_HANDLE;
		g_state.gpuTimerD3D11.Shutdown();
		g_state.shm.Close();
	}
	return result;
}

XRAPI_ATTR XrResult XRAPI_CALL Hook_xrCreateSwapchain(XrSession session, const XrSwapchainCreateInfo* createInfo,
													  XrSwapchain* swapchain) {
	const XrResult result = g_state.nextCreateSwapchain(session, createInfo, swapchain);
	if (XR_FAILED(result) || !(createInfo->usageFlags & XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT)) {
		return result;
	}

	std::lock_guard lock(g_state.mutex);
	if (Header* header = g_state.shm.GetHeader()) {
		const uint64_t area = static_cast<uint64_t>(createInfo->width) * createInfo->height;
		const uint64_t currentArea = static_cast<uint64_t>(header->swapchainWidth) * header->swapchainHeight;
		if (area > currentArea) {
			header->swapchainWidth = createInfo->width;
			header->swapchainHeight = createInfo->height;
			header->swapchainSampleCount = createInfo->sampleCount;
			header->swapchainFormat = createInfo->format;
		}
	}
	return result;
}

XRAPI_ATTR XrResult XRAPI_CALL Hook_xrLocateViews(XrSession session, const XrViewLocateInfo* viewLocateInfo,
												  XrViewState* viewState, uint32_t viewCapacityInput,
												  uint32_t* viewCountOutput, XrView* views) {
	const XrResult result =
		g_state.nextLocateViews(session, viewLocateInfo, viewState, viewCapacityInput, viewCountOutput, views);
	if (XR_FAILED(result) || !views || viewCapacityInput < 2 ||
		viewLocateInfo->viewConfigurationType != XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO) {
		return result;
	}

	if (Header* header = g_state.shm.GetHeader()) {
		header->viewCount = *viewCountOutput;
		const XrFovf& l = views[0].fov;
		const XrFovf& r = views[1].fov;
		header->fovLeft[0] = l.angleLeft;
		header->fovLeft[1] = l.angleRight;
		header->fovLeft[2] = l.angleUp;
		header->fovLeft[3] = l.angleDown;
		header->fovRight[0] = r.angleLeft;
		header->fovRight[1] = r.angleRight;
		header->fovRight[2] = r.angleUp;
		header->fovRight[3] = r.angleDown;
	}
	return result;
}

XRAPI_ATTR XrResult XRAPI_CALL Hook_xrWaitFrame(XrSession session, const XrFrameWaitInfo* frameWaitInfo,
												XrFrameState* frameState) {
	const XrResult result = g_state.nextWaitFrame(session, frameWaitInfo, frameState);
	if (XR_SUCCEEDED(result)) {
		std::lock_guard lock(g_state.mutex);
		g_state.lastWaitQpc = Now();
		g_state.predictedDisplayTime = frameState->predictedDisplayTime;
		g_state.predictedDisplayPeriod = frameState->predictedDisplayPeriod;
		g_state.shouldRender = frameState->shouldRender == XR_TRUE;
	}
	return result;
}

XRAPI_ATTR XrResult XRAPI_CALL Hook_xrBeginFrame(XrSession session, const XrFrameBeginInfo* frameBeginInfo) {
	{
		std::lock_guard lock(g_state.mutex);
		g_state.lastBeginQpc = Now();
		g_state.gpuTimerD3D11.BeginFrame();
	}
	return g_state.nextBeginFrame(session, frameBeginInfo);
}

XRAPI_ATTR XrResult XRAPI_CALL Hook_xrEndFrame(XrSession session, const XrFrameEndInfo* frameEndInfo) {
	const int64_t endStart = Now();
	{
		std::lock_guard lock(g_state.mutex);
		g_state.gpuTimerD3D11.EndFrame();
	}
	const XrResult result = g_state.nextEndFrame(session, frameEndInfo);
	const int64_t endDone = Now();

	std::lock_guard lock(g_state.mutex);
	if (!g_state.shm.IsOpen() || session != g_state.session) {
		return result;
	}

	Sample sample{};
	sample.waitFrameQpc = g_state.lastWaitQpc;
	sample.beginFrameQpc = g_state.lastBeginQpc;
	sample.endFrameQpc = endStart;
	sample.predictedDisplayTimeNs = g_state.predictedDisplayTime;
	sample.predictedDisplayPeriodNs = g_state.predictedDisplayPeriod;
	sample.appCpuUs = g_state.lastBeginQpc ? QpcToUs(endStart - g_state.lastBeginQpc) : 0;
	sample.renderCpuUs = QpcToUs(endDone - endStart);
	uint32_t gpuUs = 0;
	if (g_state.gpuTimerD3D11.Poll(gpuUs)) {
		g_state.lastGpuUs = gpuUs;
		g_state.lastGpuValid = true;
	}
	sample.appGpuUs = g_state.lastGpuUs;
	sample.flags = g_state.shouldRender ? FlagNone : FlagShouldRenderFalse;
	if (g_state.lastGpuValid) {
		sample.flags |= FlagGpuValid;
	}
	g_state.shm.Push(sample);

	// Per-eye render resolution: use the projection view's imageRect, since apps (e.g. iRacing) may
	// render both eyes side by side into a single, larger swapchain.
	if (frameEndInfo && frameEndInfo->layers) {
		for (uint32_t i = 0; i < frameEndInfo->layerCount; ++i) {
			const XrCompositionLayerBaseHeader* layer = frameEndInfo->layers[i];
			if (!layer || layer->type != XR_TYPE_COMPOSITION_LAYER_PROJECTION) {
				continue;
			}
			const auto* proj = reinterpret_cast<const XrCompositionLayerProjection*>(layer);
			if (proj->viewCount > 0 && proj->views) {
				const XrExtent2Di& extent = proj->views[0].subImage.imageRect.extent;
				if (extent.width > 0 && extent.height > 0) {
					Header* header = g_state.shm.GetHeader();
					header->swapchainWidth = static_cast<uint32_t>(extent.width);
					header->swapchainHeight = static_cast<uint32_t>(extent.height);
				}
			}
			break;
		}
	}

	if (g_state.predictedDisplayPeriod > 0) {
		g_state.shm.GetHeader()->displayRefreshRate
	}
	return result;
}

XRAPI_ATTR XrResult XRAPI_CALL Hook_xrDestroyInstance(XrInstance instance) {
	{
		std::lock_guard lock(g_state.mutex);
		g_state.gpuTimerD3D11.Shutdown();
		g_state.shm.Close();
		g_state.session = XR_NULL_HANDLE;
		g_state.instance = XR_NULL_HANDLE;
	}
	return g_state.nextDestroyInstance(instance);
}

XRAPI_ATTR XrResult XRAPI_CALL Layer_xrGetInstanceProcAddr(XrInstance instance, const char* name,
														   PFN_xrVoidFunction* function) {
	const XrResult result = g_state.nextGetInstanceProcAddr(instance, name, function);
	if (XR_FAILED(result)) {
		return result;
	}

#define HOOK(fn)                                                                                                   \
	if (std::strcmp(name, #fn) == 0) {                                                                             \
		*function = reinterpret_cast<PFN_xrVoidFunction>(Hook_##fn);                                               \
		return result;                                                                                             \
	}
	HOOK(xrDestroyInstance)
	HOOK(xrCreateSession)
	HOOK(xrDestroySession)
	HOOK(xrCreateSwapchain)
	HOOK(xrLocateViews)
	HOOK(xrWaitFrame)
	HOOK(xrBeginFrame)
	HOOK(xrEndFrame)
#undef HOOK

	if (std::strcmp(name, "xrGetInstanceProcAddr") == 0) {
		*function = reinterpret_cast<PFN_xrVoidFunction>(Layer_xrGetInstanceProcAddr);
	}
	return result;
}

XRAPI_ATTR XrResult XRAPI_CALL Layer_xrCreateApiLayerInstance(const XrInstanceCreateInfo* info,
															  const XrApiLayerCreateInfo* apiLayerInfo,
															  XrInstance* instance) {
	if (!apiLayerInfo || !apiLayerInfo->nextInfo ||
		std::strcmp(apiLayerInfo->nextInfo->layerName, LAYER_NAME) != 0) {
		return XR_ERROR_INITIALIZATION_FAILED;
	}

	XrApiLayerCreateInfo chainInfo = *apiLayerInfo;
	chainInfo.nextInfo = apiLayerInfo->nextInfo->next;

	const XrResult result = apiLayerInfo->nextInfo->nextCreateApiLayerInstance(info, &chainInfo, instance);
	if (XR_FAILED(result)) {
		return result;
	}

	std::lock_guard lock(g_state.mutex);
	g_state.instance = *instance;
	g_state.nextGetInstanceProcAddr = apiLayerInfo->nextInfo->nextGetInstanceProcAddr;

	Resolve(*instance, "xrDestroyInstance", g_state.nextDestroyInstance);
	Resolve(*instance, "xrCreateSession", g_state.nextCreateSession);
	Resolve(*instance, "xrDestroySession", g_state.nextDestroySession);
	Resolve(*instance, "xrCreateSwapchain", g_state.nextCreateSwapchain);
	Resolve(*instance, "xrLocateViews", g_state.nextLocateViews);
	Resolve(*instance, "xrWaitFrame", g_state.nextWaitFrame);
	Resolve(*instance, "xrBeginFrame", g_state.nextBeginFrame);
	Resolve(*instance, "xrEndFrame", g_state.nextEndFrame);
	Resolve(*instance, "xrGetSystemProperties", g_state.nextGetSystemProperties);
	Resolve(*instance, "xrGetInstanceProperties", g_state.nextGetInstanceProperties);

	CopyString(g_state.appName, info->applicationInfo.applicationName);
	CopyString(g_state.engineName, info->applicationInfo.engineName);

	if (g_state.nextGetInstanceProperties) {
		XrInstanceProperties props{XR_TYPE_INSTANCE_PROPERTIES};
		if (XR_SUCCEEDED(g_state.nextGetInstanceProperties(*instance, &props))) {
			CopyString(g_state.runtimeName, props.runtimeName);
		}
	}

	return result;
}

} // namespace

extern "C" __declspec(dllexport) XrResult XRAPI_CALL
xrNegotiateLoaderApiLayerInterface(const XrNegotiateLoaderInfo* loaderInfo, const char* layerName,
								   XrNegotiateApiLayerRequest* apiLayerRequest) {
	if (!loaderInfo || !apiLayerRequest || !layerName || std::strcmp(layerName, LAYER_NAME) != 0 ||
		loaderInfo->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO ||
		loaderInfo->structVersion != XR_LOADER_INFO_STRUCT_VERSION ||
		loaderInfo->structSize != sizeof(XrNegotiateLoaderInfo) ||
		apiLayerRequest->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST ||
		apiLayerRequest->structVersion != XR_API_LAYER_INFO_STRUCT_VERSION ||
		apiLayerRequest->structSize != sizeof(XrNegotiateApiLayerRequest) ||
		loaderInfo->minInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION ||
		loaderInfo->maxInterfaceVersion < XR_CURRENT_LOADER_API_LAYER_VERSION ||
		XR_VERSION_MAJOR(loaderInfo->minApiVersion) > 1) {
		return XR_ERROR_INITIALIZATION_FAILED;
	}

	apiLayerRequest->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
	apiLayerRequest->layerApiVersion = XR_MAKE_VERSION(1, 0, 0);
	apiLayerRequest->getInstanceProcAddr = Layer_xrGetInstanceProcAddr;
	apiLayerRequest->createApiLayerInstance = Layer_xrCreateApiLayerInstance;
	return XR_SUCCESS;
}
