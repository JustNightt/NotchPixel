# NotchPixel / OpenPixel — Project Guide

## What this repository is

A Windows desktop companion app ("OpenPixel", UI name NotchPixel): a notched
Dynamic-Island-style pill, a custom bottom taskbar, desktop widgets, an optional
live video wallpaper, and an Android "deck" remote that talks to the PC over a
pinned TLS connection.

## The three trees (important)

| Path | What it is | Status |
| --- | --- | --- |
| `csharp/` + `OpenPixel.csproj` | **The app of record.** C# / .NET 10 WinForms. Builds `OpenPixel.exe` + `OpenPixel.dll` into the repository root. | Active |
| `App/` | Android deck (APK) + `build_apk.ps1`, `test_deck_protocol.ps1` | Active |
| `src/` | Older, second C++/Direct2D implementation (`CMakeLists.txt`; `build/` holds only a CMake cache). | **Legacy — not built, not modified.** The E2E suite and `--self-test` target the C# app only. |

`tests/NotchPixel.E2E.Tests.csproj` is an xUnit suite that drives the built
`OpenPixel.exe` (CLI flags, window snapshots, loopback HTTP, deck TLS mock).

## Build and run

```bash
dotnet build OpenPixel.csproj -c Release   # writes OpenPixel.exe/dll to repo root
./OpenPixel.exe                            # normal launch
```

Configuration and runtime state live under `%LOCALAPPDATA%\OpenPixel\`
(`settings.json`, `diagnostic.log`, clipboard pins, reminders).

## Architecture map (C#)

- `csharp/Program.cs` — entry point, CLI test hooks (`--self-test`, `--web-test`,
  `--qr-test`, `--video-test`, `--video-test-file`, `--render-test`), island form,
  tray icon, hotkeys, taskbar watchdog entry.
- `csharp/Rendering/LiquidGlassRenderer.cs` — layered-window renderer: blur,
  theme ambience, rounded geometry, text. Blur is clamped to 0–40 px and applied
  atomically on the render tick; a failed glass surface degrades to Black.
- `csharp/Rendering/StencilFont.cs` — path-drawn geometric display alphabet for
  the Day Clock (no font dependency), with an Inter fallback for other glyphs.
- `csharp/UI/IslandTabsRenderer.cs` — the 10 island tabs and their hit regions.
- `csharp/UI/AuxiliaryWindows.cs` — live video wallpaper window, bottom notch
  taskbar (icons, tray, status cluster, clock, bell), Start panel, drawers.
- `csharp/UI/DesktopWidgets.cs` — all widget types (Day Clock, analog/clock,
  calendar, to-do, photos, PC stats with sparklines, stocks, batteries, focus
  timer, custom `/tile` tile) with per-type sizes, scale and context menu.
- `csharp/UI/DesktopLayer.cs` — WorkerW desktop attachment so widgets survive
  Win+D, with re-discovery on Explorer restart and an `HWND_BOTTOM` fallback.
- `csharp/UI/AppIcons.cs` / `TrayIcons.cs` — real shell icons (`SHGetFileInfo`,
  `WM_GETICON`) and real notification-area icons.
- `csharp/Services/VideoWallpaperDecoder.cs` — Media Foundation `IMFSourceReader`
  decode loop: RGB32 first, NV12 fallback with YUV conversion, frame pacing,
  looping, pause/resume, average-colour sampling for glass tinting.
- `csharp/Services/WindowsSystemHooks.cs` — Win32/WMI/PDH/CoreAudio/Media
  Foundation interop, telemetry, sensors, hooks, screen recording.
- `csharp/Services/StockQuotes.cs` — real quotes over HTTP with graceful offline.
- `csharp/Core/` — models, atomic settings persistence, telemetry history.

## Verification hooks

| Command | Checks |
| --- | --- |
| `./OpenPixel.exe --self-test` | 31 assertions covering glass clamping, themes, idle CPU, widgets, real video decode, telemetry, persistence, deck protocol, settings. |
| `./OpenPixel.exe --web-test` | WebView2 runtime resolvable. |
| `./OpenPixel.exe --qr-test` | QR encoder matrix (deck pairing). |
| `./OpenPixel.exe --video-test [path]` | Writes a synthetic clip and decodes it end-to-end. |
| `./OpenPixel.exe --video-test-file <path>` | Decodes an existing `.mp4`/`.mkv` and reports dimensions. |
| `./OpenPixel.exe --render-test [png]` | Renders the Day Clock typography to a PNG for visual comparison. |
| `dotnet test tests/NotchPixel.E2E.Tests.csproj` | Full end-to-end suite. |
| `powershell -File App/test_deck_protocol.ps1` | Android deck protocol harness. |

## Known constraints

- Video decoding needs codecs installed in Windows; unsupported files fall back
  to the gradient backdrop instead of crashing.
- Tray-icon enumeration is cross-process and may fail on some shells; the status
  cluster is hidden rather than stubbed.
- Numeric taskbar badges are only shown from real notification state, which
  requires user consent; otherwise they are omitted.
