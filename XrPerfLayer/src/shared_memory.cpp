#include "shared_memory.h"

#include <atomic>
#include <cstring>

namespace xrperf {

bool SharedMemoryWriter::Open() {
	if (m_header) {
		return true;
	}

	m_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
								   static_cast<DWORD>(kTotalSize), kMappingName);
	if (!m_mapping) {
		return false;
	}

	void* view = MapViewOfFile(m_mapping, FILE_MAP_ALL_ACCESS, 0, 0, kTotalSize);
	if (!view) {
		CloseHandle(m_mapping);
		m_mapping = nullptr;
		return false;
	}

	m_header = static_cast<Header*>(view);
	m_samples = reinterpret_cast<Sample*>(static_cast<char*>(view) + sizeof(Header));
	Reset();
	return true;
}

void SharedMemoryWriter::Close() {
	if (m_header) {
		m_header->magic = 0;
		UnmapViewOfFile(m_header);
		m_header = nullptr;
		m_samples = nullptr;
	}
	if (m_mapping) {
		CloseHandle(m_mapping);
		m_mapping = nullptr;
	}
}

void SharedMemoryWriter::Reset() {
	if (!m_header) {
		return;
	}

	std::memset(m_header, 0, sizeof(Header));
	LARGE_INTEGER freq{};
	LARGE_INTEGER now{};
	QueryPerformanceFrequency(&freq);
	QueryPerformanceCounter(&now);

	m_header->version = kVersion;
	m_header->headerSize = sizeof(Header);
	m_header->sampleSize = sizeof(Sample);
	m_header->capacity = kCapacity;
	m_header->processId = GetCurrentProcessId();
	m_header->qpcFrequency = freq.QuadPart;
	m_header->sessionStartQpc = now.QuadPart;
	std::atomic_thread_fence(std::memory_order_release);
	m_header->magic = kMagic;
}

void SharedMemoryWriter::Push(const Sample& sample) {
	if (!m_header) {
		return;
	}

	std::atomic_ref<int64_t> writeIndex(m_header->writeIndex);
	const int64_t index = writeIndex.load(std::memory_order_relaxed);
	Sample copy = sample;
	copy.frameIndex = index;
	m_samples[index % kCapacity] = copy;
	writeIndex.store(index + 1, std::memory_order_release);
}

void CopyString(char (&dest)[64], const char* src) {
	if (!src) {
		dest[0] = '\0';
		return;
	}
	strncpy_s(dest, src, _TRUNCATE);
}

} // namespace xrperf
