using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

namespace OpenPixel.UI
{
    // ========================================================================
    // IslandTabsRenderer (C# .NET 10)
    // - Commits queued slider/theme changes atomically at the start of the frame
    // - Supports Disableable Game Overlay (Off/Hide completely, SlimStrip, Normal)
    // - Includes built-in Calendar in the Island view (0.5.92)
    // - Includes Free Buff coding agent integration & zero-placeholder empty states
    // ========================================================================
    public static class IslandTabsRenderer
    {
        public static string FormatTimeSec(double sec)
        {
            int total = Math.Max(0, (int)sec);
            return $"{total / 60}:{(total % 60):D2}";
        }

        public static void ComputeCurrentIslandGeometry(
            GlobalAppState state, float canvasW,
            out float outLeft, out float outTop, out float outW, out float outH, out float outRadius)
        {
            var s = state.Settings;

            // Disableable Game Overlay Mode Handling
            if (state.IsFullscreenGameActive && !state.IslandOpen && !state.ToastActive)
            {
                if (!s.GameOverlayMasterEnabled || s.GameOverlayMode == GameOverlayMode.OffHideCompletely)
                {
                    outLeft = (canvasW - 10.0f) * 0.5f;
                    outTop = -100.0f;
                    outW = 0.0f;
                    outH = 0.0f;
                    outRadius = 0.0f;
                    return;
                }
                if (s.GameOverlayMode == GameOverlayMode.SlimStrip)
                {
                    outW = 386.0f * (s.PillSizePercent / 100.0f);
                    outH = 22.0f * (s.PillSizePercent / 100.0f);
                    outTop = 0.0f;
                    outLeft = (canvasW - outW) * 0.5f;
                    outRadius = 11.0f * s.CornerRadiusScale;
                    return;
                }
            }

            float rawOpen = Math.Clamp(state.OpenSpring.Value / 100.0f, 0.0f, 1.25f);
            float openT = (rawOpen <= 1.0f)
                ? (0.65f * rawOpen + 0.35f * SpringPresets.CubicEaseOut(rawOpen))
                : rawOpen;
            float notchT = Math.Clamp(state.NotchSpring.Value / 100.0f, 0.0f, 1.0f);
            float recT = Math.Clamp(state.RecSpring.Value / 100.0f, 0.0f, 1.0f);

            float pillBaseW = DipMetrics.PillW + (DipMetrics.RecPillW - DipMetrics.PillW) * recT;
            if (state.ToastActive && !state.IslandOpen)
            {
                pillBaseW = (state.ToastPopupType == TopPopupType.Volume || state.ToastPopupType == TopPopupType.Brightness)
                    ? 248.0f
                    : 296.0f;
            }
            else if (s.ShowSongTitleInClosedPill && state.Media.HasSession && !state.IslandOpen)
            {
                pillBaseW = 258.0f;
            }

            float pillW = pillBaseW * (s.PillSizePercent / 100.0f);
            float pillH = DipMetrics.PillH * (s.PillSizePercent / 100.0f);
            if (state.ToastActive && state.ToastIsReminder && !state.IslandOpen)
            {
                pillH = 44.0f * (s.PillSizePercent / 100.0f);
            }

            float targetCardH = ThemeSpec.GetExpandedHeight(state.ActivePanel);
            float cardW = DipMetrics.CardW * (s.OpenSizePercent / 100.0f);
            float cardH = targetCardH * (s.OpenSizePercent / 100.0f);

            outW = pillW + (cardW - pillW) * openT;
            outH = pillH + (cardH - pillH) * openT;

            float islandGap = DipMetrics.TopOffsetIsland + s.TopEdgeDistanceDip;
            outTop = (notchT <= 0.5f) ? (islandGap * (1.0f - notchT * 2.0f)) : 0.0f;

            if (s.Edge == EdgePlacement.LeftEdge) outLeft = outTop;
            else if (s.Edge == EdgePlacement.RightEdge) outLeft = canvasW - outW - outTop;
            else outLeft = (canvasW - outW) * 0.5f;

            float baseRadius = 18.0f + (36.0f - 18.0f) * Math.Clamp(openT, 0.0f, 1.0f);
            outRadius = Math.Max(4.0f, baseRadius * Math.Clamp(s.CornerRadiusScale, 0.15f, 1.0f));
        }

        public static void RenderIslandFrame(
            LiquidGlassRenderer r, GlobalAppState state,
            Rectangle windowScreenRect, float canvasW, float canvasH, float timeSec,
            Action onOpenSettings, Action<WidgetType> onToggleDesktopWidget)
        {
            // Commit any queued slider/theme updates atomically on the render tick!
            state.CommitPendingParametersOnRenderTick();

            lock (state.SyncRoot)
            {
                r.ClearHitRegions();

                ComputeCurrentIslandGeometry(state, canvasW, out float left, out float top, out float width, out float height, out float radius);
                if (width <= 1.0f || height <= 1.0f) return;

                var s = state.Settings;
                // Cross-faded surface: a theme switch eases in instead of hard-cutting.
                ThemeSpec theme = state.CurrentTheme;
                float notchFactor01 = Math.Clamp(state.NotchSpring.Value / 100.0f, 0.0f, 1.0f);
                float openFactor01 = Math.Clamp(state.OpenSpring.Value / 100.0f, 0.0f, 1.0f);

                using GraphicsPath path = r.CreateIslandOrNotchPath(left, top, width, height, radius, notchFactor01, s.Edge);
                Region prevClip = r.G.Clip;
                r.G.SetClip(path, CombineMode.Replace);

                // 1. Surface Fill: Liquid Glass (Default!) or Candy Glass with safe fallback to Black
                bool drewGlass = false;
                if (!s.DrawWithProcessor && (s.Theme == SurfaceTheme.LiquidGlass || s.Theme == SurfaceTheme.CandyGlass))
                {
                    var subScreen = new Rectangle(
                        windowScreenRect.Left + (int)(left * r.DpiScale),
                        windowScreenRect.Top + (int)(top * r.DpiScale),
                        Math.Max(16, (int)(width * r.DpiScale)),
                        Math.Max(12, (int)(height * r.DpiScale)));

                    drewGlass = r.RenderLiquidGlassBackdrop(
                        subScreen, left, top, width, height,
                        s.GlassBlurRadius, s.Theme == SurfaceTheme.CandyGlass);
                }

                if (!drewGlass)
                {
                    // Automatic graceful fallback to Black (#0B0B0C) if glass capture fails
                    Color surfaceFill = theme.IsGlass ? Color.FromArgb(255, 11, 11, 12) : theme.Fill;
                    if (state.IsFullscreenGameActive && !state.IslandOpen && !s.GameStripShowBackground)
                    {
                        surfaceFill = Color.FromArgb(4, 0, 0, 0);
                    }
                    r.FillRoundedRect(new RectangleF(left - 20, top - 20, width + 40, height + 40), 1.0f, surfaceFill);
                }

                // 2. Full-Bleed Top-Edge Album-Art Color Tinting (when real media plays)
                if (s.CardBackground == CardBackgroundMode.AlbumColours &&
                    state.Media.HasSession &&
                    (!state.IslandOpen || state.ActivePanel == PanelId.Music))
                {
                    r.RenderAlbumBackdrop(state.Media, left, top, width, height);
                }

                // 3. Soft Theme Ambience Particles (each side fades with the cross-fade)
                float themeBlend = state.ThemeBlendProgress01;
                if (themeBlend < 0.995f)
                    r.RenderThemeAmbience(state.ThemeBlendFrom, left, top, width, height, timeSec, 1.0f - themeBlend);
                if (themeBlend > 0.005f)
                    r.RenderThemeAmbience(state.ThemeBlendTo, left, top, width, height, timeSec, themeBlend);

                // 4. Resting Face when closed
                float restingAlpha = Math.Clamp(1.0f - openFactor01 * 2.2f, 0.0f, 1.0f);
                if (restingAlpha > 0.02f)
                {
                    RenderRestingFace(r, state, left, top, width, height, theme, timeSec);
                }

                // 5. Expanded Card Content when open
                float cardAlpha = SpringPresets.CubicEaseInOut(Math.Clamp((openFactor01 - 0.28f) / 0.72f, 0.0f, 1.0f));
                if (cardAlpha > 0.02f)
                {
                    RenderTopTabBar(r, state, left, top, width, theme, onOpenSettings);
                    RenderActiveTabBody(r, state, left, top, width, height, theme, timeSec, onToggleDesktopWidget);
                }

                r.G.Clip = prevClip;

                // Subtle 1 DIP border rim
                Color borderCol = s.HigherContrast ? Color.FromArgb(145, 255, 255, 255) : theme.Edge;
                using var rimPen = new Pen(borderCol, s.HigherContrast ? 1.6f : 1.0f);
                r.G.DrawPath(rimPen, path);
            }
        }

        private static void RenderRestingFace(
            LiquidGlassRenderer r, GlobalAppState state,
            float left, float top, float width, float height, ThemeSpec theme, float timeSec)
        {
            var s = state.Settings;
            float cy = top + height * 0.5f;

            // 1. Fullscreen 10 Hz Game Strip (when Mode == SlimStrip)
            if (state.IsFullscreenGameActive && !state.IslandOpen && !state.ToastActive &&
                s.GameOverlayMasterEnabled && s.GameOverlayMode == GameOverlayMode.SlimStrip)
            {
                string line = "";
                if (s.GameStripShowFps) line += $"FPS {(int)state.Telemetry.Fps}   ";
                if (s.GameStripShowCpu)
                {
                    line += $"CPU {(int)state.Telemetry.CpuPercent}%";
                    if (s.GameStripShowTemps && state.Telemetry.CpuTempAvailable) line += $" {(int)state.Telemetry.CpuTempC}\u00B0";
                    line += "   ";
                }
                if (s.GameStripShowGpu) line += $"GPU {(int)state.Telemetry.GpuPercent}%   ";
                if (s.GameStripShowRam) line += $"RAM {(int)state.Telemetry.RamPercent}%   ";
                if (s.GameStripShowNet)
                {
                    line += s.GameStripNetInMbps
                        ? $"\u2193 {(state.Telemetry.NetDownKbps / 1000.0f):F1} Mbps   "
                        : $"\u2193 {(int)state.Telemetry.NetDownKbps} Kbps   ";
                }
                line += DateTime.Now.ToString("HH:mm");
                r.DrawTextStr(line, new RectangleF(left + 6, top, width - 12, height), 10.5f, true, theme.TextPrimary, mono: true, hAlign: StringAlignment.Center);
                return;
            }

            // 2. Top-Center Pop-Ups (Volume, Brightness, Mic Mute, Color Picker, Free Buff Alert, Reminder)
            if (state.ToastActive)
            {
                if (ConfigManager.GetTickMs() > state.ToastExpireMs && !state.ToastIsReminder)
                {
                    state.ToastActive = false;
                    state.ToastPopupType = TopPopupType.None;
                }
                else
                {
                    if (state.ToastPopupType == TopPopupType.Volume || state.ToastPopupType == TopPopupType.Brightness)
                    {
                        r.DrawLucideIcon(state.ToastPopupType == TopPopupType.Volume ? "volume" : "sun", left + 20, cy, 14, theme.TextPrimary);
                        float trackL = left + 36, trackR = left + width - 48, trackW = Math.Max(20, trackR - trackL);
                        r.FillRoundedRect(new RectangleF(trackL, cy - 3, trackW, 6), 3, Color.FromArgb(46, 255, 255, 255));
                        float fillW = trackW * Math.Clamp(state.ToastProgress01, 0.0f, 1.0f);
                        if (fillW > 2) r.FillRoundedRect(new RectangleF(trackL, cy - 3, fillW, 6), 3, theme.TextPrimary);
                        r.DrawTextStr(state.ToastTitle, new RectangleF(trackR + 4, top, 40, height), 11.0f, true, theme.TextPrimary, mono: true, hAlign: StringAlignment.Far);
                        return;
                    }
                    if (state.ToastPopupType == TopPopupType.MicMute)
                    {
                        Color col = state.GlobalMicMuted ? Color.FromArgb(255, 255, 69, 58) : Color.FromArgb(255, 48, 209, 88);
                        r.DrawLucideIcon("mic", left + 18, cy, 14, col);
                        string timerStr = state.Sensors.MicCallStartMs > 0
                            ? FormatTimeSec((ConfigManager.GetTickMs() - state.Sensors.MicCallStartMs) / 1000.0)
                            : "0:00";
                        r.DrawTextStr(timerStr, new RectangleF(left + 32, top, 80, height), 11.5f, true, theme.TextPrimary, mono: true);
                        r.DrawTextStr(state.ToastTitle, new RectangleF(left + 110, top, width - 124, height), 12.0f, true, col, hAlign: StringAlignment.Far);
                        return;
                    }
                    if (state.ToastIsReminder)
                    {
                        r.FillCircle(left + 16, cy, 4.5f, state.ToastAccent);
                        r.DrawTextStr("\u23F0 " + state.ToastTitle, new RectangleF(left + 26, top + 4, width - 144, height - 8), 12.0f, true, theme.TextPrimary);
                        var snoozeBtn = new RectangleF(left + width - 114, top + 8, 54, height - 16);
                        var doneBtn = new RectangleF(left + width - 54, top + 8, 44, height - 16);
                        r.FillRoundedRect(snoozeBtn, 10, Color.FromArgb(36, 255, 255, 255));
                        r.DrawTextStr("Snooze", snoozeBtn, 10.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        r.FillRoundedRect(doneBtn, 10, Color.FromArgb(235, 48, 209, 88));
                        r.DrawTextStr("Done", doneBtn, 10.5f, true, Color.FromArgb(255, 12, 12, 14), hAlign: StringAlignment.Center);

                        r.AddHitRegion("rem-snooze", snoozeBtn, () =>
                        {
                            foreach (var rem in state.Reminders)
                                if (rem.Id == state.ActiveReminderId) { rem.DueTimestampMs = ConfigManager.GetTickMs() + 600000; rem.Triggered = false; }
                            state.ToastActive = false;
                            state.ToastIsReminder = false;
                        });
                        r.AddHitRegion("rem-done", doneBtn, () =>
                        {
                            foreach (var rem in state.Reminders)
                                if (rem.Id == state.ActiveReminderId) rem.Completed = true;
                            state.ToastActive = false;
                            state.ToastIsReminder = false;
                        });
                        return;
                    }

                    r.FillCircle(left + 16, cy, 5.0f, state.ToastAccent);
                    string full = string.IsNullOrEmpty(state.ToastSubtitle) ? state.ToastTitle : $"{state.ToastTitle} \u00B7 {state.ToastSubtitle}";
                    r.DrawTextStr(full, new RectangleF(left + 28, top + 2, width - 40, height - 4), 11.5f, true, theme.TextPrimary);
                    return;
                }
            }

            // 3. Active Screen Recording Pill (`pill-rec`) or OBS Studio REC Pill
            if (state.IsRecordingScreen || (s.ShowObsRecPill && state.ObsRecordingActive))
            {
                long startMs = state.IsRecordingScreen ? state.RecordingStartMs : state.ObsRecordingStartMs;
                double elapsed = (ConfigManager.GetTickMs() - startMs) / 1000.0;
                r.FillCircle(left + 18, cy, 5.0f, Color.FromArgb(255, 255, 59, 48));
                string label = (state.IsRecordingScreen ? "REC " : "OBS REC ") + FormatTimeSec(elapsed);
                r.DrawTextStr(label, new RectangleF(left + 28, top, width - 92, height), 12.0f, true, theme.TextPrimary, mono: true);
                var stopBtn = new RectangleF(left + width - 58, top + 6, 48, height - 12);
                r.FillRoundedRect(stopBtn, 11, Color.FromArgb(230, 255, 59, 48));
                r.DrawTextStr("Stop", stopBtn, 11.0f, true, Color.White, hAlign: StringAlignment.Center);
                r.AddHitRegion("rec-stop", stopBtn, () =>
                {
                    state.IsRecordingScreen = false;
                    state.ObsRecordingActive = false;
                    state.RecSpring.SetTarget(0.0f, state.Settings.ReduceMotion);
                });
                return;
            }

            // 4. Resting Pill Left Side: Real Media (ONLY if HasSession) or Resting Info Face
            if (state.Media.HasSession && (s.RestingMode == RestingInfoMode.MediaOnly || state.Media.IsPlaying))
            {
                var cov = new RectangleF(left + 12, cy - 12, 24, 24);
                r.FillRoundedRect(cov, Math.Max(3.0f, 6.0f * s.CornerRadiusScale), state.Media.DominantColor);
                r.DrawLucideIcon("music", left + 24, cy, 12, ThemeSpec.AdaptiveInkForBackground(state.Media.DominantColor));
                if (s.ShowSongTitleInClosedPill && !string.IsNullOrEmpty(state.Media.Title))
                {
                    r.DrawTextStr(state.Media.Title, new RectangleF(left + 42, top + 2, width - 86, height - 4), 11.5f, false, theme.TextPrimary, hAlign: StringAlignment.Center);
                }
            }
            else
            {
                DateTime now = DateTime.Now;
                RestingInfoMode mode = s.RestingMode == RestingInfoMode.MediaOnly ? RestingInfoMode.Time : s.RestingMode;
                string infoText = mode switch
                {
                    RestingInfoMode.Time => s.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm tt"),
                    RestingInfoMode.Date => now.ToString("ddd M/d"),
                    RestingInfoMode.Weather => $"{((now.Hour < 6 || now.Hour >= 19) ? "\u263D" : "\u2600")} {s.WeatherTempC}\u00B0",
                    RestingInfoMode.Battery => $"\u26A1 {state.Telemetry.BatteryPercent}%",
                    RestingInfoMode.CpuTemp => $"CPU {(int)state.Telemetry.CpuPercent}% \u00B7 RAM {(int)state.Telemetry.RamPercent}%",
                    RestingInfoMode.NextEvent => s.UpcomingMeetingTitle,
                    RestingInfoMode.PixelPet => ((int)(timeSec * 2) % 2 == 0) ? "(\u2022\u03C9\u2022)" : "(^\u03C9^)",
                    _ => now.ToString("h:mm tt")
                };
                r.DrawTextStr(infoText, new RectangleF(left + 16, top, width - 64, height), 11.5f, true, theme.TextMuted);
            }

            // 5. Privacy Sensor Dots (Mic / Camera / Screen naming active app) & Call Timer
            if (state.Sensors.MicActive || state.Sensors.CamActive || state.Sensors.ScreenActive)
            {
                Color dotCol = state.GlobalMicMuted ? Color.FromArgb(255, 255, 69, 58)
                             : (state.Sensors.CamActive ? Color.FromArgb(255, 255, 159, 10) : Color.FromArgb(255, 48, 209, 88));
                r.FillCircle(left + width - 48, cy, 3.8f, dotCol);
                string app = !string.IsNullOrEmpty(state.Sensors.MicAppName) ? state.Sensors.MicAppName : state.Sensors.CamAppName;
                if (state.Sensors.MicActive && state.Sensors.MicCallStartMs > 0)
                {
                    app += " " + FormatTimeSec((ConfigManager.GetTickMs() - state.Sensors.MicCallStartMs) / 1000.0);
                }
                r.DrawTextStr(app, new RectangleF(left + 40, top, width - 94, height), 10.5f, false, dotCol, mono: true, hAlign: StringAlignment.Far);
            }

            // 6. Right Side: 4-Column Visualizer (animated only when real media plays)
            float visBaseX = left + width - 32.0f;
            Color visCol = state.Media.HasSession ? state.Media.DominantColor : theme.TextFaint;
            for (int col = 0; col < 4; ++col)
            {
                float wave = (state.Media.HasSession && state.Media.IsPlaying)
                    ? (0.30f + 0.70f * MathF.Abs(MathF.Sin(timeSec * (4.2f + col * 0.9f) + col * 1.3f)))
                    : 0.18f;
                float vx = visBaseX + col * 5.0f;
                if (s.Visualizer == VisualizerStyle.Pixels)
                {
                    int blocks = Math.Max(1, (int)MathF.Round(wave * 4.0f));
                    for (int b = 0; b < blocks; ++b)
                        r.FillRoundedRect(new RectangleF(vx, cy + 5.0f - b * 4.0f, 3.0f, 3.0f), 0.4f, visCol);
                }
                else
                {
                    float bh = Math.Max(3.0f, wave * 15.5f);
                    r.FillRoundedRect(new RectangleF(vx, cy - bh * 0.5f, 3.0f, bh), 1.5f, visCol);
                }
            }
        }

        private static void RenderTopTabBar(
            LiquidGlassRenderer r, GlobalAppState state,
            float cardLeft, float cardTop, float cardWidth, ThemeSpec theme, Action onOpenSettings)
        {
            string[] icons = { "music", "globe", "bell", "message-circle", "folder", "clipboard", "layout-grid", "sliders-horizontal", "grip-horizontal", "headphones" };
            var order = state.Settings.PanelOrder;
            int count = order.Count;
            float spacing = 22.0f * (state.Settings.OpenSizePercent / 100.0f);
            float totalW = (count + 1) * spacing;
            float startX = cardLeft + (cardWidth - totalW) * 0.5f + spacing * 0.5f;
            float tabY = cardTop + 14.0f;

            for (int i = 0; i < count; ++i)
            {
                PanelId pid = order[i];
                float cx = startX + i * spacing;
                var tabBox = new RectangleF(cx - 10, tabY - 9, 20, 20);
                bool isActive = state.ActivePanel == pid;
                bool isHov = r.IsHovered(tabBox, state.PointerDipX, state.PointerDipY);

                Color iconCol = isActive ? theme.TextPrimary : (isHov ? theme.TextMuted : theme.TextFaint);
                int idx = Math.Clamp((int)pid, 0, 9);
                r.DrawLucideIcon(icons[idx], cx, tabY, 14.0f, iconCol, isActive ? 2.0f : 1.65f);

                if (isActive)
                {
                    r.FillRoundedRect(new RectangleF(cx - 1.32f, tabY + 8.5f, 2.64f, 2.64f), 0.6f, theme.Accent);
                }
                if (pid == PanelId.Chat && state.Settings.StreamChatEnabled)
                {
                    r.FillCircle(cx + 5.5f, tabY - 5.0f, 2.6f, Color.FromArgb(255, 255, 59, 48));
                }
                if (pid == PanelId.Notifications)
                {
                    bool needsInput = state.Agents.Exists(a => a.NeedsInput);
                    bool almostDone = state.Agents.Exists(a => a.IsAlmostDone);
                    if (needsInput) r.FillCircle(cx + 5.5f, tabY - 5.0f, 2.8f, Color.FromArgb(255, 255, 159, 10));
                    else if (almostDone) r.FillCircle(cx + 5.5f, tabY - 5.0f, 2.8f, Color.FromArgb(255, 48, 209, 88));
                }

                r.AddHitRegion($"tab-{idx}", tabBox, () =>
                {
                    state.ShowCalendarInIsland = false;
                    state.PreviousPanel = state.ActivePanel;
                    state.ActivePanel = pid;
                    state.PanelSpring.Snap(0.0f);
                    state.PanelSpring.SetTarget(100.0f, state.Settings.ReduceMotion);
                });
            }

            float divX = startX + count * spacing - spacing * 0.45f;
            r.DrawLine(divX, tabY - 5.4f, divX, tabY + 5.4f, theme.Edge, 1.0f);

            float gearX = startX + count * spacing + 2.0f;
            var gearBox = new RectangleF(gearX - 10, tabY - 9, 20, 20);
            r.DrawLucideIcon("settings", gearX, tabY, 13.8f, r.IsHovered(gearBox, state.PointerDipX, state.PointerDipY) ? theme.TextPrimary : theme.TextMuted);
            r.AddHitRegion("tab-settings", gearBox, onOpenSettings);
        }

        private static void RenderActiveTabBody(
            LiquidGlassRenderer r, GlobalAppState state,
            float left, float top, float width, float height,
            ThemeSpec theme, float timeSec, Action<WidgetType> onToggleDesktopWidget)
        {
            var s = state.Settings;
            float bodyTop = top + 32.0f;
            float bodyLeft = left + 18.0f;
            float bodyW = width - 36.0f;

            switch (state.ActivePanel)
            {
                case PanelId.Music:
                {
                    float covSize = state.Media.HasSession ? (state.Media.IsPlaying ? 74.0f : 64.0f) : 64.0f;
                    float covCx = left + 22.0f + 37.0f;
                    float covCy = top + 32.0f + 37.0f;
                    var covRect = new RectangleF(covCx - covSize * 0.5f, covCy - covSize * 0.5f, covSize, covSize);
                    Color covCol = state.Media.HasSession ? state.Media.DominantColor : Color.FromArgb(18, 255, 255, 255);
                    r.FillRoundedRect(covRect, 14.0f * s.CornerRadiusScale, covCol);
                    r.DrawLucideIcon("music", covCx, covCy, 24.0f, state.Media.HasSession ? ThemeSpec.AdaptiveInkForBackground(covCol) : theme.TextFaint);

                    var palBtn = new RectangleF(left + width - 42, top + 32, 24, 24);
                    r.FillRoundedRect(palBtn, 8, Color.FromArgb(26, 255, 255, 255));
                    r.DrawLucideIcon("palette", palBtn.X + 12, palBtn.Y + 12, 13, theme.TextPrimary);
                    r.AddHitRegion("music-pal", palBtn, () =>
                    {
                        s.CardBackground = s.CardBackground == CardBackgroundMode.AlbumColours ? CardBackgroundMode.Plain : CardBackgroundMode.AlbumColours;
                        ConfigManager.SaveAtomic(state);
                    });

                    float metaLeft = left + 110.0f;
                    float metaW = (left + width - 48.0f) - metaLeft;
                    string dispTitle = state.Media.HasSession ? state.Media.Title : "Nothing playing";
                    string dispSub = state.Media.HasSession ? $"{state.Media.Artist} \u00B7 {state.Media.SourceAppName}" : "Play music or video in any app or browser";
                    r.DrawTextStr(dispTitle, new RectangleF(metaLeft, top + 33, metaW, 21), 15.5f, true, state.Media.HasSession ? theme.TextPrimary : theme.TextMuted);
                    r.DrawTextStr(dispSub, new RectangleF(metaLeft, top + 54, metaW, 18), 12.0f, false, theme.TextFaint);

                    r.DrawTextStr(FormatTimeSec(state.Media.PositionSec), new RectangleF(metaLeft, top + 78, 36, 16), 11.0f, false, theme.TextFaint, mono: true);
                    r.DrawTextStr(FormatTimeSec(state.Media.DurationSec), new RectangleF(left + width - 54, top + 78, 36, 16), 11.0f, false, theme.TextFaint, mono: true, hAlign: StringAlignment.Far);

                    // Scrub progress bar
                    float barLeft = metaLeft + 38.0f;
                    float barW = (left + width - 58.0f) - barLeft;
                    float prog = (state.Media.HasSession && state.Media.DurationSec > 0.1) ? (float)(state.Media.PositionSec / state.Media.DurationSec) : 0.0f;
                    RenderProgressBar(r, state, barLeft, top + 86.0f, barW, prog, theme, timeSec);

                    // Transport & Real CoreAudio Volume Slider
                    float transY = top + 125.0f;
                    float cx = left + width * 0.44f;
                    var prevBtn = new RectangleF(cx - 54, transY - 16, 36, 32);
                    var playBtn = new RectangleF(cx - 18, transY - 18, 36, 36);
                    var nextBtn = new RectangleF(cx + 18, transY - 16, 36, 32);
                    r.DrawLucideIcon("skip-back", cx - 36, transY, 15, theme.TextPrimary);
                    r.DrawLucideIcon(state.Media.IsPlaying ? "pause" : "play", cx, transY, 18, theme.TextPrimary);
                    r.DrawLucideIcon("skip-forward", cx + 36, transY, 15, theme.TextPrimary);
                    r.AddHitRegion("m-prev", prevBtn, () => WindowsSystemHooks.MediaPreviousTrack(state));
                    r.AddHitRegion("m-play", playBtn, () => WindowsSystemHooks.MediaTogglePlayPause(state));
                    r.AddHitRegion("m-next", nextBtn, () => WindowsSystemHooks.MediaNextTrack(state));

                    float volX = left + width - 102.0f;
                    r.DrawLucideIcon("volume", volX, transY, 13, state.MasterOutputMuted ? Color.FromArgb(255, 255, 69, 58) : theme.TextMuted);
                    var volTrack = new RectangleF(volX + 12, transY - 8, (left + width - 18) - (volX + 12), 16);
                    r.FillRoundedRect(new RectangleF(volTrack.X, transY - 2, volTrack.Width, 4), 2, Color.FromArgb(46, 255, 255, 255));
                    r.FillRoundedRect(new RectangleF(volTrack.X, transY - 2, volTrack.Width * (state.MasterVolume / 100.0f), 4), 2, theme.TextPrimary);
                    r.AddHitRegion("m-vol", volTrack, null, normX => WindowsSystemHooks.SetMasterVolume(state, normX * 100.0f, showPopup: false));
                    break;
                }

                case PanelId.Web:
                {
                    if (state.ShowDeckInIsland)
                    {
                        r.DrawTextStr("PC Deck \u00B7 Local Encrypted Wi-Fi Remote", new RectangleF(bodyLeft, bodyTop, 260, 20), 12.0f, true, theme.TextPrimary);
                        var backWeb = new RectangleF(bodyLeft + bodyW - 64, bodyTop, 64, 20);
                        r.FillRoundedRect(backWeb, 8, Color.FromArgb(24, 255, 255, 255));
                        r.DrawTextStr("Web", backWeb, 10.5f, true, theme.TextMuted, hAlign: StringAlignment.Center);
                        r.AddHitRegion("deck-to-web", backWeb, () => state.ShowDeckInIsland = false);

                        if (!s.PcDeckEnabled)
                        {
                            var box = new RectangleF(bodyLeft, bodyTop + 28, bodyW, 110);
                            r.FillRoundedRect(box, 12 * s.CornerRadiusScale, Color.FromArgb(18, 255, 255, 255));
                            r.DrawTextStr(state.IsNetworkPrivate
                                ? "PC Deck is off.\nTurn on to pair your Android phone over Private Wi-Fi."
                                : "Windows network is Public.\nSwitch to Private Wi-Fi to enable PC Deck.",
                                new RectangleF(bodyLeft + 14, bodyTop + 36, bodyW - 28, 46), 11.0f, false, theme.TextMuted, hAlign: StringAlignment.Center, noWrap: false);
                            var turnOnBtn = new RectangleF(bodyLeft + 110, bodyTop + 92, bodyW - 220, 32);
                            r.FillRoundedRect(turnOnBtn, 16, theme.Accent);
                            r.DrawTextStr("Turn on", turnOnBtn, 12.0f, true, ThemeSpec.AdaptiveInkForBackground(theme.Accent), hAlign: StringAlignment.Center);
                            r.AddHitRegion("deck-turn-on", turnOnBtn, () =>
                            {
                                if (LocalNetworkServers.DetectPrivateNetworkAndLocalIp(state))
                                {
                                    s.PcDeckEnabled = true;
                                    LocalNetworkServers.GenerateTwoMinutePairingCode(state);
                                    ConfigManager.SaveAtomic(state);
                                }
                            });
                        }
                        else
                        {
                            string qrPayload = LocalNetworkServers.BuildDeckPairingQrPayload(state);
                            r.DrawQrCode(qrPayload, bodyLeft + 4, bodyTop + 28, 98, Color.FromArgb(255, 12, 12, 14), Color.FromArgb(255, 239, 237, 232));
                            r.DrawTextStr($"Code: {state.ActivePairingCode} (2 min)", new RectangleF(bodyLeft + 114, bodyTop + 28, bodyW - 114, 20), 12.5f, true, theme.Accent, mono: true);
                            r.DrawTextStr($"Paired phones: {state.PairedDeckDevices.Count} \u00B7 {state.LocalPrivateIp}:{s.PcDeckTlsPort}", new RectangleF(bodyLeft + 114, bodyTop + 50, bodyW - 114, 18), 10.5f, false, theme.TextMuted);
                            if (state.PairedDeckDevices.Count > 0)
                            {
                                var dev = state.PairedDeckDevices[0];
                                r.DrawTextStr($"{dev.Model} ({dev.OsVersion}) \u00B7 {dev.KeyFingerprint}", new RectangleF(bodyLeft + 114, bodyTop + 70, bodyW - 114, 16), 9.5f, false, theme.TextFaint, mono: true);
                            }
                            var addBtn = new RectangleF(bodyLeft + 114, bodyTop + 94, 96, 28);
                            r.FillRoundedRect(addBtn, 10, Color.FromArgb(26, 255, 255, 255));
                            r.DrawTextStr("Add phone", addBtn, 10.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                            r.AddHitRegion("deck-add-phone", addBtn, () => LocalNetworkServers.GenerateTwoMinutePairingCode(state));

                            var offBtn = new RectangleF(bodyLeft + 218, bodyTop + 94, 80, 28);
                            r.FillRoundedRect(offBtn, 10, Color.FromArgb(52, 255, 69, 58));
                            r.DrawTextStr("Turn off", offBtn, 10.5f, true, Color.FromArgb(255, 255, 105, 97), hAlign: StringAlignment.Center);
                            r.AddHitRegion("deck-turn-off", offBtn, () => { s.PcDeckEnabled = false; ConfigManager.SaveAtomic(state); });
                        }
                        break;
                    }

                    r.DrawTextStr("Pinned sites \u00B7 Web Drawer & PC Deck", new RectangleF(bodyLeft, bodyTop, 220, 20), 11.5f, false, theme.TextMuted);
                    var deckTabBtn = new RectangleF(bodyLeft + bodyW - 88, bodyTop - 2, 88, 22);
                    r.FillRoundedRect(deckTabBtn, 10, s.PcDeckEnabled ? Color.FromArgb(255, 48, 209, 88) : Color.FromArgb(26, 255, 255, 255));
                    r.DrawTextStr(s.PcDeckEnabled ? "\u25CF PC Deck" : "PC Deck", deckTabBtn, 10.5f, true,
                        s.PcDeckEnabled ? Color.FromArgb(255, 12, 12, 14) : theme.TextPrimary, hAlign: StringAlignment.Center);
                    r.AddHitRegion("web-to-deck", deckTabBtn, () => state.ShowDeckInIsland = true);
                    float tileW = (bodyW - 16.0f) / 3.0f;
                    float rowY = bodyTop + 26.0f;
                    for (int i = 0; i < Math.Min(3, state.PinnedSites.Count); ++i)
                    {
                        var site = state.PinnedSites[i];
                        float tx = bodyLeft + i * (tileW + 8.0f);
                        var tile = new RectangleF(tx, rowY, tileW, 74);
                        r.FillRoundedRect(tile, 12.0f * s.CornerRadiusScale, Color.FromArgb(22, 255, 255, 255));
                        r.FillCircle(tx + tileW * 0.5f, rowY + 24, 13, site.BadgeColor);
                        r.DrawTextStr(site.Title.Substring(0, 1), new RectangleF(tx, rowY + 11, tileW, 26), 13.0f, true, Color.White, hAlign: StringAlignment.Center);
                        r.DrawTextStr(site.Title, new RectangleF(tx + 6, rowY + 44, tileW - 12, 22), 12.0f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        int capIdx = i;
                        r.AddHitRegion($"web-{i}", tile, () =>
                        {
                            state.CurrentWebDrawerTitle = state.PinnedSites[capIdx].Title;
                            state.CurrentWebDrawerUrl = state.PinnedSites[capIdx].Url;
                            state.WebDrawerVisible = true;
                            AuxiliaryWindows.UpdateDrawers(state);
                        });
                    }
                    var toggleDrawer = new RectangleF(bodyLeft, rowY + 86, bodyW, 34);
                    r.FillRoundedRect(toggleDrawer, 10, state.WebDrawerVisible ? theme.Accent : Color.FromArgb(18, 255, 255, 255));
                    r.DrawTextStr(state.WebDrawerVisible ? "Close Web Drawer" : "Web Drawer Dormant (0 MB WebView footprint)", toggleDrawer, 11.5f, true,
                        state.WebDrawerVisible ? ThemeSpec.AdaptiveInkForBackground(theme.Accent) : theme.TextMuted, hAlign: StringAlignment.Center);
                    r.AddHitRegion("web-toggle", toggleDrawer, () =>
                    {
                        state.WebDrawerVisible = !state.WebDrawerVisible;
                        AuxiliaryWindows.UpdateDrawers(state);
                    });
                    break;
                }

                case PanelId.Notifications:
                {
                    r.DrawTextStr("Notifications (100% Local)", new RectangleF(bodyLeft, bodyTop, 180, 18), 12.0f, true, theme.TextPrimary);
                    float ny = bodyTop + 21.0f;
                    if (state.Notifications.Count == 0)
                    {
                        var emptyBox = new RectangleF(bodyLeft, ny, bodyW, 52);
                        r.FillRoundedRect(emptyBox, 10 * s.CornerRadiusScale, Color.FromArgb(14, 255, 255, 255));
                        r.DrawTextStr("No notifications yet \u00B7 Stays 100% local on this PC", emptyBox, 11.0f, false, theme.TextFaint, hAlign: StringAlignment.Center);
                    }
                    else
                    {
                        for (int i = 0; i < Math.Min(2, state.Notifications.Count); ++i)
                        {
                            var n = state.Notifications[i];
                            var card = new RectangleF(bodyLeft, ny, bodyW, 36);
                            r.FillRoundedRect(card, 10 * s.CornerRadiusScale, Color.FromArgb(20, 255, 255, 255));
                            r.FillCircle(bodyLeft + 12, ny + 18, 5, n.AccentColor);
                            r.DrawTextStr($"{n.AppName} \u00B7 {n.Title}", new RectangleF(bodyLeft + 24, ny + 2, bodyW - 76, 16), 11.0f, true, theme.TextPrimary);
                            r.DrawTextStr(n.Body, new RectangleF(bodyLeft + 24, ny + 18, bodyW - 34, 16), 10.5f, false, theme.TextMuted);
                            r.DrawTextStr(s.ClockTimestampsInNotifications ? n.TimeStr : n.RelativeStr, new RectangleF(bodyLeft + bodyW - 56, ny + 2, 48, 16), 10.0f, false, theme.TextFaint, mono: true, hAlign: StringAlignment.Far);
                            ny += 39.0f;
                        }
                    }

                    // AI Coding Agents (First-class Free Buff + Claude Code, Codex, Antigravity, Hermes)
                    float ay = bodyTop + 102.0f;
                    r.DrawTextStr("AI Agents (Free Buff \u00B7 Claude Code \u00B7 Codex \u00B7 Antigravity \u00B7 Hermes)", new RectangleF(bodyLeft, ay, bodyW, 16), 10.5f, true, theme.TextPrimary);
                    ay += 19.0f;
                    if (state.Agents.Count == 0)
                    {
                        var idleBox = new RectangleF(bodyLeft, ay, bodyW, 64);
                        r.FillRoundedRect(idleBox, 10 * s.CornerRadiusScale, Color.FromArgb(14, 255, 255, 255));
                        r.DrawTextStr("No active agent sessions detected on this PC\nWatching Free Buff, Claude Code, Codex, Antigravity & Hermes",
                            idleBox, 10.5f, false, theme.TextFaint, hAlign: StringAlignment.Center, noWrap: false);
                    }
                    else
                    {
                        float colW = (bodyW - 8.0f) * 0.5f;
                        for (int i = 0; i < Math.Min(4, state.Agents.Count); ++i)
                        {
                            var ag = state.Agents[i];
                            float ax = bodyLeft + (i % 2) * (colW + 8.0f);
                            float rowTop = ay + (i / 2) * 34.0f;
                            var aCard = new RectangleF(ax, rowTop, colW, 30);
                            Color bg = ag.NeedsInput ? Color.FromArgb(62, 255, 159, 10)
                                     : (ag.IsAlmostDone ? Color.FromArgb(52, 48, 209, 88) : Color.FromArgb(18, 255, 255, 255));
                            r.FillRoundedRect(aCard, 8, bg);
                            Color dot = ag.NeedsInput ? Color.FromArgb(255, 255, 159, 10)
                                      : (ag.IsAlmostDone ? Color.FromArgb(255, 48, 209, 88) : Color.FromArgb(255, 78, 179, 255));
                            r.FillCircle(ax + 10, rowTop + 15, 3.8f, dot);
                            string stLabel = ag.ProgressPercent >= 0 ? $"{ag.Status} ({ag.ProgressPercent}%)" : ag.Status;
                            r.DrawTextStr($"{ag.AgentType} \u00B7 {stLabel}", new RectangleF(ax + 18, rowTop + 2, colW - 22, 13), 10.0f, true, theme.TextPrimary);
                            r.DrawTextStr(ag.CurrentStep, new RectangleF(ax + 18, rowTop + 15, colW - 22, 13), 9.5f, false, theme.TextMuted);
                        }
                    }
                    break;
                }

                case PanelId.Chat:
                {
                    var box = new RectangleF(bodyLeft, bodyTop + 4, bodyW, 52);
                    r.FillRoundedRect(box, 12 * s.CornerRadiusScale, Color.FromArgb(20, 255, 255, 255));
                    r.DrawTextStr(s.StreamChatEnabled ? "\u25CF Stream chat overlay active" : "Chat overlay is stopped.",
                        new RectangleF(bodyLeft + 14, bodyTop + 10, bodyW - 110, 22), 12.5f, true,
                        s.StreamChatEnabled ? Color.FromArgb(255, 48, 209, 88) : theme.TextPrimary);
                    r.DrawTextStr("Movable Twitch & YouTube chat drawer + OBS REC pill",
                        new RectangleF(bodyLeft + 14, bodyTop + 32, bodyW - 110, 18), 11.0f, false, theme.TextMuted);

                    var startBtn = new RectangleF(bodyLeft + bodyW - 88, bodyTop + 14, 76, 32);
                    r.FillRoundedRect(startBtn, 16, s.StreamChatEnabled ? Color.FromArgb(230, 255, 69, 58) : theme.Accent);
                    r.DrawTextStr(s.StreamChatEnabled ? "\u25A0 Stop" : "\u25B6 Start", startBtn, 12.0f, true,
                        s.StreamChatEnabled ? Color.White : ThemeSpec.AdaptiveInkForBackground(theme.Accent), hAlign: StringAlignment.Center);
                    r.AddHitRegion("chat-toggle", startBtn, () =>
                    {
                        s.StreamChatEnabled = !s.StreamChatEnabled;
                        state.ChatDrawerVisible = s.StreamChatEnabled;
                        AuxiliaryWindows.UpdateDrawers(state);
                        ConfigManager.SaveAtomic(state);
                    });

                    var obsBtn = new RectangleF(bodyLeft, bodyTop + 66, bodyW, 32);
                    r.FillRoundedRect(obsBtn, 10, state.ObsRecordingActive ? Color.FromArgb(64, 255, 59, 48) : Color.FromArgb(20, 255, 255, 255));
                    r.DrawTextStr(state.ObsRecordingActive ? "\u25CF OBS REC Pill Active (Click to stop)" : "OBS Studio REC Pill: Ready",
                        obsBtn, 11.5f, true, state.ObsRecordingActive ? Color.FromArgb(255, 255, 89, 78) : theme.TextMuted, hAlign: StringAlignment.Center);
                    r.AddHitRegion("chat-obs", obsBtn, () =>
                    {
                        state.ObsRecordingActive = !state.ObsRecordingActive;
                        if (state.ObsRecordingActive) state.ObsRecordingStartMs = ConfigManager.GetTickMs();
                    });
                    break;
                }

                case PanelId.Shelf:
                {
                    if (state.SendToPhoneQrActive)
                    {
                        r.DrawQrCode(state.SendToPhoneUrl, bodyLeft + 4, bodyTop + 2, 102, Color.FromArgb(255, 12, 12, 14), Color.FromArgb(255, 239, 237, 232));
                        r.DrawTextStr("Scan with your phone camera", new RectangleF(bodyLeft + 118, bodyTop + 6, bodyW - 118, 22), 13.5f, true, theme.TextPrimary);
                        r.DrawTextStr("Same Wi-Fi \u00B7 No account or app needed", new RectangleF(bodyLeft + 118, bodyTop + 28, bodyW - 118, 18), 11.5f, false, theme.TextMuted);
                        r.DrawTextStr(state.SendToPhoneUrl, new RectangleF(bodyLeft + 118, bodyTop + 50, bodyW - 118, 20), 10.5f, false, theme.Accent, mono: true);
                        var backBtn = new RectangleF(bodyLeft + 118, bodyTop + 76, 100, 28);
                        r.FillRoundedRect(backBtn, 12, Color.FromArgb(30, 255, 255, 255));
                        r.DrawTextStr("\u2190 Back", backBtn, 11.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        r.AddHitRegion("shelf-back", backBtn, () => state.SendToPhoneQrActive = false);
                    }
                    else
                    {
                        r.DrawTextStr($"Shelf \u00B7 {state.ShelfFiles.Count} / 12 files", new RectangleF(bodyLeft, bodyTop, 140, 24), 12.5f, true, theme.TextPrimary);
                        var phoneBtn = new RectangleF(bodyLeft + bodyW - 148, bodyTop, 112, 24);
                        r.FillRoundedRect(phoneBtn, 12, theme.Accent);
                        r.DrawTextStr("Send to phone", phoneBtn, 11.0f, true, ThemeSpec.AdaptiveInkForBackground(theme.Accent), hAlign: StringAlignment.Center);
                        r.AddHitRegion("shelf-phone", phoneBtn, () =>
                        {
                            state.SendToPhoneUrl = $"http://{state.LocalPrivateIp}:{s.PcDeckPort}/shelf";
                            state.SendToPhoneQrActive = true;
                        });

                        var clrBtn = new RectangleF(bodyLeft + bodyW - 30, bodyTop, 30, 24);
                        r.FillRoundedRect(clrBtn, 8, Color.FromArgb(20, 255, 255, 255));
                        r.DrawTextStr("\u00D7", clrBtn, 14.0f, true, theme.TextMuted, hAlign: StringAlignment.Center);
                        r.AddHitRegion("shelf-clr", clrBtn, () => state.ShelfFiles.Clear());

                        float fy = bodyTop + 30.0f;
                        if (state.ShelfFiles.Count == 0)
                        {
                            var dropZone = new RectangleF(bodyLeft, fy, bodyW, 76);
                            r.FillRoundedRect(dropZone, 12 * s.CornerRadiusScale, Color.FromArgb(14, 255, 255, 255));
                            r.DrawRoundedRect(dropZone, 12 * s.CornerRadiusScale, theme.Edge, 1.0f);
                            r.DrawTextStr("Drop up to 12 files here to hold, drag out, or send to your phone", dropZone, 11.5f, false, theme.TextFaint, hAlign: StringAlignment.Center);
                        }
                        else
                        {
                            for (int i = 0; i < Math.Min(5, state.ShelfFiles.Count); ++i)
                            {
                                var f = state.ShelfFiles[i];
                                float fx = bodyLeft + i * 74.0f;
                                var fCard = new RectangleF(fx, fy, 68, 76);
                                r.FillRoundedRect(fCard, 10 * s.CornerRadiusScale, Color.FromArgb(22, 255, 255, 255));
                                r.DrawLucideIcon("folder", fx + 34, fy + 26, 20, theme.Accent);
                                r.DrawTextStr(f.DisplayName, new RectangleF(fx + 4, fy + 50, 60, 22), 10.0f, false, theme.TextPrimary, hAlign: StringAlignment.Center);
                            }
                        }
                    }
                    break;
                }

                case PanelId.Clipboard:
                {
                    string[] chips = { "All", "Text", "Links", "Images", "Files", "Snippets" };
                    float chipX = bodyLeft;
                    for (int c = 0; c < 6; ++c)
                    {
                        float cw = (c == 0) ? 38.0f : (c == 5 ? 64.0f : 50.0f);
                        var chip = new RectangleF(chipX, bodyTop, cw, 22);
                        bool act = (int)state.ActiveClipCat == c;
                        r.FillRoundedRect(chip, 11, act ? theme.Accent : Color.FromArgb(20, 255, 255, 255));
                        r.DrawTextStr(chips[c], chip, 10.5f, true, act ? ThemeSpec.AdaptiveInkForBackground(theme.Accent) : theme.TextMuted, hAlign: StringAlignment.Center);
                        int capC = c;
                        r.AddHitRegion($"clip-c-{c}", chip, () => state.ActiveClipCat = (ClipboardCategory)capC);
                        chipX += cw + 5.0f;
                    }

                    float ry = bodyTop + 28.0f;
                    int shown = 0;
                    for (int i = 0; i < state.ClipboardHistory.Count && shown < 3; ++i)
                    {
                        var item = state.ClipboardHistory[i];
                        if (state.ActiveClipCat != ClipboardCategory.All && item.Category != state.ActiveClipCat) continue;
                        var row = new RectangleF(bodyLeft, ry, bodyW, 36);
                        r.FillRoundedRect(row, 9 * s.CornerRadiusScale, Color.FromArgb(20, 255, 255, 255));
                        string badge = item.IsCurrent ? "CURRENT" : (item.IsPinned ? "PINNED" : item.SourceInfo);
                        r.DrawTextStr(badge, new RectangleF(bodyLeft + 10, ry + 3, 90, 13), 9.0f, true, item.IsCurrent ? Color.FromArgb(255, 48, 209, 88) : theme.Accent, mono: true);
                        r.DrawTextStr(item.Preview, new RectangleF(bodyLeft + 10, ry + 16, bodyW - 86, 18), 11.5f, false, theme.TextPrimary);

                        var pinBtn = new RectangleF(bodyLeft + bodyW - 70, ry + 6, 62, 24);
                        r.FillRoundedRect(pinBtn, 7, Color.FromArgb(26, 255, 255, 255));
                        r.DrawTextStr(item.IsPinned ? "Unpin" : "Pin", pinBtn, 10.0f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        int capI = i;
                        r.AddHitRegion($"clip-pin-{i}", pinBtn, () => state.ClipboardHistory[capI].IsPinned = !state.ClipboardHistory[capI].IsPinned);
                        r.AddHitRegion($"clip-row-{i}", row, () =>
                        {
                            foreach (var e in state.ClipboardHistory) e.IsCurrent = false;
                            state.ClipboardHistory[capI].IsCurrent = true;
                            try { Clipboard.SetText(state.ClipboardHistory[capI].Text); } catch { }
                        });
                        ry += 40.0f;
                        ++shown;
                    }
                    if (shown == 0)
                    {
                        var emptyClip = new RectangleF(bodyLeft, ry, bodyW, 110);
                        r.FillRoundedRect(emptyClip, 12 * s.CornerRadiusScale, Color.FromArgb(14, 255, 255, 255));
                        r.DrawTextStr("Nothing copied yet \u00B7 Press Ctrl+C in any app\nStored 100% locally on this PC", emptyClip, 11.5f, false, theme.TextFaint, hAlign: StringAlignment.Center, noWrap: false);
                    }
                    break;
                }

                case PanelId.Widgets:
                {
                    // ========================================================
                    // TAB 07: WIDGETS & BUILT-IN CALENDAR IN THE ISLAND (0.5.92)
                    // ========================================================
                    if (state.ShowCalendarInIsland)
                    {
                        DateTime now = DateTime.Now.AddMonths(state.CalendarMonthOffset);
                        r.DrawTextStr(now.ToString("MMMM yyyy") + " \u00B7 Calendar in the Island",
                            new RectangleF(bodyLeft, bodyTop, 230, 22), 13.0f, true, theme.TextPrimary);

                        var backBtn = new RectangleF(bodyLeft + bodyW - 84, bodyTop, 84, 22);
                        r.FillRoundedRect(backBtn, 8, Color.FromArgb(26, 255, 255, 255));
                        r.DrawTextStr("\u2190 Widgets", backBtn, 10.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        r.AddHitRegion("cal-back", backBtn, () => state.ShowCalendarInIsland = false);

                        // 7-Column Calendar Month Grid + Upcoming Events/Reminders on Right
                        float gridLeft = bodyLeft;
                        float cellW = 28.0f;
                        float cellH = 18.0f;
                        string[] dow = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
                        for (int d = 0; d < 7; ++d)
                        {
                            r.DrawTextStr(dow[d], new RectangleF(gridLeft + d * cellW, bodyTop + 26, cellW, 16), 9.5f, true, theme.TextFaint, mono: true, hAlign: StringAlignment.Center);
                        }

                        DateTime firstDay = new(now.Year, now.Month, 1);
                        int startDow = (int)firstDay.DayOfWeek;
                        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
                        int todayDay = (state.CalendarMonthOffset == 0) ? DateTime.Now.Day : -1;

                        for (int day = 1; day <= daysInMonth; ++day)
                        {
                            int slot = startDow + day - 1;
                            int col = slot % 7;
                            int row = slot / 7;
                            if (row >= 6) break;
                            var cRect = new RectangleF(gridLeft + col * cellW + 2, bodyTop + 44 + row * cellH, cellW - 4, cellH - 2);
                            bool isToday = day == todayDay;
                            if (isToday)
                            {
                                r.FillRoundedRect(cRect, 6, theme.Accent);
                            }
                            r.DrawTextStr(day.ToString(), cRect, 9.5f, isToday,
                                isToday ? ThemeSpec.AdaptiveInkForBackground(theme.Accent) : theme.TextPrimary,
                                mono: true, hAlign: StringAlignment.Center);
                        }

                        // Right Column: Upcoming Reminders & Events + Desktop Day Clock Pin
                        float rightX = bodyLeft + 204.0f;
                        float rightW = bodyW - 204.0f;
                        var evtCard = new RectangleF(rightX, bodyTop + 28, rightW, 68);
                        r.FillRoundedRect(evtCard, 10, Color.FromArgb(20, 255, 255, 255));
                        r.DrawTextStr("Upcoming & Reminders", new RectangleF(rightX + 10, bodyTop + 32, rightW - 20, 18), 11.0f, true, theme.Accent);
                        string evtLine = state.Reminders.Count > 0 ? $"\u23F0 {state.Reminders[0].Title}" : s.UpcomingMeetingTitle;
                        r.DrawTextStr(evtLine, new RectangleF(rightX + 10, bodyTop + 52, rightW - 20, 36), 10.5f, false, theme.TextMuted, noWrap: false);

                        var pinDayClockBtn = new RectangleF(rightX, bodyTop + 104, rightW, 36);
                        r.FillRoundedRect(pinDayClockBtn, 10, theme.Accent);
                        r.DrawTextStr("Pin Day Clock to Desktop", pinDayClockBtn, 10.5f, true, ThemeSpec.AdaptiveInkForBackground(theme.Accent), hAlign: StringAlignment.Center);
                        r.AddHitRegion("cal-pin-dayclock", pinDayClockBtn, () => onToggleDesktopWidget?.Invoke(WidgetType.DayClock));
                        break;
                    }

                    float tileW = (bodyW - 10.0f) * 0.5f;
                    float tileH = 68.0f;
                    DateTime dt = DateTime.Now;

                    // Tile 0: Day Clock / World Clock & Calendar in the Island trigger
                    var t0 = new RectangleF(bodyLeft, bodyTop, tileW, tileH);
                    r.FillRoundedRect(t0, 12 * s.CornerRadiusScale, Color.FromArgb(22, 255, 255, 255));
                    string clkStr = s.Use24HourClock ? dt.ToString("HH:mm") : dt.ToString("h:mm tt");
                    r.DrawTextStr(clkStr, new RectangleF(t0.X + 12, t0.Y + 8, t0.Width - 24, 26), 16.5f, true, theme.TextPrimary, mono: true);
                    r.DrawTextStr($"{dt:ddd d MMM} \u00B7 Open Calendar \u25B8", new RectangleF(t0.X + 12, t0.Y + 38, t0.Width - 24, 22), 10.5f, true, theme.Accent);
                    r.AddHitRegion("w-cal-open", t0, () => state.ShowCalendarInIsland = true);

                    // Tile 1: Desktop Day Clock & To-Do Widget Pin
                    var t1 = new RectangleF(bodyLeft + tileW + 10, bodyTop, tileW, tileH);
                    r.FillRoundedRect(t1, 12 * s.CornerRadiusScale, Color.FromArgb(22, 255, 255, 255));
                    r.DrawTextStr("Day Clock & To-Do", new RectangleF(t1.X + 12, t1.Y + 8, t1.Width - 24, 20), 12.0f, true, theme.TextPrimary);
                    r.DrawTextStr("Click to pin Day Clock to desktop \u2197", new RectangleF(t1.X + 12, t1.Y + 34, t1.Width - 24, 26), 10.5f, false, theme.TextMuted);
                    r.AddHitRegion("w-dayclock", t1, () => onToggleDesktopWidget?.Invoke(WidgetType.DayClock));

                    // Tile 2: Minimal Weather (moon icon at night)
                    float row2Y = bodyTop + tileH + 10.0f;
                    var t2 = new RectangleF(bodyLeft, row2Y, tileW, tileH);
                    r.FillRoundedRect(t2, 12 * s.CornerRadiusScale, Color.FromArgb(22, 255, 255, 255));
                    bool isNight = dt.Hour < 6 || dt.Hour >= 19;
                    r.DrawTextStr($"{(isNight ? "\u263D Clear" : "\u2600 Clear")} {s.WeatherTempC}\u00B0 {s.WeatherCity}", new RectangleF(t2.X + 12, t2.Y + 10, t2.Width - 24, 24), 13.0f, true, theme.TextPrimary);
                    r.DrawTextStr($"H {s.WeatherHighC}\u00B0  L {s.WeatherLowC}\u00B0 \u00B7 Pin \u2197", new RectangleF(t2.X + 12, t2.Y + 38, t2.Width - 24, 22), 11.0f, false, theme.TextMuted);
                    r.AddHitRegion("w-weather", t2, () => onToggleDesktopWidget?.Invoke(WidgetType.MinimalWeather));

                    // Tile 3: Real PC Stats (CPU, RAM, Storage bar — no fake motherboard temps!)
                    var t3 = new RectangleF(bodyLeft + tileW + 10, row2Y, tileW, tileH);
                    r.FillRoundedRect(t3, 12 * s.CornerRadiusScale, Color.FromArgb(22, 255, 255, 255));
                    r.DrawTextStr($"CPU {(int)state.Telemetry.CpuPercent}% \u00B7 RAM {(int)state.Telemetry.RamPercent}%", new RectangleF(t3.X + 12, t3.Y + 10, t3.Width - 24, 22), 12.0f, true, theme.TextPrimary, mono: true);
                    float diskFrac = Math.Clamp(state.Telemetry.StoragePercent / 100.0f, 0.05f, 1.0f);
                    r.FillRoundedRect(new RectangleF(t3.X + 12, t3.Y + 42, tileW - 24, 8), 4, Color.FromArgb(36, 255, 255, 255));
                    r.FillRoundedRect(new RectangleF(t3.X + 12, t3.Y + 42, (tileW - 24) * diskFrac, 8), 4, Color.FromArgb(255, 48, 209, 88));
                    r.AddHitRegion("w-stats", t3, () => onToggleDesktopWidget?.Invoke(WidgetType.PcStats));

                    // Central Bell Button (Fast Reminder)
                    float bellCx = bodyLeft + bodyW * 0.5f;
                    float bellCy = bodyTop + tileH + 5.0f;
                    r.FillCircle(bellCx, bellCy, 15, theme.Accent);
                    r.DrawLucideIcon("bell", bellCx, bellCy, 14, ThemeSpec.AdaptiveInkForBackground(theme.Accent));
                    r.AddHitRegion("w-bell", new RectangleF(bellCx - 16, bellCy - 16, 32, 32), () =>
                    {
                        var rem = new ReminderItem { Id = ConfigManager.GetTickMs(), Title = "Quick Reminder", DueTimestampMs = ConfigManager.GetTickMs() + 1000 };
                        state.Reminders.Add(rem);
                        state.ActiveReminderId = rem.Id;
                        state.ToastPopupType = TopPopupType.Reminder;
                        state.ToastTitle = rem.Title;
                        state.ToastIsReminder = true;
                        state.ToastActive = true;
                        state.ToastAccent = Color.FromArgb(255, 255, 159, 10);
                        state.ToastExpireMs = ConfigManager.GetTickMs() + 60000;
                    });
                    break;
                }

                case PanelId.Controls:
                {
                    r.DrawTextStr("Brightness", new RectangleF(bodyLeft, bodyTop, 80, 22), 11.5f, true, theme.TextPrimary);
                    var brTrack = new RectangleF(bodyLeft + 86, bodyTop + 6, bodyW - 124, 10);
                    r.FillRoundedRect(brTrack, 5, Color.FromArgb(36, 255, 255, 255));
                    r.FillRoundedRect(new RectangleF(brTrack.X, brTrack.Y, brTrack.Width * (state.MonitorBrightness / 100.0f), 10), 5, theme.Accent);
                    r.DrawTextStr($"{(int)state.MonitorBrightness}%", new RectangleF(bodyLeft + bodyW - 36, bodyTop, 36, 22), 11.0f, true, theme.TextMuted, mono: true, hAlign: StringAlignment.Far);
                    r.AddHitRegion("c-br", new RectangleF(brTrack.X, bodyTop, brTrack.Width, 22), null, n => WindowsSystemHooks.SetMonitorBrightnessLevel(state, n * 100.0f, showPopup: false));

                    float volY = bodyTop + 30.0f;
                    r.DrawTextStr("Volume", new RectangleF(bodyLeft, volY, 80, 22), 11.5f, true, theme.TextPrimary);
                    var vlTrack = new RectangleF(bodyLeft + 86, volY + 6, bodyW - 124, 10);
                    r.FillRoundedRect(vlTrack, 5, Color.FromArgb(36, 255, 255, 255));
                    r.FillRoundedRect(new RectangleF(vlTrack.X, vlTrack.Y, vlTrack.Width * (state.MasterVolume / 100.0f), 10), 5, theme.Accent);
                    r.DrawTextStr($"{(int)state.MasterVolume}%", new RectangleF(bodyLeft + bodyW - 36, volY, 36, 22), 11.0f, true, theme.TextMuted, mono: true, hAlign: StringAlignment.Far);
                    r.AddHitRegion("c-vl", new RectangleF(vlTrack.X, volY, vlTrack.Width, 22), null, n => WindowsSystemHooks.SetMasterVolume(state, n * 100.0f, showPopup: false));

                    string[] powerNames = { "Power: Eco", "Power: Balanced", "Power: Best" };
                    (string label, bool active, Action act)[] toggles =
                    {
                        ("Wi-Fi", state.WifiEnabled, () => state.WifiEnabled = !state.WifiEnabled),
                        ("Bluetooth", state.BluetoothEnabled, () => state.BluetoothEnabled = !state.BluetoothEnabled),
                        ("Night light", state.NightLightEnabled, () => { state.NightLightEnabled = !state.NightLightEnabled; WindowsSystemHooks.SetNightLightEnabled(state.NightLightEnabled); }),
                        ("Focus mode", state.FocusModeEnabled, () => state.FocusModeEnabled = !state.FocusModeEnabled),
                        (powerNames[state.PowerModeIndex % 3], state.PowerModeIndex == 2, () => state.PowerModeIndex = (state.PowerModeIndex + 1) % 3),
                        (state.GlobalMicMuted ? "Mic: Muted" : "Mic: Live", !state.GlobalMicMuted, () => WindowsSystemHooks.ToggleGlobalMicMute(state))
                    };

                    float btnW = (bodyW - 16.0f) / 3.0f;
                    float gridY = bodyTop + 62.0f;
                    for (int i = 0; i < 6; ++i)
                    {
                        float bx = bodyLeft + (i % 3) * (btnW + 8.0f);
                        float by = gridY + (i / 3) * 50.0f;
                        var bRect = new RectangleF(bx, by, btnW, 42);
                        Color bg = toggles[i].active ? theme.Accent : Color.FromArgb(20, 255, 255, 255);
                        Color fg = toggles[i].active ? ThemeSpec.AdaptiveInkForBackground(theme.Accent) : theme.TextPrimary;
                        r.FillRoundedRect(bRect, 11 * s.CornerRadiusScale, bg);
                        r.DrawTextStr(toggles[i].label, bRect, 11.5f, true, fg, hAlign: StringAlignment.Center);
                        r.AddHitRegion($"c-t-{i}", bRect, toggles[i].act);
                    }
                    break;
                }

                case PanelId.Launcher:
                {
                    float topBtnW = (bodyW - 18.0f) / 4.0f;
                    string[] capLabels = { "Screenshot", "Snip", "\u25CF Record", "Colour" };
                    for (int i = 0; i < 4; ++i)
                    {
                        float bx = bodyLeft + i * (topBtnW + 6.0f);
                        var bRect = new RectangleF(bx, bodyTop + 2, topBtnW, 36);
                        Color bg = (i == 2 && state.IsRecordingScreen) ? Color.FromArgb(230, 255, 59, 48) : Color.FromArgb(26, 255, 255, 255);
                        r.FillRoundedRect(bRect, 10 * s.CornerRadiusScale, bg);
                        r.DrawTextStr(capLabels[i], bRect, 11.0f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        int capI = i;
                        r.AddHitRegion($"l-cap-{i}", bRect, () =>
                        {
                            if (capI == 0) WindowsSystemHooks.CaptureFullScreenshot(state);
                            else if (capI == 1) try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); } catch { }
                            else if (capI == 2)
                            {
                                state.IsRecordingScreen = !state.IsRecordingScreen;
                                state.RecordingStartMs = ConfigManager.GetTickMs();
                                state.RecSpring.SetTarget(state.IsRecordingScreen ? 100.0f : 0.0f, s.ReduceMotion);
                            }
                            else WindowsSystemHooks.PickScreenColorUnderCursor(state);
                        });
                    }

                    float slotW = (bodyW - 35.0f) / 6.0f;
                    float slotY = bodyTop + 48.0f;
                    for (int i = 0; i < 6; ++i)
                    {
                        float sx = bodyLeft + i * (slotW + 7.0f);
                        var sRect = new RectangleF(sx, slotY, slotW, 62);
                        bool configured = !string.IsNullOrEmpty(state.LauncherSlots[i].TargetPath);
                        r.FillRoundedRect(sRect, 11 * s.CornerRadiusScale, Color.FromArgb(16, 255, 255, 255));
                        if (!configured)
                        {
                            r.DrawRoundedRect(sRect, 11 * s.CornerRadiusScale, theme.Edge, 1.0f);
                            r.DrawTextStr("+ Add", sRect, 10.5f, true, theme.TextFaint, hAlign: StringAlignment.Center);
                        }
                        else
                        {
                            r.FillCircle(sx + slotW * 0.5f, slotY + 20, 11, state.LauncherSlots[i].BadgeColor);
                            r.DrawTextStr((i + 1).ToString(), new RectangleF(sx, slotY + 9, slotW, 22), 11.0f, true, Color.White, mono: true, hAlign: StringAlignment.Center);
                            r.DrawTextStr(state.LauncherSlots[i].Label, new RectangleF(sx + 2, slotY + 36, slotW - 4, 22), 9.5f, false, theme.TextPrimary, hAlign: StringAlignment.Center);
                        }
                    }
                    break;
                }

                case PanelId.Calls:
                {
                    float micCx = bodyLeft + 56.0f;
                    float micCy = bodyTop + 64.0f;
                    Color micCol = state.GlobalMicMuted ? Color.FromArgb(255, 255, 69, 58) : Color.FromArgb(255, 48, 209, 88);
                    r.FillCircle(micCx, micCy, 42, Color.FromArgb(56, micCol.R, micCol.G, micCol.B));
                    r.FillCircle(micCx, micCy, 34, micCol);
                    r.DrawLucideIcon("mic", micCx, micCy - 6, 20, Color.FromArgb(255, 12, 12, 14), 2.2f);
                    r.DrawTextStr(state.GlobalMicMuted ? "Muted" : "Hearing you", new RectangleF(micCx - 44, micCy + 6, 88, 18), 10.0f, true, Color.FromArgb(255, 12, 12, 14), hAlign: StringAlignment.Center);
                    r.DrawTextStr("Ctrl+Alt+M", new RectangleF(micCx - 48, micCy + 38, 96, 18), 10.0f, false, theme.TextMuted, mono: true, hAlign: StringAlignment.Center);
                    r.AddHitRegion("calls-mic", new RectangleF(micCx - 42, micCy - 42, 84, 84), () => WindowsSystemHooks.ToggleGlobalMicMute(state));

                    float rx = bodyLeft + 124.0f;
                    float rw = bodyW - 124.0f;
                    var meetCard = new RectangleF(rx, bodyTop + 2, rw, 46);
                    r.FillRoundedRect(meetCard, 11 * s.CornerRadiusScale, Color.FromArgb(20, 255, 255, 255));
                    r.DrawTextStr(s.UpcomingMeetingTitle, new RectangleF(rx + 12, bodyTop + 6, rw - 24, 20), 12.0f, true, theme.TextPrimary);
                    r.DrawTextStr(s.UpcomingMeetingTime, new RectangleF(rx + 12, bodyTop + 26, rw - 24, 18), 10.5f, false, theme.TextMuted);

                    var spkBtn = new RectangleF(rx, bodyTop + 56, rw * 0.48f, 30);
                    r.FillRoundedRect(spkBtn, 9, Color.FromArgb(20, 255, 255, 255));
                    r.DrawTextStr("Speakers \u25BE [Test]", spkBtn, 10.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                    r.AddHitRegion("calls-spk", spkBtn, () => System.Media.SystemSounds.Asterisk.Play());

                    var camBtn = new RectangleF(rx + rw * 0.52f, bodyTop + 56, rw * 0.48f, 30);
                    r.FillRoundedRect(camBtn, 9, Color.FromArgb(20, 255, 255, 255));
                    r.DrawTextStr("Check camera", camBtn, 10.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                    r.AddHitRegion("calls-cam", camBtn, () => { try { Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true }); } catch { } });
                    break;
                }
            }
        }

        private static void RenderProgressBar(
            LiquidGlassRenderer r, GlobalAppState state,
            float barLeft, float barY, float barWidth, float progress01, ThemeSpec theme, float timeSec)
        {
            progress01 = Math.Clamp(progress01, 0.0f, 1.0f);
            float headX = barLeft + barWidth * progress01;
            var hitBox = new RectangleF(barLeft - 4, barY - 10, barWidth + 8, 20);
            r.AddHitRegion("music-scrub", hitBox, null, normX => state.Media.PositionSec = normX * Math.Max(1.0, state.Media.DurationSec));

            Color accent = state.Media.HasSession ? state.Media.DominantColor : theme.Accent;
            Color unplayed = Color.FromArgb(46, 239, 237, 232);

            switch (state.Settings.ProgressBar)
            {
                case OpenPixel.Core.ProgressBarStyle.Wave:
                    r.DrawLine(headX, barY, barLeft + barWidth, barY, unplayed, 2.5f);
                    r.DrawLine(barLeft, barY, headX, barY + MathF.Sin(timeSec * 5.0f) * (state.Media.IsPlaying ? 2.0f : 0.2f), accent, 2.5f);
                    r.FillRoundedRect(new RectangleF(headX - 2, barY - 6, 4, 12), 2, theme.TextPrimary);
                    break;
                case OpenPixel.Core.ProgressBarStyle.Pixels:
                    for (float x = barLeft; x < barLeft + barWidth - 2; x += 4.5f)
                        r.FillRoundedRect(new RectangleF(x, barY - 1.5f, 3, 3), 0.4f, x <= headX ? accent : unplayed);
                    break;
                case OpenPixel.Core.ProgressBarStyle.Comet:
                    r.FillRoundedRect(new RectangleF(barLeft, barY - 2, barWidth, 4), 2, unplayed);
                    r.FillRoundedRect(new RectangleF(barLeft, barY - 2, Math.Max(4, headX - barLeft), 4), 2, accent);
                    r.FillCircle(headX, barY, 4.5f, Color.White);
                    break;
                case OpenPixel.Core.ProgressBarStyle.SongShape:
                    for (int i = 0; i < 40; ++i)
                    {
                        float bx = barLeft + i * (barWidth / 40.0f);
                        float bh = 4.0f + MathF.Abs(MathF.Sin(i * 0.45f)) * 8.0f;
                        r.FillRoundedRect(new RectangleF(bx, barY - bh * 0.5f, 2.4f, bh), 1.2f, bx <= headX ? accent : unplayed);
                    }
                    break;
                case OpenPixel.Core.ProgressBarStyle.Hearts:
                    for (int i = 0; i < 16; ++i)
                    {
                        float hx = barLeft + (i + 0.5f) * (barWidth / 16.0f);
                        r.DrawLucideIcon(hx <= headX ? "heart-filled" : "heart", hx, barY, 8.5f, hx <= headX ? accent : unplayed);
                    }
                    break;
                default:
                    r.FillRoundedRect(new RectangleF(barLeft, barY - 2, barWidth, 4), 2, unplayed);
                    r.FillRoundedRect(new RectangleF(barLeft, barY - 2, Math.Max(4, headX - barLeft), 4), 2, accent);
                    break;
            }
        }
    }
}
