# OpenPixel (`v0.5.126`) + OpenPixel Deck (`v1.0.5`)

**OpenPixel** is a native Windows 10/11 Dynamic Island and top-notch desktop overlay built in C# / .NET 10, paired with **OpenPixel Deck** (`App/main.apk`), a local-first Android companion app for wireless media control, trackpad/keyboard input, Stream Deck app launching, synced widgets, and PC power management over Private Wi-Fi.

---

## Highlights

- **100% Local-First & Private**: Zero accounts, zero cloud servers, zero telemetry. All settings, clipboard history, paired device tokens, and TLS certificates stay on your PC and phone.
- **Native Windows Performance**: Layered Win32 per-monitor DPI-aware rendering (`PerMonitorV2`), CoreAudio COM integration, WinRT SMTC media sessions, WMI/DDC-CI brightness, and Media Foundation MP4 screen recording.
- **Encrypted Android Companion**: Mutual Private Wi-Fi QR pairing with interactive PC approval, SHA-256 TLS certificate pinning, `usesCleartextTraffic="false"`, and Android KeyStore AES-GCM token storage.

---

## Windows Desktop Features (`OpenPixel.exe`)

- **Liquid Glass & 11 Surface Themes**: `LiquidGlass`, `CandyGlass`, `Acrylic`, `Black`, `PureBlack`, `Graphite`, `PureWhite`, `Rose`, `Lavender`, `Peach`, and `Sakura`, plus a **Sync UI colours to playing audio** toggle that tints buttons, accents, borders, and sub-colours to the active track.
- **Hover Island & Notch Modes**: Switch between floating Island and top-edge Notch silhouettes with configurable edge placement (`TopCenter`, `LeftEdge`, `RightEdge`), pill/card scale, corner radius, and optional auto-hide.
- **Media & Synced Lyrics**: Real Windows SMTC media controls, album-art dominant color wash, balanced 4-bar audio visualizer, inline resting clock while playing, and synchronized LRCLIB lyrics drawer.
- **Global Mic Mute & Voice Wave Bar**: Mutes all active CoreAudio capture endpoints at once from the Calls tab, Controls tab, hotkey, or Android app, with a toggleable 60fps curved voice waveform bar when speaking.
- **Optional Bottom Notch Taskbar & All-Apps Start Menu**: Floating hinged bottom dock with running/pinned apps, inline mini media player, system tray icons, smooth Arial clock/date, glitch-free fullscreen auto-hide, and a searchable, scrollable All-Apps Start Menu scanning installed Windows shortcuts.
- **Desktop Widgets & Snap Guides**: Pin **Day Clock** (Anurati Regular-inspired typography), **Analog Clock**, **To-Do**, **Weather**, **Calendar**, **PC Stats**, and **Stock Prices** widgets with zero-lag dragging and full-screen `+` center-snap crosshairs.
- **Live Video Wallpapers**: Hardware-decoded `.mp4` / `.mkv` desktop video wallpapers that automatically pause when covered by maximized/fullscreen windows.
- **Clipboard History & File Shelf**: Persistent searchable clipboard history and drag-and-drop File Shelf with one-click local Wi-Fi QR sharing to any phone.
- **Web Drawer**: Built-in WebView2 drawer for pinned web apps right inside the island.
- **Screen Capture Tools**: Full-screen screenshot capture (copies to clipboard, saves PNG, adds to Shelf), region snip, screen colour picker, and Media Foundation `.mp4` screen recording.
- **Gaming & Streaming Overlays**: Slim fullscreen telemetry strip (FPS, CPU, GPU, RAM, network), OBS recording pill, and movable live stream chat drawer.
- **AI Coding Agent Monitor**: Tracks local coding agents (Claude Code, Codex, Antigravity, Hermes, OpenClaw, and Free Buff) with once-per-session notch reminders and click-to-focus notification history.

---

## Android Companion App (`OpenPixel Deck` — `App/main.apk`)

- **Private Wi-Fi QR Pairing**: Scan the QR code shown in OpenPixel on the same Private Wi-Fi network, confirm the interactive approval prompt on Windows, and lock in a pinned SHA-256 TLS certificate + AES-GCM encrypted auth token.
- **Kyant0-Style Liquid Glass Bottom Bar**: Spring-animated, draggable liquid glass capsule tab bar across **Now**, **Pad**, **Apps**, **Modes**, and **PC**, with smoked translucent glass cards and full-app music color sync (plus 5 custom gradient presets: `None`, `Midnight`, `Crimson`, `Aurora`, `Emerald`).
- **Now Playing & Live Audio Wave**: Album art, seek/volume liquid sliders, play/pause/skip, lock-screen media notification, and a zero-lag cached-shader audio wave backdrop that stops when silent.
- **Smooth Widgets & File Shelf Previews**: Spring-interpolated CPU / GPU / RAM cards, synced **PC Clipboard** (tap to copy), synced **PC To-Do** (tap to toggle or `+ Add`), and **PC Shelf** with rounded image thumbnails (tap to **Download** to `Downloads/OpenPixel/` or **Copy**).
- **Wireless Trackpad & Keyboard (`Pad`)**: Coalesced UDP mouse movement (`47826`) with smooth tapered glowing ribbon cursor, tap-to-click, two-finger right-click/scroll, hold-to-drag, and direct Unicode text/shortcut input.
- **Stream Deck & Landscape Fullscreen (`Apps`)**: Launch 6 pinned PC apps (with custom icons) or enter non-scrolling Landscape Fullscreen Deck mode with a right-hand control sidebar (**Mute Audio**, **Mute Mic**, **Test Audio**, **Exit Deck**).
- **Modes (`Game`, `Present`, `Movie`, `Work`)**: Dedicated remote shortcuts for recording/screenshots, presentation slides + timer, movie playback, and focus controls.
- **PC Power & Wake-on-LAN (`PC`)**: Live CPU/GPU/RAM telemetry, **Lock PC**, **Wake PC** (UDP + Wake-on-LAN magic packet), and press-and-hold safety buttons for **Sleep**, **Restart**, and **Shut down**.

---

## Default Keyboard Shortcuts (Windows)

All primary shortcuts can be customized in **Settings → Shortcuts**:

| Default Shortcut | Action |
| :--- | :--- |
| `Ctrl + Alt + N` | Open / close island |
| `Ctrl + Alt + P` | Play / pause media |
| `Ctrl + Alt + M` | Global microphone mute / unmute |
| `Ctrl + Alt + H` | Hide / show island |
| `Ctrl + Alt + S` | Capture full screenshot |
| `Ctrl + Alt + C` | Launch screen colour picker |
| `Ctrl + Alt + 1` – `6` | Launch pinned Launcher Slots 1–6 |

---

## Requirements & Installation

### Windows Desktop
- **OS**: Windows 10 (build 19041+) or Windows 11 (x64)
- **Runtime**: .NET 10 Desktop Runtime (Windows) & Microsoft Edge WebView2 Runtime (preinstalled on Windows 11)
- **Install**: Download and extract the Windows release archive (`OpenPixel.exe` alongside its runtime DLLs and `runtimes/` folder) and run `OpenPixel.exe`.

### Android Companion (`App/main.apk`)
- **OS**: Android 9.0 (API 28) or higher (targets Android 15 / API 35)
- **Install**: Transfer `main.apk` to your Android device, allow installation from unknown sources if prompted, and install **OpenPixel Deck**.
- **Pair**: On Windows, open **Settings → PC Deck** (or the **PC Deck** tab), enable **PC Deck**, ensure both devices are on the same Private Wi-Fi network, and scan the QR code in the Android app.

---

## Building from Source

### Windows (`OpenPixel.exe`)
Requires the **.NET 10 SDK** on Windows:
```powershell
dotnet build -c Release OpenPixel.csproj
```
The compiled Release output is written to `bin/Release/net10.0-windows10.0.19041.0/OpenPixel.exe` and automatically synced to the repository root (`OpenPixel.exe`).

### Android (`App/main.apk`)
Requires **Android SDK Build-Tools** (`aapt2`, `d8`, `zipalign`, `apksigner`), Android Platform JAR, and **JDK 11+** (configured in `App/build_apk.ps1`):
```powershell
powershell -ExecutionPolicy Bypass -File ".\App\build_apk.ps1"
```
Outputs the aligned, signed, and verified APK to `App/build/main.apk` and `App/main.apk`.

---

## Privacy & Architecture

OpenPixel and OpenPixel Deck are built strictly local-first:
- No user accounts, analytics, crash uploaders, or external backend servers.
- Local network services bind to your LAN/loopback only when enabled: HTTP Shelf (`47824`), TLS PC Deck (`47825`), and UDP Pad/Discovery (`47826`).
- Android enforces `android:allowBackup="false"` and `android:usesCleartextTraffic="false"`, storing pairing tokens encrypted via Android KeyStore AES-GCM.
