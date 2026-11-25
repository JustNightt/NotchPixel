#pragma once
#include "../render/D2DRenderer.h"

namespace np {

// ============================================================================
// Categorized Settings Window (580 x 520 DIP Native Direct2D Window)
// Organized into all 10 required categories:
// 1. General
// 2. Accessibility
// 3. Island (incl. Appearance: Shape, Monitor, Edge, Corner Radius, Delay,
//            11 Themes, Glass Blur Slider, 6 Progress Bars, Visualizer, Fonts)
// 4. Panels
// 5. Widgets
// 6. Calls
// 7. Streaming
// 8. Gaming
// 9. Tools (PC Deck Private Wi-Fi + 2-min Pairing Code, Bottom Notch Taskbar)
// 10. Keys (Ctrl+Alt+N, Ctrl+Alt+H, Ctrl+Alt+M, Ctrl+Alt+1..6)
// Zero accounts, zero telemetry, zero ads, crisp cards without notch cutouts.
// ============================================================================
class SettingsWindow {
public:
    static bool Create(HINSTANCE hInst, GlobalAppState& state);
    static void Show(GlobalAppState& state);
    static void Destroy(GlobalAppState& state);
};

} // namespace np
