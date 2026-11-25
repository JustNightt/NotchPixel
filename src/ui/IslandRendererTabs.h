#pragma once
#include "../render/D2DRenderer.h"

namespace np {

// ============================================================================
// Complete Island & Notch Renderer
// Renders:
// - Resting Pill (212x36 DIP), Recording Pill (250x36 DIP), Game Strip (10Hz)
// - Sensor dots (Mic/Cam/Screen + Call Timer), Caps/Num/Scroll Lock Pop-Ups
// - Reminder Alert Banner (Snooze 10m / Done) over fullscreen games
// - Top Tab Bar (10 panels + divider + Settings gear)
// - All 10 Expanded Cards:
//   01 Music (6 progress bars: Wave, Pixels, Comet, Song shape, Simple, Hearts)
//   02 Web (Pinned site tiles + Add site + lazy Web Drawer trigger)
//   03 Notifications & AI Agents (Claude Code, Codex, Antigravity, Hermes)
//   04 Stream Chat & OBS REC Pill
//   05 File Shelf (up to 12 dropped files, multi-select drag, Send to Phone QR)
//   06 Clipboard History (6 filter chips, current badge, pins, OCR Copy Text)
//   07 Widgets & Fast Reminders (2x2 tiles, Pin to Desktop, Focus Timer)
//   08 Quick Controls (Brightness DDC/CI, Volume, Wi-Fi, BT, Night Light, Focus, Power)
//   09 Launcher (Screenshot, Snip, Record + 6 Quick-Launcher slots Ctrl+Alt+1..6)
//   10 Calls (Ctrl+Alt+M Live Mic button, Next Meeting Join, Mic/Speaker/Cam Test)
// ============================================================================
class IslandUI {
public:
    static void ComputeCurrentIslandGeometry(
        const GlobalAppState& state,
        float canvasW, float& outLeft, float& outTop, float& outW, float& outH, float& outRadius);

    static void RenderIslandFrame(
        D2DRenderer& r, GlobalAppState& state,
        HWND hwnd, const RECT& windowScreenRect,
        float canvasW, float canvasH, float timeSec);
};

} // namespace np
