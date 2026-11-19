# CleanTB 🪟⚡

> **Ultra-Lightweight, Standalone Windows Taskbar Customizer & System Monitor**  
> *~4 MB – 6 MB RAM • Pure Native Win32 • 0% CPU • Zero Telemetry*

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows)](https://github.com/GHOSTIEx9/CleanTB)
[![Runtime](https://img.shields.io/badge/.NET-8.0--windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Memory Footprint](https://img.shields.io/badge/RAM-~4--6%20MB-brightgreen)](#-performance--efficiency)
[![CPU Usage](https://img.shields.io/badge/CPU-0.0%25-brightgreen)](#-performance--efficiency)
[![Telemetry](https://img.shields.io/badge/Telemetry-Zero%20%2F%20100%25%20Offline-success)](#-privacy--zero-telemetry)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## 📖 Overview

**CleanTB** is an original, ultra-lightweight standalone application built from scratch to give you complete visual control over your Windows taskbar while providing live system monitoring right where you need it.

Engineered with pure native Win32 APIs and zero bulky UI frameworks, CleanTB idles at just **4 to 6 MB of RAM** and **0.0% CPU**, giving you crystal-clear aesthetics, real-time hardware telemetry, and wireless device battery monitoring without slowing down your system or consuming resources.

---

## ✨ Features

### 🪟 Pure Taskbar Styling
- **Clear (100% Transparent)**: Removes the taskbar background completely, letting your desktop wallpaper shine through from edge to edge.
- **Blur**: Classic frosted glass aesthetic.
- **Acrylic**: Fluent Windows 11 backdrop blur with soft lighting diffusion.
- **Tinted Dark / Light**: Semi-transparent dark or light tints for optimal contrast against bright or busy wallpapers.
- **Hide Top Border Line**: Removes the thin 1px line that Windows 11 draws across the top of the taskbar.
- **Multi-Monitor Support**: Automatically themes both your primary taskbar and all connected secondary monitor taskbars.
- **Explorer Crash Recovery**: Automatically detects if Windows Explorer restarts and instantly restores your chosen theme.

---

### 📊 Real-Time Taskbar Hardware Stats HUD
- **Live Network Speed**: Real-time download and upload speeds (e.g. `40.1 KB/s`), with optional direction arrows (`▲` / `▼`) or clean text prefixes.
- **Live System Utilization**: Real-time **CPU %**, **RAM %**, and **GPU 3D %** right next to your taskbar items.
- **100% Transparent Canvas**: Rendered directly over your wallpaper with no background boxes, pills, or tints. Features a subtle 1px drop shadow for razor-sharp legibility on light, dark, and textured wallpapers alike.
- **Clock-Matched Typography**: Perfectly matches the Windows 11 taskbar clock font (`Segoe UI Variable Small`) and automatically adapts to your monitor's display scaling (100%, 125%, 150%, 200% DPI).
- **Interactive**: Double-click the hardware HUD to instantly launch Windows Task Manager (`taskmgr.exe`).

---

### 🔋 Wireless Device Battery HUD
- **Live Peripheral Battery Levels**: View the battery percentage of your wireless gear right on the taskbar:
  - 🎧 **Wireless Headphones & Earbuds**
  - 🖱 **Wireless Mice**
  - ⌨ **Wireless Keyboards**
  - 🎮 **Wireless Game Controllers (Xbox & Bluetooth)**
- **Real-Time Auto-Refresh**: Uses Windows `DeviceWatcher` and hardware broadcast listeners (`WM_DEVICECHANGE`) to detect connections and disconnections in real time. The HUD automatically appears when a device powers on or connects, and automatically disappears when turned off or disconnected.
- **Perfect Icon Alignment**: Custom-calibrated Segoe Fluent Icons that sit dead-center aligned with the numeric percentages and text.
- **Device Filters**: Easily toggle individual device types on or off from the tray menu.
- **Interactive**: Double-click the battery HUD to open Windows Bluetooth settings (`ms-settings:bluetooth`).

---

### ↔️ Independent Placement & Custom Dragging
- **Dock Separately**:
  - Dock Hardware Stats to the **Left** (empty taskbar space) or **Right** (next to the system tray).
  - Dock Wireless Device Batteries to the **Left** or **Right** **independently**.
  - Example: Keep your Hardware Stats on the left and your Device Battery stats on the right!
- **Free Drag & Drop**: Drag either HUD with your mouse to place it anywhere on your taskbar or desktop. Positions are automatically remembered across reboots.

---

### ⚡ Quick & Simple Controls
- **Left-Click Tray Icon**: Instantly toggle between **Clear** (Transparent) and **Normal** Windows mode.
- **Right-Click Tray Icon**: Access all customization options, styles, device toggles, placement settings, and update management.
- **1-Click Auto-Updates**: Check for updates and download latest releases directly from GitHub with seamless in-place restart.
- **Start with Windows**: Toggle automatic startup on or off with a single click.

---

## ⚡ Performance & Efficiency

CleanTB is designed to be completely unnoticeable on your system resources:

| Metric | CleanTB Footprint |
| :--- | :--- |
| **Active Memory (RAM)** | **~4 MB – 6 MB** |
| **CPU Utilization** | **0.0%** (idle event-driven message loop) |
| **Disk I/O** | **0 KB/s** (zero telemetry, zero background logs) |
| **Network Usage** | **0 KB/s** (100% offline, no network calls) |
| **Startup Time** | **< 0.1 seconds** |

---

## 🎮 How to Use

CleanTB runs quietly in your **System Tray** (near the clock):

- **Left-Click the Tray Icon**: Toggles between Transparent and Default mode.
- **Right-Click the Tray Icon**:
  - Change style: **Clear**, **Blur**, **Acrylic**, **Tinted Dark**, **Tinted Light**, **Normal**
  - Adjust **Hardware Stats**: Toggle Download, Upload, CPU, RAM, GPU, or dock Left/Right
  - Adjust **Device Batteries**: Toggle Headphones, Mouse, Keyboard, Controller, or dock Left/Right
  - Toggle **Hide Top Border Line**
  - Toggle **Start with Windows**
  - **Exit**: Safely closes CleanTB and restores standard Windows taskbar appearance

---

## 📦 Download & Run

CleanTB is **100% self-contained and portable** — no installer, no wizard, no extra dependencies (like .NET Desktop Runtime or Visual C++ runtimes), and zero administrator privileges required.

1. Download **`CleanTB.exe`** directly from the [Releases](https://github.com/GHOSTIEx9/CleanTB/releases) page.
2. Double-click **`CleanTB.exe`** anywhere (Desktop, Downloads, or any folder of your choice).
3. CleanTB launches immediately into your system tray — ready in < 0.1 seconds!
   * *Tip: Right-click the tray icon and toggle **Start with Windows** to keep it running automatically on boot.*

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
- **Local Storage**: All settings are stored locally in a plain text `config.json` inside the app directory.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
