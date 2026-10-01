#include "gpu_timer_d3d11.h"

namespace xrperf {

bool GpuTimerD3D11::Initialize(ID3D11Device* device) {
	Shutdown();
	if (!device) {
		return false;
	}

	D3D11_QUERY_DESC disjointDesc{D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
	D3D11_QUERY_DESC timestampDesc{D3D11_QUERY_TIMESTAMP, 0};
	for (Slot& slot : m_slots) {
		if (FAILED(device->CreateQuery(&disjointDesc, &slot.disjoint)) ||
			FAILED(device->CreateQuery(&timestampDesc, &slot.start)) ||
			FAILED(device->CreateQuery(&timestampDesc, &slot.end))) {
			Shutdown();
			return false;
		}
	}

	device->GetImmediateContext(&m_context);
	return m_context != nullptr;
}

void GpuTimerD3D11::Shutdown() {
	for (Slot& slot : m_slots) {
		slot = Slot{};
	}
	m_context.Reset();
	m_writeFrame = 0;
	m_readFrame = 0;
}

void GpuTimerD3D11::BeginFrame() {
	if (!m_context) {
		return;
	}

	Slot& slot = m_slots[m_writeFrame % kSlots];
	if (slot.pending || slot.open) {
		// Ring full (GPU far behind) - skip measuring this frame.
		return;
	}

	m_context->Begin(slot.disjoint.Get());
	m_context->End(slot.start.Get());
	slot.open = true;
}

void GpuTimerD3D11::EndFrame() {
	if (!m_context) {
		return;
	}

	Slot& slot = m_slots[m_writeFrame % kSlots];
	if (!slot.open) {
		return;
	}

	m_context->End(slot.end.Get());
	m_context->End(slot.disjoint.Get());
	slot.open = false;
	slot.pending = true;
	m_writeFrame++;
}

bool GpuTimerD3D11::Poll(uint32_t& gpuUs) {
	if (!m_context) {
		return false;
	}

	bool found = false;
	while (m_readFrame < m_writeFrame) {
		Slot& slot = m_slots[m_readFrame % kSlots];

		D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint{};
		if (m_context->GetData(slot.disjoint.Get(), &disjoint, sizeof(disjoint), D3D11_ASYNC_GETDATA_DONOTFLUSH) !=
			S_OK) {
			break;
		}

		uint64_t start = 0;
		uint64_t end = 0;
		const bool ready =
			m_context->GetData(slot.start.Get(), &start, sizeof(start), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK &&
			m_context->GetData(slot.end.Get(), &end, sizeof(end), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK;
		if (!ready) {
			break;
		}

		if (!disjoint.Disjoint && disjoint.Frequency > 0 && end > start) {
			gpuUs = static_cast<uint32_t>(((end - start) * 1000000ull) / disjoint.Frequency);
			found = true;
		}

		slot.pending = false;
		m_readFrame++;
	}
	return found;
}

} // namespace xrperf
