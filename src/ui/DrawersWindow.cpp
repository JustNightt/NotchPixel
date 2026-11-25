#include "DrawersWindow.h"
#include <shellapi.h>

namespace np {

static D2DRenderer s_drawerRenderer;

static void RenderDrawerWindow(HWND hwnd, GlobalAppState& state, int drawerKind) {
    RECT rc;
    GetWindowRect(hwnd, &rc);
    int wPx = rc.right - rc.left;
    int hPx = rc.bottom - rc.top;
    if (wPx <= 0 || hPx <= 0) return;

    if (!s_drawerRenderer.RT()) {
        s_drawerRenderer.Initialize(state.settings.drawWithProcessor);
    }

    float dpi = GetDpiForWindow(hwnd) / 96.0f;
    if (!s_drawerRenderer.BeginLayeredFrame(hwnd, wPx, hPx, dpi)) return;
    s_drawerRenderer.ClearHitRegions();

    float wDip = wPx / dpi;
    float hDip = hPx / dpi;
    const ThemeSpec& theme = GetThemeSpec(state.settings.theme);

    D2D1_RECT_F card = D2D1::RectF(2.0f, 2.0f, wDip - 2.0f, hDip - 2.0f);
    s_drawerRenderer.FillRoundedRect(card, 20.0f * state.settings.cornerRadiusScale,
                                     theme.isGlass ? Rgba(14, 16, 22, 0.92f) : theme.fill);
    s_drawerRenderer.DrawRoundedRect(card, 20.0f * state.settings.cornerRadiusScale, theme.edge, 1.0f);

    if (drawerKind == 0) {
        // 1. Synced Lyrics Drawer (LRCLIB)
        s_drawerRenderer.DrawTextStr(L"Synced Lyrics \x00B7 " + state.media.title + L" (" + state.media.artist + L")",
                                     D2D1::RectF(16.0f, 10.0f, wDip - 40.0f, 32.0f),
                                     12.0f, 700, theme.accent, false);
        float y = 38.0f;
        for (size_t i = 0; i < state.media.lyrics.size(); ++i) {
            bool active = (static_cast<int>(i) == state.media.activeLyricIndex);
            s_drawerRenderer.DrawTextStr(
                state.media.lyrics[i].text,
                D2D1::RectF(16.0f, y, wDip - 16.0f, y + 22.0f),
                active ? 14.0f : 12.0f,
                active ? 700 : 400,
                active ? theme.textPrimary : theme.textFaint,
                false, DWRITE_TEXT_ALIGNMENT_CENTER);
            y += 23.0f;
        }
    } else if (drawerKind == 1) {
        // 2. Movable Twitch & YouTube Stream Chat Drawer
        s_drawerRenderer.DrawTextStr(L"\x25CF LIVE CHAT \x00B7 #" + state.settings.twitchChannel + L" (Drag anywhere)",
                                     D2D1::RectF(16.0f, 10.0f, wDip - 40.0f, 32.0f),
                                     12.0f, 700, Rgba(255, 69, 58, 1.0f), false);
        float y = 38.0f;
        for (const auto& msg : state.chatMessages) {
            s_drawerRenderer.DrawTextStr(msg.username + L": ",
                                         D2D1::RectF(16.0f, y, 110.0f, y + 22.0f),
                                         12.0f, 700, msg.userColor, false);
            s_drawerRenderer.DrawTextStr(msg.message,
                                         D2D1::RectF(104.0f, y, wDip - 16.0f, y + 36.0f),
                                         12.0f, 400, theme.textPrimary, false,
                                         DWRITE_TEXT_ALIGNMENT_LEADING, DWRITE_PARAGRAPH_ALIGNMENT_NEAR, false);
            y += 38.0f;
        }
    } else if (drawerKind == 2) {
        // 3. Lazy-Loaded Web Drawer (Pinned sites over apps and fullscreen games without stealing game focus)
        s_drawerRenderer.DrawTextStr(L"Web Drawer \x00B7 " + state.currentWebDrawerTitle + L" (No-Focus Overlay)",
                                     D2D1::RectF(16.0f, 10.0f, wDip - 110.0f, 32.0f),
                                     12.5f, 700, theme.textPrimary, false);
        s_drawerRenderer.DrawTextStr(state.currentWebDrawerUrl,
                                     D2D1::RectF(16.0f, 32.0f, wDip - 110.0f, 50.0f),
                                     11.0f, 400, theme.accent, true);

        D2D1_RECT_F extBtn = D2D1::RectF(wDip - 108.0f, 12.0f, wDip - 16.0f, 38.0f);
        s_drawerRenderer.FillRoundedRect(extBtn, 10.0f, theme.accent);
        s_drawerRenderer.DrawTextStr(L"Open in Edge", extBtn, 11.0f, 600,
                                     AdaptiveInkForBackground(theme.accent), false, DWRITE_TEXT_ALIGNMENT_CENTER);
        s_drawerRenderer.AddHitRegion("web-open-ext", extBtn, [&state]() {
            ShellExecuteW(nullptr, L"open", state.currentWebDrawerUrl.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
        });

        D2D1_RECT_F viewport = D2D1::RectF(14.0f, 56.0f, wDip - 14.0f, hDip - 14.0f);
        s_drawerRenderer.FillRoundedRect(viewport, 12.0f, Rgba(255, 255, 255, 0.06f));
        s_drawerRenderer.DrawTextStr(
            L"Pinned Web Drawer Active (" + state.currentWebDrawerTitle + L")\n"
            L"Lazy-loaded on demand \x00B7 0 MB WebView footprint when closed\n"
            L"Opens over fullscreen games with WS_EX_NOACTIVATE so game focus is never stolen.",
            D2D1::RectF(28.0f, 74.0f, wDip - 28.0f, hDip - 28.0f),
            12.0f, 500, theme.textMuted, false,
            DWRITE_TEXT_ALIGNMENT_CENTER, DWRITE_PARAGRAPH_ALIGNMENT_CENTER, false);
    }

    POINT screenPos = { rc.left, rc.top };
    s_drawerRenderer.EndLayeredFrame(hwnd, screenPos, 248);
}

static LRESULT CALLBACK DrawerWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    auto& state = GetAppState();
    int kind = static_cast<int>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    switch (msg) {
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE; // Never steal focus from fullscreen games!
        case WM_NCHITTEST: {
            POINT pt = { LOWORD(lParam), HIWORD(lParam) };
            ScreenToClient(hwnd, &pt);
            if (pt.y < 36) return HTCAPTION; // Draggable top bar
            return HTCLIENT;
        }
        case WM_LBUTTONDOWN: {
            float dpi = GetDpiForWindow(hwnd) / 96.0f;
            s_drawerRenderer.DispatchClick(LOWORD(lParam) / dpi, HIWORD(lParam) / dpi);
            RenderDrawerWindow(hwnd, state, kind);
            return 0;
        }
        case WM_PAINT: {
            PAINTSTRUCT ps;
            BeginPaint(hwnd, &ps);
            RenderDrawerWindow(hwnd, state, kind);
            EndPaint(hwnd, &ps);
            return 0;
        }
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

void DrawersWindow::Initialize(HINSTANCE hInst, GlobalAppState& state) {
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DrawerWndProc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"OpenPixelDrawerWnd";
    RegisterClassExW(&wc);

    int scrW = GetSystemMetrics(SM_CXSCREEN);

    // 0: Synced Lyrics Drawer (docked under island)
    state.hwndLyricsDrawer = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        wc.lpszClassName, L"OpenPixel Lyrics Drawer", WS_POPUP,
        (scrW - 380) / 2, 168, 380, 190, nullptr, nullptr, hInst, nullptr);
    SetWindowLongPtrW(state.hwndLyricsDrawer, GWLP_USERDATA, 0);

    // 1: Movable Twitch & YouTube Stream Chat Drawer
    state.hwndChatDrawer = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        wc.lpszClassName, L"OpenPixel Stream Chat Drawer", WS_POPUP,
        scrW - 340, 90, 310, 210, nullptr, nullptr, hInst, nullptr);
    SetWindowLongPtrW(state.hwndChatDrawer, GWLP_USERDATA, 1);

    // 2: Lazy-Loaded Web Drawer (under island, dormant until opened)
    state.hwndWebDrawer = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        wc.lpszClassName, L"OpenPixel Web Drawer", WS_POPUP,
        (scrW - 420) / 2, 248, 420, 240, nullptr, nullptr, hInst, nullptr);
    SetWindowLongPtrW(state.hwndWebDrawer, GWLP_USERDATA, 2);
}

void DrawersWindow::RefreshVisibleDrawers(GlobalAppState& state) {
    if (state.hwndLyricsDrawer && state.lyricsDrawerVisible)
        RenderDrawerWindow(state.hwndLyricsDrawer, state, 0);
    if (state.hwndChatDrawer && state.chatDrawerVisible)
        RenderDrawerWindow(state.hwndChatDrawer, state, 1);
    if (state.hwndWebDrawer && state.webDrawerVisible)
        RenderDrawerWindow(state.hwndWebDrawer, state, 2);
}

void DrawersWindow::DestroyAll(GlobalAppState& state) {
    if (state.hwndLyricsDrawer) { DestroyWindow(state.hwndLyricsDrawer); state.hwndLyricsDrawer = nullptr; }
    if (state.hwndChatDrawer)   { DestroyWindow(state.hwndChatDrawer);   state.hwndChatDrawer = nullptr; }
    if (state.hwndWebDrawer)    { DestroyWindow(state.hwndWebDrawer);    state.hwndWebDrawer = nullptr; }
}

} // namespace np
