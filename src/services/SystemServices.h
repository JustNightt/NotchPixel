#pragma once
#include "../core/ConfigManager.h"
#include <shellapi.h>

namespace np {

// ============================================================================
// Windows Native System Integration Services (Zero Placeholders)
// - Real CoreAudio Master Volume & Mute + 0.5.119 Device-Switch Pop-up Guard
// - Real Display Brightness (COM WMI WmiMonitorBrightness + VESA DDC/CI)
// - Real Windows Media Player Session Detection & LRCLIB Synced Lyrics
// - First-Class Free Buff + Claude Code, Codex, Antigravity, Hermes Detector
// - Privacy Sensor Detection (Mic, Camera, Screen Recording active apps)
// - 10 Hz Hardware Telemetry (filters out fake desktop motherboard temps)
// - Color Picker (Ctrl+Alt+C), Screenshot, Interactive Snip, Screen Recorder
// - Bottom Taskbar Out-of-Process Watchdog Failsafe
// ============================================================================
class SystemServices {
public:
    static void Initialize(GlobalAppState& state);
    static void Shutdown(GlobalAppState& state);

    // Periodic 10 Hz & 1 Hz background updates (zero overhead when idle)
    static void Tick10HzTelemetryAndSensors(GlobalAppState& state);
    static void Tick1HzMediaAndReminders(GlobalAppState& state);

    // Real CoreAudio Volume & Mute (with Device-Switch Pop-up Suppression)
    static void SyncSystemAudioState(GlobalAppState& state, bool allowPopup);
    static void SetMasterVolume(GlobalAppState& state, float volume0to100, bool showPopup = true);
    static void ToggleGlobalMicMute(GlobalAppState& state);

    // Real Display Brightness (WMI + VESA DDC/CI across monitors)
    static bool ReadMonitorBrightnessLevel(float& outBrightness0to100);
    static void SetMonitorBrightnessLevel(GlobalAppState& state, float brightness0to100, bool showPopup = true);
    static void SetNightLightEnabled(bool enabled);

    // Real Media Session Detection & Transport
    static void PollRealMediaSession(GlobalAppState& state);
    static void MediaTogglePlayPause(GlobalAppState& state);
    static void MediaNextTrack(GlobalAppState& state);
    static void MediaPreviousTrack(GlobalAppState& state);
    static void SeekMediaPosition(GlobalAppState& state, double positionSec);

    // Free Buff & Coding Agents Live Detector (Free Buff, Claude Code, Codex, Antigravity, Hermes)
    static void PollCodingAgents(GlobalAppState& state);

    // Clipboard, OCR, File Shelf & Color Picker (Ctrl+Alt+C)
    static void OnClipboardUpdated(GlobalAppState& state);
    static void CopyTextToClipboard(const std::wstring& text);
    static void AddDroppedFilesToShelf(GlobalAppState& state, HDROP hDrop);
    static std::wstring PickScreenColorUnderCursor(GlobalAppState& state);

    // Capture Tools & Quick Launcher
    static void CaptureFullScreenshot(GlobalAppState& state);
    static void StartInteractiveSnip(GlobalAppState& state);
    static void StartScreenRecording(GlobalAppState& state);
    static void StopScreenRecording(GlobalAppState& state);
    static void ExecuteLauncherSlot(GlobalAppState& state, int slotIndex);

    // Desktop Widgets & Screenshot Capture Affinity
    static void SpawnOrToggleDesktopWidget(GlobalAppState& state, WidgetType type);
    static void ApplyWindowCaptureAffinity(HWND hwnd, const GlobalAppState& state);

    // Out-of-Process Taskbar Watchdog Failsafe
    static void SpawnTaskbarWatchdogProcess();
    static int RunWatchdogMain(DWORD parentPid);
};

} // namespace np
