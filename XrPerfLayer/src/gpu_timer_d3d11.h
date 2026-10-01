#pragma once

#include <d3d11.h>
#include <wrl/client.h>

#include <array>
#include <cstdint>

namespace xrperf {

// Measures GPU time between xrBeginFrame and xrEndFrame on the application's D3D11 immediate context.
// Results are read back non-blocking a few frames later.
class GpuTimerD3D11 {
public:
	bool Initialize(ID3D11Device* device);
	void Shutdown();
	bool IsInitialized() const { return m_context != nullptr; }

	void BeginFrame();
	void EndFrame();

	// Returns true and the latest completed GPU frame time in microseconds, if a new result is available.
	bool Poll(uint32_t& gpuUs);

private:
	static constexpr size_t kSlots = 6;

	struct Slot {
		Microsoft::WRL::ComPtr<ID3D11Query> disjoint;
		Microsoft::WRL::ComPtr<ID3D11Query> start;
		Microsoft::WRL::ComPtr<ID3D11Query> end;
		bool pending = false;
		bool open = false;
	};

	Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_context;
	std::array<Slot, kSlots> m_slots;
	uint64_t m_writeFrame = 0;
	uint64_t m_readFrame = 0;
};

} // namespace xrperf
