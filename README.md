# CleanTB 🪟⚡

> **Ultra-Lightweight, Standalone Windows Taskbar Customizer & System Monitor**  
> *Minimal Footprint • Pure Native Win32 • < 0.1% CPU • Zero Telemetry*

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows)](https://github.com/GHOSTIEx9/CleanTB)
[![Runtime](https://img.shields.io/badge/.NET-8.0--windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Memory Footprint](https://img.shields.io/badge/RAM-Low%20Footprint-brightgreen)](#-performance--efficiency)
[![CPU Usage](https://img.shields.io/badge/CPU-%3C0.1%25-brightgreen)](#-performance--efficiency)
[![Telemetry](https://img.shields.io/badge/Telemetry-Zero%20%2F%20100%25%20Offline-success)](#-privacy--zero-telemetry)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## 📖 Overview

**CleanTB** is an original, ultra-lightweight standalone application built to give you complete visual control over your Windows taskbar while providing live system telemetry right where you need it.

Engineered around native Win32 APIs and `ExplorerTAP.dll`, CleanTB delivers crystal-clear taskbar styling, real-time CPU/RAM/GPU/network stats, optional on-demand hardware temperatures with smart thermal accents, and wireless device battery tracking in one portable single-file executable.

---

## ✨ Features

### 🪟 Pure Taskbar Styling
- **Clear (100% Transparent)**: Removes the taskbar background completely, letting your desktop wallpaper shine through from edge to edge.
- **Blur**: Classic frosted glass aesthetic.
- **Acrylic**: Fluent Windows 11 backdrop blur with soft lighting diffusion.
- **Tinted Dark / Light**: Semi-transparent dark or light tints for optimal contrast against bright or busy wallpapers.
- **Hide Top Border Line**: Removes the thin 1px line that Windows 11 draws across the top of the taskbar.
- **Multi-Monitor Support**: Automatically themes both your primary taskbar and all connected secondary monitor taskbars.
- **Elevated-Safe Taskbar Bridge**: Automatically pairs Administrator startup privileges (for instant sensor toggling without restarting) with a trimmed ~1–3 MB standard-privilege `--tap-worker` bridge so every taskbar mode keeps working seamlessly.
- **Fullscreen & Aero Peek Safe**: Automatically hides during fullscreen games/videos (including browser fullscreen playback) and stays visible when hovering taskbar window thumbnails.
- **Explorer Crash Recovery**: Automatically detects if Windows Explorer restarts and instantly restores your chosen theme.

---

### 📊 Real-Time Taskbar Hardware Stats & Temperatures HUD
- **Live Network Speed**: Real-time download and upload speeds (e.g. `40.1 KB/s`), with optional direction arrows (`▲` / `▼`) or `DN` / `UP` prefixes.
- **Live System Utilization**: Real-time **CPU %**, **RAM %**, and **GPU 3D %** right on your taskbar.
- **On-Demand Compact Inline Temperatures (CPU, GPU & DDR4/DDR5 RAM)**:
  - Off and completely unloaded by default to minimize RAM and CPU usage.
  - Toggle **CPU**, **GPU**, or **RAM** (live DIMM SPD thermal probe when supported by hardware) anytime from the tray menu — activates immediately without restarting the app.
  - Supports native Windows WDDM (`D3DKMT`) / NVIDIA `NVAPI` / AMD `ADL2` for GPU, signed `PawnIO` (`LibreHardwareMonitorLib`) for CPU & RAM SPD, plus automatic zero-config bridges for **HWiNFO64**, **Core Temp**, **AIDA64**, and **LibreHardwareMonitor / OpenHardwareMonitor**.
- **Smart Thermal Warning Accents**: Color-coded degree readouts highlight **Normal** (ice-cyan), **Warm** (amber), and **Hot** (coral-red) component temperatures with per-component thresholds and **°C / °F** switching.
- **100% Transparent Canvas & Clock-Matched Typography**: Matches Windows 11 `Segoe UI Variable Small` with a subtle 1px drop shadow and full Per-Monitor DPI scaling (100%–200%+).
- **Interactive**: Double-click the hardware HUD to launch Windows Task Manager (`taskmgr.exe`).

---

### 🔋 Wireless Device Battery HUD
- **Live Peripheral Battery Levels (Bluetooth + 2.4 GHz USB Dongles)**: View battery percentages right on the taskbar across standard Bluetooth and major 2.4 GHz USB receivers:
  - 🎧 **Wireless Headphones & Earbuds**: Bluetooth + SteelSeries Arctis/Nova, Turtle Beach Stealth Pro II / 700 Gen 3 / Atlas Air, ASUS ROG Strix Go 2.4 / Cetra SpeedNova / Delta II, Corsair Void/HS70/HS80/Virtuoso, HyperX Cloud/Flight/Alpha, Razer BlackShark/Barracuda, Logitech G PRO/G533/G733/G933/Astro A50, EPOS H3PRO, Sony INZONE Buds, Audeze Maxwell, JBL Quantum, Skullcandy PLYR
  - 🖱 **Wireless Mice**: Bluetooth + Logitech Lightspeed/Unifying/Bolt (HID++), Razer HyperSpeed, ASUS ROG/TUF & OMNI Receiver, SteelSeries Rival 3/Aerox/Prime, Redragon (CompX), Pulsar, VXE, ATK, Lamzu, MCHOSE, Glorious, Dareu, Rapoo, Keychron 8K, Endgame Gear, Finalmouse, VAXEE, Attack Shark (Beken), WLMouse, AJAZZ
  - ⌨ **Wireless Keyboards**: Bluetooth + Logitech Unifying/Bolt, ASUS ROG OMNI (Azoth/Falchion/Scope II), AULA, CompX 2.4 GHz, MCHOSE, Rapoo & HID Battery `0x85`
  - 🎮 **Wireless Game Controllers**: Xbox XInput/Bluetooth, Sony DualSense / DualSense Edge / DualShock 4 (+ `CUH-ZWA1` dongle), GameSir (`0xFF7A` & XInput), FlyDigi, BEITONG, Nintendo Switch Pro / Joy-Con, 8BitDo
- **Sticky Tray Menu**: Click toggles stay open cleanly across multiple adjustments and dismiss when clicking anywhere else or pressing **Enter** / **Esc**.
- **Real-Time Auto-Refresh**: Uses Windows `WM_DEVICECHANGE` and non-blocking overlapped native Win32 HID polling to detect connections and disconnections cleanly.
- **Perfect Icon Alignment & Filters**: Custom-calibrated Segoe Fluent Icons with per-category tray toggles.
- **Interactive**: Double-click the battery HUD to open Windows Bluetooth settings (`ms-settings:bluetooth`).

---

### ↔️ Independent Placement & Custom Dragging
- **Dock Separately**: Dock Hardware Stats (Left by default on fresh launch) and Wireless Device Batteries independently to the **Left** (empty taskbar space) or **Right** (next to the system tray).
- **Free Drag & Drop**: Drag either HUD anywhere on screen; positions are remembered across reboots.

---

### ⚡ Quick & Simple Controls
- **Black & White CleanTB Logo**: Embedded directly inside the standalone `.exe` for crisp display in both the system tray and tray menu header.
- **Left-Click Tray Icon**: Instantly toggle between **Clear** (Transparent) and **Normal** Windows mode.
- **Right-Click Tray Icon**: Access taskbar modes, hardware & temperature toggles (`°C` / `°F`), device filters, placement controls, and updates.
- **1-Click Auto-Updates**: Checks GitHub Releases and performs seamless in-place executable replacement and restart.
- **Start with Windows**: Registers an elevated logon task automatically so CleanTB boots cleanly with full sensor and taskbar support without repeated UAC prompts.

---

## ⚡ Performance & Efficiency

CleanTB keeps resource usage ultra-low by keeping heavy sensor engines completely unloaded unless enabled:

| Metric | CleanTB Design |
| :--- | :--- |
| **Memory Footprint** | **On-demand**: thermal engine unloaded until toggled on; working-set compaction & trimmed TAP bridge (~1–3 MB worker) |
| **CPU Utilization** | **< 0.1%** (event-driven message loop + 1s lightweight native Win32 polling) |
| **Disk I/O** | **0 KB/s** (no background logging or telemetry) |
| **Network Usage** | **0 KB/s** (offline except optional GitHub Releases update check) |
| **Portable Packaging** | **Single standalone `CleanTB.exe`** (no visible `.dll` clutter in your app folder — native runtime files are extracted cleanly inside `%LOCALAPPDATA%\CleanTB`) |

---

## 🎮 How to Use

CleanTB runs quietly in your **System Tray** (near the clock):

- **Left-Click the Tray Icon**: Toggles between Clear (Transparent) and Normal mode.
- **Right-Click the Tray Icon**:
  - Change style: **Clear**, **Blur**, **Acrylic**, **Tinted Dark**, **Tinted Light**, **Normal**
  - Adjust **Hardware Stats**: Toggle Download, Upload, CPU, RAM, GPU, **Temperatures (`CPU`, `RAM`, `GPU`, Warning Accents, `°C`/`°F`)**, or dock Left/Right
  - Adjust **Device Batteries**: Toggle Headphones, Mouse, Keyboard, Controller, or dock Left/Right
  - Toggle **Hide Top Border Line**
  - Toggle **Start with Windows**
  - **Exit CleanTB**: Safely restores standard Windows taskbar appearance and closes cleanly

---

## 📦 Download & Run

CleanTB is **100% self-contained and portable** — a single `CleanTB.exe` with no installer, no wizard, and no extra runtime downloads required.

1. Download **`CleanTB.exe`** directly from the [Releases](https://github.com/GHOSTIEx9/CleanTB/releases) page.
2. Double-click **`CleanTB.exe`** anywhere and approve the Windows UAC prompt (allows instant CPU/RAM temperature toggling without restarting).
3. CleanTB launches directly into your system tray!
   * *Tip: Right-click the tray icon and enable **Start with Windows** so CleanTB launches cleanly on boot without prompting UAC.*
   * *Tip: To monitor CPU or RAM temperatures, enable them under **Taskbar Hardware Stats → Temperatures** (requires the signed [PawnIO](https://github.com/namazso/PawnIO.Setup/releases) driver, or a running background monitor like HWiNFO64 / Core Temp / AIDA64 / LibreHardwareMonitor).*

---

## 🛠️ Building from Source

### Prerequisites
- Windows 10 (1809+) or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build Steps

```powershell
# Clone the repository
git clone https://github.com/GHOSTIEx9/CleanTB.git
cd CleanTB

# Build & publish single-file standalone executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish_sc
```

---

## 🔐 Privacy & Zero Telemetry

CleanTB is built with a strict privacy-first design:
- **Zero Telemetry or Analytics**: CleanTB never tracks, collects, or uploads user data or analytics.
- **GitHub-Only Network Access**: Network requests are exclusively restricted to querying public GitHub Releases API for update checks (can be disabled anytime via the tray menu).
- **Local Storage**: All settings are stored locally in `config.json` inside the app directory.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
