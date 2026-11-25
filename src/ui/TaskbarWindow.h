#pragma once
#include "../render/D2DRenderer.h"

namespace np {

// ============================================================================
// Optional Bottom Notch Taskbar Window
// - Inverted bottom-edge notch replacing Windows' Shell_TrayWnd
// - Start button (opens Start above taskbar without shifting other windows)
// - Pinned & running apps with Dock hover magnification & live window hover preview
// - Now-playing/timer pill, tray icons, quick settings trigger, 12/24h clock & date
// - Steps aside automatically for fullscreen games
// - Protected by out-of-process Watchdog Failsafe restoring Shell_TrayWnd on exit
// ============================================================================
class TaskbarWindow {
public:
    static bool CreateOrUpdate(HINSTANCE hInst, GlobalAppState& state);
    static void Destroy(GlobalAppState& state);
    static void RefreshRunningApps(GlobalAppState& state);
};

} // namespace np
