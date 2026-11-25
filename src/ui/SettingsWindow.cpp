#include "SettingsWindow.h"
#include "TaskbarWindow.h"
#include "DesktopWidgetsWindow.h"
#include "../services/SystemServices.h"
#include "../services/NetworkServers.h"
#include <windowsx.h>

namespace np {

static D2DRenderer s_settingsRenderer;
static float s_setMouseX = -1000.0f;
static float s_setMouseY = -1000.0f;

static void DrawSegmentedButtons(
    D2DRenderer& r, GlobalAppState& state,
    const std::wstring& rowLabel, float x, float& y, float totalW,
    const std::vector<std::wstring>& labels, int activeIdx,
    const ThemeSpec& theme, std::function<void(int)> onSelect)
{
    (void)state;
    r.DrawTextStr(rowLabel, D2D1::RectF(x, y, x + 114.0f, y + 24.0f), 11.5f, 600, theme.textMuted, false);
    float segLeft = x + 118.0f;
    float segW = totalW - 118.0f;
    int count = std::max(1, static_cast<int>(labels.size()));
    float btnW = (segW - (count - 1) * 4.0f) / static_cast<float>(count);

    for (int i = 0; i < count; ++i) {
        float bx = segLeft + i * (btnW + 4.0f);
        D2D1_RECT_F bRect = D2D1::RectF(bx, y + 1.0f, bx + btnW, y + 23.0f);
        bool isAct = (i == activeIdx);
        bool isHov = r.IsHovered(bRect, s_setMouseX, s_setMouseY);

        D2D1_COLOR_F bg = isAct ? theme.textPrimary
                        : (isHov ? Rgba(255, 255, 255, 0.14f) : Rgba(255, 255, 255, 0.07f));
        D2D1_COLOR_F fg = isAct ? AdaptiveInkForBackground(theme.textPrimary) : theme.textPrimary;
        r.FillRoundedRect(bRect, 11.0f, bg);
        r.DrawTextStr(labels[i], bRect, 10.5f, isAct ? 700 : 500, fg, false, DWRITE_TEXT_ALIGNMENT_CENTER);
        r.AddHitRegion("seg-" + WideToUtf8(rowLabel) + "-" + std::to_string(i), bRect, [onSelect, i]() {
            onSelect(i);
        });
    }
    y += 28.0f;
}

static void DrawToggleRow(
    D2DRenderer& r, const std::wstring& label, const std::wstring& subtitle,
    float x, float& y, float totalW, bool value,
    const ThemeSpec& theme, std::function<void()> onToggle)
{
    D2D1_RECT_F card = D2D1::RectF(x, y, x + totalW, y + 36.0f);
    r.FillRoundedRect(card, 9.0f, Rgba(255, 255, 255, 0.06f));
    r.DrawTextStr(label, D2D1::RectF(x + 10.0f, y + 2.0f, x + totalW - 64.0f, y + 19.0f), 11.5f, 600, theme.textPrimary, false);
    r.DrawTextStr(subtitle, D2D1::RectF(x + 10.0f, y + 18.0f, x + totalW - 64.0f, y + 33.0f), 10.0f, 400, theme.textMuted, false);

    D2D1_RECT_F pill = D2D1::RectF(x + totalW - 50.0f, y + 8.0f, x + totalW - 10.0f, y + 28.0f);
    r.FillRoundedRect(pill, 10.0f, value ? Rgba(48, 209, 88, 1.0f) : Rgba(255, 255, 255, 0.16f));
    float knobX = value ? (pill.right - 10.0f) : (pill.left + 10.0f);
    r.FillCircle(knobX, y + 18.0f, 7.0f, Rgba(255, 255, 255, 1.0f));

    r.AddHitRegion("tog-" + WideToUtf8(label), card, std::move(onToggle));
    y += 41.0f;
}

static void DrawSliderRow(
    D2DRenderer& r, const std::wstring& label, const std::wstring& valText,
    float x, float& y, float totalW, float norm01,
    const ThemeSpec& theme, std::function<void(float)> onSlide)
{
    r.DrawTextStr(label, D2D1::RectF(x, y, x + 138.0f, y + 22.0f), 11.5f, 600, theme.textMuted, false);
    D2D1_RECT_F track = D2D1::RectF(x + 142.0f, y + 7.0f, x + totalW - 54.0f, y + 15.0f);
    r.FillRoundedRect(track, 4.0f, Rgba(255, 255, 255, 0.14f));
    float fillW = (track.right - track.left) * std::clamp(norm01, 0.0f, 1.0f);
    r.FillRoundedRect(D2D1::RectF(track.left, track.top, track.left + fillW, track.bottom), 4.0f, theme.accent);
    r.DrawTextStr(valText, D2D1::RectF(x + totalW - 50.0f, y, x + totalW, y + 22.0f),
                  10.5f, 600, theme.textPrimary, true, DWRITE_TEXT_ALIGNMENT_TRAILING);
    r.AddHitRegion("sld-" + WideToUtf8(label), D2D1::RectF(track.left - 4.0f, y, track.right + 4.0f, y + 22.0f), nullptr, std::move(onSlide));
    y += 26.0f;
}

static void RenderSettings(HWND hwnd, GlobalAppState& state) {
    if (!hwnd || !IsWindow(hwnd)) return;
    RECT rc;
    GetWindowRect(hwnd, &rc);
    int wPx = rc.right - rc.left;
    int hPx = rc.bottom - rc.top;
    if (wPx <= 0 || hPx <= 0) return;

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    if (!s_settingsRenderer.RT()) {
        s_settingsRenderer.Initialize(state.settings.drawWithProcessor);
    }

    float dpi = GetDpiForWindow(hwnd) / 96.0f;
    if (!s_settingsRenderer.BeginLayeredFrame(hwnd, wPx, hPx, dpi)) return;
    s_settingsRenderer.ClearHitRegions();

    float wDip = wPx / dpi;
    float hDip = hPx / dpi;
    auto& s = state.settings;
    const ThemeSpec& theme = (s.theme == SurfaceTheme::PureWhite)
        ? GetThemeSpec(SurfaceTheme::PureWhite)
        : GetThemeSpec(SurfaceTheme::Black);

    D2D1_RECT_F frame = D2D1::RectF(1.0f, 1.0f, wDip - 1.0f, hDip - 1.0f);
    s_settingsRenderer.FillRoundedRect(frame, 14.0f, theme.fill);
    s_settingsRenderer.DrawRoundedRect(frame, 14.0f, Rgba(239, 237, 232, 0.16f), 1.0f);

    // Titlebar (38 DIP): Brand Logo + OpenPixel 0.5.126 + Escape hint + Close X
    s_settingsRenderer.FillRoundedRect(D2D1::RectF(14.0f, 10.0f, 32.0f, 28.0f), 5.0f, Rgba(26, 122, 240, 1.0f));
    s_settingsRenderer.DrawTextStr(L"OpenPixel  0.5.126",
                                   D2D1::RectF(40.0f, 7.0f, 210.0f, 31.0f), 13.0f, 700, theme.textPrimary, false);
    s_settingsRenderer.DrawTextStr(L"Zero accounts \x00B7 Zero telemetry \x00B7 Esc to close",
                                   D2D1::RectF(210.0f, 7.0f, wDip - 48.0f, 31.0f), 10.5f, 500, theme.textFaint, false,
                                   DWRITE_TEXT_ALIGNMENT_TRAILING);

    D2D1_RECT_F closeBtn = D2D1::RectF(wDip - 40.0f, 6.0f, wDip - 10.0f, 32.0f);
    bool closeHov = s_settingsRenderer.IsHovered(closeBtn, s_setMouseX, s_setMouseY);
    s_settingsRenderer.FillRoundedRect(closeBtn, 8.0f, closeHov ? Rgba(255, 69, 58, 0.85f) : Rgba(255, 255, 255, 0.10f));
    s_settingsRenderer.DrawTextStr(L"\x00D7", closeBtn, 16.0f, 700,
                                   closeHov ? Rgba(255, 255, 255, 1.0f) : theme.textPrimary,
                                   false, DWRITE_TEXT_ALIGNMENT_CENTER);
    s_settingsRenderer.AddHitRegion("settings-close", closeBtn, [hwnd]() {
        ShowWindow(hwnd, SW_HIDE);
    });

    s_settingsRenderer.DrawLine(12.0f, 38.0f, wDip - 12.0f, 38.0f, theme.edge, 1.0f);

    // Left Sidebar: All 10 Required Categories
    static const wchar_t* catNames[] = {
        L"General",
        L"Accessibility",
        L"Island",
        L"Panels",
        L"Widgets",
        L"Calls",
        L"Streaming",
        L"Gaming",
        L"Tools",
        L"Keys"
    };

    float sideX = 12.0f;
    float sideW = 120.0f;
    float sideY = 46.0f;
    for (int i = 0; i < static_cast<int>(SettingsCategory::Count); ++i) {
        D2D1_RECT_F cBtn = D2D1::RectF(sideX, sideY, sideX + sideW, sideY + 31.0f);
        bool active = (static_cast<int>(state.activeSettingsCat) == i);
        bool hov = s_settingsRenderer.IsHovered(cBtn, s_setMouseX, s_setMouseY);
        if (active || hov) {
            s_settingsRenderer.FillRoundedRect(cBtn, 8.0f, active ? theme.textPrimary : Rgba(255, 255, 255, 0.08f));
        }
        D2D1_COLOR_F fg = active ? AdaptiveInkForBackground(theme.textPrimary) : theme.textPrimary;
        s_settingsRenderer.DrawTextStr(catNames[i], D2D1::RectF(sideX + 10.0f, sideY, sideX + sideW - 6.0f, sideY + 31.0f),
                                       12.0f, active ? 700 : 500, fg, false);
        s_settingsRenderer.AddHitRegion("set-cat-" + std::to_string(i), cBtn, [&state, i]() {
            state.activeSettingsCat = static_cast<SettingsCategory>(i);
        });
        sideY += 35.0f;
    }

    s_settingsRenderer.DrawLine(142.0f, 44.0f, 142.0f, hDip - 12.0f, theme.edge, 1.0f);

    float paneX = 156.0f;
    float paneW = wDip - paneX - 14.0f;
    float py = 46.0f;

    auto refreshIsland = [&state]() {
        ConfigManager::SaveAtomic(state);
        if (state.hwndIsland && IsWindow(state.hwndIsland)) {
            SystemServices::ApplyWindowCaptureAffinity(state.hwndIsland, state);
            InvalidateRect(state.hwndIsland, nullptr, FALSE);
        }
        if (state.hwndTaskbar && IsWindow(state.hwndTaskbar)) {
            InvalidateRect(state.hwndTaskbar, nullptr, FALSE);
        }
        DesktopWidgetsWindow::RefreshAll(state);
    };

    switch (state.activeSettingsCat) {
        case SettingsCategory::General: {
            DrawToggleRow(s_settingsRenderer, L"Launch at Windows startup", L"Start OpenPixel silently in the system tray",
                          paneX, py, paneW, s.launchAtStartup, theme, [&]() { s.launchAtStartup = !s.launchAtStartup; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Draw with the processor (CPU fallback)", L"Software rendering for RTX 50 / non-DX12 GPUs",
                          paneX, py, paneW, s.drawWithProcessor, theme, [&]() { s.drawWithProcessor = !s.drawWithProcessor; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Reverse scrolling", L"Invert trackpad and wheel scroll direction",
                          paneX, py, paneW, s.reverseScrolling, theme, [&]() { s.reverseScrolling = !s.reverseScrolling; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Pause glass & particles on battery", L"Near-0% CPU battery saver throttling",
                          paneX, py, paneW, s.pauseEffectsOnBattery, theme, [&]() { s.pauseEffectsOnBattery = !s.pauseEffectsOnBattery; refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Font family", paneX, py, paneW,
                                 { L"Geist", L"Segoe", L"Inter", L"Mono", L"Serif", L"System" }, s.fontIndex, theme,
                                 [&](int idx) { s.fontIndex = idx; refreshIsland(); });
            break;
        }

        case SettingsCategory::Accessibility: {
            DrawSliderRow(s_settingsRenderer, L"Windows-scaled text",
                          std::to_wstring(static_cast<int>(s.textScale * 100.0f)) + L"%",
                          paneX, py, paneW, (s.textScale - 1.0f) / 0.5f, theme,
                          [&](float n) { s.textScale = 1.0f + n * 0.5f; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Higher contrast", L"Stronger borders and adaptive black/white label ink",
                          paneX, py, paneW, s.higherContrast, theme, [&]() { s.higherContrast = !s.higherContrast; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Reduce motion", L"Snap island & tabs immediately without bounce springs",
                          paneX, py, paneW, s.reduceMotion, theme, [&]() { s.reduceMotion = !s.reduceMotion; refreshIsland(); });
            DrawSliderRow(s_settingsRenderer, L"Notice duration",
                          std::to_wstring(static_cast<int>(s.noticeDurationSec)) + L"s",
                          paneX, py, paneW, (s.noticeDurationSec - 3.0f) / 12.0f, theme,
                          [&](float n) { s.noticeDurationSec = 3.0f + n * 12.0f; refreshIsland(); });
            break;
        }

        case SettingsCategory::Island: {
            DrawSegmentedButtons(s_settingsRenderer, state, L"Shape", paneX, py, paneW,
                                 { L"Island", L"Notch" }, static_cast<int>(s.shape), theme,
                                 [&](int idx) {
                                     s.shape = static_cast<IslandShape>(idx);
                                     state.notchSpring.SetTarget(s.shape == IslandShape::Notch ? 100.0f : 0.0f, s.reduceMotion);
                                     refreshIsland();
                                 });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Edge placement", paneX, py, paneW,
                                 { L"Top center", L"Left edge", L"Right edge" }, static_cast<int>(s.edge), theme,
                                 [&](int idx) { s.edge = static_cast<EdgePlacement>(idx); refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Monitor", paneX, py, paneW,
                                 { L"Primary (1)", L"Display 2", L"Display 3" }, std::clamp(s.monitorIndex, 0, 2), theme,
                                 [&](int idx) { s.monitorIndex = idx; refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Surface Group 1", paneX, py, paneW,
                                 { L"Liquid Glass", L"Candy glass", L"Acrylic", L"Black", L"Pure black", L"Graphite" },
                                 (static_cast<int>(s.theme) < 6) ? static_cast<int>(s.theme) : -1, theme,
                                 [&](int idx) { s.theme = static_cast<SurfaceTheme>(idx); refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Surface Group 2", paneX, py, paneW,
                                 { L"Pure white", L"Ros\x00E9", L"Lavender", L"Peach", L"Sakura" },
                                 (static_cast<int>(s.theme) >= 6) ? (static_cast<int>(s.theme) - 6) : -1, theme,
                                 [&](int idx) { s.theme = static_cast<SurfaceTheme>(idx + 6); refreshIsland(); });
            DrawSliderRow(s_settingsRenderer, L"Glass blur slider",
                          std::to_wstring(static_cast<int>(s.glassBlurRadius)) + L" px",
                          paneX, py, paneW, s.glassBlurRadius / 40.0f, theme,
                          [&](float n) { s.glassBlurRadius = std::round(n * 40.0f); refreshIsland(); });
            DrawSliderRow(s_settingsRenderer, L"Corner roundness",
                          std::to_wstring(static_cast<int>(s.cornerRadiusScale * 100.0f)) + L"%",
                          paneX, py, paneW, (s.cornerRadiusScale - 0.15f) / 0.85f, theme,
                          [&](float n) { s.cornerRadiusScale = 0.15f + n * 0.85f; refreshIsland(); });
            DrawSliderRow(s_settingsRenderer, L"Hover open delay",
                          std::to_wstring(s.hoverOpenDelaySec).substr(0, 4) + L"s",
                          paneX, py, paneW, s.hoverOpenDelaySec / 1.5f, theme,
                          [&](float n) { s.hoverOpenDelaySec = std::round(n * 15.0f) / 10.0f; refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Progress bar", paneX, py, paneW,
                                 { L"Wave", L"Pixels", L"Comet", L"Song shape", L"Simple", L"Hearts" },
                                 static_cast<int>(s.progressBar), theme,
                                 [&](int idx) { s.progressBar = static_cast<ProgressBarStyle>(idx); refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Visualizer & BG", paneX, py, paneW,
                                 { L"Pixels Vis", L"Bars Vis", L"Album BG", L"Plain BG" },
                                 static_cast<int>(s.visualizer), theme,
                                 [&](int idx) {
                                     if (idx < 2) s.visualizer = static_cast<VisualizerStyle>(idx);
                                     else s.cardBackground = static_cast<CardBackgroundMode>(idx - 2);
                                     refreshIsland();
                                 });
            DrawToggleRow(s_settingsRenderer, L"Show song title in closed island", L"Display track title between cover and visualizer",
                          paneX, py, paneW, s.showSongTitleInClosedPill, theme,
                          [&]() { s.showSongTitleInClosedPill = !s.showSongTitleInClosedPill; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Hide from screenshots and recordings", L"Glass & Candy glass always stay hidden from captures",
                          paneX, py, paneW, s.hideFromScreenshots, theme,
                          [&]() { s.hideFromScreenshots = !s.hideFromScreenshots; refreshIsland(); });
            break;
        }

        case SettingsCategory::Panels: {
            DrawToggleRow(s_settingsRenderer, L"Collapse onto last-viewed tab", L"Reopen on last tab instead of always resetting to Music",
                          paneX, py, paneW, s.reopenOnLastTab, theme, [&]() { s.reopenOnLastTab = !s.reopenOnLastTab; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Clock timestamps in Notifications", L"Show exact time (14:32) instead of relative (2m ago)",
                          paneX, py, paneW, s.clockTimestampsInNotifications, theme,
                          [&]() { s.clockTimestampsInNotifications = !s.clockTimestampsInNotifications; refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Music app filter", paneX, py, paneW,
                                 { L"All media apps", L"Music players only", L"Use exclude list" }, s.musicFilterMode, theme,
                                 [&](int idx) { s.musicFilterMode = idx; refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Resting info mode", paneX, py, paneW,
                                 { L"Media", L"Time", L"Date", L"Weather", L"Battery", L"CPU", L"Event", L"Pet" },
                                 static_cast<int>(s.restingMode), theme,
                                 [&](int idx) { s.restingMode = static_cast<RestingInfoMode>(idx); refreshIsland(); });
            break;
        }

        case SettingsCategory::Widgets: {
            DrawToggleRow(s_settingsRenderer, L"24-hour clock format", L"Use 24-hour time on island, taskbar, and widgets",
                          paneX, py, paneW, s.use24HourClock, theme, [&]() { s.use24HourClock = !s.use24HourClock; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Lock desktop widgets in place", L"Or right-click any desktop widget to toggle Lock / Unpin",
                          paneX, py, paneW, s.lockDesktopWidgets, theme, [&]() { s.lockDesktopWidgets = !s.lockDesktopWidgets; refreshIsland(); });
            DrawSegmentedButtons(s_settingsRenderer, state, L"Pin to Desktop", paneX, py, paneW,
                                 { L"Clock", L"Analog Clock", L"Weather", L"To-Do List", L"PC Stats" }, -1, theme,
                                 [&](int idx) {
                                     static const WidgetType map[] = {
                                         WidgetType::Clock, WidgetType::AnalogClock, WidgetType::MinimalWeather,
                                         WidgetType::TodoList, WidgetType::PcStats
                                     };
                                     DesktopWidgetsWindow::ToggleWidgetOnDesktop(state, map[idx]);
                                 });
            break;
        }

        case SettingsCategory::Calls: {
            DrawToggleRow(s_settingsRenderer, L"Global mic mute notice", L"Show island pop-up when pressing Ctrl+Alt+M",
                          paneX, py, paneW, s.showGlobalMuteNotice, theme,
                          [&]() { s.showGlobalMuteNotice = !s.showGlobalMuteNotice; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Ignore background Windows services", L"Sensor dots only name active foreground call apps (0.5.124)",
                          paneX, py, paneW, true, theme, []() {});
            break;
        }

        case SettingsCategory::Streaming: {
            DrawToggleRow(s_settingsRenderer, L"Twitch & YouTube chat drawer", L"Movable live chat drawer + red dot on Chat tab",
                          paneX, py, paneW, s.streamChatEnabled, theme, [&]() {
                              s.streamChatEnabled = !s.streamChatEnabled;
                              state.chatDrawerVisible = s.streamChatEnabled;
                              if (state.hwndChatDrawer) ShowWindow(state.hwndChatDrawer, state.chatDrawerVisible ? SW_SHOWNOACTIVATE : SW_HIDE);
                              refreshIsland();
                          });
            DrawToggleRow(s_settingsRenderer, L"Island chat pop-ups", L"Briefly surface new chat messages in closed island",
                          paneX, py, paneW, s.showIslandChatPopups, theme, [&]() { s.showIslandChatPopups = !s.showIslandChatPopups; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"OBS Studio REC pill", L"Show red REC pill with elapsed timer while OBS records",
                          paneX, py, paneW, s.showObsRecPill, theme, [&]() { s.showObsRecPill = !s.showObsRecPill; refreshIsland(); });
            break;
        }

        case SettingsCategory::Gaming: {
            DrawSegmentedButtons(s_settingsRenderer, state, L"Game strip mode", paneX, py, paneW,
                                 { L"Fullscreen games", L"Every fullscreen app", L"Off" }, s.gameStripMode, theme,
                                 [&](int idx) { s.gameStripMode = idx; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Game strip pill background", L"Turn off for numbers-only zero-overhead software overlay",
                          paneX, py, paneW, s.gameStripShowBackground, theme,
                          [&]() { s.gameStripShowBackground = !s.gameStripShowBackground; refreshIsland(); });
            DrawToggleRow(s_settingsRenderer, L"Network speed in Mbps", L"Display download/upload in Mbps instead of Kbps",
                          paneX, py, paneW, s.gameStripNetInMbps, theme,
                          [&]() { s.gameStripNetInMbps = !s.gameStripNetInMbps; refreshIsland(); });
            break;
        }

        case SettingsCategory::Tools: {
            std::wstring netSub = state.isNetworkPrivate
                ? (L"Private Wi-Fi (" + state.localPrivateIp + L":" + std::to_wstring(s.pcDeckPort) + L") \x00B7 2-min code: " + state.activePairingCode)
                : L"Blocked: Windows network is marked Public (Private Wi-Fi required)";
            DrawToggleRow(s_settingsRenderer, L"PC Deck (Local Wi-Fi Companion Remote)", netSub,
                          paneX, py, paneW, s.pcDeckEnabled, theme, [&]() {
                              if (NetworkServers::DetectPrivateNetworkAndLocalIp(state)) {
                                  s.pcDeckEnabled = !s.pcDeckEnabled;
                                  if (s.pcDeckEnabled) NetworkServers::GenerateTwoMinutePairingCode(state);
                              }
                              refreshIsland();
                          });

            DrawToggleRow(s_settingsRenderer, L"Bottom Notch Taskbar (Optional)",
                          L"Replaces Windows taskbar with Start, pins, tray & crash watchdog failsafe",
                          paneX, py, paneW, s.bottomTaskbarEnabled, theme, [&]() {
                              s.bottomTaskbarEnabled = !s.bottomTaskbarEnabled;
                              TaskbarWindow::CreateOrUpdate(GetModuleHandleW(nullptr), state);
                              refreshIsland();
                          });
            DrawToggleRow(s_settingsRenderer, L"Taskbar dock magnification & live hover previews",
                          L"Magnify icons on hover and preview window titles above taskbar",
                          paneX, py, paneW, s.taskbarDockMagnification, theme, [&]() {
                              s.taskbarDockMagnification = !s.taskbarDockMagnification;
                              s.taskbarLiveHoverPreviews = s.taskbarDockMagnification;
                              refreshIsland();
                          });
            DrawToggleRow(s_settingsRenderer, L"Keep screenshots and recordings off the Shelf",
                          L"Save captures to Pictures without adding them to the 12-file Shelf",
                          paneX, py, paneW, s.keepCapturesOffShelf, theme, [&]() {
                              s.keepCapturesOffShelf = !s.keepCapturesOffShelf;
                              refreshIsland();
                          });

            if (s.pcDeckEnabled && state.isNetworkPrivate) {
                std::wstring deckUrl = L"http://" + state.localPrivateIp + L":" + std::to_wstring(s.pcDeckPort);
                s_settingsRenderer.DrawQrCode(deckUrl, paneX, py + 2.0f, 78.0f, Rgba(12, 12, 14, 1.0f), Rgba(239, 237, 232, 1.0f));
                s_settingsRenderer.DrawTextStr(L"Pairing Code (2 min): " + state.activePairingCode,
                                               D2D1::RectF(paneX + 88.0f, py + 4.0f, paneX + paneW, py + 24.0f),
                                               12.5f, 700, theme.accent, true);
                s_settingsRenderer.DrawTextStr(L"URL: " + deckUrl + L" \x00B7 Paired: " + std::to_wstring(state.pairedDeckDevices.size()),
                                               D2D1::RectF(paneX + 88.0f, py + 24.0f, paneX + paneW, py + 44.0f),
                                               10.5f, 500, theme.textMuted, true);

                D2D1_RECT_F regenBtn = D2D1::RectF(paneX + 88.0f, py + 48.0f, paneX + 220.0f, py + 74.0f);
                s_settingsRenderer.FillRoundedRect(regenBtn, 8.0f, Rgba(255, 255, 255, 0.10f));
                s_settingsRenderer.DrawTextStr(L"New 2-Min Code", regenBtn, 10.5f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                s_settingsRenderer.AddHitRegion("deck-regen-code", regenBtn, [&]() {
                    NetworkServers::GenerateTwoMinutePairingCode(state);
                });

                D2D1_RECT_F clearDevBtn = D2D1::RectF(paneX + 228.0f, py + 48.0f, paneX + paneW, py + 74.0f);
                s_settingsRenderer.FillRoundedRect(clearDevBtn, 8.0f, Rgba(255, 69, 58, 0.22f));
                s_settingsRenderer.DrawTextStr(L"Remove Paired Phones", clearDevBtn, 10.5f, 600, Rgba(255, 105, 97, 1.0f), false, DWRITE_TEXT_ALIGNMENT_CENTER);
                s_settingsRenderer.AddHitRegion("deck-clear-devs", clearDevBtn, [&]() {
                    state.pairedDeckDevices.clear();
                });
            }
            break;
        }

        case SettingsCategory::Keys: {
            static const wchar_t* keyRows[][2] = {
                { L"Open / Collapse Island", L"Ctrl + Alt + N" },
                { L"Hide / Show Island",     L"Ctrl + Alt + H" },
                { L"Global Microphone Mute", L"Ctrl + Alt + M" },
                { L"Screen Colour Picker",   L"Ctrl + Alt + C" },
                { L"Quick Launcher Slot 1",  L"Ctrl + Alt + 1" },
                { L"Quick Launcher Slot 2",  L"Ctrl + Alt + 2" },
                { L"Quick Launcher Slot 3",  L"Ctrl + Alt + 3" },
                { L"Quick Launcher Slot 4",  L"Ctrl + Alt + 4" },
                { L"Quick Launcher Slot 5",  L"Ctrl + Alt + 5" },
                { L"Quick Launcher Slot 6",  L"Ctrl + Alt + 6" }
            };
            for (const auto& kr : keyRows) {
                D2D1_RECT_F row = D2D1::RectF(paneX, py, paneX + paneW, py + 31.0f);
                s_settingsRenderer.FillRoundedRect(row, 8.0f, Rgba(255, 255, 255, 0.06f));
                s_settingsRenderer.DrawTextStr(kr[0], D2D1::RectF(paneX + 12.0f, py, paneX + paneW - 140.0f, py + 31.0f),
                                               11.5f, 600, theme.textPrimary, false);
                s_settingsRenderer.DrawTextStr(kr[1], D2D1::RectF(paneX + paneW - 140.0f, py, paneX + paneW - 12.0f, py + 31.0f),
                                               11.0f, 600, theme.accent, true, DWRITE_TEXT_ALIGNMENT_TRAILING);
                py += 35.0f;
            }
            break;
        }

        default:
            break;
    }

    POINT screenPos = { rc.left, rc.top };
    s_settingsRenderer.EndLayeredFrame(hwnd, screenPos, 252);
}

static LRESULT CALLBACK SettingsWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto& state = GetAppState();
    switch (msg) {
        case WM_CLOSE:
            ShowWindow(hwnd, SW_HIDE);
            return 0;

        case WM_KEYDOWN:
            if (wParam == VK_ESCAPE) {
                ShowWindow(hwnd, SW_HIDE);
                return 0;
            }
            break;

        case WM_NCHITTEST: {
            POINT pt = { GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam) };
            ScreenToClient(hwnd, &pt);
            RECT rc = {};
            GetClientRect(hwnd, &rc);
            float dpi = GetDpiForWindow(hwnd) / 96.0f;
            float dipX = pt.x / dpi;
            float dipY = pt.y / dpi;
            float wDip = (rc.right - rc.left) / dpi;

            // Keep top-right Close button (wDip - 48..wDip) as HTCLIENT so it is always clickable!
            if (dipY < 38.0f && dipX < (wDip - 48.0f)) {
                return HTCAPTION;
            }
            return HTCLIENT;
        }

        case WM_MOUSEMOVE: {
            float dpi = GetDpiForWindow(hwnd) / 96.0f;
            s_setMouseX = GET_X_LPARAM(lParam) / dpi;
            s_setMouseY = GET_Y_LPARAM(lParam) / dpi;
            if (wParam & MK_LBUTTON) {
                s_settingsRenderer.DispatchDrag(s_setMouseX, s_setMouseY);
            }
            RenderSettings(hwnd, state);
            return 0;
        }

        case WM_LBUTTONDOWN: {
            float dpi = GetDpiForWindow(hwnd) / 96.0f;
            s_setMouseX = GET_X_LPARAM(lParam) / dpi;
            s_setMouseY = GET_Y_LPARAM(lParam) / dpi;
            s_settingsRenderer.DispatchClick(s_setMouseX, s_setMouseY);
            if (IsWindowVisible(hwnd)) {
                RenderSettings(hwnd, state);
            }
            return 0;
        }

        case WM_PAINT: {
            PAINTSTRUCT ps;
            BeginPaint(hwnd, &ps);
            RenderSettings(hwnd, state);
            EndPaint(hwnd, &ps);
            return 0;
        }
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

bool SettingsWindow::Create(HINSTANCE hInst, GlobalAppState& state) {
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = SettingsWndProc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"OpenPixelSettingsWnd";
    RegisterClassExW(&wc);

    // Fit smaller/narrow laptop screens without clipping
    RECT workArea = { 0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN) };
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &workArea, 0);
    int availW = workArea.right - workArea.left;
    int availH = workArea.bottom - workArea.top;

    int wPx = std::min(560, static_cast<int>(availW * 0.88f));
    int hPx = std::min(440, static_cast<int>(availH * 0.86f));

    state.hwndSettings = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOPMOST,
        wc.lpszClassName,
        L"OpenPixel Settings",
        WS_POPUP,
        workArea.left + (availW - wPx) / 2,
        workArea.top + (availH - hPx) / 2,
        wPx, hPx,
        nullptr, nullptr, hInst, nullptr);

    return state.hwndSettings != nullptr;
}

void SettingsWindow::Show(GlobalAppState& state) {
    if (state.hwndSettings && IsWindow(state.hwndSettings)) {
        if (IsWindowVisible(state.hwndSettings)) {
            ShowWindow(state.hwndSettings, SW_HIDE);
        } else {
            ShowWindow(state.hwndSettings, SW_SHOW);
            SetForegroundWindow(state.hwndSettings);
            RenderSettings(state.hwndSettings, state);
        }
    }
}

void SettingsWindow::Destroy(GlobalAppState& state) {
    if (state.hwndSettings) {
        DestroyWindow(state.hwndSettings);
        state.hwndSettings = nullptr;
    }
}

} // namespace np
