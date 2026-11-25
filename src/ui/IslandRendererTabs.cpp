#include "IslandRendererTabs.h"
#include "../services/SystemServices.h"
#include <iomanip>
#include <sstream>
#include <shellapi.h>

namespace np {

static std::wstring FormatTimeSec(double sec) {
    int total = std::max(0, static_cast<int>(sec));
    int m = total / 60;
    int s = total % 60;
    wchar_t buf[32];
    swprintf_s(buf, L"%d:%02d", m, s);
    return buf;
}

void IslandUI::ComputeCurrentIslandGeometry(
    const GlobalAppState& state,
    float canvasW, float& outLeft, float& outTop, float& outW, float& outH, float& outRadius)
{
    const auto& s = state.settings;

    // Fullscreen Game Strip Mode (razor-slim 22 DIP top overlay at 10 Hz)
    if (state.isFullscreenGameActive && !state.islandOpen && !state.toastActive && s.gameStripMode != 2) {
        outW = 386.0f * (s.pillSizePercent / 100.0f);
        outH = 22.0f * (s.pillSizePercent / 100.0f);
        outTop = 0.0f;
        outLeft = (canvasW - outW) * 0.5f;
        outRadius = 11.0f * s.cornerRadiusScale;
        return;
    }

    float rawOpen = std::clamp(state.openSpring.value / 100.0f, 0.0f, 1.25f);
    // Combine spring physics with smooth cubic ease-out for silky whole-pixel interpolation
    float openT = (rawOpen <= 1.0f) ? (0.65f * rawOpen + 0.35f * CubicEaseOut(rawOpen)) : rawOpen;
    float notchT = std::clamp(state.notchSpring.value / 100.0f, 0.0f, 1.0f);
    float recT = std::clamp(state.recSpring.value / 100.0f, 0.0f, 1.0f);

    float pillBaseW = PILL_W + (REC_PILL_W - PILL_W) * recT;
    if (state.toastActive && !state.islandOpen) {
        pillBaseW = (state.toastPopupType == TopPopupType::Volume || state.toastPopupType == TopPopupType::Brightness)
            ? 248.0f
            : 296.0f;
    } else if (s.showSongTitleInClosedPill && state.media.hasSession && !state.islandOpen) {
        pillBaseW = 258.0f;
    }

    float pillW = pillBaseW * (s.pillSizePercent / 100.0f);
    float pillH = PILL_H * (s.pillSizePercent / 100.0f);
    if (state.toastActive && state.toastIsReminder && !state.islandOpen) {
        pillH = 44.0f * (s.pillSizePercent / 100.0f);
    }

    float targetCardH = GetPanelExpandedHeight(state.activePanel);
    float cardW = CARD_W * (s.openSizePercent / 100.0f);
    float cardH = targetCardH * (s.openSizePercent / 100.0f);

    outW = pillW + (cardW - pillW) * openT;
    outH = pillH + (cardH - pillH) * openT;

    float islandGap = TOP_OFFSET_ISLAND + s.topEdgeDistanceDip;
    outTop = (notchT <= 0.5f) ? (islandGap * (1.0f - notchT * 2.0f)) : 0.0f;

    if (s.edge == EdgePlacement::LeftEdge) {
        outLeft = outTop;
    } else if (s.edge == EdgePlacement::RightEdge) {
        outLeft = canvasW - outW - outTop;
    } else {
        outLeft = (canvasW - outW) * 0.5f;
    }

    float baseRadius = 18.0f + (36.0f - 18.0f) * std::clamp(openT, 0.0f, 1.0f);
    outRadius = std::max(4.0f, baseRadius * std::clamp(s.cornerRadiusScale, 0.15f, 1.0f));
}

// ============================================================================
// Render All 6 Music Scrub/Seek Progress Bar Styles
// 1. Wave, 2. Pixels, 3. Comet, 4. Song shape, 5. Simple, 6. Hearts
// ============================================================================
static void RenderMusicProgressBar(
    D2DRenderer& r, GlobalAppState& state,
    float barLeft, float barY, float barWidth, float progress01,
    const ThemeSpec& theme, float timeSec)
{
    progress01 = std::clamp(progress01, 0.0f, 1.0f);
    float headX = barLeft + barWidth * progress01;
    D2D1_RECT_F hitBox = D2D1::RectF(barLeft - 4.0f, barY - 10.0f, barLeft + barWidth + 4.0f, barY + 10.0f);
    bool hovered = r.IsHovered(hitBox, state.pointerDipX, state.pointerDipY);

    r.AddHitRegion("music-scrub-bar", hitBox, nullptr, [&state](float normX) {
        SystemServices::SeekMediaPosition(state, normX * std::max(1.0, state.media.durationSec));
    });

    D2D1_COLOR_F accent = (state.media.hasSession &&
                          (state.settings.theme == SurfaceTheme::Black || state.settings.theme == SurfaceTheme::Graphite))
        ? state.media.dominantColor
        : theme.accent;
    D2D1_COLOR_F unplayedCol = theme.isLight ? Rgba(12, 12, 14, 0.18f) : Rgba(239, 237, 232, 0.20f);

    ProgressBarStyle style = state.settings.progressBar;

    if (style == ProgressBarStyle::Wave) {
        r.DrawLine(headX, barY, barLeft + barWidth, barY, unplayedCol, 2.5f);
        int segs = std::max(2, static_cast<int>((headX - barLeft) / 3.0f));
        float prevX = barLeft;
        float prevY = barY;
        float amp = (state.media.hasSession && state.media.isPlaying) ? (hovered ? 3.2f : 2.3f) : 0.4f;
        for (int i = 1; i <= segs; ++i) {
            float t = static_cast<float>(i) / segs;
            float cx = barLeft + (headX - barLeft) * t;
            float cy = barY + std::sin(cx * 0.24f - timeSec * 5.2f) * amp;
            r.DrawLine(prevX, prevY, cx, cy, accent, 2.4f);
            prevX = cx;
            prevY = cy;
        }
        float tw = hovered ? 4.4f : 3.4f;
        float th = hovered ? 14.0f : 11.0f;
        r.FillRoundedRect(D2D1::RectF(headX - tw * 0.5f, barY - th * 0.5f, headX + tw * 0.5f, barY + th * 0.5f), tw * 0.5f, theme.textPrimary);
    } else if (style == ProgressBarStyle::Pixels) {
        for (float x = barLeft; x < barLeft + barWidth - 2.0f; x += 4.5f) {
            bool played = (x <= headX);
            int stack = 1;
            if (hovered && std::fabs(x - state.pointerDipX) < 22.0f) {
                stack = (std::fabs(x - state.pointerDipX) < 9.0f) ? 3 : 2;
            }
            D2D1_COLOR_F col = played ? accent : unplayedCol;
            for (int s = 0; s < stack; ++s) {
                float py = barY - 1.5f - s * 4.0f;
                r.FillRoundedRect(D2D1::RectF(x, py, x + 3.0f, py + 3.0f), 0.4f, col);
            }
        }
        r.FillRoundedRect(D2D1::RectF(headX - 2.0f, barY - 2.0f, headX + 2.0f, barY + 2.0f), 0.6f, Rgba(255, 255, 255, 1.0f));
    } else if (style == ProgressBarStyle::Comet) {
        r.FillRoundedRect(D2D1::RectF(barLeft, barY - 1.5f, barLeft + barWidth, barY + 1.5f), 1.5f, unplayedCol);
        r.FillRoundedRect(D2D1::RectF(barLeft, barY - 2.0f, headX, barY + 2.0f), 2.0f, accent);
        for (int i = 0; i < 8; ++i) {
            float pPhase = std::fmod(timeSec * 2.4f + i * 0.37f, 1.0f);
            float sx = headX - pPhase * 28.0f;
            if (sx >= barLeft) {
                float sy = barY + std::sin(i * 2.5f + timeSec * 8.0f) * 4.5f * pPhase;
                r.FillCircle(sx, sy, (1.0f - pPhase) * 2.2f, Rgba(255, 225, 168, 1.0f - pPhase));
            }
        }
        float pulse = 3.8f + std::sin(timeSec * 6.0f) * 0.9f;
        r.FillCircle(headX, barY, pulse * 1.6f, D2D1::ColorF(accent.r, accent.g, accent.b, 0.32f));
        r.FillCircle(headX, barY, pulse, Rgba(255, 255, 255, 1.0f));
    } else if (style == ProgressBarStyle::SongShape) {
        int numBars = 44;
        float step = barWidth / static_cast<float>(numBars);
        uint32_t seed = 2166136261u;
        for (wchar_t ch : state.media.title) seed = (seed ^ static_cast<uint32_t>(ch)) * 16777619u;

        for (int i = 0; i < numBars; ++i) {
            float bx = barLeft + i * step;
            float env = 0.25f + 0.75f * std::fabs(std::sin((i * 0.43f) + (seed & 0xFF) * 0.05f));
            if (state.media.isPlaying && std::fabs(bx - headX) < 16.0f) {
                env = std::min(1.0f, env + 0.25f * std::sin(timeSec * 10.0f + i));
            }
            float bh = 3.0f + env * 10.0f;
            D2D1_COLOR_F col = (bx <= headX) ? accent : unplayedCol;
            r.FillRoundedRect(D2D1::RectF(bx, barY - bh * 0.5f, bx + 2.4f, barY + bh * 0.5f), 1.2f, col);
        }
    } else if (style == ProgressBarStyle::Simple) {
        float h = hovered ? 7.5f : 4.0f;
        r.FillRoundedRect(D2D1::RectF(barLeft, barY - h * 0.5f, barLeft + barWidth, barY + h * 0.5f), h * 0.5f, unplayedCol);
        r.FillRoundedRect(D2D1::RectF(barLeft, barY - h * 0.5f, std::max(barLeft + h, headX), barY + h * 0.5f), h * 0.5f, accent);
    } else if (style == ProgressBarStyle::Hearts) {
        int numHearts = 18;
        float step = barWidth / static_cast<float>(numHearts);
        for (int i = 0; i < numHearts; ++i) {
            float hx = barLeft + (i + 0.5f) * step;
            bool played = (hx <= headX);
            bool isCurrent = (std::fabs(hx - headX) < step * 0.65f);
            float waveY = played && state.media.isPlaying ? std::sin(timeSec * 5.0f - i * 0.5f) * 1.4f : 0.0f;
            if (isCurrent) {
                r.DrawLucideIcon("heart-filled", hx, barY + waveY, 10.5f, Rgba(255, 255, 255, 1.0f));
            } else if (played) {
                r.DrawLucideIcon("heart-filled", hx, barY + waveY, 8.5f, accent);
            } else {
                r.DrawLucideIcon("heart", hx, barY, 7.5f, unplayedCol);
            }
        }
    }
}

// ============================================================================
// Render Resting Pill / Notch / Game Strip / Top-Center Pop-Ups (Volume, Brightness,
// Mic Mute, Lock Key, Color Picker, Free Buff Nudge, Reminder)
// ============================================================================
static void RenderRestingFace(
    D2DRenderer& r, GlobalAppState& state,
    float left, float top, float width, float height,
    const ThemeSpec& theme, float timeSec, float restingAlpha)
{
    if (restingAlpha <= 0.02f) return;
    const auto& s = state.settings;
    float cy = top + height * 0.5f;

    // 1. Fullscreen 10 Hz Game Strip Mode (Avoids fake CPU temps on desktops!)
    if (state.isFullscreenGameActive && !state.islandOpen && !state.toastActive && s.gameStripMode != 2) {
        std::wostringstream ss;
        if (s.gameStripShowFps) ss << L"FPS " << static_cast<int>(state.telemetry.fps) << L"   ";
        if (s.gameStripShowCpu) {
            ss << L"CPU " << static_cast<int>(state.telemetry.cpuPercent) << L"%";
            if (s.gameStripShowTemps && state.telemetry.cpuTempAvailable)
                ss << L" " << static_cast<int>(state.telemetry.cpuTempC) << L"\x00B0";
            ss << L"   ";
        }
        if (s.gameStripShowGpu) {
            ss << L"GPU " << static_cast<int>(state.telemetry.gpuPercent) << L"%";
            if (s.gameStripShowTemps && state.telemetry.gpuTempAvailable)
                ss << L" " << static_cast<int>(state.telemetry.gpuTempC) << L"\x00B0";
            ss << L"   ";
        }
        if (s.gameStripShowRam) ss << L"RAM " << static_cast<int>(state.telemetry.ramPercent) << L"%   ";
        if (s.gameStripShowNet) {
            if (s.gameStripNetInMbps) {
                ss << L"\x2193 " << std::fixed << std::setprecision(1) << (state.telemetry.netDownKbps / 1000.0f) << L" Mbps   ";
            } else {
                ss << L"\x2193 " << static_cast<int>(state.telemetry.netDownKbps) << L" Kbps   ";
            }
        }
        SYSTEMTIME st;
        GetLocalTime(&st);
        wchar_t clk[16];
        swprintf_s(clk, L"%02d:%02d", st.wHour, st.wMinute);
        ss << clk;

        r.DrawTextStr(ss.str(), D2D1::RectF(left + 8.0f, top, left + width - 8.0f, top + height),
                      10.5f, 600, Rgba(239, 237, 232, restingAlpha), true,
                      DWRITE_TEXT_ALIGNMENT_CENTER, DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        return;
    }

    // 2. Active Top-Center Pop-Ups (Volume, Brightness, Mic Mute, Color Picker, Free Buff Alert, Reminder)
    if (state.toastActive) {
        if (GetTickMs() > state.toastExpireMs && !state.toastIsReminder) {
            state.toastActive = false;
            state.toastPopupType = TopPopupType::None;
        } else {
            // A. Volume & Brightness Top-Center Pop-Up (matches /press volume-battery.jpg:
            //    left white icon + center white horizontal pill progress bar + right readable percentage)
            if (state.toastPopupType == TopPopupType::Volume || state.toastPopupType == TopPopupType::Brightness) {
                r.DrawLucideIcon(state.toastPopupType == TopPopupType::Volume ? "volume" : "sun",
                                 left + 20.0f, cy, 14.0f, theme.textPrimary);
                float trackL = left + 36.0f;
                float trackR = left + width - 48.0f;
                float trackW = std::max(20.0f, trackR - trackL);
                r.FillRoundedRect(D2D1::RectF(trackL, cy - 3.0f, trackR, cy + 3.0f), 3.0f, Rgba(255, 255, 255, 0.18f));
                float fillW = trackW * std::clamp(state.toastProgress01, 0.0f, 1.0f);
                if (fillW > 2.0f) {
                    r.FillRoundedRect(D2D1::RectF(trackL, cy - 3.0f, trackL + fillW, cy + 3.0f), 3.0f, theme.textPrimary);
                }
                r.DrawTextStr(state.toastTitle,
                              D2D1::RectF(trackR + 4.0f, top, left + width - 10.0f, top + height),
                              11.0f, 600, theme.textPrimary, true, DWRITE_TEXT_ALIGNMENT_TRAILING);
                return;
            }

            // B. Global Mic Mute Pop-Up (matches /press mic-mute.jpg: red mic icon + elapsed timer on left, red "muted" on right)
            if (state.toastPopupType == TopPopupType::MicMute) {
                D2D1_COLOR_F col = state.globalMicMuted ? Rgba(255, 69, 58, 1.0f) : Rgba(48, 209, 88, 1.0f);
                r.DrawLucideIcon("mic", left + 18.0f, cy, 14.0f, col);
                std::wstring timerStr = L"0:00";
                if (state.sensors.micCallStartMs > 0) {
                    timerStr = FormatTimeSec((GetTickMs() - state.sensors.micCallStartMs) / 1000.0);
                }
                r.DrawTextStr(timerStr, D2D1::RectF(left + 32.0f, top, left + 110.0f, top + height),
                              11.5f, 600, theme.textPrimary, true);
                r.DrawTextStr(state.toastTitle, D2D1::RectF(left + 110.0f, top, left + width - 14.0f, top + height),
                              12.0f, 700, col, false, DWRITE_TEXT_ALIGNMENT_TRAILING);
                return;
            }

            // C. Due Reminder Alert (Snooze 10m / Done over apps and fullscreen games)
            if (state.toastIsReminder) {
                r.FillCircle(left + 16.0f, cy, 4.5f, state.toastAccent);
                r.DrawTextStr(L"\x23F0 " + state.toastTitle,
                              D2D1::RectF(left + 26.0f, top + 4.0f, left + width - 118.0f, top + height - 4.0f),
                              12.0f, 600, theme.textPrimary, false);
                D2D1_RECT_F snoozeBtn = D2D1::RectF(left + width - 114.0f, top + 8.0f, left + width - 58.0f, top + height - 8.0f);
                D2D1_RECT_F doneBtn   = D2D1::RectF(left + width - 54.0f,  top + 8.0f, left + width - 10.0f, top + height - 8.0f);
                r.FillRoundedRect(snoozeBtn, 10.0f, Rgba(255, 255, 255, 0.14f));
                r.DrawTextStr(L"Snooze", snoozeBtn, 10.5f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                r.FillRoundedRect(doneBtn, 10.0f, Rgba(48, 209, 88, 0.92f));
                r.DrawTextStr(L"Done", doneBtn, 10.5f, 600, Rgba(12, 12, 14, 1.0f), false, DWRITE_TEXT_ALIGNMENT_CENTER);

                r.AddHitRegion("reminder-snooze", snoozeBtn, [&state]() {
                    for (auto& rem : state.reminders) {
                        if (rem.id == state.activeReminderId) {
                            rem.dueTimestampMs = GetTickMs() + 10 * 60 * 1000;
                            rem.triggered = false;
                        }
                    }
                    state.toastActive = false;
                    state.toastIsReminder = false;
                });
                r.AddHitRegion("reminder-done", doneBtn, [&state]() {
                    for (auto& rem : state.reminders) {
                        if (rem.id == state.activeReminderId) rem.completed = true;
                    }
                    state.toastActive = false;
                    state.toastIsReminder = false;
                });
                return;
            }

            // D. Lock Key / Color Picker Swatch / Free Buff Coding Agent Nudge / General Notice
            r.FillCircle(left + 16.0f, cy, 5.0f, state.toastAccent);
            std::wstring full = state.toastTitle;
            if (!state.toastSubtitle.empty()) full += L" \x00B7 " + state.toastSubtitle;
            r.DrawTextStr(full, D2D1::RectF(left + 28.0f, top + 2.0f, left + width - 14.0f, top + height - 2.0f),
                          11.5f, 600, theme.textPrimary, false);
            return;
        }
    }

    // 3. Active Screen Recording Pill (250x36 DIP `pill-rec`) or OBS Studio REC Pill
    if (state.isRecordingScreen || (s.showObsRecPill && state.obsRecordingActive)) {
        uint64_t startMs = state.isRecordingScreen ? state.recordingStartMs : state.obsRecordingStartMs;
        double elapsed = (GetTickMs() - startMs) / 1000.0;
        float pulse = 0.55f + 0.45f * std::sin(timeSec * 6.0f);
        r.FillCircle(left + 18.0f, cy, 5.0f, Rgba(255, 59, 48, pulse));
        std::wstring label = state.isRecordingScreen ? L"REC " : L"OBS REC ";
        r.DrawTextStr(label + FormatTimeSec(elapsed),
                      D2D1::RectF(left + 28.0f, top, left + width - 64.0f, top + height),
                      12.0f, 600, theme.textPrimary, true);
        D2D1_RECT_F stopBtn = D2D1::RectF(left + width - 58.0f, top + 6.0f, left + width - 10.0f, top + height - 6.0f);
        r.FillRoundedRect(stopBtn, 11.0f, Rgba(255, 59, 48, 0.90f));
        r.DrawTextStr(L"Stop", stopBtn, 11.0f, 600, Rgba(255, 255, 255, 1.0f), false, DWRITE_TEXT_ALIGNMENT_CENTER);
        r.AddHitRegion("rec-stop-pill", stopBtn, [&state]() {
            if (state.isRecordingScreen) SystemServices::StopScreenRecording(state);
            state.obsRecordingActive = false;
        });
        return;
    }

    // 4. Resting Pill Content: Real Media (ONLY if state.media.hasSession == true!) or Configurable Resting Info Mode
    if (state.media.hasSession && (s.restingMode == RestingInfoMode::MediaOnly || state.media.isPlaying)) {
        D2D1_RECT_F cov = D2D1::RectF(left + 12.0f, cy - 12.0f, left + 36.0f, cy + 12.0f);
        float covRad = std::max(3.0f, 6.0f * s.cornerRadiusScale);
        r.FillRoundedRect(cov, covRad, state.media.dominantColor);
        r.DrawLucideIcon("music", left + 24.0f, cy, 12.0f, AdaptiveInkForBackground(state.media.dominantColor));

        if (s.showSongTitleInClosedPill && !state.media.title.empty()) {
            r.DrawTextStr(state.media.title,
                          D2D1::RectF(left + 44.0f, top + 2.0f, left + width - 48.0f, top + height - 2.0f),
                          11.5f, 500, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
        }
    } else {
        // Clean Resting Face when no media is playing (or when a resting info mode is selected)
        std::wstring infoText;
        SYSTEMTIME st;
        GetLocalTime(&st);
        wchar_t buf[64];
        RestingInfoMode effectiveMode = (s.restingMode == RestingInfoMode::MediaOnly)
            ? RestingInfoMode::Time
            : s.restingMode;

        switch (effectiveMode) {
            case RestingInfoMode::Time: {
                int hr = s.use24HourClock ? st.wHour : ((st.wHour % 12 == 0) ? 12 : (st.wHour % 12));
                swprintf_s(buf, s.use24HourClock ? L"%02d:%02d" : L"%d:%02d %s",
                           hr, st.wMinute, st.wHour >= 12 ? L"PM" : L"AM");
                infoText = buf;
                break;
            }
            case RestingInfoMode::Date: {
                static const wchar_t* days[] = { L"Sun", L"Mon", L"Tue", L"Wed", L"Thu", L"Fri", L"Sat" };
                swprintf_s(buf, L"%s %d/%d", days[st.wDayOfWeek % 7], st.wMonth, st.wDay);
                infoText = buf;
                break;
            }
            case RestingInfoMode::Weather: {
                bool isNight = (st.wHour < 6 || st.wHour >= 19);
                swprintf_s(buf, L"%s %d\x00B0", isNight ? L"\x263D" : L"\x2600", s.weatherTempC);
                infoText = buf;
                break;
            }
            case RestingInfoMode::Battery:
                swprintf_s(buf, L"\x26A1 %d%%", state.telemetry.batteryPercent);
                infoText = buf;
                break;
            case RestingInfoMode::CpuTemp:
                if (state.telemetry.cpuTempAvailable) {
                    swprintf_s(buf, L"CPU %d%% \x00B7 %d\x00B0C",
                               static_cast<int>(state.telemetry.cpuPercent),
                               static_cast<int>(state.telemetry.cpuTempC));
                } else {
                    swprintf_s(buf, L"CPU %d%% \x00B7 RAM %d%%",
                               static_cast<int>(state.telemetry.cpuPercent),
                               static_cast<int>(state.telemetry.ramPercent));
                }
                infoText = buf;
                break;
            case RestingInfoMode::NextEvent:
                infoText = s.upcomingMeetingTitle;
                break;
            case RestingInfoMode::PixelPet:
                infoText = (static_cast<int>(timeSec * 2.0f) % 2 == 0) ? L"(\x2022\x03C9\x2022)" : L"(^\x03C9^)";
                break;
            default:
                break;
        }
        r.DrawTextStr(infoText, D2D1::RectF(left + 16.0f, top, left + width - 48.0f, top + height),
                      11.5f, 600, theme.textMuted, false);
    }

    // 5. Privacy Sensor Dots (Mic / Camera / Screen naming active app) & Call Timer
    float sensorX = left + width - 52.0f;
    if (state.sensors.micActive || state.sensors.camActive || state.sensors.screenActive) {
        D2D1_COLOR_F dotCol = state.globalMicMuted
            ? Rgba(255, 69, 58, 1.0f)
            : (state.sensors.camActive ? Rgba(255, 159, 10, 1.0f) : Rgba(48, 209, 88, 1.0f));
        r.FillCircle(sensorX, cy, 3.8f, dotCol);

        std::wstring activeApp = !state.sensors.micAppName.empty() ? state.sensors.micAppName
                               : (!state.sensors.camAppName.empty() ? state.sensors.camAppName : state.sensors.screenAppName);
        if (state.sensors.micActive && state.sensors.micCallStartMs > 0) {
            double callSec = (GetTickMs() - state.sensors.micCallStartMs) / 1000.0;
            activeApp += L" " + FormatTimeSec(callSec);
        }
        r.DrawTextStr(activeApp, D2D1::RectF(left + 40.0f, top, sensorX - 6.0f, top + height),
                      10.5f, 500, dotCol, true, DWRITE_TEXT_ALIGNMENT_TRAILING);
    }

    // 6. Right Side of Resting Pill: 4-Column Visualizer (animated ONLY when real media is playing; flat when idle)
    float visBaseX = left + width - 32.0f;
    D2D1_COLOR_F visCol = state.media.hasSession ? state.media.dominantColor : theme.textFaint;
    for (int col = 0; col < 4; ++col) {
        float wave = (state.media.hasSession && state.media.isPlaying)
            ? (0.30f + 0.70f * std::fabs(std::sin(timeSec * (4.2f + col * 0.9f) + col * 1.3f)))
            : 0.18f;
        float vx = visBaseX + col * 5.0f;
        if (s.visualizer == VisualizerStyle::Pixels) {
            int blocks = std::max(1, static_cast<int>(std::round(wave * 4.0f)));
            for (int b = 0; b < blocks; ++b) {
                float by = cy + 5.0f - b * 4.0f;
                r.FillRoundedRect(D2D1::RectF(vx, by, vx + 3.0f, by + 3.0f), 0.4f, visCol);
            }
        } else {
            float bh = std::max(3.0f, wave * 15.5f);
            r.FillRoundedRect(D2D1::RectF(vx, cy - bh * 0.5f, vx + 3.0f, cy + bh * 0.5f), 1.5f, visCol);
        }
    }
}

// ============================================================================
// Render Top Tab Bar inside Expanded Card
// ============================================================================
static void RenderTopTabBar(
    D2DRenderer& r, GlobalAppState& state,
    float cardLeft, float cardTop, float cardWidth, const ThemeSpec& theme)
{
    static const char* panelIcons[] = {
        "music",              // Music
        "globe",              // Web
        "bell",               // Notifications & Agents
        "message-circle",     // Chat
        "folder",             // Shelf
        "clipboard",          // Clipboard
        "layout-grid",        // Widgets
        "sliders-horizontal", // Controls
        "grip-horizontal",    // Launcher
        "headphones"          // Calls
    };

    const auto& order = state.settings.panelOrder;
    int count = static_cast<int>(order.size());
    float spacing = 22.0f * (state.settings.openSizePercent / 100.0f);
    float totalW = (count + 1) * spacing;
    float startX = cardLeft + (cardWidth - totalW) * 0.5f + spacing * 0.5f;
    float tabY = cardTop + 14.0f;

    for (int i = 0; i < count; ++i) {
        PanelId pid = order[i];
        float cx = startX + i * spacing;
        D2D1_RECT_F tabBox = D2D1::RectF(cx - 10.0f, tabY - 9.0f, cx + 10.0f, tabY + 11.0f);
        bool isActive = (state.activePanel == pid);
        bool isHov = r.IsHovered(tabBox, state.pointerDipX, state.pointerDipY);

        D2D1_COLOR_F iconCol = isActive ? theme.textPrimary : (isHov ? theme.textMuted : theme.textFaint);
        int idx = std::clamp(static_cast<int>(pid), 0, 9);
        r.DrawLucideIcon(panelIcons[idx], cx, tabY, 14.0f, iconCol, isActive ? 2.0f : 1.65f);

        if (isActive) {
            r.FillRoundedRect(D2D1::RectF(cx - 1.32f, tabY + 8.5f, cx + 1.32f, tabY + 11.14f), 0.6f, theme.accent);
        }

        // Red live dot on Chat tab when Stream Chat is active
        if (pid == PanelId::Chat && state.settings.streamChatEnabled) {
            r.FillCircle(cx + 5.5f, tabY - 5.0f, 2.6f, Rgba(255, 59, 48, 1.0f));
        }
        // Status dot on Notifications/Agents tab when Free Buff or another coding agent needs input or is almost done
        if (pid == PanelId::Notifications) {
            bool agentNeedsInput = false;
            bool agentAlmostDone = false;
            for (const auto& ag : state.agents) {
                if (ag.needsInput) agentNeedsInput = true;
                if (ag.isAlmostDone) agentAlmostDone = true;
            }
            if (agentNeedsInput) {
                r.FillCircle(cx + 5.5f, tabY - 5.0f, 2.8f, Rgba(255, 159, 10, 1.0f));
            } else if (agentAlmostDone) {
                r.FillCircle(cx + 5.5f, tabY - 5.0f, 2.8f, Rgba(48, 209, 88, 1.0f));
            }
        }

        r.AddHitRegion("tab-" + std::to_string(idx), tabBox, [&state, pid]() {
            if (state.activePanel != pid) {
                state.previousPanel = state.activePanel;
                state.activePanel = pid;
                state.panelSpring.Snap(0.0f);
                state.panelSpring.SetTarget(100.0f, state.settings.reduceMotion);
            }
        });
    }

    float divX = startX + count * spacing - spacing * 0.45f;
    r.DrawLine(divX, tabY - 5.4f, divX, tabY + 5.4f, theme.edge, 1.0f);

    float gearX = startX + count * spacing + 2.0f;
    D2D1_RECT_F gearBox = D2D1::RectF(gearX - 10.0f, tabY - 9.0f, gearX + 10.0f, tabY + 11.0f);
    bool gearHov = r.IsHovered(gearBox, state.pointerDipX, state.pointerDipY);
    r.DrawLucideIcon("settings", gearX, tabY, 13.8f, gearHov ? theme.textPrimary : theme.textMuted);
    r.AddHitRegion("tab-settings", gearBox, [&state]() {
        if (state.hwndSettings) {
            bool vis = IsWindowVisible(state.hwndSettings) != FALSE;
            ShowWindow(state.hwndSettings, vis ? SW_HIDE : SW_SHOW);
            if (!vis) SetForegroundWindow(state.hwndSettings);
        }
    });
}

// ============================================================================
// Master Island Frame Renderer (Zero Placeholders, Clean Empty States & Free Buff)
// ============================================================================
void IslandUI::RenderIslandFrame(
    D2DRenderer& r, GlobalAppState& state,
    HWND hwnd, const RECT& windowScreenRect,
    float canvasW, float canvasH, float timeSec)
{
    (void)canvasH;
    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    r.ClearHitRegions();

    float left = 0, top = 0, width = 0, height = 0, radius = 18.0f;
    ComputeCurrentIslandGeometry(state, canvasW, left, top, width, height, radius);

    const auto& s = state.settings;
    const ThemeSpec& theme = GetThemeSpec(s.theme);
    float notchFactor01 = std::clamp(state.notchSpring.value / 100.0f, 0.0f, 1.0f);
    float openFactor01  = std::clamp(state.openSpring.value / 100.0f, 0.0f, 1.0f);

    ID2D1PathGeometry* path = r.CreateIslandOrNotchPath(left, top, width, height, radius, notchFactor01, s.edge);
    if (!path) return;

    ID2D1Layer* clipLayer = nullptr;
    if (SUCCEEDED(r.RT()->CreateLayer(nullptr, &clipLayer)) && clipLayer) {
        r.RT()->PushLayer(
            D2D1::LayerParameters(
                D2D1::InfiniteRect(),
                path,
                D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                D2D1::IdentityMatrix(),
                1.0f,
                nullptr,
                D2D1_LAYER_OPTIONS_NONE),
            clipLayer);

        // 1. Surface Fill: Liquid Glass / Candy Glass live blur & refraction with safe automatic fallback to Black
        bool drewGlass = false;
        if (!s.drawWithProcessor &&
            (s.theme == SurfaceTheme::LiquidGlass || s.theme == SurfaceTheme::CandyGlass))
        {
            RECT subScreen = windowScreenRect;
            subScreen.left   += static_cast<LONG>(left * r.DpiScale());
            subScreen.top    += static_cast<LONG>(top * r.DpiScale());
            subScreen.right   = subScreen.left + static_cast<LONG>(width * r.DpiScale());
            subScreen.bottom  = subScreen.top + static_cast<LONG>(height * r.DpiScale());

            drewGlass = r.RenderLiquidGlassBackdrop(
                hwnd, subScreen, left, top, width, height,
                s.glassBlurRadius, s.theme == SurfaceTheme::CandyGlass);
        }

        if (!drewGlass) {
            // Automatic fallback to Black (#0b0b0c) if glass surface fails or is unsupported
            D2D1_COLOR_F surfaceFill = theme.isGlass ? Rgba(11, 11, 12, 1.0f) : theme.fill;
            if (state.isFullscreenGameActive && !state.islandOpen && !s.gameStripShowBackground) {
                surfaceFill = Rgba(0, 0, 0, 0.01f);
            }
            r.FillRoundedRect(D2D1::RectF(left - 20.0f, top - 20.0f, left + width + 20.0f, top + height + 20.0f), 0.0f, surfaceFill);
        }

        // 2. Full-Bleed Top-Edge Album-Art Color Tinting (ONLY when a real media session is active!)
        bool showAlbumTint = (s.cardBackground == CardBackgroundMode::AlbumColours) &&
                             state.media.hasSession &&
                             (!state.islandOpen || state.activePanel == PanelId::Music);
        if (showAlbumTint) {
            r.RenderAlbumBackdrop(state.media, left, top, width, height);
        }

        // 3. Soft Themes Ambience Particles (Rosé hearts, Lavender stars, Peach bokeh, Sakura petals)
        r.RenderThemeAmbience(s.theme, left, top, width, height, timeSec);

        // 4. Resting Face when closed
        float restingAlpha = std::clamp(1.0f - openFactor01 * 2.2f, 0.0f, 1.0f);
        if (restingAlpha > 0.01f) {
            RenderRestingFace(r, state, left, top, width, height, theme, timeSec, restingAlpha);
        }

        // 5. Expanded Card Content when open
        float cardAlpha = CubicEaseInOut(std::clamp((openFactor01 - 0.28f) / 0.72f, 0.0f, 1.0f));
        if (cardAlpha > 0.02f) {
            RenderTopTabBar(r, state, left, top, width, theme);

            float bodyTop = top + 32.0f;
            float bodyLeft = left + 18.0f;
            float bodyW = width - 36.0f;

            if (state.isRecordingScreen) {
                D2D1_RECT_F recBanner = D2D1::RectF(left + width - 78.0f, top + 6.0f, left + width - 16.0f, top + 24.0f);
                r.FillRoundedRect(recBanner, 9.0f, Rgba(255, 59, 48, 0.92f));
                r.DrawTextStr(L"\x25A0 Stop REC", recBanner, 9.5f, 600, Rgba(255, 255, 255, 1.0f), false, DWRITE_TEXT_ALIGNMENT_CENTER);
                r.AddHitRegion("stop-rec-global", recBanner, [&state]() {
                    SystemServices::StopScreenRecording(state);
                });
            }

            switch (state.activePanel) {
                case PanelId::Music: {
                    // ========================================================
                    // TAB 01: MUSIC (400 x 150 DIP)
                    // Zero Placeholders: Shows clean idle state when nothing is playing!
                    // ========================================================
                    float covSize = state.media.hasSession ? (state.media.isPlaying ? 74.0f : 64.0f) : 64.0f;
                    float covCx = left + 22.0f + 37.0f;
                    float covCy = top + 32.0f + 37.0f;
                    D2D1_RECT_F covRect = D2D1::RectF(
                        covCx - covSize * 0.5f, covCy - covSize * 0.5f,
                        covCx + covSize * 0.5f, covCy + covSize * 0.5f);
                    D2D1_COLOR_F covCol = state.media.hasSession
                        ? state.media.dominantColor
                        : Rgba(255, 255, 255, 0.07f);
                    r.FillRoundedRect(covRect, 14.0f * s.cornerRadiusScale, covCol);
                    r.DrawLucideIcon("music", covCx, covCy, 24.0f,
                                     state.media.hasSession ? AdaptiveInkForBackground(covCol) : theme.textFaint);

                    if (state.media.hasSession && !state.media.sourceAppName.empty()) {
                        D2D1_RECT_F badgeRect = D2D1::RectF(left + 76.0f, top + 86.0f, left + 100.0f, top + 110.0f);
                        r.FillRoundedRect(badgeRect, 8.0f * s.cornerRadiusScale, theme.accent);
                        r.DrawTextStr(state.media.sourceAppName.substr(0, 1), badgeRect, 11.0f, 700,
                                      AdaptiveInkForBackground(theme.accent), false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    }

                    // Top-right Palette Background Toggle
                    D2D1_RECT_F palBtn = D2D1::RectF(left + width - 42.0f, top + 32.0f, left + width - 18.0f, top + 56.0f);
                    r.FillRoundedRect(palBtn, 8.0f, Rgba(255, 255, 255, 0.10f));
                    r.DrawLucideIcon("palette", (palBtn.left + palBtn.right) * 0.5f, (palBtn.top + palBtn.bottom) * 0.5f, 13.0f, theme.textPrimary);
                    r.AddHitRegion("music-palette-btn", palBtn, [&state]() {
                        state.settings.cardBackground = (state.settings.cardBackground == CardBackgroundMode::AlbumColours)
                            ? CardBackgroundMode::Plain
                            : CardBackgroundMode::AlbumColours;
                        ConfigManager::SaveAtomic(state);
                    });

                    float metaLeft = left + 110.0f;
                    float metaRight = left + width - 48.0f;
                    std::wstring dispTitle = state.media.hasSession ? state.media.title : L"Nothing playing";
                    std::wstring dispSub   = state.media.hasSession
                        ? (state.media.artist + L" \x00B7 " + state.media.sourceAppName)
                        : L"Play music or video in any app or browser";

                    r.DrawTextStr(dispTitle, D2D1::RectF(metaLeft, top + 33.0f, metaRight, top + 54.0f),
                                  15.5f, 600, state.media.hasSession ? theme.textPrimary : theme.textMuted, false);
                    r.DrawTextStr(dispSub, D2D1::RectF(metaLeft, top + 54.0f, metaRight, top + 72.0f),
                                  12.0f, 400, theme.textFaint, false);

                    // Timestamps & Progress Bar
                    r.DrawTextStr(FormatTimeSec(state.media.positionSec),
                                  D2D1::RectF(metaLeft, top + 78.0f, metaLeft + 36.0f, top + 94.0f),
                                  11.0f, 500, theme.textFaint, true);
                    r.DrawTextStr(FormatTimeSec(state.media.durationSec),
                                  D2D1::RectF(left + width - 54.0f, top + 78.0f, left + width - 18.0f, top + 94.0f),
                                  11.0f, 500, theme.textFaint, true, DWRITE_TEXT_ALIGNMENT_TRAILING);

                    float barLeft = metaLeft + 38.0f;
                    float barW = (left + width - 58.0f) - barLeft;
                    float prog = (state.media.hasSession && state.media.durationSec > 0.1)
                        ? static_cast<float>(state.media.positionSec / state.media.durationSec)
                        : 0.0f;
                    RenderMusicProgressBar(r, state, barLeft, top + 86.0f, barW, prog, theme, timeSec);

                    // Transport Controls Row + Live CoreAudio Volume Slider
                    float transY = top + 125.0f;
                    float centerTransX = left + width * 0.44f;

                    D2D1_RECT_F lyrBtn = D2D1::RectF(metaLeft, transY - 13.0f, metaLeft + 46.0f, transY + 13.0f);
                    r.FillRoundedRect(lyrBtn, 8.0f, state.lyricsDrawerVisible ? theme.accent : Rgba(255, 255, 255, 0.10f));
                    r.DrawTextStr(L"Lyrics", lyrBtn, 10.5f, 600,
                                  state.lyricsDrawerVisible ? AdaptiveInkForBackground(theme.accent) : theme.textPrimary,
                                  false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.AddHitRegion("music-lyrics-toggle", lyrBtn, [&state]() {
                        state.lyricsDrawerVisible = !state.lyricsDrawerVisible;
                        if (state.hwndLyricsDrawer) {
                            ShowWindow(state.hwndLyricsDrawer, state.lyricsDrawerVisible ? SW_SHOWNOACTIVATE : SW_HIDE);
                        }
                    });

                    D2D1_RECT_F prevBtn = D2D1::RectF(centerTransX - 54.0f, transY - 16.0f, centerTransX - 18.0f, transY + 16.0f);
                    if (r.IsHovered(prevBtn, state.pointerDipX, state.pointerDipY))
                        r.FillRoundedRect(prevBtn, 10.0f, Rgba(255, 255, 255, 0.10f));
                    r.DrawLucideIcon("skip-back", centerTransX - 36.0f, transY, 15.0f, theme.textPrimary);
                    r.AddHitRegion("music-prev", prevBtn, [&state]() {
                        SystemServices::MediaPreviousTrack(state);
                    });

                    D2D1_RECT_F playBtn = D2D1::RectF(centerTransX - 18.0f, transY - 18.0f, centerTransX + 18.0f, transY + 18.0f);
                    if (r.IsHovered(playBtn, state.pointerDipX, state.pointerDipY))
                        r.FillRoundedRect(playBtn, 12.0f, Rgba(255, 255, 255, 0.14f));
                    r.DrawLucideIcon(state.media.isPlaying ? "pause" : "play", centerTransX, transY, 18.0f, theme.textPrimary);
                    r.AddHitRegion("music-play", playBtn, [&state]() {
                        SystemServices::MediaTogglePlayPause(state);
                    });

                    D2D1_RECT_F nextBtn = D2D1::RectF(centerTransX + 18.0f, transY - 16.0f, centerTransX + 54.0f, transY + 16.0f);
                    if (r.IsHovered(nextBtn, state.pointerDipX, state.pointerDipY))
                        r.FillRoundedRect(nextBtn, 10.0f, Rgba(255, 255, 255, 0.10f));
                    r.DrawLucideIcon("skip-forward", centerTransX + 36.0f, transY, 15.0f, theme.textPrimary);
                    r.AddHitRegion("music-next", nextBtn, [&state]() {
                        SystemServices::MediaNextTrack(state);
                    });

                    // Real CoreAudio Volume Slider
                    float volX = left + width - 102.0f;
                    r.DrawLucideIcon("volume", volX, transY, 13.0f, state.masterOutputMuted ? Rgba(255, 69, 58, 1.0f) : theme.textMuted);
                    D2D1_RECT_F volTrack = D2D1::RectF(volX + 12.0f, transY - 8.0f, left + width - 18.0f, transY + 8.0f);
                    bool volHov = r.IsHovered(volTrack, state.pointerDipX, state.pointerDipY);
                    float vh = volHov ? 6.0f : 3.2f;
                    float vw = volTrack.right - volTrack.left;
                    r.FillRoundedRect(D2D1::RectF(volTrack.left, transY - vh * 0.5f, volTrack.right, transY + vh * 0.5f), vh * 0.5f, Rgba(255, 255, 255, 0.18f));
                    r.FillRoundedRect(D2D1::RectF(volTrack.left, transY - vh * 0.5f, volTrack.left + vw * (state.masterVolume / 100.0f), transY + vh * 0.5f), vh * 0.5f, theme.textPrimary);
                    r.AddHitRegion("music-vol-slider", volTrack, nullptr, [&state](float normX) {
                        SystemServices::SetMasterVolume(state, normX * 100.0f, false);
                    });
                    break;
                }

                case PanelId::Web: {
                    // ========================================================
                    // TAB 02: WEB DRAWER (400 x 232 DIP)
                    // ========================================================
                    r.DrawTextStr(L"Pinned sites \x00B7 Opens in rounded drawer over apps & games",
                                  D2D1::RectF(bodyLeft, bodyTop, bodyLeft + bodyW, bodyTop + 20.0f),
                                  11.5f, 500, theme.textMuted, false);

                    float tileW = (bodyW - 16.0f) / 3.0f;
                    float tileH = 74.0f;
                    float rowY = bodyTop + 26.0f;

                    for (size_t i = 0; i < state.pinnedSites.size() && i < 3; ++i) {
                        const auto& site = state.pinnedSites[i];
                        float tx = bodyLeft + i * (tileW + 8.0f);
                        D2D1_RECT_F tile = D2D1::RectF(tx, rowY, tx + tileW, rowY + tileH);
                        bool hov = r.IsHovered(tile, state.pointerDipX, state.pointerDipY);
                        r.FillRoundedRect(tile, 12.0f * s.cornerRadiusScale,
                                          hov ? Rgba(255, 255, 255, 0.14f) : Rgba(255, 255, 255, 0.08f));
                        r.FillCircle(tx + tileW * 0.5f, rowY + 24.0f, 13.0f, site.badgeColor);
                        r.DrawTextStr(site.title.substr(0, 1),
                                      D2D1::RectF(tx, rowY + 11.0f, tx + tileW, rowY + 37.0f),
                                      13.0f, 700, Rgba(255, 255, 255, 1.0f), false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        r.DrawTextStr(site.title,
                                      D2D1::RectF(tx + 6.0f, rowY + 44.0f, tx + tileW - 6.0f, rowY + 66.0f),
                                      12.0f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);

                        r.AddHitRegion("web-site-" + std::to_string(i), tile, [&state, i]() {
                            state.currentWebDrawerTitle = state.pinnedSites[i].title;
                            state.currentWebDrawerUrl   = state.pinnedSites[i].url;
                            state.webDrawerVisible      = true;
                            if (state.hwndWebDrawer) {
                                ShowWindow(state.hwndWebDrawer, SW_SHOWNOACTIVATE);
                                InvalidateRect(state.hwndWebDrawer, nullptr, FALSE);
                            }
                        });
                    }

                    D2D1_RECT_F addBtn = D2D1::RectF(bodyLeft, rowY + tileH + 12.0f, bodyLeft + bodyW * 0.48f, rowY + tileH + 46.0f);
                    r.FillRoundedRect(addBtn, 10.0f, Rgba(255, 255, 255, 0.10f));
                    r.DrawTextStr(L"+ Add / Restore defaults", addBtn, 11.0f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.AddHitRegion("web-add-site", addBtn, [&state]() {
                        state.pinnedSites = {
                            { L"YT Music", L"https://music.youtube.com", Rgba(255, 46, 76, 1.0f) },
                            { L"YouTube",  L"https://www.youtube.com",   Rgba(255, 0, 51, 1.0f) },
                            { L"Spotify",  L"https://open.spotify.com",  Rgba(30, 215, 96, 1.0f) }
                        };
                    });

                    D2D1_RECT_F closeDrawerBtn = D2D1::RectF(bodyLeft + bodyW * 0.52f, rowY + tileH + 12.0f, bodyLeft + bodyW, rowY + tileH + 46.0f);
                    r.FillRoundedRect(closeDrawerBtn, 10.0f, state.webDrawerVisible ? theme.accent : Rgba(255, 255, 255, 0.06f));
                    r.DrawTextStr(state.webDrawerVisible ? L"Close Web Drawer" : L"Drawer Dormant (0 MB)",
                                  closeDrawerBtn, 11.0f, 600,
                                  state.webDrawerVisible ? AdaptiveInkForBackground(theme.accent) : theme.textMuted,
                                  false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.AddHitRegion("web-toggle-drawer", closeDrawerBtn, [&state]() {
                        state.webDrawerVisible = !state.webDrawerVisible;
                        if (state.hwndWebDrawer) {
                            ShowWindow(state.hwndWebDrawer, state.webDrawerVisible ? SW_SHOWNOACTIVATE : SW_HIDE);
                        }
                    });
                    break;
                }

                case PanelId::Notifications: {
                    // ========================================================
                    // TAB 03: NOTIFICATIONS & AI CODING AGENTS (400 x 232 DIP)
                    // First-class Free Buff + Claude Code, Codex, Antigravity, Hermes
                    // ========================================================
                    r.DrawTextStr(L"Notifications", D2D1::RectF(bodyLeft, bodyTop, bodyLeft + 130.0f, bodyTop + 18.0f),
                                  12.0f, 600, theme.textPrimary, false);
                    if (!state.notifications.empty()) {
                        D2D1_RECT_F clearBtn = D2D1::RectF(bodyLeft + bodyW - 58.0f, bodyTop, bodyLeft + bodyW, bodyTop + 18.0f);
                        r.DrawTextStr(L"Clear all", clearBtn, 11.0f, 500, theme.textMuted, false, DWRITE_TEXT_ALIGNMENT_TRAILING);
                        r.AddHitRegion("notif-clear", clearBtn, [&state]() {
                            state.notifications.clear();
                        });
                    }

                    float ny = bodyTop + 21.0f;
                    if (state.notifications.empty()) {
                        D2D1_RECT_F emptyBox = D2D1::RectF(bodyLeft, ny, bodyLeft + bodyW, ny + 54.0f);
                        r.FillRoundedRect(emptyBox, 10.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.05f));
                        r.DrawTextStr(L"No notifications yet \x00B7 Stays 100% local on this PC",
                                      emptyBox, 11.0f, 500, theme.textFaint, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    } else {
                        for (size_t i = 0; i < std::min<size_t>(2, state.notifications.size()); ++i) {
                            const auto& n = state.notifications[i];
                            D2D1_RECT_F card = D2D1::RectF(bodyLeft, ny, bodyLeft + bodyW, ny + 36.0f);
                            r.FillRoundedRect(card, 10.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.08f));
                            r.FillCircle(bodyLeft + 12.0f, ny + 18.0f, 5.0f, n.accentColor);
                            r.DrawTextStr(n.appName + L" \x00B7 " + n.title,
                                          D2D1::RectF(bodyLeft + 24.0f, ny + 2.0f, bodyLeft + bodyW - 58.0f, ny + 18.0f),
                                          11.0f, 600, theme.textPrimary, false);
                            r.DrawTextStr(n.body,
                                          D2D1::RectF(bodyLeft + 24.0f, ny + 18.0f, bodyLeft + bodyW - 14.0f, ny + 34.0f),
                                          10.5f, 400, theme.textMuted, false);
                            std::wstring ts = s.clockTimestampsInNotifications ? n.timeStr : n.relativeStr;
                            r.DrawTextStr(ts, D2D1::RectF(bodyLeft + bodyW - 56.0f, ny + 2.0f, bodyLeft + bodyW - 8.0f, ny + 18.0f),
                                          10.0f, 500, theme.textFaint, true, DWRITE_TEXT_ALIGNMENT_TRAILING);
                            ny += 39.0f;
                        }
                    }

                    // Bottom Section: AI Coding Agents (Free Buff, Claude Code, Codex, Antigravity, Hermes)
                    float ay = bodyTop + 102.0f;
                    int needsCount = 0, almostCount = 0, workCount = 0;
                    for (const auto& ag : state.agents) {
                        if (ag.needsInput) ++needsCount;
                        else if (ag.isAlmostDone) ++almostCount;
                        else if (ag.status == L"Working") ++workCount;
                    }

                    std::wstring agentHdr = L"AI Agents (Free Buff \x00B7 Claude Code \x00B7 Codex \x00B7 Antigravity \x00B7 Hermes)";
                    r.DrawTextStr(agentHdr, D2D1::RectF(bodyLeft, ay, bodyLeft + bodyW - 50.0f, ay + 16.0f),
                                  10.5f, 600, theme.textPrimary, false);

                    if (!state.agents.empty()) {
                        D2D1_RECT_F clrAgBtn = D2D1::RectF(bodyLeft + bodyW - 46.0f, ay, bodyLeft + bodyW, ay + 16.0f);
                        r.DrawTextStr(L"Clear", clrAgBtn, 10.5f, 500, theme.textMuted, false, DWRITE_TEXT_ALIGNMENT_TRAILING);
                        r.AddHitRegion("agents-clear", clrAgBtn, [&state]() {
                            state.agents.clear();
                        });
                    }
                    ay += 19.0f;

                    if (state.agents.empty()) {
                        D2D1_RECT_F idleAgBox = D2D1::RectF(bodyLeft, ay, bodyLeft + bodyW, ay + 64.0f);
                        r.FillRoundedRect(idleAgBox, 10.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.05f));
                        r.DrawTextStr(L"No active agent sessions detected on this PC\n"
                                      L"Watching Free Buff, Claude Code, Codex, Antigravity & Hermes",
                                      D2D1::RectF(bodyLeft + 10.0f, ay + 6.0f, bodyLeft + bodyW - 10.0f, ay + 58.0f),
                                      10.5f, 500, theme.textFaint, false,
                                      DWRITE_TEXT_ALIGNMENT_CENTER, DWRITE_PARAGRAPH_ALIGNMENT_CENTER, false);
                    } else {
                        float colW = (bodyW - 8.0f) * 0.5f;
                        for (size_t i = 0; i < std::min<size_t>(4, state.agents.size()); ++i) {
                            const auto& ag = state.agents[i];
                            float ax = bodyLeft + (i % 2) * (colW + 8.0f);
                            float rowTop = ay + (i / 2) * 34.0f;
                            D2D1_RECT_F aCard = D2D1::RectF(ax, rowTop, ax + colW, rowTop + 30.0f);

                            // Highlight amber when Free Buff needs input, emerald-cyan when almost done, subtle glass when working
                            D2D1_COLOR_F bg = ag.needsInput ? Rgba(255, 159, 10, 0.24f)
                                            : (ag.isAlmostDone ? Rgba(48, 209, 88, 0.20f) : Rgba(255, 255, 255, 0.07f));
                            r.FillRoundedRect(aCard, 8.0f, bg);
                            if (ag.needsInput || ag.isAlmostDone) {
                                r.DrawRoundedRect(aCard, 8.0f, ag.needsInput ? Rgba(255, 159, 10, 0.75f) : Rgba(48, 209, 88, 0.65f), 1.0f);
                            }

                            D2D1_COLOR_F dot = ag.needsInput ? Rgba(255, 159, 10, 1.0f)
                                             : (ag.isAlmostDone ? Rgba(48, 209, 88, 1.0f)
                                             : (ag.status == L"Working" ? Rgba(78, 179, 255, 1.0f) : theme.textFaint));
                            r.FillCircle(ax + 10.0f, rowTop + 15.0f, 3.8f, dot);

                            std::wstring statusLabel = ag.status;
                            if (ag.progressPercent >= 0) {
                                statusLabel += L" (" + std::to_wstring(ag.progressPercent) + L"%)";
                            }
                            r.DrawTextStr(ag.agentType + L" \x00B7 " + statusLabel,
                                          D2D1::RectF(ax + 18.0f, rowTop + 2.0f, ax + colW - 6.0f, rowTop + 15.0f),
                                          10.5f, 700, theme.textPrimary, false);
                            r.DrawTextStr(ag.currentStep,
                                          D2D1::RectF(ax + 18.0f, rowTop + 15.0f, ax + colW - 6.0f, rowTop + 28.0f),
                                          9.5f, 400, theme.textMuted, false);
                        }
                    }
                    break;
                }

                case PanelId::Chat: {
                    // ========================================================
                    // TAB 04: STREAM CHAT & OBS STUDIO REC PILL (400 x 150 DIP)
                    // ========================================================
                    D2D1_RECT_F statusBox = D2D1::RectF(bodyLeft, bodyTop + 4.0f, bodyLeft + bodyW, bodyTop + 56.0f);
                    r.FillRoundedRect(statusBox, 12.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.08f));

                    std::wstring statusText = s.streamChatEnabled
                        ? (L"\x25CF Stream chat overlay active")
                        : L"Chat overlay is stopped.";
                    r.DrawTextStr(statusText,
                                  D2D1::RectF(bodyLeft + 14.0f, bodyTop + 10.0f, bodyLeft + bodyW - 96.0f, bodyTop + 32.0f),
                                  12.5f, 600, s.streamChatEnabled ? Rgba(48, 209, 88, 1.0f) : theme.textPrimary, false);
                    r.DrawTextStr(L"Movable Twitch & YouTube chat drawer + OBS REC pill",
                                  D2D1::RectF(bodyLeft + 14.0f, bodyTop + 32.0f, bodyLeft + bodyW - 96.0f, bodyTop + 50.0f),
                                  11.0f, 400, theme.textMuted, false);

                    D2D1_RECT_F startBtn = D2D1::RectF(bodyLeft + bodyW - 88.0f, bodyTop + 14.0f, bodyLeft + bodyW - 12.0f, bodyTop + 46.0f);
                    r.FillRoundedRect(startBtn, 16.0f, s.streamChatEnabled ? Rgba(255, 69, 58, 0.90f) : theme.accent);
                    r.DrawTextStr(s.streamChatEnabled ? L"\x25A0 Stop" : L"\x25B6 Start",
                                  startBtn, 12.0f, 600,
                                  s.streamChatEnabled ? Rgba(255, 255, 255, 1.0f) : AdaptiveInkForBackground(theme.accent),
                                  false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.AddHitRegion("chat-start-stop", startBtn, [&state]() {
                        state.settings.streamChatEnabled = !state.settings.streamChatEnabled;
                        state.chatDrawerVisible = state.settings.streamChatEnabled;
                        if (state.hwndChatDrawer) {
                            ShowWindow(state.hwndChatDrawer, state.chatDrawerVisible ? SW_SHOWNOACTIVATE : SW_HIDE);
                        }
                        ConfigManager::SaveAtomic(state);
                    });

                    D2D1_RECT_F drawerBtn = D2D1::RectF(bodyLeft, bodyTop + 66.0f, bodyLeft + bodyW * 0.48f, bodyTop + 98.0f);
                    r.FillRoundedRect(drawerBtn, 10.0f, Rgba(255, 255, 255, 0.08f));
                    r.DrawTextStr(state.chatDrawerVisible ? L"Hide Chat Drawer" : L"Show Chat Drawer",
                                  drawerBtn, 11.5f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.AddHitRegion("chat-drawer-toggle", drawerBtn, [&state]() {
                        state.chatDrawerVisible = !state.chatDrawerVisible;
                        if (state.hwndChatDrawer) {
                            ShowWindow(state.hwndChatDrawer, state.chatDrawerVisible ? SW_SHOWNOACTIVATE : SW_HIDE);
                        }
                    });

                    D2D1_RECT_F obsBtn = D2D1::RectF(bodyLeft + bodyW * 0.52f, bodyTop + 66.0f, bodyLeft + bodyW, bodyTop + 98.0f);
                    r.FillRoundedRect(obsBtn, 10.0f, state.obsRecordingActive ? Rgba(255, 59, 48, 0.25f) : Rgba(255, 255, 255, 0.08f));
                    r.DrawTextStr(state.obsRecordingActive ? L"\x25CF OBS REC Active" : L"OBS REC Pill: Ready",
                                  obsBtn, 11.5f, 600, state.obsRecordingActive ? Rgba(255, 89, 78, 1.0f) : theme.textMuted,
                                  false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.AddHitRegion("chat-obs-toggle", obsBtn, [&state]() {
                        state.obsRecordingActive = !state.obsRecordingActive;
                        if (state.obsRecordingActive) state.obsRecordingStartMs = GetTickMs();
                    });
                    break;
                }

                case PanelId::Shelf: {
                    // ========================================================
                    // TAB 05: FILE SHELF & SEND TO PHONE QR (400 x 156 DIP)
                    // Zero Fake Files: Shows dashed drop zone when empty
                    // ========================================================
                    if (state.sendToPhoneQrActive) {
                        r.DrawQrCode(state.sendToPhoneUrl, bodyLeft + 4.0f, bodyTop + 2.0f, 102.0f,
                                     Rgba(12, 12, 14, 1.0f), Rgba(239, 237, 232, 1.0f));
                        r.DrawTextStr(L"Scan with your phone camera",
                                      D2D1::RectF(bodyLeft + 118.0f, bodyTop + 6.0f, bodyLeft + bodyW, bodyTop + 28.0f),
                                      13.5f, 600, theme.textPrimary, false);
                        r.DrawTextStr(L"Same Wi-Fi \x00B7 No account or app needed",
                                      D2D1::RectF(bodyLeft + 118.0f, bodyTop + 28.0f, bodyLeft + bodyW, bodyTop + 46.0f),
                                      11.5f, 400, theme.textMuted, false);
                        r.DrawTextStr(state.sendToPhoneUrl,
                                      D2D1::RectF(bodyLeft + 118.0f, bodyTop + 50.0f, bodyLeft + bodyW, bodyTop + 70.0f),
                                      10.5f, 500, theme.accent, true);

                        D2D1_RECT_F backBtn = D2D1::RectF(bodyLeft + 118.0f, bodyTop + 76.0f, bodyLeft + 210.0f, bodyTop + 104.0f);
                        r.FillRoundedRect(backBtn, 12.0f, Rgba(255, 255, 255, 0.12f));
                        r.DrawTextStr(L"\x2190 Back to Shelf", backBtn, 11.5f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        r.AddHitRegion("shelf-qr-back", backBtn, [&state]() {
                            state.sendToPhoneQrActive = false;
                        });
                    } else {
                        std::wstring hdr = L"Shelf \x00B7 " + std::to_wstring(state.shelfFiles.size()) + L" / 12 files";
                        r.DrawTextStr(hdr, D2D1::RectF(bodyLeft, bodyTop, bodyLeft + 140.0f, bodyTop + 24.0f),
                                      12.5f, 600, theme.textPrimary, false);

                        D2D1_RECT_F phoneBtn = D2D1::RectF(bodyLeft + bodyW - 148.0f, bodyTop, bodyLeft + bodyW - 36.0f, bodyTop + 24.0f);
                        r.FillRoundedRect(phoneBtn, 12.0f, theme.accent);
                        r.DrawTextStr(L"Send to phone", phoneBtn, 11.0f, 600,
                                      AdaptiveInkForBackground(theme.accent), false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        r.AddHitRegion("shelf-send-phone", phoneBtn, [&state]() {
                            state.sendToPhoneUrl = L"http://" + state.localPrivateIp + L":" +
                                                   std::to_wstring(state.settings.pcDeckPort) + L"/shelf";
                            state.sendToPhoneQrActive = true;
                        });

                        D2D1_RECT_F clrBtn = D2D1::RectF(bodyLeft + bodyW - 30.0f, bodyTop, bodyLeft + bodyW, bodyTop + 24.0f);
                        r.FillRoundedRect(clrBtn, 8.0f, Rgba(255, 255, 255, 0.08f));
                        r.DrawTextStr(L"\x00D7", clrBtn, 14.0f, 600, theme.textMuted, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        r.AddHitRegion("shelf-clear", clrBtn, [&state]() {
                            state.shelfFiles.clear();
                        });

                        float fy = bodyTop + 30.0f;
                        if (state.shelfFiles.empty()) {
                            D2D1_RECT_F dropZone = D2D1::RectF(bodyLeft, fy, bodyLeft + bodyW, fy + 76.0f);
                            r.FillRoundedRect(dropZone, 12.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.05f));
                            r.DrawRoundedRect(dropZone, 12.0f * s.cornerRadiusScale, theme.edge, 1.0f);
                            r.DrawTextStr(L"Drop up to 12 files here to hold, drag out, or send to your phone",
                                          dropZone, 11.5f, 500, theme.textFaint, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        } else {
                            float cardW = 68.0f;
                            float cardH = 76.0f;
                            for (size_t i = 0; i < std::min<size_t>(5, state.shelfFiles.size()); ++i) {
                                const auto& f = state.shelfFiles[i];
                                float fx = bodyLeft + i * (cardW + 6.0f);
                                D2D1_RECT_F fCard = D2D1::RectF(fx, fy, fx + cardW, fy + cardH);
                                r.FillRoundedRect(fCard, 10.0f * s.cornerRadiusScale,
                                                  f.selected ? Rgba(26, 122, 240, 0.28f) : Rgba(255, 255, 255, 0.08f));
                                r.DrawLucideIcon("folder", fx + cardW * 0.5f, fy + 26.0f, 20.0f, theme.accent);
                                r.DrawTextStr(f.displayName,
                                              D2D1::RectF(fx + 4.0f, fy + 50.0f, fx + cardW - 4.0f, fy + 72.0f),
                                              10.5f, 500, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                                r.AddHitRegion("shelf-file-" + std::to_string(i), fCard, [&state, i]() {
                                    SystemServices::CopyTextToClipboard(state.shelfFiles[i].fullPath);
                                    state.shelfFiles[i].selected = !state.shelfFiles[i].selected;
                                });
                            }
                        }
                    }
                    break;
                }

                case PanelId::Clipboard: {
                    // ========================================================
                    // TAB 06: CLIPBOARD HISTORY (400 x 196 DIP)
                    // Zero Fake Items: Populated from real Windows clipboard events
                    // ========================================================
                    static const wchar_t* chipLabels[] = { L"All", L"Text", L"Links", L"Images", L"Files", L"Snippets" };
                    float chipX = bodyLeft;
                    for (int c = 0; c < 6; ++c) {
                        float cw = (c == 0) ? 38.0f : (c == 5 ? 64.0f : 50.0f);
                        D2D1_RECT_F chip = D2D1::RectF(chipX, bodyTop, chipX + cw, bodyTop + 22.0f);
                        bool active = (static_cast<int>(state.activeClipCat) == c);
                        r.FillRoundedRect(chip, 11.0f, active ? theme.accent : Rgba(255, 255, 255, 0.08f));
                        r.DrawTextStr(chipLabels[c], chip, 10.5f, 600,
                                      active ? AdaptiveInkForBackground(theme.accent) : theme.textMuted,
                                      false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        r.AddHitRegion("clip-chip-" + std::to_string(c), chip, [&state, c]() {
                            state.activeClipCat = static_cast<ClipboardCategory>(c);
                        });
                        chipX += cw + 5.0f;
                    }

                    float ry = bodyTop + 28.0f;
                    int shown = 0;
                    for (size_t i = 0; i < state.clipboardHistory.size() && shown < 3; ++i) {
                        auto& item = state.clipboardHistory[i];
                        if (state.activeClipCat != ClipboardCategory::All && item.category != state.activeClipCat)
                            continue;

                        D2D1_RECT_F row = D2D1::RectF(bodyLeft, ry, bodyLeft + bodyW, ry + 36.0f);
                        bool hov = r.IsHovered(row, state.pointerDipX, state.pointerDipY);
                        r.FillRoundedRect(row, 9.0f * s.cornerRadiusScale,
                                          hov ? Rgba(255, 255, 255, 0.14f) : Rgba(255, 255, 255, 0.07f));

                        std::wstring badge = item.isCurrent ? L"CURRENT" : (item.isPinned ? L"PINNED" : item.sourceInfo);
                        D2D1_COLOR_F badgeCol = item.isCurrent ? Rgba(48, 209, 88, 1.0f) : theme.accent;
                        r.DrawTextStr(badge, D2D1::RectF(bodyLeft + 10.0f, ry + 3.0f, bodyLeft + 90.0f, ry + 16.0f),
                                      9.0f, 700, badgeCol, true);

                        r.DrawTextStr(item.preview,
                                      D2D1::RectF(bodyLeft + 10.0f, ry + 15.0f, bodyLeft + bodyW - 86.0f, ry + 33.0f),
                                      11.5f, 500, theme.textPrimary, false);

                        D2D1_RECT_F actBtn = D2D1::RectF(bodyLeft + bodyW - 78.0f, ry + 6.0f, bodyLeft + bodyW - 8.0f, ry + 30.0f);
                        r.FillRoundedRect(actBtn, 7.0f, Rgba(255, 255, 255, 0.10f));
                        std::wstring actLabel = (item.category == ClipboardCategory::Images)
                            ? L"Copy text"
                            : (item.isPinned ? L"Unpin" : L"Pin");
                        r.DrawTextStr(actLabel, actBtn, 10.0f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);

                        r.AddHitRegion("clip-act-" + std::to_string(i), actBtn, [&state, i]() {
                            auto& entry = state.clipboardHistory[i];
                            if (entry.category == ClipboardCategory::Images && !entry.ocrText.empty()) {
                                SystemServices::CopyTextToClipboard(entry.ocrText);
                            } else {
                                entry.isPinned = !entry.isPinned;
                                ConfigManager::SaveAtomic(state);
                            }
                        });

                        r.AddHitRegion("clip-row-" + std::to_string(i), row, [&state, i]() {
                            for (auto& e : state.clipboardHistory) e.isCurrent = false;
                            state.clipboardHistory[i].isCurrent = true;
                            SystemServices::CopyTextToClipboard(state.clipboardHistory[i].text);
                        });

                        ry += 40.0f;
                        ++shown;
                    }

                    if (shown == 0) {
                        D2D1_RECT_F emptyClip = D2D1::RectF(bodyLeft, ry, bodyLeft + bodyW, ry + 110.0f);
                        r.FillRoundedRect(emptyClip, 12.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.05f));
                        r.DrawTextStr(L"Nothing copied yet \x00B7 Press Ctrl+C in any app\nStored 100% locally on this PC",
                                      emptyClip, 11.5f, 500, theme.textFaint, false,
                                      DWRITE_TEXT_ALIGNMENT_CENTER, DWRITE_PARAGRAPH_ALIGNMENT_CENTER, false);
                    }
                    break;
                }

                case PanelId::Widgets: {
                    // ========================================================
                    // TAB 07: WIDGETS & FAST REMINDERS (400 x 192 DIP)
                    // ========================================================
                    float tileW = (bodyW - 10.0f) * 0.5f;
                    float tileH = 68.0f;

                    // Tile 0: Clock (12/24h) & World Clock
                    D2D1_RECT_F t0 = D2D1::RectF(bodyLeft, bodyTop, bodyLeft + tileW, bodyTop + tileH);
                    r.FillRoundedRect(t0, 12.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.08f));
                    SYSTEMTIME st;
                    GetLocalTime(&st);
                    int hr = s.use24HourClock ? st.wHour : ((st.wHour % 12 == 0) ? 12 : (st.wHour % 12));
                    wchar_t clkBuf[64];
                    swprintf_s(clkBuf, s.use24HourClock ? L"%02d:%02d" : L"%d:%02d %s",
                               hr, st.wMinute, st.wHour >= 12 ? L"PM" : L"AM");
                    r.DrawTextStr(clkBuf, D2D1::RectF(t0.left + 12.0f, t0.top + 8.0f, t0.right - 12.0f, t0.top + 34.0f),
                                  17.0f, 700, theme.textPrimary, true);
                    int worldHr = (st.wHour + s.worldClockOffsetHours + 24) % 24;
                    wchar_t wldBuf[64];
                    swprintf_s(wldBuf, L"%s %02d:%02d \x00B7 Pin \x2197", s.worldClockCity.c_str(), worldHr, st.wMinute);
                    r.DrawTextStr(wldBuf, D2D1::RectF(t0.left + 12.0f, t0.top + 38.0f, t0.right - 12.0f, t0.bottom - 8.0f),
                                  11.0f, 500, theme.textMuted, false);
                    r.AddHitRegion("widget-clock-tile", t0, [&state]() {
                        SystemServices::SpawnOrToggleDesktopWidget(state, WidgetType::Clock);
                    });

                    // Tile 1: To-Do List (+ Add item button when empty)
                    D2D1_RECT_F t1 = D2D1::RectF(bodyLeft + tileW + 10.0f, bodyTop, bodyLeft + bodyW, bodyTop + tileH);
                    r.FillRoundedRect(t1, 12.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.08f));
                    r.DrawTextStr(L"To do \x00B7 " + std::to_wstring(state.todoList.size()) + L"  [+]",
                                  D2D1::RectF(t1.left + 12.0f, t1.top + 6.0f, t1.right - 12.0f, t1.top + 24.0f),
                                  11.5f, 600, theme.accent, false);
                    if (state.todoList.empty()) {
                        r.DrawTextStr(L"Click to add a task or pin to desktop",
                                      D2D1::RectF(t1.left + 12.0f, t1.top + 28.0f, t1.right - 10.0f, t1.bottom - 8.0f),
                                      10.5f, 400, theme.textFaint, false);
                    } else {
                        for (size_t i = 0; i < std::min<size_t>(2, state.todoList.size()); ++i) {
                            std::wstring line = (state.todoList[i].done ? L"\x2611 " : L"\x2610 ") + state.todoList[i].text;
                            r.DrawTextStr(line,
                                          D2D1::RectF(t1.left + 12.0f, t1.top + 25.0f + i * 19.0f, t1.right - 10.0f, t1.top + 43.0f + i * 19.0f),
                                          11.0f, 400, state.todoList[i].done ? theme.textFaint : theme.textPrimary, false);
                        }
                    }
                    r.AddHitRegion("widget-todo-tile", t1, [&state]() {
                        if (state.todoList.empty()) {
                            state.todoList.push_back({ L"New task", false });
                        } else {
                            state.todoList[0].done = !state.todoList[0].done;
                        }
                        SystemServices::SpawnOrToggleDesktopWidget(state, WidgetType::TodoList);
                    });

                    // Tile 2: Minimal Weather (moon icon at night)
                    float row2Y = bodyTop + tileH + 10.0f;
                    D2D1_RECT_F t2 = D2D1::RectF(bodyLeft, row2Y, bodyLeft + tileW, row2Y + tileH);
                    r.FillRoundedRect(t2, 12.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.08f));
                    bool isNight = (st.wHour < 6 || st.wHour >= 19);
                    wchar_t wthBuf[64];
                    swprintf_s(wthBuf, L"%s %d\x00B0 %s", isNight ? L"\x263D Clear" : L"\x2600 Clear",
                               s.weatherTempC, s.weatherCity.c_str());
                    r.DrawTextStr(wthBuf, D2D1::RectF(t2.left + 12.0f, t2.top + 10.0f, t2.right - 12.0f, t2.top + 34.0f),
                                  13.5f, 600, theme.textPrimary, false);
                    wchar_t hlBuf[64];
                    swprintf_s(hlBuf, L"H %d\x00B0  L %d\x00B0 \x00B7 Pin \x2197", s.weatherHighC, s.weatherLowC);
                    r.DrawTextStr(hlBuf, D2D1::RectF(t2.left + 12.0f, t2.top + 38.0f, t2.right - 12.0f, t2.bottom - 8.0f),
                                  11.0f, 400, theme.textMuted, false);
                    r.AddHitRegion("widget-weather-tile", t2, [&state]() {
                        SystemServices::SpawnOrToggleDesktopWidget(state, WidgetType::MinimalWeather);
                    });

                    // Tile 3: Focus Timer & Real PC Stats (CPU, RAM, C: Storage bar)
                    D2D1_RECT_F t3 = D2D1::RectF(bodyLeft + tileW + 10.0f, row2Y, bodyLeft + bodyW, row2Y + tileH);
                    r.FillRoundedRect(t3, 12.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.08f));
                    wchar_t statBuf[64];
                    swprintf_s(statBuf, L"CPU %d%% \x00B7 RAM %d%%",
                               static_cast<int>(state.telemetry.cpuPercent),
                               static_cast<int>(state.telemetry.ramPercent));
                    r.DrawTextStr(statBuf, D2D1::RectF(t3.left + 12.0f, t3.top + 10.0f, t3.right - 12.0f, t3.top + 32.0f),
                                  12.0f, 600, theme.textPrimary, true);
                    float barX = t3.left + 12.0f;
                    float barW = tileW - 24.0f;
                    float diskFrac = std::clamp(state.telemetry.storagePercent / 100.0f, 0.05f, 1.0f);
                    r.FillRoundedRect(D2D1::RectF(barX, t3.top + 42.0f, barX + barW, t3.top + 50.0f), 4.0f, Rgba(255, 255, 255, 0.14f));
                    r.FillRoundedRect(D2D1::RectF(barX, t3.top + 42.0f, barX + barW * diskFrac, t3.top + 50.0f), 4.0f, Rgba(48, 209, 88, 1.0f));
                    r.AddHitRegion("widget-stats-tile", t3, [&state]() {
                        state.focusTimerRunning = !state.focusTimerRunning;
                        SystemServices::SpawnOrToggleDesktopWidget(state, WidgetType::PcStats);
                    });

                    // Central Circular Bell Button (Fast Reminder Creator)
                    float centerBellX = bodyLeft + bodyW * 0.5f;
                    float centerBellY = bodyTop + tileH + 5.0f;
                    r.FillCircle(centerBellX, centerBellY, 15.0f, theme.accent);
                    r.DrawLucideIcon("bell", centerBellX, centerBellY, 14.0f, AdaptiveInkForBackground(theme.accent));
                    D2D1_RECT_F bellHit = D2D1::RectF(centerBellX - 16.0f, centerBellY - 16.0f, centerBellX + 16.0f, centerBellY + 16.0f);
                    r.AddHitRegion("widget-fast-reminder", bellHit, [&state]() {
                        ReminderItem rem;
                        rem.id = GetTickMs();
                        rem.title = L"Reminder";
                        rem.dueTimestampMs = GetTickMs() + 1000;
                        rem.repeatRule = L"None";
                        state.reminders.push_back(rem);
                        state.activeReminderId = rem.id;
                        state.toastPopupType = TopPopupType::Reminder;
                        state.toastTitle = rem.title;
                        state.toastIsReminder = true;
                        state.toastActive = true;
                        state.toastAccent = Rgba(255, 159, 10, 1.0f);
                        state.toastExpireMs = GetTickMs() + 60000;
                    });
                    break;
                }

                case PanelId::Controls: {
                    // ========================================================
                    // TAB 08: QUICK CONTROLS (400 x 204 DIP)
                    // Wired directly to REAL Windows CoreAudio & WMI/DDC-CI Brightness
                    // ========================================================
                    r.DrawTextStr(L"Brightness", D2D1::RectF(bodyLeft, bodyTop, bodyLeft + 80.0f, bodyTop + 22.0f),
                                  11.5f, 600, theme.textPrimary, false);
                    D2D1_RECT_F brTrack = D2D1::RectF(bodyLeft + 86.0f, bodyTop + 6.0f, bodyLeft + bodyW - 38.0f, bodyTop + 16.0f);
                    r.FillRoundedRect(brTrack, 5.0f, Rgba(255, 255, 255, 0.14f));
                    float brFillW = (brTrack.right - brTrack.left) * (state.monitorBrightness / 100.0f);
                    r.FillRoundedRect(D2D1::RectF(brTrack.left, brTrack.top, brTrack.left + brFillW, brTrack.bottom), 5.0f, theme.accent);
                    r.DrawTextStr(std::to_wstring(static_cast<int>(state.monitorBrightness)) + L"%",
                                  D2D1::RectF(bodyLeft + bodyW - 34.0f, bodyTop, bodyLeft + bodyW, bodyTop + 22.0f),
                                  11.0f, 600, theme.textMuted, true, DWRITE_TEXT_ALIGNMENT_TRAILING);
                    r.AddHitRegion("ctrl-brightness", brTrack, nullptr, [&state](float normX) {
                        SystemServices::SetMonitorBrightnessLevel(state, normX * 100.0f, false);
                    });

                    float volY = bodyTop + 30.0f;
                    r.DrawTextStr(L"Volume", D2D1::RectF(bodyLeft, volY, bodyLeft + 80.0f, volY + 22.0f),
                                  11.5f, 600, theme.textPrimary, false);
                    D2D1_RECT_F vlTrack = D2D1::RectF(bodyLeft + 86.0f, volY + 6.0f, bodyLeft + bodyW - 38.0f, volY + 16.0f);
                    r.FillRoundedRect(vlTrack, 5.0f, Rgba(255, 255, 255, 0.14f));
                    float vlFillW = (vlTrack.right - vlTrack.left) * (state.masterVolume / 100.0f);
                    r.FillRoundedRect(D2D1::RectF(vlTrack.left, vlTrack.top, vlTrack.left + vlFillW, vlTrack.bottom), 5.0f, theme.accent);
                    r.DrawTextStr(std::to_wstring(static_cast<int>(state.masterVolume)) + L"%",
                                  D2D1::RectF(bodyLeft + bodyW - 34.0f, volY, bodyLeft + bodyW, volY + 22.0f),
                                  11.0f, 600, theme.textMuted, true, DWRITE_TEXT_ALIGNMENT_TRAILING);
                    r.AddHitRegion("ctrl-volume", vlTrack, nullptr, [&state](float normX) {
                        SystemServices::SetMasterVolume(state, normX * 100.0f, false);
                    });

                    static const wchar_t* powerNames[] = { L"Power: Eco", L"Power: Balanced", L"Power: Best" };
                    struct ToggleDef {
                        std::wstring label;
                        bool active;
                        std::function<void()> action;
                    };
                    std::array<ToggleDef, 6> toggles = {{
                        { L"Wi-Fi", state.wifiEnabled, [&state]() {
                            state.wifiEnabled = !state.wifiEnabled;
                            ShellExecuteW(nullptr, L"open", L"ms-settings:network-wifi", nullptr, nullptr, SW_SHOWNORMAL);
                        } },
                        { L"Bluetooth", state.bluetoothEnabled, [&state]() {
                            state.bluetoothEnabled = !state.bluetoothEnabled;
                            ShellExecuteW(nullptr, L"open", L"ms-settings:bluetooth", nullptr, nullptr, SW_SHOWNORMAL);
                        } },
                        { L"Night light", state.nightLightEnabled, [&state]() {
                            state.nightLightEnabled = !state.nightLightEnabled;
                            SystemServices::SetNightLightEnabled(state.nightLightEnabled);
                        } },
                        { L"Focus mode", state.focusModeEnabled, [&state]() { state.focusModeEnabled = !state.focusModeEnabled; } },
                        { powerNames[state.powerModeIndex % 3], state.powerModeIndex == 2, [&state]() {
                            state.powerModeIndex = (state.powerModeIndex + 1) % 3;
                        } },
                        { state.globalMicMuted ? L"Mic: Muted" : L"Mic: Live", !state.globalMicMuted, [&state]() {
                            SystemServices::ToggleGlobalMicMute(state);
                        } }
                    }};

                    float btnW = (bodyW - 16.0f) / 3.0f;
                    float btnH = 42.0f;
                    float gridY = bodyTop + 62.0f;
                    for (int i = 0; i < 6; ++i) {
                        float bx = bodyLeft + (i % 3) * (btnW + 8.0f);
                        float by = gridY + (i / 3) * (btnH + 8.0f);
                        D2D1_RECT_F bRect = D2D1::RectF(bx, by, bx + btnW, by + btnH);
                        D2D1_COLOR_F bg = toggles[i].active ? theme.accent : Rgba(255, 255, 255, 0.08f);
                        D2D1_COLOR_F fg = toggles[i].active ? AdaptiveInkForBackground(theme.accent) : theme.textPrimary;
                        r.FillRoundedRect(bRect, 11.0f * s.cornerRadiusScale, bg);
                        r.DrawTextStr(toggles[i].label, bRect, 11.5f, 600, fg, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        r.AddHitRegion("ctrl-tog-" + std::to_string(i), bRect, toggles[i].action);
                    }
                    break;
                }

                case PanelId::Launcher: {
                    // ========================================================
                    // TAB 09: LAUNCHER, CAPTURES & COLOR PICKER (400 x 160 DIP)
                    // Top Row: Screenshot, Snip, Record, Color Picker (Ctrl+Alt+C)
                    // Bottom Row: 6 App Slots ("+ Add" dashed boxes until configured)
                    // ========================================================
                    float topBtnW = (bodyW - 18.0f) / 4.0f;
                    float topBtnH = 36.0f;
                    const wchar_t* capLabels[4] = { L"Screenshot", L"Snip", L"\x25CF Record", L"Colour" };
                    for (int i = 0; i < 4; ++i) {
                        float bx = bodyLeft + i * (topBtnW + 6.0f);
                        D2D1_RECT_F bRect = D2D1::RectF(bx, bodyTop + 2.0f, bx + topBtnW, bodyTop + 2.0f + topBtnH);
                        D2D1_COLOR_F bg = (i == 2 && state.isRecordingScreen)
                            ? Rgba(255, 59, 48, 0.90f)
                            : Rgba(255, 255, 255, 0.10f);
                        r.FillRoundedRect(bRect, 10.0f * s.cornerRadiusScale, bg);
                        r.DrawTextStr(capLabels[i], bRect, 11.0f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);

                        r.AddHitRegion("launch-cap-" + std::to_string(i), bRect, [&state, i]() {
                            if (i == 0) {
                                SystemServices::CaptureFullScreenshot(state);
                            } else if (i == 1) {
                                SystemServices::StartInteractiveSnip(state);
                            } else if (i == 2) {
                                if (state.isRecordingScreen) SystemServices::StopScreenRecording(state);
                                else SystemServices::StartScreenRecording(state);
                            } else {
                                SystemServices::PickScreenColorUnderCursor(state);
                            }
                        });
                    }

                    // Bottom Row: 6 Quick-Launcher Slots (Ctrl+Alt+1 through Ctrl+Alt+6)
                    float slotW = (bodyW - 35.0f) / 6.0f;
                    float slotH = 62.0f;
                    float slotY = bodyTop + 48.0f;
                    for (int i = 0; i < 6; ++i) {
                        float sx = bodyLeft + i * (slotW + 7.0f);
                        D2D1_RECT_F sRect = D2D1::RectF(sx, slotY, sx + slotW, slotY + slotH);
                        bool hov = r.IsHovered(sRect, state.pointerDipX, state.pointerDipY);
                        bool configured = !state.launcherSlots[i].targetPath.empty();

                        r.FillRoundedRect(sRect, 11.0f * s.cornerRadiusScale,
                                          hov ? Rgba(255, 255, 255, 0.14f) : Rgba(255, 255, 255, 0.05f));
                        if (!configured) {
                            r.DrawRoundedRect(sRect, 11.0f * s.cornerRadiusScale, theme.edge, 1.0f);
                            r.DrawTextStr(L"+ Add", sRect, 10.5f, 600, theme.textFaint, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        } else {
                            r.FillCircle(sx + slotW * 0.5f, slotY + 20.0f, 11.0f, state.launcherSlots[i].badgeColor);
                            r.DrawTextStr(std::to_wstring(i + 1),
                                          D2D1::RectF(sx, slotY + 9.0f, sx + slotW, slotY + 31.0f),
                                          11.0f, 700, Rgba(255, 255, 255, 1.0f), true, DWRITE_TEXT_ALIGNMENT_CENTER);
                            r.DrawTextStr(state.launcherSlots[i].label,
                                          D2D1::RectF(sx + 2.0f, slotY + 36.0f, sx + slotW - 2.0f, slotY + 58.0f),
                                          9.5f, 500, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                        }

                        r.AddHitRegion("launch-slot-" + std::to_string(i), sRect, [&state, i]() {
                            SystemServices::ExecuteLauncherSlot(state, i);
                        });
                    }
                    break;
                }

                case PanelId::Calls: {
                    // ========================================================
                    // TAB 10: CALLS & GLOBAL MIC MUTE (400 x 176 DIP)
                    // ========================================================
                    float micCx = bodyLeft + 56.0f;
                    float micCy = bodyTop + 64.0f;
                    D2D1_COLOR_F micCol = state.globalMicMuted ? Rgba(255, 69, 58, 1.0f) : Rgba(48, 209, 88, 1.0f);
                    float ringPulse = state.globalMicMuted ? 42.0f : (42.0f + state.micPeakLevel * 12.0f);
                    r.FillCircle(micCx, micCy, ringPulse, D2D1::ColorF(micCol.r, micCol.g, micCol.b, 0.22f));
                    r.FillCircle(micCx, micCy, 34.0f, micCol);
                    r.DrawLucideIcon("mic", micCx, micCy - 6.0f, 20.0f, Rgba(12, 12, 14, 1.0f), 2.2f);
                    r.DrawTextStr(state.globalMicMuted ? L"Muted" : L"Hearing you",
                                  D2D1::RectF(micCx - 44.0f, micCy + 6.0f, micCx + 44.0f, micCy + 24.0f),
                                  10.0f, 700, Rgba(12, 12, 14, 1.0f), false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.DrawTextStr(L"Ctrl+Alt+M",
                                  D2D1::RectF(micCx - 48.0f, micCy + 38.0f, micCx + 48.0f, micCy + 56.0f),
                                  10.0f, 500, theme.textMuted, true, DWRITE_TEXT_ALIGNMENT_CENTER);

                    D2D1_RECT_F micHit = D2D1::RectF(micCx - 42.0f, micCy - 42.0f, micCx + 42.0f, micCy + 42.0f);
                    r.AddHitRegion("calls-mic-toggle", micHit, [&state]() {
                        SystemServices::ToggleGlobalMicMute(state);
                    });

                    float rx = bodyLeft + 124.0f;
                    float rw = bodyW - 124.0f;
                    D2D1_RECT_F meetCard = D2D1::RectF(rx, bodyTop + 2.0f, rx + rw, bodyTop + 48.0f);
                    r.FillRoundedRect(meetCard, 11.0f * s.cornerRadiusScale, Rgba(255, 255, 255, 0.08f));
                    r.DrawTextStr(s.upcomingMeetingTitle,
                                  D2D1::RectF(rx + 12.0f, bodyTop + 6.0f, rx + rw - 12.0f, bodyTop + 26.0f),
                                  12.0f, 600, theme.textPrimary, false);
                    r.DrawTextStr(s.upcomingMeetingTime,
                                  D2D1::RectF(rx + 12.0f, bodyTop + 26.0f, rx + rw - 12.0f, bodyTop + 44.0f),
                                  10.5f, 400, theme.textMuted, false);

                    D2D1_RECT_F spkBtn = D2D1::RectF(rx, bodyTop + 56.0f, rx + rw * 0.48f, bodyTop + 86.0f);
                    r.FillRoundedRect(spkBtn, 9.0f, Rgba(255, 255, 255, 0.08f));
                    r.DrawTextStr(L"Speakers \x25BE [Test]", spkBtn, 10.5f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.AddHitRegion("calls-spk-test", spkBtn, []() {
                        MessageBeep(MB_ICONASTERISK);
                    });

                    D2D1_RECT_F camBtn = D2D1::RectF(rx + rw * 0.52f, bodyTop + 56.0f, rx + rw, bodyTop + 86.0f);
                    r.FillRoundedRect(camBtn, 9.0f, Rgba(255, 255, 255, 0.08f));
                    r.DrawTextStr(L"Check camera", camBtn, 10.5f, 600, theme.textPrimary, false, DWRITE_TEXT_ALIGNMENT_CENTER);
                    r.AddHitRegion("calls-cam-check", camBtn, []() {
                        ShellExecuteW(nullptr, L"open", L"microsoft.windows.camera:", nullptr, nullptr, SW_SHOWNORMAL);
                    });

                    D2D1_RECT_F meterTrack = D2D1::RectF(rx, bodyTop + 96.0f, rx + rw, bodyTop + 104.0f);
                    r.FillRoundedRect(meterTrack, 4.0f, Rgba(255, 255, 255, 0.12f));
                    float level = state.globalMicMuted ? 0.04f : 0.24f;
                    r.FillRoundedRect(D2D1::RectF(rx, bodyTop + 96.0f, rx + rw * level, bodyTop + 104.0f), 4.0f, micCol);
                    break;
                }

                default:
                    break;
            }
        }

        r.RT()->PopLayer();
        clipLayer->Release();
    }

    D2D1_COLOR_F borderCol = s.higherContrast ? Rgba(255, 255, 255, 0.55f) : theme.edge;
    ID2D1SolidColorBrush* rimBrush = nullptr;
    if (SUCCEEDED(r.RT()->CreateSolidColorBrush(borderCol, &rimBrush)) && rimBrush) {
        r.RT()->DrawGeometry(path, rimBrush, s.higherContrast ? 1.6f : 1.0f);
        rimBrush->Release();
    }

    path->Release();
}

} // namespace np
