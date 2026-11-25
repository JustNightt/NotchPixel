#pragma once
#include "../render/D2DRenderer.h"

namespace np {

// ============================================================================
// Detachable Drawers Manager
// 1. Synced Lyrics Drawer (LRCLIB synchronized time-coded scrolling lyrics)
// 2. Movable Twitch & YouTube Live Stream Chat Drawer
// 3. Lazy-Loaded Web Drawer (opens over apps & fullscreen games without
//    stealing focus via WS_EX_NOACTIVATE, and stays 100% dormant when closed)
// ============================================================================
class DrawersWindow {
public:
    static void Initialize(HINSTANCE hInst, GlobalAppState& state);
    static void RefreshVisibleDrawers(GlobalAppState& state);
    static void DestroyAll(GlobalAppState& state);
};

} // namespace np
