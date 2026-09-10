# MirrorAndPlay

**MirrorAndPlay** is a lightweight Windows desktop streaming application. It captures the MirrorAndPlay window hosting an embedded YouTube player and system audio in real time and streams it directly to an external client (such as an Android device running VLC) via HTTP MPEG-TS.

Connect an outdated smartphone to your PC and place it next to your monitor to enjoy videos on a mini display while you work.
All you have to do should be to set up ADB... and find a VLC APK old enough to install.

By leveraging the Windows Graphics Capture API, Direct3D 11 (`Vortice.Direct3D11`), and Intel Quick Sync Video (`h264_qsv`) hardware encoding, it achieves minimal CPU utilization.

---

## Key Features

* **Lightweight Hardware Capture**: Utilizes `Windows.Graphics.Capture` and Direct3D 11 to acquire video frames directly on the GPU.
* **Continuous Audio Loopback**: Captures desktop audio using `NAudio` (WASAPI Loopback). Features a synthetic silence injector to prevent capture stalls during silent passages.
* **Intel QSV Hardware Acceleration**: Employs FFmpeg's Intel Quick Sync Video (`h264_qsv`) encoder for ultra-fast, low-overhead H.264 video compression.
* **HTTP MPEG-TS Streaming**: Serves an MPEG-TS live stream directly from FFmpeg's built-in HTTP server on port `8912`.
* **Automated Android Playback via ADB**: Automatically triggers VLC on an ADB-connected Android device to open and play the live stream upon launch.
* **Auto-Recovery**: Monitors the FFmpeg process and named pipe states, automatically respawning and reconnecting if a pipeline fault occurs.

---

## Architecture

```text
[ Target Window ] ──(Graphics Capture)──> [ D3D11 Texture2D ] ──(Raw BGRA / Staging)──┐
                                                                                      │ (stdin)
[ System Audio  ] ──(WASAPI Loopback)───> [ Named Pipe Server ] ─(Named Pipe / f32le)─┴─> [ FFmpeg (h264_qsv) ]
                                                                                                  │
                                                                                        (MPEG-TS over HTTP)
                                                                                                  │
                                                                                                  ▼
[ Android Device (VLC) ] <────────────────────────(ADB Intent Trigger)──────────────────────────────┘

```

---

## System Requirements

### Host PC

* **OS**: Windows 10 / Windows 11
* **Hardware**: Intel CPU with Quick Sync Video support (integrated or discrete Intel GPU)
* **Runtime**: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
* **External Tools** (must be available in system `PATH`):
  * `ffmpeg.exe` (compiled with `h264_qsv` support)
  * `adb.exe` (from Android SDK Platform-Tools)

### Client Device

* An Android device connected via USB with **USB Debugging** enabled as well as **USB tethering** enabled.
  * Configure the network profile / firewall so that the device can reach the host PC's network port.
* **VLC for Android** installed.

---

## Getting Started

### 1. Clone the Repository

```bash
git clone https://github.com/mtripg6666tdr/MirrorAndPlay.git
cd MirrorAndPlay
```

### 2. Verify External Tools

Verify that both `ffmpeg` and `adb` are accessible from your terminal:

```bash
ffmpeg -version
adb devices
```

### 3. Build & Run

```bash
dotnet build -c Release
dotnet run -c Release
```

---

## How It Works

1. **Initialization**: On startup, the main window obtains the target window handle (`HWND`) and initializes a `Direct3D11CaptureFramePool` session.
2. **Audio Pipeline**: An asynchronous named pipe server (`\\.\pipe\mirror_audio_pipe`) is established. WASAPI loopback capture starts feeding raw 32-bit floating-point PCM audio into this pipe.
3. **Encoder Launch**: FFmpeg is spawned as a child process, consuming raw video frames via standard input (`stdin`) and audio frames via the named pipe.
4. **Broadcast**: FFmpeg exposes an HTTP endpoint at `[http://0.0.0.0:8912/live](http://0.0.0.0:8912/live)`.
5. **Client Trigger**: An ADB intent command launches VLC on the connected Android device, instructing it to open and buffer the stream.
