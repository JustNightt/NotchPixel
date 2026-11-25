#pragma once
#include "../render/D2DRenderer.h"

namespace np {

// ============================================================================
// Desktop Widgets Manager (20+ Pinned Wallpaper-Native Widgets)
// - Dynamically samples live wallpaper colors beneath Glass widgets
// - Stays underneath regular windows & survives Win+D Show Desktop
// - Supports scroll-wheel resize, right-click lock/close, and To-Do scrolling
// ============================================================================
class DesktopWidgetsWindow {
public:
    static void Initialize(HINSTANCE hInst);
    static void ToggleWidgetOnDesktop(GlobalAppState& state, WidgetType type);
    static void RefreshAll(GlobalAppState& state);
    static void DestroyAll(GlobalAppState& state);
};

} // namespace np
