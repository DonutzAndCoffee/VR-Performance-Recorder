# VR Performance Recorder

*(formerly **openXRTK Graph**)*

> *Donutz: Proof that sim racing can be delicious.*

[![Discord](https://img.shields.io/badge/Discord-Join%20us-5865F2?logo=discord&logoColor=white)](https://discord.com/invite/KuSsEYgB3k)

VR Performance Recorder is a Windows tool for recording, viewing and comparing how your VR games perform. It shows FPS, frame times, CPU/GPU load and VRAM usage as easy-to-read charts. It can also show a small live overlay inside your headset and send live data to **SimHub** dashboards.

It works with **OpenXR** games and with native **OpenVR/SteamVR** games. It can also open CSV logs created by **OpenXR Toolkit**.

> **Note:** Parts of this project were developed with the assistance of GitHub Copilot (AI-assisted coding).

---

## Features

- **Record performance sessions** – FPS, app/render CPU time, GPU time, VRAM, CPU %, GPU %, RAM, 1% lows and more, saved as CSV.
- **Charts and analysis**
  - Frametime distribution (CPU/GPU histograms)
  - FPS over time
  - FPS + frame times combined
  - CPU/GPU frame time timeline
  - VRAM usage
  - **FPS drop attribution** – shows which background processes were using CPU when FPS dropped
  - Frame-by-frame analysis
- **Compare sessions** – put up to 3 recordings side by side (A / B / C).
- **Live in-headset overlay** – shows FPS, frame time, CPU/GPU, resolution and app name. You can set the position and size. *(Direct3D 11 games only)*
- **SimHub integration** – an included plugin sends live performance data to your SimHub dashboards.
- **OpenXR Toolkit compatible** – opens existing OpenXR Toolkit CSV logs.
- **Update checker** – tells you when a new version is available on GitHub.

---
### Screenshots

<img width="1336" height="743" alt="image" src="https://github.com/user-attachments/assets/3008fd0a-5edb-4d12-b5c9-3c945e7e092e" />
<img width="1668" height="1157" alt="image" src="https://github.com/user-attachments/assets/7d665b31-3f95-4fdb-8409-049dfe8276b4" />
<img width="1632" height="1102" alt="image" src="https://github.com/user-attachments/assets/4861a8d1-4e3e-4951-bea7-ab31d1f0651a" />

---

## Requirements

- Windows 10 / 11 (64-bit)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- A VR headset with an OpenXR runtime (e.g. SteamVR, Meta, WMR, Pimax, Varjo) or SteamVR for OpenVR games
- *Optional:* [SimHub](https://www.simhubdash.com/) for dashboard integration

---

## Installation

1. Download the latest release from the [Releases page](https://github.com/DonutzAndCoffee/openXRTK-Graph/releases).
2. Unzip it to a folder of your choice (for example `C:\Tools\VR Performance Recorder`).
3. Start **`VR-Performance-Recorder.exe`**.

### First-time setup

Open **Settings** in the app:

1. **OpenXR Perf Layer** → click **Install**. This is needed to record OpenXR games and to show the in-headset overlay. Windows will ask for administrator rights because the layer is registered in the system registry.
2. *(Optional)* **SimHub Plugin** → click **Install** to copy the plugin into SimHub. Restart SimHub afterwards.
3. *(Optional)* **VR Overlay** → choose what is shown, where, and how big.

You can **Disable** or **Uninstall** the layer at any time from the same Settings page.

---

## How to use

### Record a session
1. Start the VR Performance Recorder.
2. Start your VR game. The status bar shows when the performance layer is connected.
3. A **red dot** shows that recording is active (with an optional sound).
4. When you close the game, the session is saved.

Recordings are saved to:
```
%USERPROFILE%\Documents\XrPerf\sessions
```

### View a recording
Click **Open CSV** and pick a session file (or an OpenXR Toolkit log). Use the tabs to switch between the charts.

### Compare recordings
Click **Compare Sessions** and load up to three recordings, e.g. to compare graphics settings or driver versions.

### Find out what causes stutters
Turn on **Process logging** in Settings (interval 100–2000 ms). The **FPS Drop Attribution** tab then shows which processes were busy when FPS dropped. Process logs are saved to:
```
%USERPROFILE%\Documents\openxrtk-logs
```

---

## Settings overview

| Setting | Description |
|---|---|
| Recording sound | Play a sound when recording starts/stops |
| Process logging | Log CPU/RAM usage per process, with a configurable interval |
| OpenXR Perf Layer | Install, enable/disable or uninstall the OpenXR layer |
| SimHub Plugin | Install or update the SimHub plugin |
| VR Overlay | Show FPS, frame time, CPU/GPU, resolution, app name; position (4 corners); size (75 % / 100 % / 140 %) |

---

## FAQ / Troubleshooting

**The overlay does not show up in my headset.**
Check that the OpenXR Perf Layer is *installed and enabled* in Settings. The overlay only works for Direct3D 11 games.

**Nothing is being recorded.**
Make sure the layer status in the status bar is not *Missing* or *Disabled*.

**SimHub does not show any data.**
Install the plugin from Settings, restart SimHub, and enable the plugin in SimHub if asked.

**How do I remove everything?**
In Settings, click **Uninstall** for the OpenXR layer, then delete the app folder. Your recordings stay in `Documents\XrPerf`.

---

## Building from source

Requirements: Visual Studio 2026 (or newer) with the .NET desktop and C++ desktop workloads.

| Project | Target | Purpose |
|---|---|---|
| `openXRTK Graph` | .NET 10 (Windows) | Main WPF application |
| `XrPerf.Contracts` | .NET Standard 2.0 | Shared data structures / CSV schema |
| `XrPerf.SimHubPlugin` | .NET Framework 4.8 | SimHub plugin |
| `XrPerfLayer` | C++ | OpenXR API layer and overlay |

Open `openXRTK Graph.slnx` and build the solution.

---

## Community & support

- Bug reports and feature requests: [GitHub Issues](https://github.com/DonutzAndCoffee/openXRTK-Graph/issues)
- Discord: [https://discord.com/invite/KuSsEYgB3k](https://discord.com/invite/KuSsEYgB3k)

---

## License

© 2025 DonutzAndCoffee

Licensed under [Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0)](https://creativecommons.org/licenses/by-nc/4.0/).

Third-party components:
- [OxyPlot](https://github.com/oxyplot/oxyplot) – MIT License
- OpenXR-SDK headers – Apache-2.0
- SimHub SDK (plugin interfaces) – © SimHub, used under its plugin terms

---

*Donutz
