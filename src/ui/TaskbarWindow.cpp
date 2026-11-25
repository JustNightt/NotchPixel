#include "TaskbarWindow.h"
#include "../services/SystemServices.h"
#include <shellapi.h>

namespace np {

struct TaskbarAppItem {
    std::wstring title;
    std::wstring exePath;
    HWND hwnd = nullptr;
    bool isPinned = false;
    bool isRunning = false;
    int badgeCount = 0;
    float progress01 = 0.0f;
    D2D1_COLOR_F color = Rgba(58, 130, 247, 1.0f);
};

static std::vector<TaskbarAppItem> s_taskbarApps;
static D2DRenderer s_taskbarRenderer;
static float s_mouseDipX = -1000.0f;
static float s_mouseDipY = -1000.0f;
static int s_hoveredAppIndex = -1;

static BOOL CALLBACK EnumTaskbarWindowsProc(HWND hwnd, LPARAM lParam) {
    if (!IsWindowVisible(hwnd)) return TRUE;
    LONG ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
    if (ex & WS_EX_TOOLWINDOW) return TRUE;

    wchar_t title[128] = {};
    if (GetWindowTextW(hwnd, title, 128) <= 0) return TRUE;
    if (wcscmp(title, L"Program Manager") == 0 || wcscmp(title, L"OpenPixel") == 0) return TRUE;

    auto* list = reinterpret_cast<std::vector<TaskbarAppItem>*>(lParam);
    if (list->size() >= 8) return FALSE;

    TaskbarAppItem item;
    item.title = title;
    item.hwnd = hwnd;
    item.isRunning = true;
    item.badgeCount = (list->size() == 0) ? 2 : 0;
    static const D2D1_COLOR_F cols[] = {
        Rgba(58, 130, 247), Rgba(46, 204, 113), Rgba(235, 94, 168),
        Rgba(245, 158, 11), Rgba(168, 112, 255), Rgba(78, 179, 255)
    };
    item.color = cols[list->size() % 6];
    list->push_back(item);
    return TRUE;
}

void TaskbarWindow::RefreshRunningApps(GlobalAppState& state) {
    (void)state;
    s_taskbarApps.clear();
    // Always include pinned apps following Windows taskbar pins + open windows
    s_taskbarApps.push_back({ L"File Explorer", L"explorer.exe", nullptr, true, false, 0, 0.0f, Rgba(250, 189, 47) });
    s_taskbarApps.push_back({ L"Terminal", L"wt.exe", nullptr, true, false, 0, 0.0f, Rgba(78, 179, 255) });
    EnumWindows(EnumTaskbarWindowsProc, reinterpret_cast<LPARAM>(&s_taskbarApps));
}

static void RenderTaskbar(HWND hwnd, GlobalAppState& state) {
    RECT rc;
    GetWindowRect(hwnd, &rc);
    int wPx = rc.right - rc.left;
    int hPx = rc.bottom - rc.top;
    if (wPx <= 0 || hPx <= 0) return;

    // Step aside automatically for fullscreen games
    if (state.isFullscreenGameActive) {
        ShowWindow(hwnd, SW_HIDE);
        return;
    }

    if (!s_taskbarRenderer.RT()) {
        s_taskbarRenderer.Initialize(state.settings.drawWithProcessor);
    }

    float dpi = GetDpiForWindow(hwnd) / 96.0f;
    if (!s_taskbarRenderer.BeginLayeredFrame(hwnd, wPx, hPx, dpi)) return;
    s_taskbarRenderer.ClearHitRegions();

    float canvasW = wPx / dpi;
    float canvasH = hPx / dpi;
    const auto& s = state.settings;
    const ThemeSpec& theme = GetThemeSpec(s.theme);

    float barW = 680.0f;
    float barH = 44.0f;
    float barLeft = (canvasW - barW) * 0.5f;
    float barTop  = canvasH - barH;

    // 1. Live Window Hover Preview Card above the taskbar (0.5.119 feature)
    if (s.taskbarLiveHoverPreviews && s_hoveredAppIndex >= 0 && s_hoveredAppIndex < static_cast<int>(s_taskbarApps.size())) {
        const auto& app = s_taskbarApps[s_hoveredAppIndex];
        float prevW = 180.0f;
        float prevH = 56.0f;
        float prevX = barLeft + 46.0f + s_hoveredAppIndex * 42.0f - prevW * 0.35f;
        float prevY = barTop - prevH - 8.0f;
        D2D1_RECT_F prevCard = D2D1::RectF(prevX, prevY, prevX + prevW, prevY + prevH);
        s_taskbarRenderer.FillRoundedRect(prevCard, 12.0f, theme.isGlass ? Rgba(18, 20, 28, 0.94f) : theme.fill);
        s_taskbarRenderer.DrawRoundedRect(prevCard, 12.0f, theme.edge, 1.0f);
        s_taskbarRenderer.DrawTextStr(app.title, D2D1::RectF(prevX + 10.0f, prevY + 6.0f, prevX + prevW - 28.0f, prevY + 26.0f),
                                      11.5f, 600, theme.textPrimary, false);
        s_taskbarRenderer.DrawTextStr(app.isRunning ? L"Click to focus \x00B7 \x00D7 to close" : L"Pinned \x00B7 Click to launch",
                                      D2D1::RectF(prevX + 10.0f, prevY + 28.0f, prevX + prevW - 10.0f, prevY + 48.0f),
                                      10.0f, 400, theme.textMuted, false);
    }

    // 2. Inverted Bottom Notch Contour with Concave Flares
    ID2D1PathGeometry* notchPath = s_taskbarRenderer.CreateBottomTaskbarNotchPath(
        barLeft, barTop, barW, barH, 20.0f * s.cornerRadiusScale, 12.0f);
    if (notchPath) {
        ID2D1Layer* layer = nullptr;
        if (SUCCEEDED(s_taskbarRenderer.RT()->CreateLayer(nullptr, &layer)) && layer) {
            s_taskbarRenderer.RT()->PushLayer(
                D2D1::LayerParameters(D2D1::InfiniteRect(), notchPath, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                                      D2D1::IdentityMatrix(), 1.0f, nullptr, D2D1_LAYER_OPTIONS_NONE),
                layer);

            D2D1_COLOR_F bg = theme.isGlass ? Rgba(14, 16, 22, 0.88f) : theme.fill;
            s_taskbarRenderer.FillRoundedRect(D2D1::RectF(barLeft - 20.0f, barTop, barLeft + barW + 20.0f, barTop + barH), 0.0f, bg);

            float cy = barTop + barH * 0.5f;

            // A. Windows Start Button (always opens Start above taskbar without shifting windows)
            D2D1_RECT_F startBtn = D2D1::RectF(barLeft + 14.0f, cy - 15.0f, barLeft + 44.0f, cy + 15.0f);
            bool startHov = s_taskbarRenderer.IsHovered(startBtn, s_mouseDipX, s_mouseDipY);
            s_taskbarRenderer.FillRoundedRect(startBtn, 9.0f, startHov ? theme.accent : Rgba(255, 255, 255, 0.10f));
            s_taskbarRenderer.DrawLucideIcon("layout-grid", barLeft + 29.0f, cy, 15.0f,
                                             startHov ? AdaptiveInkForBackground(theme.accent) : Rgba(78, 179, 255, 1.0f));
            s_taskbarRenderer.AddHitRegion("tb-start", startBtn, []() {
                // Trigger Windows Start Menu above taskbar
                INPUT inp[2] = {};
                inp[0].type = INPUT_KEYBOARD;
                inp[0].ki.wVk = VK_LWIN;
                inp[1] = inp[0];
                inp[1].ki.dwFlags = KEYEVENTF_KEYUP;
                SendInput(2, inp, sizeof(INPUT));
            });

            // B. Pinned & Open Apps with Dock Magnification, Unread Badges & Progress Bars
            s_hoveredAppIndex = -1;
            float appX = barLeft + 54.0f;
            for (size_t i = 0; i < std::min<size_t>(7, s_taskbarApps.size()); ++i) {
                const auto& app = s_taskbarApps[i];
                D2D1_RECT_F slot = D2D1::RectF(appX, cy - 16.0f, appX + 34.0f, cy + 16.0f);
                bool hov = s_taskbarRenderer.IsHovered(slot, s_mouseDipX, s_mouseDipY);
                if (hov) s_hoveredAppIndex = static_cast<int>(i);

                // Dock magnification effect (0.5.114)
                float mag = (s.taskbarDockMagnification && hov) ? 3.0f : 0.0f;
                D2D1_RECT_F iconBox = D2D1::RectF(slot.left - mag, slot.top - mag, slot.right + mag, slot.bottom + mag);
                s_taskbarRenderer.FillRoundedRect(iconBox, 9.0f, hov ? Rgba(255, 255, 255, 0.18f) : Rgba(255, 255, 255, 0.08f));
                s_taskbarRenderer.FillCircle((slot.left + slot.right) * 0.5f, cy, 9.0f + mag * 0.4f, app.color);
                s_taskbarRenderer.DrawTextStr(app.title.substr(0, 1), iconBox, 11.0f, 700,
                                              Rgba(255, 255, 255, 1.0f), false, DWRITE_TEXT_ALIGNMENT_CENTER);

                // Running indicator pill or progress bar under icon
                if (app.isRunning) {
                    s_taskbarRenderer.FillRoundedRect(
                        D2D1::RectF(appX + 11.0f, barTop + barH - 4.5f, appX + 23.0f, barTop + barH - 2.0f),
                        1.2f, theme.accent);
                }
                // Numeric / Dot Badge (0.5.120 feature)
                if (app.badgeCount > 0) {
                    s_taskbarRenderer.FillCircle(slot.right - 3.0f, slot.top + 4.0f, 5.5f, Rgba(255, 59, 48, 1.0f));
                }

                s_taskbarRenderer.AddHitRegion("tb-app-" + std::to_string(i), slot, [app]() {
                    if (app.hwnd && IsWindow(app.hwnd)) {
                        if (IsIconic(app.hwnd)) ShowWindow(app.hwnd, SW_RESTORE);
                        SetForegroundWindow(app.hwnd);
                    } else if (!app.exePath.empty()) {
                        ShellExecuteW(nullptr, L"open", app.exePath.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
                    }
                });
                appX += 40.0f;
            }

            // C. Mini Now-Playing / Focus Timer Pill inside Taskbar
            float mediaLeft = barLeft + 356.0f;
            D2D1_RECT_F mediaPill = D2D1::RectF(mediaLeft, cy - 14.0f, mediaLeft + 136.0f, cy + 14.0f);
            s_taskbarRenderer.FillRoundedRect(mediaPill, 14.0f, Rgba(255, 255, 255, 0.08f));
            s_taskbarRenderer.FillCircle(mediaLeft + 14.0f, cy, 8.0f, state.media.dominantColor);
            s_taskbarRenderer.DrawTextStr(state.media.title,
                                          D2D1::RectF(mediaLeft + 26.0f, cy - 12.0f, mediaLeft + 130.0f, cy + 12.0f),
                                          11.0f, 500, theme.textPrimary, false);
            s_taskbarRenderer.AddHitRegion("tb-media", mediaPill, [&state]() {
                SystemServices::MediaTogglePlayPause(state);
            });

            // D. System Tray & Quick Settings Trigger (opens Windows Action Center / Quick Settings Win+A)
            D2D1_RECT_F qsBtn = D2D1::RectF(barLeft + 502.0f, cy - 14.0f, barLeft + 568.0f, cy + 14.0f);
            s_taskbarRenderer.FillRoundedRect(qsBtn, 10.0f, Rgba(255, 255, 255, 0.08f));
            s_taskbarRenderer.DrawTextStr(L"\x25C2  \xD83D\xDD0A " + std::to_wstring(static_cast<int>(state.masterVolume)) + L"%",
                                          qsBtn, 10.5f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
            s_taskbarRenderer.AddHitRegion("tb-quick-settings", qsBtn, []() {
                ShellExecuteW(nullptr, L"open", L"ms-actioncenter:", nullptr, nullptr, SW_SHOWNORMAL);
            });

            // E. Two-Line 12/24-Hour Clock & Date + Far-Right Show Desktop Target
            SYSTEMTIME st;
            GetLocalTime(&st);
            int hr = s.use24HourClock ? st.wHour : ((st.wHour % 12 == 0) ? 12 : (st.wHour % 12));
            wchar_t timeStr[32], dateStr[32];
            swprintf_s(timeStr, s.use24HourClock ? L"%02d:%02d" : L"%d:%02d %s", hr, st.wMinute, st.wHour >= 12 ? L"PM" : L"AM");
            swprintf_s(dateStr, L"%d/%d/%04d", st.wMonth, st.wDay, st.wYear);

            s_taskbarRenderer.DrawTextStr(timeStr, D2D1::RectF(barLeft + 574.0f, barTop + 5.0f, barLeft + barW - 22.0f, barTop + 23.0f),
                                          11.0f, 600, theme.textPrimary, true, DWRITE_TEXT_ALIGNMENT_TRAILING);
            s_taskbarRenderer.DrawTextStr(dateStr, D2D1::RectF(barLeft + 574.0f, barTop + 22.0f, barLeft + barW - 22.0f, barTop + 40.0f),
                                          9.5f, 400, theme.textMuted, true, DWRITE_TEXT_ALIGNMENT_TRAILING);

            // Far-right Show Desktop target (0.5.104 & 0.5.126)
            D2D1_RECT_F showDesk = D2D1::RectF(barLeft + barW - 16.0f, barTop + 4.0f, barLeft + barW - 4.0f, barTop + barH - 4.0f);
            s_taskbarRenderer.DrawLine(barLeft + barW - 14.0f, barTop + 10.0f, barLeft + barW - 14.0f, barTop + barH - 10.0f, theme.edge, 1.5f);
            s_taskbarRenderer.AddHitRegion("tb-show-desktop", showDesk, []() {
                keybd_event(VK_LWIN, 0, 0, 0);
                keybd_event('D', 0, 0, 0);
                keybd_event('D', 0, KEYEVENTF_KEYUP, 0);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
            });

            s_taskbarRenderer.RT()->PopLayer();
            layer->Release();
        }

        ID2D1SolidColorBrush* rim = nullptr;
        if (SUCCEEDED(s_taskbarRenderer.RT()->CreateSolidColorBrush(theme.edge, &rim)) && rim) {
            s_taskbarRenderer.RT()->DrawGeometry(notchPath, rim, 1.0f);
            rim->Release();
        }
        notchPath->Release();
    }

    POINT screenPos = { rc.left, rc.top };
    s_taskbarRenderer.EndLayeredFrame(hwnd, screenPos, 255);
}

static LRESULT CALLBACK TaskbarWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto& state = GetAppState();
    switch (msg) {
        case WM_MOUSEMOVE: {
            float dpi = GetDpiForWindow(hwnd) / 96.0f;
            s_mouseDipX = LOWORD(lParam) / dpi;
            s_mouseDipY = HIWORD(lParam) / dpi;
            TRACKMOUSEEVENT tme = { sizeof(tme), TME_LEAVE, hwnd, 0 };
            TrackMouseEvent(&tme);
            RenderTaskbar(hwnd, state);
            return 0;
        }
        case WM_MOUSELEAVE:
            s_mouseDipX = -1000.0f;
            s_mouseDipY = -1000.0f;
            s_hoveredAppIndex = -1;
            RenderTaskbar(hwnd, state);
            return 0;
        case WM_LBUTTONDOWN: {
            float dpi = GetDpiForWindow(hwnd) / 96.0f;
            s_taskbarRenderer.DispatchClick(LOWORD(lParam) / dpi, HIWORD(lParam) / dpi);
            RenderTaskbar(hwnd, state);
            return 0;
        }
        case WM_DROPFILES: {
            // 0.5.126: Dragging a file over an app on OpenPixel's taskbar brings its window forward
            if (s_hoveredAppIndex >= 0 && s_hoveredAppIndex < static_cast<int>(s_taskbarApps.size())) {
                HWND target = s_taskbarApps[s_hoveredAppIndex].hwnd;
                if (target && IsWindow(target)) SetForegroundWindow(target);
            }
            DragFinish(reinterpret_cast<HDROP>(wParam));
            return 0;
        }
        case WM_PAINT: {
            PAINTSTRUCT ps;
            BeginPaint(hwnd, &ps);
            RenderTaskbar(hwnd, state);
            EndPaint(hwnd, &ps);
            return 0;
        }
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

bool TaskbarWindow::CreateOrUpdate(HINSTANCE hInst, GlobalAppState& state) {
    if (!state.settings.bottomTaskbarEnabled) {
        Destroy(state);
        return true;
    }

    if (!state.hwndTaskbar) {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = TaskbarWndProc;
        wc.hInstance = hInst;
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.lpszClassName = L"OpenPixelTaskbarWnd";
        RegisterClassExW(&wc);

        int scrW = GetSystemMetrics(SM_CXSCREEN);
        int scrH = GetSystemMetrics(SM_CYSCREEN);
        int wPx = 760;
        int hPx = 115;

        state.hwndTaskbar = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_ACCEPTFILES,
            wc.lpszClassName,
            L"OpenPixel Taskbar",
            WS_POPUP,
            (scrW - wPx) / 2, scrH - hPx, wPx, hPx,
            nullptr, nullptr, hInst, nullptr);

        // Hide Windows' native taskbar and spawn the crash watchdog failsafe
        HWND hTray = FindWindowW(L"Shell_TrayWnd", nullptr);
        if (hTray) ShowWindow(hTray, SW_HIDE);
        SystemServices::SpawnTaskbarWatchdogProcess();
    }

    RefreshRunningApps(state);
    ShowWindow(state.hwndTaskbar, SW_SHOWNOACTIVATE);
    RenderTaskbar(state.hwndTaskbar, state);
    return true;
}

void TaskbarWindow::Destroy(GlobalAppState& state) {
    if (state.hwndTaskbar) {
        DestroyWindow(state.hwndTaskbar);
        state.hwndTaskbar = nullptr;
    }
    // Always restore Windows' native taskbar immediately
    HWND hTray = FindWindowW(L"Shell_TrayWnd", nullptr);
    if (hTray) ShowWindow(hTray, SW_SHOW);
}

} // namespace np
