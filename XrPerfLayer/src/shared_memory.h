#pragma once

#include "layout.h"

#include <windows.h>

namespace xrperf {

class SharedMemoryWriter {
public:
	~SharedMemoryWriter() { Close(); }

	bool Open();
	void Close();
	bool IsOpen() const { return m_header != nullptr; }

	Header* GetHeader() { return m_header; }
	void Reset();
	void Push(const Sample& sample);

private:
	HANDLE m_mapping = nullptr;
	Header* m_header = nullptr;
	Sample* m_samples = nullptr;
};

void CopyString(char (&dest)[64], const char* src);

} // namespace xrperf
