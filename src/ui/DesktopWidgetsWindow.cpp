#include "DesktopWidgetsWindow.h"
#include "../services/SystemServices.h"

namespace np {

static HINSTANCE s_hInst = nullptr;
static D2DRenderer s_widgetRenderer;

void SystemServices::SpawnOrToggleDesktopWidget(GlobalAppState& state, WidgetType type) {
    DesktopWidgetsWindow::ToggleWidgetOnDesktop(state, type);
}

static void RenderDesktopWidget(HWND hwnd, DesktopWidgetInstance& inst, GlobalAppState& state) {
    if (!hwnd || !IsWindow(hwnd)) return;
    RECT rc;
    GetWindowRect(hwnd, &rc);
    int wPx = rc.right - rc.left;
    int hPx = rc.bottom - rc.top;
    if (wPx <= 0 || hPx <= 0) return;

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    if (!s_widgetRenderer.RT()) {
        s_widgetRenderer.Initialize(state.settings.drawWithProcessor);
    }

    float dpi = GetDpiForWindow(hwnd) / 96.0f;
    if (!s_widgetRenderer.BeginLayeredFrame(hwnd, wPx, hPx, dpi)) return;

    float wDip = wPx / dpi;
    float hDip = hPx / dpi;
    D2D1_RECT_F box = D2D1::RectF(2.0f, 2.0f, wDip - 2.0f, hDip - 2.0f);

    // Sample live desktop wallpaper colors dynamically underneath Glass desktop widgets
    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    bool drewGlass = false;
    if (inst.styleMode == 0 && !state.settings.drawWithProcessor) {
        drewGlass = s_widgetRenderer.RenderLiquidGlassBackdrop(
            hwnd, rc, 2.0f, 2.0f, wDip - 4.0f, hDip - 4.0f, 18.0f, false);
    }
    if (!drewGlass) {
        D2D1_COLOR_F bgCol = (inst.styleMode == 2)
            ? Rgba(247, 247, 245, 0.94f)
            : Rgba(16, 18, 24, 0.88f);
        s_widgetRenderer.FillRoundedRect(box, 18.0f, bgCol);
    }
    s_widgetRenderer.DrawRoundedRect(box, 18.0f,
                                     (inst.styleMode == 2) ? Rgba(12, 12, 14, 0.16f) : Rgba(255, 255, 255, 0.22f),
                                     1.0f);

    D2D1_COLOR_F ink   = (inst.styleMode == 2) ? Rgba(12, 12, 14, 1.0f) : Rgba(239, 237, 232, 1.0f);
    D2D1_COLOR_F muted = (inst.styleMode == 2) ? Rgba(82, 82, 88, 1.0f) : Rgba(195, 192, 186, 1.0f);

    SYSTEMTIME st;
    GetLocalTime(&st);

    switch (inst.type) {
        case WidgetType::Clock:
        case WidgetType::WorldClock: {
            int hr = state.settings.use24HourClock ? st.wHour : ((st.wHour % 12 == 0) ? 12 : (st.wHour % 12));
            wchar_t buf[64];
            swprintf_s(buf, state.settings.use24HourClock ? L"%02d:%02d" : L"%d:%02d %s",
                       hr, st.wMinute, st.wHour >= 12 ? L"PM" : L"AM");
            s_widgetRenderer.DrawTextStr(buf, D2D1::RectF(16.0f, 14.0f, wDip - 16.0f, 54.0f), 26.0f, 700, ink, true);
            wchar_t sub[64];
            int wHr = (st.wHour + state.settings.worldClockOffsetHours + 24) % 24;
            swprintf_s(sub, L"%s %02d:%02d%s",
                       state.settings.worldClockCity.c_str(), wHr, st.wMinute,
                       state.settings.lockDesktopWidgets ? L" \x00B7 Locked" : L"");
            s_widgetRenderer.DrawTextStr(sub, D2D1::RectF(16.0f, 58.0f, wDip - 16.0f, hDip - 12.0f), 12.0f, 500, muted, false);
            break;
        }
        case WidgetType::AnalogClock: {
            float cx = wDip * 0.5f, cy = hDip * 0.5f;
            float rad = std::min(wDip, hDip) * 0.38f;
            s_widgetRenderer.DrawCircle(cx, cy, rad, ink, 2.0f);
            float hrAngle = ((st.wHour % 12) + st.wMinute / 60.0f) * (3.14159265f / 6.0f) - 1.570796f;
            float mnAngle = st.wMinute * (3.14159265f / 30.0f) - 1.570796f;
            s_widgetRenderer.DrawLine(cx, cy, cx + std::cos(hrAngle) * rad * 0.55f, cy + std::sin(hrAngle) * rad * 0.55f, ink, 2.8f);
            s_widgetRenderer.DrawLine(cx, cy, cx + std::cos(mnAngle) * rad * 0.80f, cy + std::sin(mnAngle) * rad * 0.80f, ink, 1.8f);
            break;
        }
        case WidgetType::MinimalWeather:
        case WidgetType::PlainWeather: {
            bool isNight = (st.wHour < 6 || st.wHour >= 19);
            wchar_t w1[64], w2[64];
            swprintf_s(w1, L"%s %d\x00B0C", isNight ? L"\x263D" : L"\x2600", state.settings.weatherTempC);
            swprintf_s(w2, L"%s \x00B7 H %d\x00B0 L %d\x00B0",
                       state.settings.weatherCity.c_str(), state.settings.weatherHighC, state.settings.weatherLowC);
            s_widgetRenderer.DrawTextStr(w1, D2D1::RectF(16.0f, 14.0f, wDip - 16.0f, 54.0f), 24.0f, 700, ink, false);
            s_widgetRenderer.DrawTextStr(w2, D2D1::RectF(16.0f, 58.0f, wDip - 16.0f, hDip - 12.0f), 12.0f, 500, muted, false);
            break;
        }
        case WidgetType::TodoList: {
            s_widgetRenderer.DrawTextStr(L"To-Do List", D2D1::RectF(16.0f, 10.0f, wDip - 16.0f, 32.0f), 14.0f, 700, ink, false);
            float y = 36.0f;
            if (state.todoList.empty()) {
                s_widgetRenderer.DrawTextStr(L"No tasks yet", D2D1::RectF(16.0f, y, wDip - 16.0f, y + 22.0f), 12.0f, 400, muted, false);
            } else {
                for (const auto& td : state.todoList) {
                    if (y > hDip - 20.0f) break;
                    s_widgetRenderer.DrawTextStr((td.done ? L"\x2611 " : L"\x2610 ") + td.text,
                                                 D2D1::RectF(16.0f, y, wDip - 16.0f, y + 20.0f), 12.0f, 500, td.done ? muted : ink, false);
                    y += 22.0f;
                }
            }
            break;
        }
        default: {
            wchar_t s1[64], s2[64];
            swprintf_s(s1, L"CPU %d%% \x00B7 GPU %d%%",
                       static_cast<int>(state.telemetry.cpuPercent),
                       static_cast<int>(state.telemetry.gpuPercent));
            swprintf_s(s2, L"RAM %d%% \x00B7 Disk %d%%",
                       static_cast<int>(state.telemetry.ramPercent),
                       static_cast<int>(state.telemetry.storagePercent));
            s_widgetRenderer.DrawTextStr(s1, D2D1::RectF(16.0f, 18.0f, wDip - 16.0f, 50.0f), 15.5f, 700, ink, true);
            s_widgetRenderer.DrawTextStr(s2, D2D1::RectF(16.0f, 54.0f, wDip - 16.0f, hDip - 12.0f), 12.0f, 500, muted, true);
            break;
        }
    }

    POINT screenPos = { rc.left, rc.top };
    s_widgetRenderer.EndLayeredFrame(hwnd, screenPos, 246);
}

static DesktopWidgetInstance* FindWidgetByHwnd(GlobalAppState& state, HWND hwnd) {
    for (auto& w : state.desktopWidgets) {
        if (w.hwnd == hwnd) return &w;
    }
    return nullptr;
}

// ============================================================================
// DesktopWidgetWndProc:
// - Smooth Per-Frame Pointer-Delta Dragging (SetCapture + exact click offset)
//   eliminating the HTCAPTION "instant stuck/teleport" jump on click!
// - Stays layered at HWND_BOTTOM below normal windows & survives Show Desktop
// - Right-click opens native context menu ("Lock widgets", Resize, Style, Unpin)
// ============================================================================
static LRESULT CALLBACK DesktopWidgetWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto& state = GetAppState();

    switch (msg) {
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE;

        case WM_WINDOWPOSCHANGING: {
            // Keep widget anchored at HWND_BOTTOM so it stays underneath regular windows & game launchers
            auto* wp = reinterpret_cast<WINDOWPOS*>(lParam);
            if (wp && !(wp->flags & SWP_NOZORDER)) {
                wp->hwndInsertAfter = HWND_BOTTOM;
            }
            return 0;
        }

        case WM_DISPLAYCHANGE: {
            // Reposition safely into visible monitor area on resolution/monitor changes (0.5.110)
            RECT rc;
            GetWindowRect(hwnd, &rc);
            int scrW = GetSystemMetrics(SM_CXSCREEN);
            int scrH = GetSystemMetrics(SM_CYSCREEN);
            int w = rc.right - rc.left;
            int h = rc.bottom - rc.top;
            int nx = std::clamp(static_cast<int>(rc.left), 12, std::max(12, scrW - w - 12));
            int ny = std::clamp(static_cast<int>(rc.top),  12, std::max(12, scrH - h - 12));
            SetWindowPos(hwnd, HWND_BOTTOM, nx, ny, w, h, SWP_NOACTIVATE);
            return 0;
        }

        case WM_LBUTTONDOWN: {
            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            if (state.settings.lockDesktopWidgets) return 0;

            DesktopWidgetInstance* inst = FindWidgetByHwnd(state, hwnd);
            if (inst) {
                POINT cursorScreen = {};
                GetCursorPos(&cursorScreen);
                RECT wndRect = {};
                GetWindowRect(hwnd, &wndRect);

                // Record exact pointer offset from top-left corner -> zero initial jump!
                inst->isDragging  = true;
                inst->dragOffsetX = cursorScreen.x - wndRect.left;
                inst->dragOffsetY = cursorScreen.y - wndRect.top;
                SetCapture(hwnd);
            }
            return 0;
        }

        case WM_MOUSEMOVE: {
            DesktopWidgetInstance* inst = nullptr;
            {
                std::lock_guard<std::recursive_mutex> lock(state.mutex);
                inst = FindWidgetByHwnd(state, hwnd);
                if (!inst || !inst->isDragging || state.settings.lockDesktopWidgets) return 0;
            }

            if (!(wParam & MK_LBUTTON)) {
                inst->isDragging = false;
                ReleaseCapture();
                return 0;
            }

            POINT cursorScreen = {};
            GetCursorPos(&cursorScreen);
            int newX = cursorScreen.x - inst->dragOffsetX;
            int newY = cursorScreen.y - inst->dragOffsetY;

            inst->x = newX;
            inst->y = newY;
            SetWindowPos(hwnd, HWND_BOTTOM, newX, newY, 0, 0,
                         SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            RenderDesktopWidget(hwnd, *inst, state);
            return 0;
        }

        case WM_LBUTTONUP: {
            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            DesktopWidgetInstance* inst = FindWidgetByHwnd(state, hwnd);
            if (inst && inst->isDragging) {
                inst->isDragging = false;
                ReleaseCapture();
                ConfigManager::SaveAtomic(state);
            }
            return 0;
        }

        case WM_MOUSEWHEEL: {
            // 0.5.124: Scroll wheel over desktop widget resizes it when unlocked
            if (!state.settings.lockDesktopWidgets) {
                int delta = GET_WHEEL_DELTA_WPARAM(wParam) > 0 ? 14 : -14;
                RECT rc;
                GetWindowRect(hwnd, &rc);
                int nw = std::clamp(static_cast<int>(rc.right - rc.left + delta), 150, 380);
                int nh = std::clamp(static_cast<int>(rc.bottom - rc.top + static_cast<int>(delta * 0.6f)), 90, 260);
                SetWindowPos(hwnd, HWND_BOTTOM, rc.left, rc.top, nw, nh, SWP_NOACTIVATE);
                DesktopWidgetInstance* inst = FindWidgetByHwnd(state, hwnd);
                if (inst) {
                    inst->width = nw;
                    inst->height = nh;
                    RenderDesktopWidget(hwnd, *inst, state);
                }
            }
            return 0;
        }

        case WM_RBUTTONUP: {
            // Native Right-Click Context Menu: Toggle "Lock widgets", Resize, Recolor, or Unpin
            HMENU hMenu = CreatePopupMenu();
            if (!hMenu) return 0;

            UINT lockFlags = MF_STRING | (state.settings.lockDesktopWidgets ? MF_CHECKED : MF_UNCHECKED);
            AppendMenuW(hMenu, lockFlags, 101, L"Lock widgets in place");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, nullptr);
            AppendMenuW(hMenu, MF_STRING, 102, L"Style: Liquid Glass (Live Wallpaper)");
            AppendMenuW(hMenu, MF_STRING, 103, L"Style: Dark Surface");
            AppendMenuW(hMenu, MF_STRING, 104, L"Style: Pure White");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, nullptr);
            AppendMenuW(hMenu, MF_STRING, 105, L"Size: Compact");
            AppendMenuW(hMenu, MF_STRING, 106, L"Size: Large");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, nullptr);
            AppendMenuW(hMenu, MF_STRING, 107, L"Unpin from desktop");

            POINT pt = {};
            GetCursorPos(&pt);
            SetForegroundWindow(hwnd);
            int cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.x, pt.y, 0, hwnd, nullptr);
            DestroyMenu(hMenu);

            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            DesktopWidgetInstance* inst = FindWidgetByHwnd(state, hwnd);
            if (!inst) return 0;

            if (cmd == 101) {
                state.settings.lockDesktopWidgets = !state.settings.lockDesktopWidgets;
                ConfigManager::SaveAtomic(state);
                DesktopWidgetsWindow::RefreshAll(state);
            } else if (cmd == 102 || cmd == 103 || cmd == 104) {
                inst->styleMode = (cmd - 102);
                RenderDesktopWidget(hwnd, *inst, state);
            } else if (cmd == 105 || cmd == 106) {
                inst->width  = (cmd == 105) ? 185 : 265;
                inst->height = (cmd == 105) ? 98  : 145;
                SetWindowPos(hwnd, HWND_BOTTOM, inst->x, inst->y, inst->width, inst->height, SWP_NOACTIVATE);
                RenderDesktopWidget(hwnd, *inst, state);
            } else if (cmd == 107) {
                WidgetType t = inst->type;
                DesktopWidgetsWindow::ToggleWidgetOnDesktop(state, t);
            }
            return 0;
        }

        case WM_PAINT: {
            PAINTSTRUCT ps;
            BeginPaint(hwnd, &ps);
            DesktopWidgetInstance* inst = FindWidgetByHwnd(state, hwnd);
            if (inst) RenderDesktopWidget(hwnd, *inst, state);
            EndPaint(hwnd, &ps);
            return 0;
        }
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

void DesktopWidgetsWindow::Initialize(HINSTANCE hInst) {
    s_hInst = hInst;
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DesktopWidgetWndProc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursor(nullptr, IDC_SIZEALL);
    wc.lpszClassName = L"OpenPixelDesktopWidgetWnd";
    RegisterClassExW(&wc);
}

void DesktopWidgetsWindow::ToggleWidgetOnDesktop(GlobalAppState& state, WidgetType type) {
    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    for (auto it = state.desktopWidgets.begin(); it != state.desktopWidgets.end(); ++it) {
        if (it->type == type && it->hwnd) {
            DestroyWindow(it->hwnd);
            state.desktopWidgets.erase(it);
            return;
        }
    }

    DesktopWidgetInstance inst;
    inst.id = GetTickMs();
    inst.type = type;
    inst.width = 210;
    inst.height = (type == WidgetType::TodoList) ? 140 : 104;
    inst.x = 48 + static_cast<int>(state.desktopWidgets.size()) * 224;
    inst.y = 72;

    inst.hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        L"OpenPixelDesktopWidgetWnd",
        L"OpenPixel Desktop Widget",
        WS_POPUP,
        inst.x, inst.y, inst.width, inst.height,
        nullptr, nullptr, s_hInst, nullptr);

    SetWindowPos(inst.hwnd, HWND_BOTTOM, inst.x, inst.y, inst.width, inst.height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    state.desktopWidgets.push_back(inst);
    RenderDesktopWidget(inst.hwnd, state.desktopWidgets.back(), state);
}

void DesktopWidgetsWindow::RefreshAll(GlobalAppState& state) {
    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    for (auto& w : state.desktopWidgets) {
        if (w.hwnd && IsWindowVisible(w.hwnd)) {
            RenderDesktopWidget(w.hwnd, w, state);
        }
    }
}

void DesktopWidgetsWindow::DestroyAll(GlobalAppState& state) {
    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    for (auto& w : state.desktopWidgets) {
        if (w.hwnd) DestroyWindow(w.hwnd);
    }
    state.desktopWidgets.clear();
}

} // namespace np
