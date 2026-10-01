# XrPerf Protocol (v1)

Shared contract between `XrPerfLayer` (C++ OpenXR API layer), the recorder app (`openXRTK Graph`, .NET 10)
and the SimHub plugin (`DonutzXrPerfPlugin`, net48). The .NET definitions live in `XrPerf.Contracts`.

## 1. Shared memory (layer -> app)

- Name: `Local\XrPerf_Frames` (one active OpenXR app at a time; a newer session overwrites the header).
- Created by the layer in `xrCreateSession`, kept until `xrDestroySession`.
- All values little-endian, all offsets in bytes. Strings are zero-terminated UTF-8 in fixed slots.

### Header (512 bytes)

| Offset | Type      | Name                  | Notes |
|-------:|-----------|-----------------------|-------|
| 0      | uint32    | Magic                 | `0x46505258` ("XRPF") |
| 4      | uint32    | Version               | `1` |
| 8      | uint32    | HeaderSize            | `512` |
| 12     | uint32    | SampleSize            | `64` |
| 16     | uint32    | Capacity              | number of samples in ring, `4096` |
| 20     | uint32    | ProcessId             | PID of the OpenXR app |
| 24     | int64     | WriteIndex            | total samples written; written last with release semantics |
| 32     | int64     | QpcFrequency          | `QueryPerformanceFrequency` |
| 40     | int64     | SessionStartQpc       | QPC at `xrCreateSession` |
| 48     | uint32    | GraphicsApi           | 0 unknown, 1 D3D11, 2 D3D12, 3 Vulkan, 4 OpenGL |
| 52     | uint32    | SwapchainWidth        | largest color swapchain created |
| 56     | uint32    | SwapchainHeight       | |
| 60     | uint32    | SwapchainSampleCount  | MSAA samples |
| 64     | int64     | SwapchainFormat       | native format value |
| 72     | float32   | DisplayRefreshRate    | Hz, 0 if unknown |
| 76     | uint32    | ViewCount             | |
| 80     | float32[4]| FovLeft (l, r, u, d)  | radians |
| 96     | float32[4]| FovRight              | radians |
| 112    | uint32    | SessionState          | XrSessionState |
| 116    | uint32    | Reserved0             | |
| 120    | char[64]  | AppName               | |
| 184    | char[64]  | EngineName            | |
| 248    | char[64]  | RuntimeName           | |
| 312    | char[64]  | SystemName            | headset / system name |
| 376    | char[136] | Reserved              | future context fields |

### Frame sample (64 bytes), slot = `index % Capacity`

| Offset | Type   | Name                     | Notes |
|-------:|--------|--------------------------|-------|
| 0      | int64  | FrameIndex               | monotonic, equals sample index |
| 8      | int64  | WaitFrameQpc             | QPC when `xrWaitFrame` returned |
| 16     | int64  | BeginFrameQpc            | QPC when `xrBeginFrame` was called |
| 24     | int64  | EndFrameQpc              | QPC when `xrEndFrame` was called |
| 32     | int64  | PredictedDisplayTimeNs   | XrTime |
| 40     | int64  | PredictedDisplayPeriodNs | |
| 48     | uint32 | AppCpuUs                 | `xrBeginFrame` -> `xrEndFrame` call (OXRTK "appCPU") |
| 52     | uint32 | RenderCpuUs              | time spent inside downstream `xrEndFrame` (runtime + lower layers) |
| 56     | uint32 | AppGpuUs                 | GPU timestamp delta, 0 if unavailable |
| 60     | uint32 | Flags                    | bit0 GpuValid, bit1 ShouldRenderFalse |

### Reader rules
1. Read `WriteIndex` (acquire). Read samples `[last, WriteIndex)`.
2. If `WriteIndex - last > Capacity`, skip to `WriteIndex - Capacity` (samples lost).
3. After copying a sample, verify `FrameIndex` matches the expected index; otherwise discard (overwritten).

## 2. Control pipe (plugin -> app)

- Named pipe `XrPerf.Control`, duplex, message mode, one JSON object per message (UTF-8).
- Request: `{ "Command": "...", ...fields }`, response: `{ "Success": bool, "Error": string, ...fields }`.

| Command        | Request fields                          | Response fields |
|----------------|-----------------------------------------|-----------------|
| `Status`       | –                                       | `Status` |
| `Start`        | `Label`, `Context` (key/value)          | `Status` |
| `Stop`         | –                                       | `Status` (`LastSessionPath` set) |
| `Lap`          | `LapNumber`                             | `Status` |
| `ListSessions` | `MaxCount`                              | `Sessions` |
| `OpenSession`  | `SessionPath`                           | – |

`Context` carries SimHub data, e.g. `Game`, `Track`, `Car`, `SessionType`.

## 3. Recorded files

- `<app>_<yyyyMMdd_HHmmss>.csv` – first 7 columns identical to OXRTK stats:
  `time,FPS,appCPU (us),renderCPU (us),appGPU (us),VRAM (MB),VRAM (%)`
  followed by extended columns:
  `CPU (%),GPU (%),RAM (MB),appRAM (MB),frametime avg (ms),frametime p99 (ms),FPS 1% low,lap`
- `<app>_<yyyyMMdd_HHmmss>_xrperf.json` – session context (layer header data, SimHub context, settings snapshots).
