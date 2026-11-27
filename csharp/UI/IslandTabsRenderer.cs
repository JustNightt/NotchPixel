using System;
using System.Collections.Generic;
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

            // The demo drives width/height with the open/close spring (preserving its
            // subtle overshoot on open) and lets OPEN — our FadeSpring, stepped with
            // the fade spec — carry radius and cross-fade.
            float openT = Math.Clamp(state.OpenSpring.Value / 100.0f, 0.0f, 1.15f);
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

            // If ActivePanel was mutated directly outside SelectPanel (e.g. drag-drop,
            // snapshots, or tests) while PanelSpring is settled at 100, sync ShownPanel.
            if (state.ActivePanel != state.ShownPanel && state.PanelSpring.Target > 50.0f && state.PanelSpring.IsSettled())
            {
                state.ShownPanel = state.ActivePanel;
            }

            // The hero demo keeps one 400 DIP card for every tab, and resizes height
            // smoothly via SPRING.close once the outgoing panel fades out and ShownPanel swaps.
            float targetCardW = DipMetrics.CardW;
            PanelId shownPanel = state.ShownPanel;
            float targetCardH = ThemeSpec.GetExpandedHeight(shownPanel);
            if (shownPanel == PanelId.Music && state.ShowLyricsInIsland)
            {
                targetCardH += 76.0f;
            }
            // The shelf never grows into a second row: it shows five tiles with a pager
            // chevron, so its card stays 156 DIP however many files it holds.
            state.CardWidthSpring.SetTarget(targetCardW, s.ReduceMotion);
            state.CardHeightSpring.SetTarget(targetCardH, s.ReduceMotion);

            float cardW = Math.Max(DipMetrics.PillW, state.CardWidthSpring.Value) * (s.OpenSizePercent / 100.0f);
            float cardH = Math.Max(DipMetrics.PillH, state.CardHeightSpring.Value) * (s.OpenSizePercent / 100.0f);

            outW = pillW + (cardW - pillW) * openT;
            outH = pillH + (cardH - pillH) * openT;

            float islandGap = DipMetrics.TopOffsetIsland + s.TopEdgeDistanceDip;
            outTop = (notchT <= 0.5f) ? (islandGap * (1.0f - notchT * 2.0f)) : 0.0f;

            if (s.Edge == EdgePlacement.LeftEdge) outLeft = outTop;
            else if (s.Edge == EdgePlacement.RightEdge) outLeft = canvasW - outW - outTop;
            else outLeft = (canvasW - outW) * 0.5f;

            float kRadius = Math.Clamp(state.FadeSpring.Value / 100.0f, 0.0f, 1.0f);
            float baseRadius = 18.0f + (36.0f - 18.0f) * kRadius;
            outRadius = Math.Min(outH * 0.5f, Math.Max(4.0f, baseRadius * Math.Clamp(s.CornerRadiusScale, 0.15f, 1.0f)));
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
                r.Opacity = 1.0f;
                r.TextScaleMultiplier = 1.0f;
                r.ClearHitRegions();

                ComputeCurrentIslandGeometry(state, canvasW, out float left, out float top, out float width, out float height, out float radius);
                if (width <= 1.0f || height <= 1.0f) return;

                var s = state.Settings;
                float openScaleT = Math.Clamp(state.OpenSpring.Value / 100.0f, 0.0f, 1.0f);
                float openTextScale = 1.0f + (s.OpenSizePercent / 100.0f - 1.0f) * openScaleT;
                r.TextScaleMultiplier = Math.Clamp((s.PillSizePercent / 100.0f) * openTextScale, 0.65f, 2.1f);
                // Cross-faded surface: a theme switch eases in instead of hard-cutting.
                ThemeSpec theme = state.CurrentTheme;
                float notchFactor01 = Math.Clamp(state.NotchSpring.Value / 100.0f, 0.0f, 1.0f);
                float openFactor01 = Math.Clamp(state.FadeSpring.Value / 100.0f, 0.0f, 1.0f);

                using GraphicsPath path = r.CreateIslandOrNotchPath(left, top, width, height, radius, notchFactor01, s.Edge);
                Region prevClip = r.G.Clip;
                r.G.SetClip(path, CombineMode.Replace);

                // 1. Surface Fill: Exact Kyant0/AndroidLiquidGlass C# port covering the full
                // flared notch silhouette (including both left & right top ears so no seam shows).
                bool drewGlass = false;
                if (!s.DrawWithProcessor && (s.Theme == SurfaceTheme.LiquidGlass || s.Theme == SurfaceTheme.CandyGlass))
                {
                    float flare = (s.Edge == EdgePlacement.TopCenter && notchFactor01 > 0.5f)
                        ? DipMetrics.FlareR * ((notchFactor01 - 0.5f) * 2.0f) + 2.0f
                        : 0.0f;
                    float glassL = left - flare;
                    float glassW = width + flare * 2.0f;
                    var subScreen = new Rectangle(
                        windowScreenRect.Left + (int)(glassL * r.DpiScale),
                        windowScreenRect.Top + (int)(top * r.DpiScale),
                        Math.Max(16, (int)(glassW * r.DpiScale)),
                        Math.Max(12, (int)(height * r.DpiScale)));
                    drewGlass = r.RenderLiquidGlassBackdrop(
                        subScreen, glassL, top, glassW, height,
                        s.GlassBlurRadius, s.Theme == SurfaceTheme.CandyGlass);
                    if (drewGlass)
                    {
                        // Subtle top-only specular highlight (no milky side fade)
                        var sheenRect = new RectangleF(glassL, top, glassW, Math.Max(8.0f, height * 0.24f));
                        using var sheen = new LinearGradientBrush(sheenRect,
                            Color.FromArgb(20, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90.0f);
                        r.G.FillRectangle(sheen, sheenRect);
                    }
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
                // Only inside the open music card, fading with it exactly as the hero demo's
                // ambience does (expandedA * eased.bgK). The resting pill stays a plain surface.
                if (s.CardBackground == CardBackgroundMode.AlbumColours &&
                    state.Media.HasSession && state.IslandOpen && state.ShownPanel == PanelId.Music)
                {
                    float kAmb = Math.Clamp(state.FadeSpring.Value / 100.0f, 0.0f, 1.0f);
                    float ambA = Math.Clamp((kAmb - 0.3f) / 0.5f, 0.0f, 1.0f)
                               * Math.Clamp(state.PanelSpring.Value / 100.0f, 0.0f, 1.0f)
                               * state.AlbumBgEase;
                    r.RenderAlbumBackdrop(state.Media, left, top, width, height, ambA);
                }

                // 3. Soft Theme Ambience Particles (each side fades with the cross-fade)
                float themeBlend = state.ThemeBlendProgress01;
                if (themeBlend < 0.995f)
                    r.RenderThemeAmbience(state.ThemeBlendFrom, left, top, width, height, timeSec, 1.0f - themeBlend);
                if (themeBlend > 0.005f)
                    r.RenderThemeAmbience(state.ThemeBlendTo, left, top, width, height, timeSec, themeBlend);

                // 4. Resting Face when closed
                float restingAlpha = Math.Clamp(1.0f - openFactor01 / 0.3f, 0.0f, 1.0f);
                if (restingAlpha > 0.02f)
                {
                    r.Opacity = restingAlpha;
                    RenderRestingFace(r, state, left, top, width, height, theme, timeSec);
                    r.Opacity = 1.0f;
                }

                // 5. Expanded Card Content when open
                float cardAlpha = Math.Clamp((openFactor01 - 0.3f) / 0.5f, 0.0f, 1.0f)
                                  * Math.Clamp(state.PanelSpring.Value / 100.0f, 0.0f, 1.0f);
                if (cardAlpha > 0.02f)
                {
                    r.Opacity = cardAlpha;
                    RenderTopTabBar(r, state, left, top, width, theme, onOpenSettings);
                    RenderActiveTabBody(r, state, left, top, width, height, theme, timeSec, onToggleDesktopWidget);
                    r.Opacity = 1.0f;
                }

                // Subtle 1 DIP inner hairline clipped inside the outline (matches island.js line 522)
                Color borderCol = s.HigherContrast ? Color.FromArgb(145, 255, 255, 255) : theme.Edge;
                using var rimPen = new Pen(borderCol, s.HigherContrast ? 2.6f : 2.0f);
                r.G.DrawPath(rimPen, path);

                r.G.Clip = prevClip;
            }
        }

        private static float EaseHover(GlobalAppState state, string key, bool hovered)
        {
            float target = hovered ? 1.0f : 0.0f;
            state.HoverTargets[key] = target;
            if (!state.HoverAlphas.TryGetValue(key, out float val))
            {
                state.HoverAlphas[key] = target;
                return target;
            }
            return val;
        }

        private static Color LerpCol(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0.0f, 1.0f);
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static float BackOut(float u)
        {
            float x = u - 1.0f;
            return 1.0f + 2.1f * x * x * x + 1.1f * x * x;
        }

        private static float FlipAngleDeg(GlobalAppState state, float timeSec)
        {
            if (state.Settings.ReduceMotion) return 0.0f;
            float t = timeSec - state.SwapAtSec;
            if (t < 0.0f || t >= 0.44f) return 0.0f;
            if (t < 0.18f)
            {
                float u = t / 0.18f;
                return state.SwapDir * 90.0f * u * u;
            }
            return -state.SwapDir * 90.0f * (1.0f - BackOut((t - 0.18f) / 0.26f));
        }

        private static void ComputeSwapShift(GlobalAppState state, float timeSec, out float shiftY, out float fade)
        {
            if (state.Settings.ReduceMotion) { shiftY = 0.0f; fade = 1.0f; return; }
            float t = timeSec - state.SwapAtSec;
            if (t >= 0.0f && t < 0.20f)
            {
                float u = t / 0.20f;
                shiftY = -10.0f * u * u;
                fade = 1.0f - u;
                return;
            }
            if (t >= 0.20f && t < 0.52f)
            {
                float u = (t - 0.20f) / 0.32f;
                shiftY = 12.0f * (1.0f - BackOut(u));
                fade = u;
                return;
            }
            shiftY = 0.0f;
            fade = 1.0f;
        }

        private static float KickNudge(float kickSec, float timeSec, bool reduced)
        {
            if (reduced) return 0.0f;
            float t = timeSec - kickSec;
            if (t < 0.0f || t > 0.45f) return 0.0f;
            return t < 0.06f ? (t / 0.06f) : MathF.Exp(-(t - 0.06f) / 0.09f) * MathF.Cos((t - 0.06f) * 18.0f);
        }

        private static Color AlbumArtistTint(Color dom, Color fallback)
        {
            float r = dom.R / 255.0f, g = dom.G / 255.0f, b = dom.B / 255.0f;
            float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
            float d = mx - mn;
            if (d < 0.04f) return fallback;
            float l = (mx + mn) * 0.5f;
            float s = l > 0.5f ? d / (2.0f - mx - mn) : d / (mx + mn);
            if (s < 0.12f) return fallback;
            float h = mx == r ? (g - b) / d + (g < b ? 6.0f : 0.0f)
                    : mx == g ? (b - r) / d + 2.0f
                    : (r - g) / d + 4.0f;
            h *= 60.0f;
            // Match island.js tintOf(): text at lightness 0.72, saturation boosted 1.1x
            float ns = Math.Clamp(s * 1.1f, 0.0f, 1.0f);
            float nl = 0.72f;
            float c = (1.0f - MathF.Abs(2.0f * nl - 1.0f)) * ns;
            float hp = ((h % 360.0f) + 360.0f) % 360.0f / 60.0f;
            float x = c * (1.0f - MathF.Abs(hp % 2.0f - 1.0f));
            float rr, gg, bb;
            if (hp < 1) { rr = c; gg = x; bb = 0; }
            else if (hp < 2) { rr = x; gg = c; bb = 0; }
            else if (hp < 3) { rr = 0; gg = c; bb = x; }
            else if (hp < 4) { rr = 0; gg = x; bb = c; }
            else if (hp < 5) { rr = x; gg = 0; bb = c; }
            else { rr = c; gg = 0; bb = x; }
            float m = nl - c * 0.5f;
            return Color.FromArgb(255,
                Math.Clamp((int)MathF.Round((rr + m) * 255.0f), 0, 255),
                Math.Clamp((int)MathF.Round((gg + m) * 255.0f), 0, 255),
                Math.Clamp((int)MathF.Round((bb + m) * 255.0f), 0, 255));
        }

        // Renders the active media square with smooth color-shifting gradient and
        // soft radial album glow while playing, settling cleanly when paused or idle.
        private static void RenderAnimatedMediaSquare(
            LiquidGlassRenderer r, GlobalAppState state, RectangleF rect, float radius,
            float playT, float timeSec, bool isResting)
        {
            var media = state.Media;
            Color dom = media.HasSession ? media.DominantColor : Color.FromArgb(255, 78, 179, 255);
            Color sec = media.HasSession ? media.SecondaryColor : Color.FromArgb(255, 26, 122, 240);
            float phase = (0.5f + 0.5f * MathF.Sin(timeSec * 2.4f)) * playT;
            float angle = (timeSec * 48.0f * playT + 35.0f) % 360.0f;
            Color c1 = LerpCol(dom, sec, phase);
            Color c2 = LerpCol(sec, Color.FromArgb(255, Math.Min(255, dom.R + 46), Math.Min(255, dom.G + 36), Math.Min(255, dom.B + 56)), 0.35f + 0.5f * phase);

            // Soft radial glow behind the expanded cover (matches island.js blur(22px) halo)
            if (!isResting && media.HasSession)
            {
                float glow = 0.55f * playT * state.AlbumBgEase * r.Opacity;
                if (glow > 0.01f)
                {
                    var halo = new RectangleF(rect.X - 28.0f, rect.Y - 28.0f, rect.Width + 56.0f, rect.Height + 56.0f);
                    using var gp = new GraphicsPath();
                    gp.AddEllipse(halo);
                    using var pgb = new PathGradientBrush(gp)
                    {
                        CenterColor = Color.FromArgb((int)Math.Clamp(glow * 145.0f, 0, 255), c1.R, c1.G, c1.B),
                        SurroundColors = new[] { Color.FromArgb(0, c1.R, c1.G, c1.B) }
                    };
                    r.G.FillPath(pgb, gp);
                }
            }

            if (media.HasSession && media.AlbumArt != null)
            {
                try
                {
                    using var covClip = r.CreateIslandOrNotchPath(rect.X, rect.Y, rect.Width, rect.Height, radius, 0.0f, EdgePlacement.TopCenter);
                    Region prevArtClip = r.G.Clip;
                    r.G.SetClip(covClip, CombineMode.Intersect);
                    r.G.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    r.G.DrawImage(media.AlbumArt, rect);
                    r.G.Clip = prevArtClip;
                    return;
                }
                catch { }
            }

            if (media.HasSession)
            {
                using var path = r.CreateIslandOrNotchPath(rect.X, rect.Y, rect.Width, rect.Height, radius, 0.0f, EdgePlacement.TopCenter);
                using var grad = new LinearGradientBrush(rect, c1, c2, angle);
                r.G.FillPath(grad, path);
                r.DrawLucideIcon("music", rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f,
                    isResting ? 12.0f : 24.0f, ThemeSpec.AdaptiveInkForBackground(c1));
            }
            else
            {
                r.FillRoundedRect(rect, radius, Color.FromArgb(18, 255, 255, 255));
                r.DrawLucideIcon("music", rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f,
                    isResting ? 12.0f : 24.0f, Color.FromArgb(135, 255, 255, 255));
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
                    if (state.IsRecordingScreen) WindowsSystemHooks.StopScreenRecording(state);
                    state.ObsRecordingActive = false;
                    state.RecSpring.SetTarget(0.0f, state.Settings.ReduceMotion);
                });
                return;
            }

            // 4. Live Smooth Voice Wave Mode: when unmuted mic detects speech and ShowVoiceWaveBar is on,
            // smoothly replace the resting music icon + bars with a silky 60fps curved voice wave tinted
            // in the active music's dominant color (or theme accent).
            if (s.ShowVoiceWaveBar && !state.GlobalMicMuted && state.MicPeakSmoothed > 0.022f)
            {
                float voiceAmp = Math.Clamp(MathF.Pow(state.MicPeakSmoothed * 2.4f, 0.70f), 0.10f, 1.0f);
                Color waveCol = state.Media.HasSession ? state.Media.DominantColor : theme.Accent;
                float padX = 16.0f;
                float waveW = Math.Max(48.0f, width - padX * 2.0f);
                float waveL = left + padX;
                int segs = 32;
                var pts1 = new PointF[segs + 1];
                var pts2 = new PointF[segs + 1];
                for (int k = 0; k <= segs; ++k)
                {
                    float u = k / (float)segs;
                    float env = MathF.Sin(u * MathF.PI);
                    env = env * env; // silky bell envelope tapered smoothly to 0 at both ends
                    float s1 = MathF.Sin(u * MathF.PI * 3.0f - timeSec * 5.2f) * 0.65f
                             + MathF.Sin(u * MathF.PI * 5.0f + timeSec * 3.4f) * 0.35f;
                    float s2 = MathF.Cos(u * MathF.PI * 2.5f + timeSec * 4.1f) * 0.70f
                             + MathF.Sin(u * MathF.PI * 4.5f - timeSec * 2.8f) * 0.30f;
                    float maxH = Math.Min(height * 0.38f, 11.5f);
                    pts1[k] = new PointF(waveL + u * waveW, cy + s1 * env * voiceAmp * maxH);
                    pts2[k] = new PointF(waveL + u * waveW, cy + s2 * env * voiceAmp * maxH * 0.72f);
                }

                // Soft outer ambient glow + secondary harmonic wave + bright silky primary ribbon
                using var glowPen = new Pen(Color.FromArgb((int)(90 * r.Opacity * voiceAmp), waveCol.R, waveCol.G, waveCol.B), 5.5f)
                {
                    StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
                };
                using var secPen = new Pen(Color.FromArgb((int)(125 * r.Opacity), waveCol.R, waveCol.G, waveCol.B), 2.2f)
                {
                    StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
                };
                using var priPen = new Pen(Color.FromArgb((int)(238 * r.Opacity),
                    Math.Min(255, waveCol.R + 70), Math.Min(255, waveCol.G + 70), Math.Min(255, waveCol.B + 70)), 2.0f)
                {
                    StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
                };
                r.G.DrawCurve(glowPen, pts1, 0.5f);
                r.G.DrawCurve(secPen, pts2, 0.5f);
                r.G.DrawCurve(priPen, pts1, 0.5f);
                return;
            }

            // 4b. Resting Pill Left Side: Real Media (ONLY if HasSession) or Resting Info Face
            if (state.Media.HasSession && (s.RestingMode == RestingInfoMode.MediaOnly || state.Media.IsPlaying))
            {
                float playT = Math.Clamp(state.PlaySpring.Value / 100.0f, 0.0f, 1.0f);
                var cov = new RectangleF(left + 6, cy - 12, 24, 24);
                RenderAnimatedMediaSquare(r, state, cov, Math.Max(3.0f, 6.0f * s.CornerRadiusScale), playT, timeSec, isResting: true);
                DateTime now = DateTime.Now;
                string clockText = s.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm");
                if (s.ShowSongTitleInClosedPill && !string.IsNullOrEmpty(state.Media.Title))
                {
                    r.DrawTextStr(state.Media.Title, new RectangleF(left + 36, top + 2, width - 118, height - 4), 11.0f, false, theme.TextPrimary, hAlign: StringAlignment.Near);
                    r.DrawTextStr(clockText, new RectangleF(left + width - 82, top + 2, 42, height - 4), 10.5f, true, theme.TextMuted, hAlign: StringAlignment.Far);
                }
                else
                {
                    r.DrawTextStr(clockText, new RectangleF(left + 36, top + 2, width - 76, height - 4), 11.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
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

            // 6. Right side: ALWAYS the four-column visualizer.
            // Color: music's dominant color when a media session exists, else theme accent.
            // State: 4 resting squares along the bottom when idle or paused; smooth staggered
            // 4-bar motion while audio plays so all 4 never peg at the top or rest together.
            {
                float right = left + width - 10.0f;
                bool hasLiveAudio = state.AudioPeakSmoothed > 0.014f;
                bool playing = (state.Media.HasSession && state.Media.IsPlaying) || hasLiveAudio;
                Color baseVis = state.Media.HasSession ? state.Media.DominantColor : theme.Accent;
                Color visCol = Color.FromArgb((int)Math.Clamp(r.Opacity * 255.0f, 0, 255),
                    baseVis.R, baseVis.G, baseVis.B);
                using var visBrush = new SolidBrush(visCol);
                for (int i = 0; i < 4; ++i)
                {
                    float lvl = 0.0f;
                    if (playing)
                    {
                        if (s.ReduceMotion)
                        {
                            lvl = i switch { 0 => 0.42f, 1 => 0.74f, 2 => 0.56f, _ => 0.34f };
                        }
                        else
                        {
                            // Staggered quadrature phases so at least one bar is mid/high and one is low,
                            // never all 4 touching the ceiling and never all 4 resting while audio plays.
                            float phaseA = timeSec * (3.4f + i * 0.62f) + i * 1.57f;
                            float phaseB = timeSec * (2.1f + (3 - i) * 0.48f) + i * 2.25f;
                            float wave = 0.5f + 0.32f * MathF.Sin(phaseA) + 0.18f * MathF.Cos(phaseB);
                            float energy = hasLiveAudio
                                ? Math.Clamp(0.74f + MathF.Min(0.24f, state.AudioPeakSmoothed * 0.55f), 0.74f, 0.98f)
                                : 0.80f;
                            float maxCap = i switch { 0 => 0.78f, 1 => 0.90f, 2 => 0.84f, _ => 0.72f };
                            lvl = Math.Clamp((0.24f + 0.62f * wave) * energy, 0.24f, maxCap);
                        }
                    }

                    if (s.Visualizer == VisualizerStyle.Pixels)
                    {
                        int count = playing ? Math.Clamp((int)MathF.Round(lvl * 4.0f), 1, 4) : 1;
                        float bx = right - (4 - i) * 5.5f;
                        float bottom = cy + 8.25f;              // (4 * 4.5 - 1.5) / 2
                        for (int p = 0; p < count; ++p)
                            r.G.FillRectangle(visBrush, bx, bottom - (p + 1) * 3.0f - p * 1.5f, 3.0f, 3.0f);
                    }
                    else
                    {
                        float bh = playing ? Math.Max(4.0f, 15.5f * lvl) : 3.0f;
                        float bx = right - (4 - i) * 6.0f;
                        float by = playing ? (cy - bh * 0.5f) : (cy + 5.25f);
                        r.FillRoundedRect(new RectangleF(bx, by, 3.0f, bh), 1.5f, visCol);
                    }
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
            const float spacing = 22.0f;                        // TABS[i] * 22
            float tabStart = cardWidth * 0.5f - (count * spacing + 10.0f) * 0.5f;
            float tabY = cardTop + 13.0f;
            float gearX = cardLeft + tabStart + count * spacing + 10.0f;

            for (int i = 0; i < count; ++i)
            {
                PanelId pid = order[i];
                float cx = cardLeft + tabStart + i * spacing;
                bool isActive = state.ActivePanel == pid;
                int idx = Math.Clamp((int)pid, 0, 9);
                // paper(1) active, paper(.4) at rest: Geist's own paper colour, not a blend
                Color iconCol = isActive
                    ? theme.TextPrimary
                    : Color.FromArgb(102, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B);
                r.DrawLucideIcon(icons[idx], cx, tabY, 15.0f, iconCol, 1.75f);

                if (isActive)
                {
                    // a 2.64 DIP square, not a dot
                    using (var dotBrush = new SolidBrush(theme.Accent))
                        r.G.FillRectangle(dotBrush, cx - 1.32f, tabY + 6.0f + 3.84f, 2.64f, 2.64f);
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

                r.AddHitRegion($"tab-{idx}", new RectangleF(cx - 11.0f, cardTop + 2.0f, 22.0f, 25.0f), () =>
                {
                    state.ShowCalendarInIsland = false;
                    state.SelectPanel(pid);
                });
            }

            // divider, then the gear, both at the demo's fixed offsets
            r.G.FillRectangle(new SolidBrush(Color.FromArgb(46, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B)),
                gearX - 16.5f, tabY - 5.4f, 1.0f, 10.8f);
            r.DrawLucideIcon("settings", gearX, tabY, 13.8f,
                Color.FromArgb(102, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B));
            r.AddHitRegion("tab-settings", new RectangleF(gearX - 11.0f, cardTop + 2.0f, 22.0f, 25.0f), onOpenSettings);

            if (state.IsRecordingScreen)
            {
                var stopRec = new RectangleF(cardLeft + cardWidth - 58.0f, tabY - 9.0f, 44.0f, 18.0f);
                r.FillRoundedRect(stopRec, 9.0f, Color.FromArgb(235, 255, 59, 48));
                r.DrawTextStr("\\u25CF Stop", stopRec, 9.5f, true, Color.White, hAlign: StringAlignment.Center);
                r.AddHitRegion("tab-stop-rec", stopRec, () =>
                {
                    state.IsRecordingScreen = false;
                    state.RecSpring.SetTarget(0.0f, state.Settings.ReduceMotion);
                });
            }
        }

        private static void RenderActiveTabBody(
            LiquidGlassRenderer r, GlobalAppState state,
            float left, float top, float width, float height,
            ThemeSpec theme, float timeSec, Action<WidgetType> onToggleDesktopWidget)
        {
            var s = state.Settings;
            float prevOp = r.Opacity;
            float bodyTop = top + 32.0f;
            float bodyLeft = left + 18.0f;
            float bodyW = width - 36.0f;

            switch (state.ShownPanel)
            {
                case PanelId.Music:
                {
                    float playT = Math.Clamp(state.PlaySpring.Value / 100.0f, 0.0f, 1.0f);
                    float morphT = Math.Clamp(state.MorphEase, 0.0f, 1.0f);
                    float L = left;
                    float x0 = L + 254.0f;                          // L + CARD_W - 18 - 128

                    // cover: 74 DIP, scaled to 63.6 and dimmed by 45% when paused, flipped 3D on song change
                    float coverScale = 1.0f - 0.14f * (1.0f - playT);
                    float flipDeg = FlipAngleDeg(state, timeSec);
                    float flipCos = MathF.Max(0.001f, MathF.Cos(flipDeg * MathF.PI / 180.0f));
                    float coverW = 74.0f * coverScale * flipCos;
                    float coverH = 74.0f * coverScale;
                    var covRect = new RectangleF(L + 59.0f - coverW * 0.5f, top + 69.0f - coverH * 0.5f, coverW, coverH);
                    RenderAnimatedMediaSquare(r, state, covRect, 14.0f * s.CornerRadiusScale, playT, timeSec, isResting: false);
                    float dim = 0.45f * (1.0f - playT);
                    if (dim > 0.002f)
                        r.FillRoundedRect(covRect, 14.0f * s.CornerRadiusScale, Color.FromArgb((int)(255 * dim), 0, 0, 0));

                    // the source badge: music glyph on the album's strong tint, ringed in the card colour
                    Color strong = state.Media.HasSession ? state.Media.DominantColor : theme.Accent;
                    float bs = 1.0f - 0.15f * (1.0f - playT), bw = 24.0f * bs;
                    float bx = L + 96.0f + 7.0f * bs - bw, by = top + 106.0f + 7.0f * bs - bw;
                    r.FillRoundedRect(new RectangleF(bx - 2.5f, by - 2.5f, bw + 5.0f, bw + 5.0f), 8.0f * bs + 2.5f, theme.Fill);
                    r.FillRoundedRect(new RectangleF(bx, by, bw, bw), 8.0f * bs, strong);
                    r.DrawLucideIcon("music", bx + bw * 0.5f, by + bw * 0.5f, 14.0f * bs, Color.FromArgb(255, 12, 12, 14));

                    // title (600 16, gliding marquee when long) and artist (400 13, album-tinted)
                    ComputeSwapShift(state, timeSec, out float swShift, out float swFade);
                    string dispTitle = state.Media.HasSession ? state.Media.Title : "Nothing playing";
                    string dispSub = state.Media.HasSession
                        ? state.Media.Artist
                        : "Play music or video";
                    float textX = L + 112.0f, textW = 136.0f;
                    float savedTextOp = r.Opacity;
                    r.Opacity = savedTextOp * Math.Clamp(swFade, 0.0f, 1.0f);
                    Color titleCol = state.Media.HasSession ? theme.TextPrimary : theme.TextMuted;
                    Color artistCol = state.Media.HasSession
                        ? AlbumArtistTint(state.Media.DominantColor, Color.FromArgb(153, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B))
                        : theme.TextMuted;

                    var titleRect = new RectangleF(textX, top + 60.0f - 18.0f + swShift, textW, 26.0f);
                    using (var measureFont = UiFonts.Get(16.0f * r.TextScaleMultiplier, bold: true, mono: false))
                    {
                        float titleW = r.G.MeasureString(dispTitle, measureFont, 2000, StringFormat.GenericTypographic).Width;
                        if (titleW > textW && !s.ReduceMotion && state.Media.HasSession)
                        {
                            float gap = 22.0f;
                            float runSec = (titleW + gap) / 30.0f;
                            float k = state.MarqueeSec % (3.0f + runSec);
                            float dx = k < 3.0f ? 0.0f : (k - 3.0f) * 30.0f;
                            Region oldClip = r.G.Clip;
                            r.G.SetClip(titleRect, CombineMode.Intersect);
                            r.DrawTextStr(dispTitle, new RectangleF(textX - dx, titleRect.Y, titleW + 16.0f, 26.0f), 16.0f, true, titleCol);
                            r.DrawTextStr(dispTitle, new RectangleF(textX - dx + titleW + gap, titleRect.Y, titleW + 16.0f, 26.0f), 16.0f, true, titleCol);
                            r.G.Clip = oldClip;
                        }
                        else
                        {
                            r.DrawTextStr(dispTitle, titleRect, 16.0f, true, titleCol);
                        }
                    }
                    r.DrawTextStr(dispSub, new RectangleF(textX, top + 80.0f - 15.0f + swShift, textW, 22.0f), 13.0f, false, artistCol);
                    r.Opacity = savedTextOp;

                    // previous | play/pause | next, with the demo's soft hover squares & click kicks
                    var prevBtn = new RectangleF(x0, top + 52.0f, 36.0f, 36.0f);
                    var playBtn = new RectangleF(x0 + 40.0f, top + 46.0f, 48.0f, 48.0f);
                    var nextBtn = new RectangleF(x0 + 92.0f, top + 52.0f, 36.0f, 36.0f);
                    float prevHov = EaseHover(state, "m-prev", r.IsHovered(prevBtn, state.PointerDipX, state.PointerDipY));
                    float playHov = EaseHover(state, "m-play", r.IsHovered(playBtn, state.PointerDipX, state.PointerDipY));
                    float nextHov = EaseHover(state, "m-next", r.IsHovered(nextBtn, state.PointerDipX, state.PointerDipY));
                    if (prevHov > 0.003f) r.FillRoundedRect(prevBtn, 12.0f, Color.FromArgb((int)(46 * prevHov), 255, 255, 255));
                    if (playHov > 0.003f) r.FillRoundedRect(playBtn, 16.0f, Color.FromArgb((int)(46 * playHov), 255, 255, 255));
                    if (nextHov > 0.003f) r.FillRoundedRect(nextBtn, 12.0f, Color.FromArgb((int)(46 * nextHov), 255, 255, 255));
                    float prevNudge = KickNudge(state.PrevKickSec, timeSec, s.ReduceMotion);
                    float nextNudge = KickNudge(state.NextKickSec, timeSec, s.ReduceMotion);
                    float playNudge = KickNudge(state.PlayKickSec, timeSec, s.ReduceMotion);
                    float playScale = (state.PlayButtonPressed || playNudge > 0.3f) ? 0.88f : 1.0f;
                    DrawSolidSkip(r, x0 + 18.0f - prevNudge * 7.0f, top + 70.0f, 20.0f, theme.TextPrimary, back: true);
                    DrawSolidSkip(r, x0 + 110.0f + nextNudge * 7.0f, top + 70.0f, 20.0f, theme.TextPrimary, back: false);
                    DrawPlayPauseGlyph(r, x0 + 64.0f, top + 70.0f, 30.0f * playScale, theme.TextPrimary, morphT);
                    r.AddHitRegion("m-prev", prevBtn, () =>
                    {
                        state.PrevKickSec = timeSec;
                        state.SwapAtSec = timeSec;
                        state.SwapDir = -1;
                        state.MarqueeSec = 0.0f;
                        WindowsSystemHooks.MediaPreviousTrack(state);
                    });
                    r.AddHitRegion("m-play", playBtn, () =>
                    {
                        state.PlayButtonPressed = true;
                        state.PlayKickSec = timeSec;
                        WindowsSystemHooks.MediaTogglePlayPause(state);
                    });
                    r.AddHitRegion("m-next", nextBtn, () =>
                    {
                        state.NextKickSec = timeSec;
                        state.SwapAtSec = timeSec;
                        state.SwapDir = 1;
                        state.MarqueeSec = 0.0f;
                        WindowsSystemHooks.MediaNextTrack(state);
                    });

                    // progress bar at y 122, times under its ends at baseline 139
                    float prog = (state.Media.HasSession && state.Media.DurationSec > 0.1)
                        ? (float)(state.Media.PositionSec / state.Media.DurationSec) : 0.0f;
                    RenderProgressBar(r, state, L + 22.0f, top + 122.0f, 356.0f, prog, theme, timeSec);
                    Color timeCol = Color.FromArgb(160, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B);
                    r.DrawTextStr(FormatTimeSec(state.Media.PositionSec), new RectangleF(L + 22.0f, top + 139.0f - 12.0f, 44.0f, 18), 11.0f, false, timeCol, mono: true);
                    double remain = Math.Max(0.0, state.Media.DurationSec - state.Media.PositionSec);
                    r.DrawTextStr("-" + FormatTimeSec(remain), new RectangleF(L + 334.0f, top + 139.0f - 12.0f, 44.0f, 18), 11.0f, false, timeCol, mono: true, hAlign: StringAlignment.Far);

                    // volume, centred under the bar: mute icon at -54+9, track -30..54 (clean pill, no thumb circle)
                    float vx = L + 146.0f;
                    var muteBtn = new RectangleF(vx - 3.0f, top + 123.0f, 24.0f, 22.0f);
                    float muteHov = EaseHover(state, "m-mute", r.IsHovered(muteBtn, state.PointerDipX, state.PointerDipY));
                    bool muted = state.MasterOutputMuted;
                    float level = muted ? 0.0f : Math.Clamp(state.MasterVolume / 100.0f, 0.0f, 1.0f);
                    r.DrawLucideIcon(level <= 0.001f ? "volume-x" : level > 0.5f ? "volume-2" : "volume1",
                        vx + 9.0f, top + 134.0f, 15.0f,
                        Color.FromArgb((int)((0.69f + 0.31f * muteHov) * 255), theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B));
                    var volTrack = new RectangleF(vx + 24.0f, top + 125.0f, 84.0f, 18.0f);
                    float vk = EaseHover(state, "m-vol", r.IsHovered(volTrack, state.PointerDipX, state.PointerDipY));
                    float vh = 3.0f + 3.0f * vk;
                    r.FillRoundedRect(new RectangleF(vx + 24.0f, top + 134.0f - vh * 0.5f, 84.0f, vh), vh * 0.5f,
                        Color.FromArgb(38, 255, 255, 255));
                    if (level > 0.0f)
                        r.FillRoundedRect(new RectangleF(vx + 24.0f, top + 134.0f - vh * 0.5f, Math.Max(vh, 84.0f * level), vh), vh * 0.5f, theme.TextPrimary);
                    r.AddHitRegion("m-vol", volTrack, null, normX => WindowsSystemHooks.SetMasterVolume(state, normX * 100.0f, showPopup: false));
                    r.AddHitRegion("m-mute", muteBtn, () =>
                    {
                        WindowsSystemHooks.ToggleMasterOutputMute(state, showPopup: false);
                    });

                    // palette button in the corner: album colours behind the card, on or off
                    bool albumBg = s.CardBackground == CardBackgroundMode.AlbumColours;
                    float palX = L + 362.0f;                        // -L - 14 - 24
                    var palBtn = new RectangleF(palX, top + 7.0f, 24.0f, 24.0f);
                    float palHov = EaseHover(state, "m-pal", r.IsHovered(palBtn, state.PointerDipX, state.PointerDipY));
                    if (albumBg)
                    {
                        r.FillRoundedRect(palBtn, 8.0f, Color.FromArgb((int)((0.12f + 0.1f * palHov) * 255), 255, 255, 255));
                    }
                    else
                    {
                        if (palHov > 0.003f)
                            r.FillRoundedRect(palBtn, 8.0f, Color.FromArgb((int)(0.12f * palHov * 255), 255, 255, 255));
                        using var palPen = new Pen(Color.FromArgb((int)(0.165f * 255), theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B), 1.0f);
                        r.G.DrawPath(palPen, RoundedRectPath(new RectangleF(palX + 0.5f, top + 7.5f, 23.0f, 23.0f), 7.5f));
                    }
                    r.DrawLucideIcon("palette", palX + 12.0f, top + 19.0f, 15.0f,
                        albumBg ? theme.TextPrimary
                                : Color.FromArgb(128, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B));
                    Color dotCol = albumBg ? strong : Color.FromArgb(64, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B);
                    using (var dotBrush = new SolidBrush(dotCol))
                    {
                        foreach (var d in new[] { new PointF(13.5f, 6.5f), new PointF(17.5f, 10.5f), new PointF(8.5f, 7.5f), new PointF(6.5f, 12.5f) })
                            r.G.FillEllipse(dotBrush, palX + 12.0f + (d.X - 12.0f) * 0.625f - 1.06f,
                                top + 19.0f + (d.Y - 12.0f) * 0.625f - 1.06f, 2.12f, 2.12f);
                    }
                    r.AddHitRegion("m-pal", palBtn, () =>
                    {
                        s.CardBackground = albumBg ? CardBackgroundMode.Plain : CardBackgroundMode.AlbumColours;
                        ConfigManager.SaveAtomic(state);
                    });

                    // lyrics stays, tucked to the left of the palette so the corner matches the demo
                    var lyrBtn = new RectangleF(L + 332.0f, top + 7.0f, 24.0f, 24.0f);
                    r.FillRoundedRect(lyrBtn, 8.0f, state.ShowLyricsInIsland ? theme.Accent : Color.FromArgb(26, 255, 255, 255));
                    r.DrawLucideIcon("message-circle", lyrBtn.X + 12.0f, lyrBtn.Y + 12.0f, 13.0f,
                        state.ShowLyricsInIsland ? ThemeSpec.AdaptiveInkForBackground(theme.Accent) : theme.TextPrimary);
                    r.AddHitRegion("music-lyrics", lyrBtn, () => state.ShowLyricsInIsland = !state.ShowLyricsInIsland);

                    if (state.ShowLyricsInIsland)
                    {
                        float lyrY = top + 148.0f;
                        var lyrBox = new RectangleF(bodyLeft, lyrY, bodyW, 68.0f);
                        r.FillRoundedRect(lyrBox, 10 * s.CornerRadiusScale, Color.FromArgb(24, 255, 255, 255));
                        if (!state.Media.HasSession || state.Media.Lyrics.Count == 0)
                        {
                            string msg = state.Media.LyricsFetching ? "Searching synced lyrics..." : "No synced lyrics available";
                            r.DrawTextStr(msg, lyrBox, 11.0f, false, theme.TextFaint, hAlign: StringAlignment.Center);
                            if (state.Media.LyricsFetching)
                            {
                                float loadW = Math.Max(48.0f, bodyW * 0.28f);
                                float loadT = 0.5f + 0.5f * MathF.Sin(timeSec * 3.6f);
                                float loadX = bodyLeft + 16.0f + (bodyW - 32.0f - loadW) * loadT;
                                r.FillRoundedRect(new RectangleF(loadX, lyrY + 54.0f, loadW, 3.0f), 1.5f, theme.Accent);
                            }
                        }
                        else
                        {
                            int cur = Math.Clamp(state.Media.ActiveLyricIndex, 0, state.Media.Lyrics.Count - 1);
                            string prevL = cur > 0 ? state.Media.Lyrics[cur - 1].Text : "";
                            string curL = state.Media.Lyrics[cur].Text;
                            string nextL = cur + 1 < state.Media.Lyrics.Count ? state.Media.Lyrics[cur + 1].Text : "";
                            r.DrawTextStr(prevL, new RectangleF(bodyLeft + 10, lyrY + 5, bodyW - 20, 17), 10.5f, false, theme.TextFaint, hAlign: StringAlignment.Center);
                            r.DrawTextStr(curL, new RectangleF(bodyLeft + 10, lyrY + 24, bodyW - 20, 20), 12.0f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                            r.DrawTextStr(nextL, new RectangleF(bodyLeft + 10, lyrY + 46, bodyW - 20, 17), 10.5f, false, theme.TextMuted, hAlign: StringAlignment.Center);
                        }
                    }
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
                                    LocalNetworkServers.ApplyDeckEnabled(state);
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
                            r.AddHitRegion("deck-turn-off", offBtn, () => { s.PcDeckEnabled = false; LocalNetworkServers.ApplyDeckEnabled(state); ConfigManager.SaveAtomic(state); });
                        }
                        break;
                    }

                    // 3-column × 2-row Web Drawer site grid matching notchpixel.com (step_65_shot_1.png):
                    // brand-tinted horizontal gradient cards, left icon badge, title + domain,
                    // hover 'x' remove icon, and dashed "+ Add a site" slot in the next cell.
                    int siteCount = Math.Min(5, state.PinnedSites.Count);
                    for (int i = 0; i < siteCount; ++i)
                    {
                        int col = i % 3;
                        int row = i / 3;
                        float tx = left + 16.0f + col * 126.0f;
                        float ty = top + 28.0f + row * 54.0f;
                        var tile = new RectangleF(tx, ty, 118.0f, 46.0f);
                        var site = state.PinnedSites[i];
                        Color brand = site.BadgeColor;
                        bool hovered = r.IsHovered(tile, state.PointerDipX, state.PointerDipY);
                        float tHov = EaseHover(state, $"web-card-{i}", hovered);
                        using (var cardPath = RoundedRectPath(tile, 13.0f))
                        using (var cardGrad = new LinearGradientBrush(tile,
                            Color.FromArgb((int)(92 + 26 * tHov), brand.R, brand.G, brand.B),
                            Color.FromArgb(238, 22, 22, 24), 0.0f))
                        {
                            r.G.FillPath(cardGrad, cardPath);
                            using var borderPen = new Pen(Color.FromArgb((int)(22 + 28 * tHov), 255, 255, 255), 1.0f);
                            r.G.DrawPath(borderPen, cardPath);
                        }
                        var badgeBox = new RectangleF(tx + 9.0f, ty + 9.0f, 28.0f, 28.0f);
                        r.FillRoundedRect(badgeBox, 8.0f, Color.FromArgb(225, 16, 16, 18));
                        r.FillCircle(badgeBox.X + 14.0f, badgeBox.Y + 14.0f, 8.5f, brand);
                        string glyph = string.IsNullOrEmpty(site.Title) ? "W" : site.Title.Substring(0, 1).ToUpperInvariant();
                        r.DrawTextStr(glyph, badgeBox, 10.5f, true, Color.White, hAlign: StringAlignment.Center);

                        r.DrawTextStr(site.Title,
                            new RectangleF(tx + 43.0f, ty + 6.0f, 64.0f, 17.0f), 11.5f, true, theme.TextPrimary);
                        string shortUrl = site.Url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
                        r.DrawTextStr(shortUrl,
                            new RectangleF(tx + 43.0f, ty + 23.0f, 68.0f, 14.0f), 9.5f, false, theme.TextMuted);

                        int capIdx = i;
                        if (hovered && state.PinnedSites.Count > 1)
                        {
                            var delRect = new RectangleF(tile.Right - 18.0f, tile.Top + 4.0f, 14.0f, 14.0f);
                            r.DrawTextStr("\u00D7", delRect, 10.0f, true, Color.FromArgb(200, 230, 232, 236), hAlign: StringAlignment.Center);
                            r.AddHitRegion($"web-del-{i}", delRect, () =>
                            {
                                if (capIdx >= 0 && capIdx < state.PinnedSites.Count)
                                    state.PinnedSites.RemoveAt(capIdx);
                            });
                        }

                        r.AddHitRegion($"web-{i}", tile, () =>
                        {
                            if (capIdx >= 0 && capIdx < state.PinnedSites.Count)
                            {
                                state.CurrentWebDrawerTitle = state.PinnedSites[capIdx].Title;
                                state.CurrentWebDrawerUrl = state.PinnedSites[capIdx].Url;
                                state.WebDrawerVisible = true;
                                AuxiliaryWindows.UpdateDrawers(state);
                            }
                        });
                    }

                    // Dashed "+ Add a site" tile placed cleanly in the next grid cell
                    int addCol = siteCount % 3;
                    int addRow = siteCount / 3;
                    var addTile = new RectangleF(left + 16.0f + addCol * 126.0f, top + 28.0f + addRow * 54.0f, 118.0f, 46.0f);
                    using (var dash = new Pen(theme.Edge, 1.3f))
                    {
                        dash.DashStyle = DashStyle.Custom;
                        dash.DashPattern = new[] { 4.0f, 4.0f };
                        r.G.DrawPath(dash, RoundedRectPath(addTile, 13.0f));
                    }
                    r.DrawLucideIcon("plus", addTile.X + 22.0f, addTile.Y + 23.0f, 12.5f, theme.TextMuted);
                    r.DrawTextStr("Add a site", new RectangleF(addTile.X + 34.0f, addTile.Y + 11.0f, 78.0f, 24.0f), 11.5f, false, theme.TextPrimary);
                    r.AddHitRegion("web-add", addTile, () =>
                    {
                        state.CurrentWebDrawerTitle = "New tab";
                        state.CurrentWebDrawerUrl = "https://www.google.com";
                        state.WebDrawerVisible = true;
                        AuxiliaryWindows.UpdateDrawers(state);
                    });

                    r.DrawTextStr("Add a site: any page, signed in, one click away.",
                        new RectangleF(left + 16.0f, top + 202.0f, 368.0f, 18.0f), 11.5f, false, theme.TextMuted);

                    // PC Deck stays reachable from here, but only once the user turns it on.
                    if (s.PcDeckEnabled)
                    {
                        var deckBtn = new RectangleF(left + 300.0f, top + 76.0f, 84.0f, 28.0f);
                        r.FillRoundedRect(deckBtn, 14.0f, Color.FromArgb(26, 255, 255, 255));
                        r.DrawTextStr("PC Deck", deckBtn, 12.0f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        r.AddHitRegion("web-to-deck", deckBtn, () => state.ShowDeckInIsland = true);
                    }
                    break;
                }

                case PanelId.Notifications:
                {
                    int shown = Math.Min(3, state.Notifications.Count);
                    if (shown == 0)
                    {
                        r.DrawLucideIcon("bell", left + width * 0.5f, top + 92.0f, 22.0f, theme.TextFaint, 1.5f);
                        r.DrawTextStr("No notifications",
                            new RectangleF(left + 16.0f, top + 114.0f, width - 32.0f, 22.0f), 12.5f, false, theme.TextMuted, hAlign: StringAlignment.Center);
                    }
                    else
                    {
                        for (int i = 0; i < shown; ++i)
                        {
                            float ry = top + 28.0f + i * 52.5f;
                            var row = new RectangleF(left + 16.0f, ry, 368.0f, 45.0f);
                            var n = state.Notifications[i];
                            float nHov = EaseHover(state, $"notif-{i}", r.IsHovered(row, state.PointerDipX, state.PointerDipY));
                            r.FillRoundedRect(row, 14.0f, Color.FromArgb((int)(20 + 16 * nHov), 255, 255, 255));
                            r.FillRoundedRect(new RectangleF(left + 26.0f, ry + 6.5f, 32.0f, 32.0f), 10.0f, n.AccentColor);
                            r.DrawLucideIcon("bell",
                                left + 42.0f, ry + 22.5f, 16.0f, Color.FromArgb(255, 12, 12, 14));
                            Color notifTitleCol = theme.IsLight ? theme.TextPrimary : Color.FromArgb(255, 255, 255, 255);
                            Color notifBodyCol = theme.IsLight ? theme.TextMuted : Color.FromArgb(210, 245, 245, 248);
                            r.DrawTextStr(n.Title,
                                new RectangleF(left + 68.0f, ry + 4.0f, 240.0f, 20.0f), 13.0f, true, notifTitleCol);
                            r.DrawTextStr(n.Body,
                                new RectangleF(left + 68.0f, ry + 23.0f, 250.0f, 18.0f), 11.5f, false, notifBodyCol);
                            string ts = s.ClockTimestampsInNotifications && !string.IsNullOrEmpty(n.TimeStr)
                                ? n.TimeStr
                                : (string.IsNullOrEmpty(n.RelativeStr) ? "now" : n.RelativeStr);
                            r.DrawTextStr(ts,
                                new RectangleF(left + 322.0f, ry + 5.0f, 52.0f, 18.0f), 10.5f, false, notifBodyCol, mono: false, hAlign: StringAlignment.Far);
                            NotificationItem capturedNotif = n;
                            r.AddHitRegion($"notif-open-{i}", row, () =>
                            {
                                WindowsSystemHooks.OpenNotificationTarget(capturedNotif);
                                state.IslandOpen = false;
                            });
                        }

                        r.DrawTextStr($"{shown} of {state.Notifications.Count}",
                            new RectangleF(left + 16.0f, top + 197.0f, 180.0f, 18.0f), 11.5f, false, theme.TextMuted);
                        r.DrawTextStr("Clear all",
                            new RectangleF(left + 300.0f, top + 196.0f, 84.0f, 20.0f), 12.5f, true, theme.TextPrimary, hAlign: StringAlignment.Far);
                        r.AddHitRegion("notif-clear", new RectangleF(left + 300.0f, top + 192.0f, 84.0f, 26.0f), () =>
                        {
                            state.Notifications.Clear();
                        });
                    }
                    break;
                }

                case PanelId.Chat:
                {
                    // Centered status circle + label inside the 368 x 71 status box
                    var box = new RectangleF(left + 16.0f, top + 28.0f, 368.0f, 71.0f);
                    r.FillRoundedRect(box, 16.0f, Color.FromArgb(20, 255, 255, 255));
                    string chatLabel = s.StreamChatEnabled ? "Chat overlay is live." : "Chat overlay is stopped.";
                    float boxMidY = box.Y + box.Height * 0.5f;
                    float labelEstW = chatLabel.Length * 7.8f;
                    float groupW = 16.0f + 10.0f + labelEstW;
                    float groupX = box.X + (box.Width - groupW) * 0.5f;
                    Color ringCol = s.StreamChatEnabled ? Color.FromArgb(255, 48, 209, 88) : Color.FromArgb(145, 255, 255, 255);
                    if (s.StreamChatEnabled)
                        r.FillCircle(groupX + 8.0f, boxMidY, 4.5f, ringCol);
                    using (var ring = new Pen(ringCol, 2.0f))
                        r.G.DrawEllipse(ring, groupX, boxMidY - 8.0f, 16.0f, 16.0f);
                    r.DrawTextStr(chatLabel,
                        new RectangleF(groupX + 26.0f, boxMidY - 12.0f, box.Right - (groupX + 34.0f), 24.0f), 14.0f, false, theme.TextPrimary);

                    var startBtn = new RectangleF(left + 16.0f, top + 111.0f, 70.0f, 22.0f);
                    r.FillRoundedRect(startBtn, 11.0f, Color.FromArgb(255, 239, 237, 232));
                    using (var playBrush = new SolidBrush(Color.FromArgb(255, 12, 12, 14)))
                    {
                        r.G.FillPolygon(playBrush, new[]
                        {
                            new PointF(startBtn.X + 12.0f, startBtn.Y + 6.0f),
                            new PointF(startBtn.X + 21.0f, startBtn.Y + 11.0f),
                            new PointF(startBtn.X + 12.0f, startBtn.Y + 16.0f)
                        });
                    }
                    r.DrawTextStr(s.StreamChatEnabled ? "Stop" : "Start",
                        new RectangleF(startBtn.X + 26.0f, startBtn.Y, 42.0f, startBtn.Height), 15.0f, true, Color.FromArgb(255, 12, 12, 14));
                    r.AddHitRegion("chat-toggle", new RectangleF(left + 12.0f, top + 105.0f, 74.0f, 34.0f), () =>
                    {
                        s.StreamChatEnabled = !s.StreamChatEnabled;
                        state.ChatDrawerVisible = s.StreamChatEnabled;
                        AuxiliaryWindows.UpdateDrawers(state);
                        ConfigManager.SaveAtomic(state);
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
                        // Header: regular "Shelf", bold count, spacious Send-to-phone pill, three icons
                        r.DrawTextStr("Shelf", new RectangleF(bodyLeft, bodyTop - 1.0f, 54.0f, 22.0f), 14.5f, false, theme.TextPrimary);
                        r.DrawTextStr($"\u00B7 {state.ShelfFiles.Count} files", new RectangleF(bodyLeft + 44.0f, bodyTop + 1.0f, 96.0f, 19.0f), 12.5f, true,
                            Color.FromArgb(228, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B));

                        // When a phone is already paired & Deck is ON, Shelf syncs directly to the phone
                        // so hide the redundant "Send to phone" button.
                        bool phoneConnected = s.PcDeckEnabled && state.PairedDeckDevices.Count > 0;
                        if (!phoneConnected)
                        {
                            var phoneBtn = new RectangleF(bodyLeft + 162.0f, bodyTop - 1.0f, 124.0f, 22.0f);
                            r.FillRoundedRect(phoneBtn, 11.0f, theme.TextPrimary);
                            r.DrawLucideIcon("smartphone", phoneBtn.X + 19.0f, phoneBtn.Y + 11.0f, 12.5f, Color.FromArgb(255, 16, 16, 18));
                            r.DrawTextStr("Send to phone", new RectangleF(phoneBtn.X + 28.0f, phoneBtn.Y, 92.0f, 22.0f), 11.5f, false, Color.FromArgb(255, 16, 16, 18));
                            r.AddHitRegion("shelf-phone", phoneBtn, () =>
                            {
                                state.SendToPhoneUrl = $"http://{state.LocalPrivateIp}:{s.PcDeckPort}/shelf";
                                state.SendToPhoneQrActive = true;
                            });
                        }

                        r.DrawLucideIcon("download", bodyLeft + 307.0f, bodyTop + 10.0f, 14.5f, theme.TextMuted);
                        r.DrawLucideIcon("upload", bodyLeft + 335.0f, bodyTop + 10.0f, 14.5f, theme.TextMuted);
                        r.DrawLucideIcon("trash", bodyLeft + 360.0f, bodyTop + 10.0f, 14.5f, theme.TextMuted);
                        r.AddHitRegion("shelf-clr", new RectangleF(bodyLeft + 344.0f, bodyTop - 2.0f, 32.0f, 24.0f), () => state.ShelfFiles.Clear());

                        if (state.ShelfFiles.Count == 0)
                        {
                            var dropZone = new RectangleF(bodyLeft, bodyTop + 40.0f, bodyW, 70.0f);
                            r.FillRoundedRect(dropZone, 12.0f, Color.FromArgb(14, 255, 255, 255));
                            r.DrawRoundedRect(dropZone, 12.0f, theme.Edge, 1.0f);
                            r.DrawTextStr("Drop up to 12 files here to hold, drag out, or send to your phone",
                                dropZone, 11.5f, false, theme.TextFaint, hAlign: StringAlignment.Center, noWrap: false);
                        }
                        else
                        {
                            int visCount = Math.Min(5, state.ShelfFiles.Count);
                            for (int i = 0; i < visCount; ++i)
                            {
                                var f = state.ShelfFiles[i];
                                float cx = bodyLeft + 36.0f + i * 74.0f;
                                var tile = new RectangleF(cx - 30.0f, bodyTop + 30.0f, 60.0f, 60.0f);
                                if (f.Selected)
                                    r.FillRoundedRect(tile, 10.0f, Color.FromArgb(46, 255, 255, 255));

                                string ext = (f.Extension ?? "").TrimStart('.').ToUpperInvariant();
                                bool isImageFile = ext is "PNG" or "JPG" or "JPEG" or "WEBP" or "BMP" or "GIF";
                                Bitmap? realThumb = isImageFile ? LoadShelfImageThumb(f.FullPath) : null;
                                Bitmap? richArt = Art(ShelfTileArt(ext));
                                if (realThumb != null)
                                {
                                    // Draw the user's actual image inside the white tilted Polaroid frame
                                    var prevTransform = r.G.Transform;
                                    r.G.TranslateTransform(cx, bodyTop + 60.0f);
                                    r.G.RotateTransform(-2.5f);
                                    r.FillRoundedRect(new RectangleF(-26.0f, -27.0f, 52.0f, 54.0f), 4.0f, Color.FromArgb(255, 245, 243, 238));
                                    r.G.DrawImage(realThumb, -22.5f, -23.5f, 45.0f, 38.0f);
                                    r.G.Transform = prevTransform;
                                }
                                else if (richArt != null)
                                {
                                    // Filmstrip (.mp4/.mov/.mkv), Vinyl (.mp3/.wav/.flac), PDF, ZIP, or Polaroid art
                                    r.G.DrawImage(richArt, tile.X, tile.Y, tile.Width, tile.Height);
                                }
                                else
                                {
                                    Bitmap ico = AppIcons.ForShortcut(f.FullPath);
                                    if (ico != null)
                                    {
                                        r.FillRoundedRect(tile, 10.0f, Color.FromArgb(20, 255, 255, 255));
                                        try { r.G.DrawImage(ico, tile.X + 12.0f, tile.Y + 12.0f, 36.0f, 36.0f); } catch { }
                                    }
                                    else
                                    {
                                        r.FillRoundedRect(tile, 10.0f, Color.FromArgb(20, 255, 255, 255));
                                        DrawShelfThumb(r, tile, ext);
                                    }
                                }

                                r.DrawTextStr(MiddleEllipsis(f.DisplayName, 11), new RectangleF(cx - 36.0f, bodyTop + 96.0f, 72.0f, 18.0f), 10.5f, false,
                                    Color.FromArgb(228, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B), hAlign: StringAlignment.Center);
                                int idx = i;
                                r.AddHitRegion($"shelf-{i}", new RectangleF(cx - 37.0f, bodyTop + 28.0f, 74.0f, 90.0f),
                                    () => state.ShelfFiles[idx].Selected = !state.ShelfFiles[idx].Selected);
                            }

                            if (state.ShelfFiles.Count > 5)
                            {
                                r.FillCircle(bodyLeft + 357.0f, bodyTop + 60.0f, 14.0f, Color.FromArgb(235, 36, 36, 38));
                                r.DrawCircle(bodyLeft + 357.0f, bodyTop + 60.0f, 14.0f, Color.FromArgb(38, 255, 255, 255), 1.0f);
                                r.DrawLucideIcon("chevron-right", bodyLeft + 357.0f, bodyTop + 60.0f, 13.0f, theme.TextPrimary);
                            }
                        }
                    }
                    break;
                }

                case PanelId.Clipboard:
                {
                    (string label, float x, float w)[] chips =
                    {
                        ("All", 16.0f, 32.0f), ("Text", 52.0f, 41.0f), ("Links", 98.0f, 45.0f),
                        ("Images", 148.0f, 55.0f), ("Files", 208.0f, 42.0f), ("Snippets", 254.0f, 65.0f)
                    };
                    for (int c = 0; c < chips.Length; ++c)
                    {
                        var chip = new RectangleF(bodyLeft - 16.0f + chips[c].x, bodyTop - 2.0f, chips[c].w, 22.0f);
                        bool act = (int)state.ActiveClipCat == c;
                        r.FillRoundedRect(chip, 11.0f, act ? theme.TextPrimary : Color.FromArgb(24, 255, 255, 255));
                        r.DrawTextStr(chips[c].label, chip, 11.5f, false,
                            act ? Color.FromArgb(255, 16, 16, 18) : Color.FromArgb(215, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B),
                            hAlign: StringAlignment.Center);
                        int capC = c;
                        r.AddHitRegion($"clip-c-{c}", chip, () => state.ActiveClipCat = (ClipboardCategory)capC);
                    }

                    r.DrawTextStr("Clear", new RectangleF(bodyLeft + 312.0f, bodyTop, 56.0f, 18.0f), 11.5f, false, theme.TextMuted, hAlign: StringAlignment.Far);
                    r.AddHitRegion("clip-clear", new RectangleF(bodyLeft + 312.0f, bodyTop - 2.0f, 56.0f, 24.0f),
                        () => state.ClipboardHistory.RemoveAll(e => !e.IsPinned));

                    var shown = new List<ClipboardEntry>();
                    foreach (var entry in state.ClipboardHistory)
                    {
                        if (state.ActiveClipCat != ClipboardCategory.All && entry.Category != state.ActiveClipCat) continue;
                        shown.Add(entry);
                        if (shown.Count == 3) break;
                    }

                    for (int i = 0; i < shown.Count; ++i)
                    {
                        var item = shown[i];
                        float cx = bodyLeft + i * 125.0f;
                        var card = new RectangleF(cx, bodyTop + 30.0f, 114.0f, 124.0f);
                        r.FillRoundedRect(card, 14.0f, Color.FromArgb(22, 255, 255, 255));

                        Color headBg, headInk;
                        string headIcon, headLabel;
                        switch (item.Category)
                        {
                            case ClipboardCategory.Images:
                                headBg = Color.FromArgb(255, 16, 62, 38); headInk = Color.FromArgb(255, 57, 217, 138);
                                headIcon = "image"; headLabel = "Image"; break;
                            case ClipboardCategory.Files:
                                headBg = Color.FromArgb(255, 64, 45, 16); headInk = Color.FromArgb(255, 245, 166, 35);
                                headIcon = item.FilePaths.Count == 1 ? "file-text" : "folder";
                                headLabel = item.FilePaths.Count == 1 ? "File" : "Files"; break;
                            case ClipboardCategory.Links:
                                headBg = Color.FromArgb(255, 24, 40, 68); headInk = Color.FromArgb(255, 90, 160, 255);
                                headIcon = "globe"; headLabel = "Link"; break;
                            case ClipboardCategory.Snippets:
                                headBg = Color.FromArgb(255, 48, 32, 64); headInk = Color.FromArgb(255, 191, 128, 255);
                                headIcon = "clipboard"; headLabel = "Snippet"; break;
                            default:
                                headBg = Color.FromArgb(255, 34, 34, 36); headInk = theme.TextPrimary;
                                headIcon = "file-text"; headLabel = "Text"; break;
                        }

                        // Header band (26 DIP) with crisp regular-weight label and generous gap before "now"
                        r.FillRoundedRect(new RectangleF(cx, card.Y, 114.0f, 26.0f), 14.0f, headBg);
                        r.FillRoundedRect(new RectangleF(cx, card.Y + 13.0f, 114.0f, 13.0f), 0.0f, headBg);
                        r.DrawLucideIcon(headIcon, cx + 15.0f, card.Y + 13.0f, 13.0f, headInk);
                        r.DrawTextStr(headLabel, new RectangleF(cx + 25.0f, card.Y, 54.0f, 26.0f), 12.0f, false, headInk);

                        long age = item.TimestampMs > 0 ? Math.Max(0, (ConfigManager.GetTickMs() - item.TimestampMs) / 1000) : -1;
                        string when = age < 0 ? "" : age < 60 ? "now" : age < 3600 ? $"{age / 60}m" : $"{age / 3600}h";
                        r.DrawTextStr(when, new RectangleF(cx + 76.0f, card.Y, 30.0f, 26.0f), 11.0f, false,
                            Color.FromArgb(215, 255, 255, 255), hAlign: StringAlignment.Far);

                        string imageDims = "Image";
                        if (item.Category == ClipboardCategory.Images)
                        {
                            var shot = new RectangleF(card.X, card.Y + 26.0f, card.Width, card.Height - 46.0f);
                            bool drew = false;
                            using (var clipPath = RoundedRectPath(card, 14.0f))
                            {
                                using Region prev = r.G.Clip;
                                r.G.SetClip(clipPath, CombineMode.Intersect);
                                if (!string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                                {
                                    try
                                    {
                                        using var img = Image.FromFile(item.ImagePath);
                                        r.G.DrawImage(img, shot);
                                        imageDims = $"{img.Width} \u00D7 {img.Height}";
                                        drew = true;
                                    }
                                    catch { drew = false; }
                                }
                                if (!drew)
                                {
                                    r.FillRoundedRect(shot, 0.0f, Color.FromArgb(18, 255, 255, 255));
                                    r.DrawLucideIcon(headIcon, shot.X + 57.0f, shot.Y + 34.0f, 22.0f, headInk);
                                }
                                r.FillRoundedRect(new RectangleF(card.X, card.Bottom - 20.0f, card.Width, 20.0f), 0.0f, Color.FromArgb(225, 16, 16, 18));
                                r.G.Clip = prev;
                            }
                        }
                        else if (item.Category == ClipboardCategory.Files)
                        {
                            bool single = item.FilePaths.Count == 1;
                            Bitmap? miniArt = Art(single ? "clip-mini-file" : "clip-mini-trio");
                            if (miniArt != null)
                            {
                                if (single)
                                    r.G.DrawImage(miniArt, cx + 10.0f, card.Y + 32.0f, 26.0f, 30.0f);
                                else
                                    r.G.DrawImage(miniArt, cx + 10.0f, card.Y + 32.0f, 60.0f, 30.0f);
                            }
                            else
                            {
                                for (int s2 = single ? 0 : 2; s2 >= 0; --s2)
                                {
                                    var mini = new RectangleF(cx + 14.0f + s2 * 8.0f, card.Y + 40.0f + s2 * 2.0f, 30.0f, 24.0f);
                                    Color tint = single || s2 == 0 ? Color.FromArgb(255, 247, 210, 214)
                                        : s2 == 1 ? Color.FromArgb(255, 232, 67, 111) : Color.FromArgb(255, 196, 144, 46);
                                    r.FillRoundedRect(mini, 4.0f, tint);
                                    r.DrawLucideIcon("file-text", mini.X + 15.0f, mini.Y + 12.0f, 12.0f, Color.FromArgb(200, 20, 20, 24));
                                }
                            }
                            string singleName = single ? Path.GetFileName(item.FilePaths[0]) : "";
                            string fileTitle = single
                                ? (!string.IsNullOrWhiteSpace(item.Preview) && item.Preview != singleName
                                    ? item.Preview
                                    : Path.GetFileNameWithoutExtension(singleName))
                                : $"{Math.Max(1, item.FilePaths.Count)} files";
                            r.DrawTextStr(fileTitle, new RectangleF(cx + 10.0f, card.Y + 65.0f, 96.0f, 18.0f), single ? 12.5f : 13.5f, false, theme.TextPrimary);
                            if (item.FilePaths.Count > 0)
                                r.DrawTextStr(EndEllipsis(Path.GetFileName(item.FilePaths[0]), 15), new RectangleF(cx + 10.0f, card.Y + 83.0f, 98.0f, 15.0f),
                                    single ? 12.0f : 11.0f, false, single ? theme.TextPrimary : theme.TextMuted);
                        }
                        else
                        {
                            string body = string.IsNullOrWhiteSpace(item.Preview) ? item.Text : item.Preview;
                            string trimmed = (body ?? "").Trim();
                            if (trimmed.StartsWith("#") && (trimmed.Length == 7 || trimmed.Length == 4))
                            {
                                try
                                {
                                    Color swatch = ColorTranslator.FromHtml(trimmed);
                                    r.FillRoundedRect(new RectangleF(cx + 10.0f, card.Y + 34.0f, 94.0f, 44.0f), 10.0f, swatch);
                                    r.DrawTextStr(trimmed.ToUpperInvariant(), new RectangleF(cx + 10.0f, card.Y + 82.0f, 94.0f, 16.0f), 12.0f, false, theme.TextPrimary, mono: true);
                                }
                                catch
                                {
                                    r.DrawTextStr(body, new RectangleF(cx + 10.0f, card.Y + 34.0f, 94.0f, 64.0f), 12.0f, false, theme.TextPrimary, noWrap: false);
                                }
                            }
                            else
                            {
                                r.DrawTextStr(body, new RectangleF(cx + 10.0f, card.Y + 34.0f, 94.0f, 64.0f), 12.0f, false,
                                    theme.TextPrimary, noWrap: false);
                            }
                        }

                        string footer = item.Category == ClipboardCategory.Images ? imageDims : item.SourceInfo;
                        r.DrawTextStr(footer, new RectangleF(cx + 10.0f, card.Bottom - 18.0f, 98.0f, 15.0f), 11.0f, false,
                            item.Category == ClipboardCategory.Images ? theme.TextPrimary : theme.TextMuted);

                        int capI = i;
                        r.AddHitRegion($"clip-row-{i}", card, () =>
                        {
                            foreach (var e in state.ClipboardHistory) e.IsCurrent = false;
                            if (capI < shown.Count)
                            {
                                shown[capI].IsCurrent = true;
                                try { Clipboard.SetText(shown[capI].Text); } catch { }
                            }
                        });
                    }

                    if (shown.Count == 0)
                    {
                        var emptyClip = new RectangleF(bodyLeft, bodyTop + 31.0f, bodyW, 108.0f);
                        r.FillRoundedRect(emptyClip, 14.0f, Color.FromArgb(14, 255, 255, 255));
                        r.DrawTextStr("Nothing copied yet \u00B7 Press Ctrl+C in any app", emptyClip, 12.5f, false, theme.TextFaint, hAlign: StringAlignment.Center);
                    }
                    else
                    {
                        // The demo's chevron is already baked into the image card's own
                        // photograph, so it is drawn only for the text/file cards.
                        float chevX = bodyLeft + 357.0f, chevY = bodyTop + 92.0f;
                        if (shown[0].Category != ClipboardCategory.Images)
                        {
                            r.FillCircle(chevX, chevY, 15.0f, Color.FromArgb(240, 30, 30, 32));
                            r.DrawLucideIcon("chevron-right", chevX, chevY, 16.0f, theme.TextPrimary);
                        }
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
                        r.DrawTextStr(now.ToString("MMMM yyyy"),
                            new RectangleF(bodyLeft, bodyTop - 1, 152, 22), 13.5f, true, theme.Accent);

                        // Clean < and > month navigation pills
                        var prevMo = new RectangleF(bodyLeft + 150, bodyTop, 22, 21);
                        var nextMo = new RectangleF(bodyLeft + 175, bodyTop, 22, 21);
                        r.FillRoundedRect(prevMo, 7, Color.FromArgb(24, 255, 255, 255));
                        r.FillRoundedRect(nextMo, 7, Color.FromArgb(24, 255, 255, 255));
                        r.DrawTextStr("\u2039", prevMo, 13.0f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        r.DrawTextStr("\u203A", nextMo, 13.0f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        r.AddHitRegion("cal-prev", prevMo, () => state.CalendarMonthOffset--);
                        r.AddHitRegion("cal-next", nextMo, () => state.CalendarMonthOffset++);

                        var backBtn = new RectangleF(bodyLeft + bodyW - 76, bodyTop, 76, 21);
                        r.FillRoundedRect(backBtn, 8, Color.FromArgb(26, 255, 255, 255));
                        r.DrawTextStr("\u2190 Back", backBtn, 10.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                        r.AddHitRegion("cal-back", backBtn, () => state.ShowCalendarInIsland = false);

                        float gridLeft = bodyLeft;
                        float cellW = 28.5f;
                        float cellH = 18.5f;
                        string[] dow = { "M", "T", "W", "T", "F", "S", "S" };
                        for (int d = 0; d < 7; ++d)
                        {
                            Color dowCol = d >= 5 ? Color.FromArgb(165, theme.Accent.R, theme.Accent.G, theme.Accent.B) : theme.TextFaint;
                            r.DrawTextStr(dow[d], new RectangleF(gridLeft + d * cellW, bodyTop + 24, cellW, 15), 9.0f, true, dowCol, hAlign: StringAlignment.Center);
                        }

                        DateTime firstDay = new(now.Year, now.Month, 1);
                        int startDow = ((int)firstDay.DayOfWeek + 6) % 7; // Monday-first
                        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
                        int todayDay = (state.CalendarMonthOffset == 0) ? DateTime.Now.Day : -1;

                        for (int day = 1; day <= daysInMonth; ++day)
                        {
                            int slot = startDow + day - 1;
                            int col = slot % 7;
                            int row = slot / 7;
                            if (row >= 6) break;
                            float cx = gridLeft + col * cellW + cellW * 0.5f;
                            float cy = bodyTop + 40 + row * cellH + cellH * 0.5f;
                            var cRect = new RectangleF(gridLeft + col * cellW, bodyTop + 40 + row * cellH, cellW, cellH);
                            bool isToday = day == todayDay;
                            if (isToday)
                            {
                                r.FillCircle(cx, cy, 8.2f, theme.Accent);
                            }
                            Color dayCol = isToday
                                ? ThemeSpec.AdaptiveInkForBackground(theme.Accent)
                                : (col >= 5 ? theme.TextMuted : theme.TextPrimary);
                            r.DrawTextStr(day.ToString(), cRect, 9.5f, isToday, dayCol, hAlign: StringAlignment.Center);
                        }

                        float rightX = bodyLeft + 206.0f;
                        float rightW = bodyW - 206.0f;
                        var evtCard = new RectangleF(rightX, bodyTop + 26, rightW, 74);
                        r.FillRoundedRect(evtCard, 12, Color.FromArgb(20, 255, 255, 255));
                        r.DrawTextStr("Upcoming", new RectangleF(rightX + 10, bodyTop + 32, rightW - 20, 18), 11.0f, true, theme.Accent);
                        string evtLine = state.Reminders.Count > 0 ? $"\u23F0 {state.Reminders[0].Title}" : s.UpcomingMeetingTitle;
                        r.DrawTextStr(evtLine, new RectangleF(rightX + 10, bodyTop + 52, rightW - 20, 42), 10.5f, false, theme.TextMuted, noWrap: false);

                        var pinDayClockBtn = new RectangleF(rightX, bodyTop + 108, rightW, 34);
                        r.FillRoundedRect(pinDayClockBtn, 11, theme.Accent);
                        r.DrawTextStr("Pin Calendar Widget", pinDayClockBtn, 10.5f, true, ThemeSpec.AdaptiveInkForBackground(theme.Accent), hAlign: StringAlignment.Center);
                        r.AddHitRegion("cal-pin-dayclock", pinDayClockBtn, () => onToggleDesktopWidget?.Invoke(WidgetType.Calendar));
                        break;
                    }

                    // The hero capture: clock, to-do, weather and battery cards over a
                    // two-by-two grid with the reminder bell sitting in the cross.
                    DateTime dt = DateTime.Now;
                    float tileW = 180.0f;
                    float topH = 62.0f;
                    float botY = bodyTop + 70.0f;
                    float botH = 69.0f;

                    // Clock + world clock line (exact match to notchpixel.com widgets.webp)
                    var t0 = new RectangleF(bodyLeft, bodyTop, tileW, topH);
                    r.FillRoundedRect(t0, 14.0f, Color.FromArgb(24, 255, 255, 255));
                    string time = s.Use24HourClock ? dt.ToString("HH:mm") : dt.ToString("h:mm");
                    r.DrawTextStr(time, new RectangleF(t0.X + 10.0f, t0.Y + 3.0f, 96.0f, 30.0f), 23.0f, true, theme.TextPrimary);
                    if (!s.Use24HourClock)
                    {
                        float ampmX = t0.X + (time.Length >= 5 ? 96.0f : 80.0f);
                        r.DrawTextStr(dt.ToString("tt").ToUpperInvariant(), new RectangleF(ampmX, t0.Y + 14.0f, 34.0f, 16.0f), 10.0f, true, theme.TextMuted);
                    }
                    r.DrawTextStr(dt.ToString("ddd").ToUpperInvariant(), new RectangleF(t0.Right - 46.0f, t0.Y + 8.0f, 34.0f, 16.0f), 11.5f, false, theme.TextPrimary, hAlign: StringAlignment.Far);
                    r.DrawLucideIcon("globe", t0.X + 18.0f, t0.Bottom - 15.0f, 13.0f, theme.TextMuted);
                    string worldCity = string.IsNullOrWhiteSpace(s.WorldClockCity) ? "Tokyo" : s.WorldClockCity;
                    DateTime worldDt = DateTime.UtcNow.AddHours(s.WorldClockOffsetHours);
                    string worldTime = s.Use24HourClock ? worldDt.ToString("HH:mm") : worldDt.ToString("h:mm tt");
                    r.DrawTextStr(worldCity, new RectangleF(t0.X + 28.0f, t0.Bottom - 24.0f, 76.0f, 18.0f), 12.5f, false, theme.TextPrimary);
                    r.DrawTextStr(worldTime, new RectangleF(t0.Right - 82.0f, t0.Bottom - 24.0f, 70.0f, 18.0f), 12.5f, false, theme.TextPrimary, hAlign: StringAlignment.Far);
                    r.AddHitRegion("w-cal-open", t0, () => state.ShowCalendarInIsland = true);

                    // To-do (real items, tapped to tick off)
                    var t1 = new RectangleF(bodyLeft + tileW + 8.0f, bodyTop, tileW, topH);
                    r.FillRoundedRect(t1, 14.0f, Color.FromArgb(24, 255, 255, 255));
                    r.DrawTextStr("To do", new RectangleF(t1.X + 14.0f, t1.Y + 6.0f, 44.0f, 18.0f), 13.0f, true, theme.TextPrimary);
                    r.DrawTextStr(state.TodoList.Count.ToString(), new RectangleF(t1.X + 54.0f, t1.Y + 6.0f, 24.0f, 18.0f), 13.0f, false, theme.TextPrimary);
                    float plusCx = t1.Right - 20.0f, plusCy = t1.Y + 16.0f;
                    r.FillCircle(plusCx, plusCy, 10.5f, Color.FromArgb(24, 255, 255, 255));
                    r.DrawLucideIcon("plus", plusCx, plusCy, 11.0f, theme.TextMuted);
                    r.AddHitRegion("w-todo-add", new RectangleF(plusCx - 12, plusCy - 12, 24, 24), () => onToggleDesktopWidget?.Invoke(WidgetType.TodoList));

                    int rowCount = Math.Min(2, state.TodoList.Count);
                    if (rowCount == 0)
                    {
                        r.DrawTextStr("Nothing planned", new RectangleF(t1.X + 14.0f, t1.Y + 28.0f, t1.Width - 28.0f, 18.0f), 12.5f, false, theme.TextFaint);
                    }
                    for (int i = 0; i < rowCount; ++i)
                    {
                        TodoItem todo = state.TodoList[i];
                        float ty = t1.Y + 26.0f + i * 17.5f;
                        if (todo.Done)
                        {
                            r.FillCircle(t1.X + 20.0f, ty + 6.0f, 5.8f, theme.Accent);
                            r.DrawLucideIcon("check", t1.X + 20.0f, ty + 6.0f, 8.5f, Color.FromArgb(255, 12, 12, 14), 2.0f);
                        }
                        else
                        {
                            r.DrawCircle(t1.X + 20.0f, ty + 6.0f, 5.5f, Color.FromArgb(128, 255, 255, 255), 1.4f);
                        }
                        r.DrawTextStr(todo.Text, new RectangleF(t1.X + 32.0f, ty - 2.5f, t1.Width - 44.0f, 17.0f), 12.5f, false, theme.TextPrimary);
                        int capT = i;
                        r.AddHitRegion($"w-todo-{i}", new RectangleF(t1.X + 10.0f, ty - 4.0f, t1.Width - 20.0f, 19.0f),
                            () => state.TodoList[capT].Done = !state.TodoList[capT].Done);
                    }

                    // Weather: warm amber-gold sun gradient into deep slate-blue (exact match to widgets.webp)
                    var t2 = new RectangleF(bodyLeft, botY, tileW, botH);
                    using (var wPath = RoundedRectPath(t2, 14.0f))
                    using (var wGrad = new LinearGradientBrush(t2, Color.FromArgb(225, 138, 96, 22), Color.FromArgb(235, 24, 36, 52), 18.0f))
                    {
                        r.G.FillPath(wGrad, wPath);
                    }
                    r.DrawLucideIcon("sun", t2.X + 26.0f, t2.Y + 25.0f, 25.0f, Color.FromArgb(255, 245, 184, 46), 2.0f);
                    r.DrawTextStr($"{s.WeatherTempC}\u00B0", new RectangleF(t2.X + 48.0f, t2.Y + 5.0f, 92.0f, 36.0f), 27.0f, true, theme.TextPrimary);
                    r.DrawTextStr($"Sunny \u00B7 {s.WeatherCity}", new RectangleF(t2.X + 12.0f, t2.Bottom - 21.0f, 98.0f, 17.0f), 11.5f, false, Color.FromArgb(235, 239, 237, 232));
                    r.DrawTextStr($"H {s.WeatherHighC}\u00B0  L {s.WeatherLowC}\u00B0", new RectangleF(t2.Right - 80.0f, t2.Bottom - 21.0f, 68.0f, 17.0f), 11.5f, false, Color.FromArgb(220, 239, 237, 232), hAlign: StringAlignment.Far);
                    r.AddHitRegion("w-weather", t2, () => onToggleDesktopWidget?.Invoke(WidgetType.MinimalWeather));

                    // Battery: clean icon + This PC + vibrant green bar + 100%
                    var t3 = new RectangleF(bodyLeft + tileW + 8.0f, botY, tileW, botH);
                    r.FillRoundedRect(t3, 14.0f, Color.FromArgb(24, 255, 255, 255));
                    r.DrawLucideIcon("battery", t3.X + 21.0f, t3.Y + 35.0f, 16.0f, theme.TextMuted);
                    r.DrawTextStr("This PC", new RectangleF(t3.X + 33.0f, t3.Y + 26.0f, 54.0f, 18.0f), 12.5f, false, theme.TextPrimary);
                    int pct = Math.Clamp(state.Telemetry.BatteryPercent, 0, 100);
                    var track = new RectangleF(t3.X + 86.0f, t3.Y + 32.5f, 38.0f, 5.0f);
                    r.FillRoundedRect(track, 2.5f, Color.FromArgb(36, 255, 255, 255));
                    if (pct > 0)
                        r.FillRoundedRect(new RectangleF(track.X, track.Y, Math.Max(4.0f, track.Width * pct / 100.0f), 5.0f), 2.5f, Color.FromArgb(255, 42, 209, 91));
                    r.DrawTextStr($"{pct}%", new RectangleF(t3.Right - 54.0f, t3.Y + 26.0f, 46.0f, 18.0f), 12.5f, false, Color.FromArgb(255, 42, 209, 91), hAlign: StringAlignment.Far);
                    r.AddHitRegion("w-stats", t3, () => onToggleDesktopWidget?.Invoke(WidgetType.PcStats));

                    // Central floating reminder bell with dark cutout ring
                    float bellCx = bodyLeft + 184.0f;
                    float bellCy = bodyTop + 66.0f;
                    r.FillCircle(bellCx, bellCy, 21.5f, Color.FromArgb(255, 11, 11, 12));
                    r.FillCircle(bellCx, bellCy, 18.5f, Color.FromArgb(255, 34, 34, 36));
                    r.DrawCircle(bellCx, bellCy, 18.5f, Color.FromArgb(44, 255, 255, 255), 1.0f);
                    r.DrawLucideIcon("bell", bellCx, bellCy, 15.5f, theme.TextPrimary);
                    r.AddHitRegion("w-bell", new RectangleF(bellCx - 20, bellCy - 20, 40, 40), () =>
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
                    // Left: Tall vertical pill sliders for Brightness & Volume (NotchPixel layout)
                    float pillW = 50.0f;
                    float pillH = 122.0f;
                    float pillGap = 8.0f;
                    float rad = 14.0f * s.CornerRadiusScale;

                    void DrawVerticalSlider(string id, float px, float pct, string iconName, Action<float> onDrag)
                    {
                        var track = new RectangleF(px, bodyTop, pillW, pillH);
                        r.FillRoundedRect(track, rad, Color.FromArgb(24, 255, 255, 255));
                        float frac = Math.Clamp(pct / 100.0f, 0.0f, 1.0f);
                        float fillH = Math.Max(14.0f, pillH * frac);
                        var fillRect = new RectangleF(px, bodyTop + pillH - fillH, pillW, fillH);
                        Color fillCol = Color.FromArgb(242, 240, 240, 242);
                        r.FillRoundedRect(fillRect, rad, fillCol);
                        Color topInk = frac > 0.80f ? Color.FromArgb(255, 16, 16, 18) : theme.TextPrimary;
                        Color botInk = frac > 0.14f ? Color.FromArgb(255, 16, 16, 18) : theme.TextPrimary;
                        r.DrawTextStr($"{(int)MathF.Round(pct)}%", new RectangleF(px, bodyTop + 6, pillW, 18), 10.5f, true, topInk, mono: true, hAlign: StringAlignment.Center);
                        r.DrawLucideIcon(iconName, px + pillW * 0.5f, bodyTop + pillH - 18, 15, botInk);
                        r.AddHitRegion(id, track, null, onDrag, verticalDrag: true);
                    }

                    DrawVerticalSlider("c-br", bodyLeft, state.MonitorBrightness, "sun",
                        n => WindowsSystemHooks.SetMonitorBrightnessLevel(state, n * 100.0f, showPopup: false));
                    DrawVerticalSlider("c-vl", bodyLeft + pillW + pillGap, state.MasterOutputMuted ? 0.0f : state.MasterVolume, "volume",
                        n => WindowsSystemHooks.SetMasterVolume(state, n * 100.0f, showPopup: false));

                    // Right: 2x3 quick toggles grid (exact match to notchpixel.com controls.webp)
                    float rightLeft = bodyLeft + (pillW + pillGap) * 2.0f + 4.0f;
                    float rightW = (bodyLeft + bodyW) - rightLeft;
                    float tileW = (rightW - 12.0f) / 3.0f;
                    float tileH = 57.0f;

                    (string label, string sub, string icon, bool active, bool isAlert, Action act)[] toggles =
                    {
                        ("Wi-Fi", state.WifiEnabled ? "On" : "Off", "wifi", state.WifiEnabled, false, () => WindowsSystemHooks.SetWifiEnabled(state, !state.WifiEnabled)),
                        ("Bluetooth", state.BluetoothEnabled ? "On" : "Off", "bluetooth", state.BluetoothEnabled, false, () => WindowsSystemHooks.SetBluetoothEnabled(state, !state.BluetoothEnabled)),
                        ("Night light", state.NightLightEnabled ? "On" : "Off", "moon", state.NightLightEnabled, false, () => { state.NightLightEnabled = !state.NightLightEnabled; WindowsSystemHooks.SetNightLightEnabled(state.NightLightEnabled); }),
                        ("Focus", state.FocusModeEnabled ? "On" : "Off", "bell-off", state.FocusModeEnabled, false, () => WindowsSystemHooks.SetFocusModeEnabled(state, !state.FocusModeEnabled)),
                        ("Sound", state.MasterOutputMuted ? "Muted" : "On", state.MasterOutputMuted ? "volume-x" : "volume-2", state.MasterOutputMuted, state.MasterOutputMuted, () => WindowsSystemHooks.ToggleMasterOutputMute(state, showPopup: false)),
                        ("Mic", state.GlobalMicMuted ? "Muted" : "Live", "mic", state.GlobalMicMuted, state.GlobalMicMuted, () => WindowsSystemHooks.ToggleGlobalMicMute(state))
                    };

                    for (int i = 0; i < 6; ++i)
                    {
                        float bx = rightLeft + (i % 3) * (tileW + 6.0f);
                        float by = bodyTop + (i / 3) * (tileH + 8.0f);
                        var bRect = new RectangleF(bx, by, tileW, tileH);
                        bool act = toggles[i].active;
                        bool isAlert = toggles[i].isAlert;
                        Color cardCol = isAlert
                            ? Color.FromArgb(220, 74, 22, 24)
                            : (act ? Color.FromArgb(230, 19, 45, 73) : Color.FromArgb(24, 255, 255, 255));
                        Color circleCol = isAlert
                            ? Color.FromArgb(255, 255, 69, 58)
                            : (act ? Color.FromArgb(255, 10, 132, 255) : Color.FromArgb(34, 255, 255, 255));
                        Color iconCol = (act || isAlert) ? Color.White : theme.TextPrimary;

                        r.FillRoundedRect(bRect, 13.0f, cardCol);
                        r.FillCircle(bx + 17.0f, by + 16.0f, 10.5f, circleCol);
                        r.DrawLucideIcon(toggles[i].icon, bx + 17.0f, by + 16.0f, 11.5f, iconCol, 1.6f);
                        r.DrawTextStr(toggles[i].label, new RectangleF(bx + 8.0f, by + 29.0f, tileW - 12.0f, 14.0f), 10.5f, true, theme.TextPrimary);
                        r.DrawTextStr(toggles[i].sub, new RectangleF(bx + 8.0f, by + 42.0f, tileW - 12.0f, 13.0f), 9.5f, false, theme.TextMuted);
                        r.AddHitRegion($"c-t-{i}", bRect, toggles[i].act);
                    }

                    // Bottom: 3-segment power mode pill
                    float pwrY = bodyTop + pillH + 10.0f;
                    var pwrTrack = new RectangleF(bodyLeft, pwrY, bodyW, 28.0f);
                    r.FillRoundedRect(pwrTrack, 14, Color.FromArgb(20, 255, 255, 255));
                    string[] pwrModes = { "Battery", "Balanced", "Performance" };
                    float segW = (bodyW - 6.0f) / 3.0f;
                    for (int p = 0; p < 3; ++p)
                    {
                        var segRect = new RectangleF(bodyLeft + 3.0f + p * segW, pwrY + 3.0f, segW, 22.0f);
                        bool pAct = (state.PowerModeIndex % 3) == p;
                        if (pAct) r.FillRoundedRect(segRect, 11, theme.Accent);
                        r.DrawTextStr(pwrModes[p], segRect, 10.5f, true,
                            pAct ? ThemeSpec.AdaptiveInkForBackground(theme.Accent) : theme.TextMuted, hAlign: StringAlignment.Center);
                        int capP = p;
                        r.AddHitRegion($"c-pwr-{p}", segRect, () => WindowsSystemHooks.SetPowerMode(state, capP));
                    }
                    break;
                }

                case PanelId.Launcher:
                {
                    // The hero capture: Screenshot / Snip / Record across the top, then
                    // six dashed 50 DIP slots with their Add labels underneath.
                    string[] capLabels = { "Screenshot", "Snip", "Record" };
                    string[] capIcons = { "camera", "scissors", "" };
                    for (int i = 0; i < 3; ++i)
                    {
                        float bx = left + 16.0f + i * 126.0f;
                        var bRect = new RectangleF(bx, top + 28.0f, 118.0f, 39.0f);
                        float bHov = EaseHover(state, $"l-cap-h-{i}", r.IsHovered(bRect, state.PointerDipX, state.PointerDipY));
                        r.FillRoundedRect(bRect, 14.0f, Color.FromArgb((int)(26 + 14 * bHov), 255, 255, 255));
                        if (i == 2)
                        {
                            r.FillCircle(bx + 26.0f, top + 47.5f, 5.0f, Color.FromArgb(255, 255, 69, 58));
                            r.DrawTextStr(capLabels[i], new RectangleF(bx + 38.0f, top + 36.0f, 76.0f, 24.0f), 12.5f, false, theme.TextPrimary);
                        }
                        else
                        {
                            float ix = i == 0 ? bx + 18.0f : bx + 34.0f;
                            r.DrawLucideIcon(capIcons[i], ix, top + 47.5f, 15.0f, theme.TextPrimary);
                            r.DrawTextStr(capLabels[i], new RectangleF(ix + 12.0f, top + 36.0f, 86.0f, 24.0f), 12.5f, false, theme.TextPrimary);
                        }
                        int capI = i;
                        r.AddHitRegion($"l-cap-{i}", bRect, () =>
                        {
                            if (capI == 0)
                            {
                                state.IslandOpen = false;
                                WindowsSystemHooks.CaptureFullScreenshot(state);
                            }
                            else if (capI == 1)
                            {
                                state.IslandOpen = false;
                                try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
                                catch { WindowsSystemHooks.LaunchRegionSnip(state); }
                            }
                            else
                            {
                                state.IslandOpen = false;
                                if (state.IsRecordingScreen)
                                    WindowsSystemHooks.StopScreenRecording(state);
                                else
                                    WindowsSystemHooks.StartScreenRecording(state);
                            }
                        });
                    }

                    for (int i = 0; i < 6; ++i)
                    {
                        float sx = left + 16.0f + i * 63.0f;
                        var sRect = new RectangleF(sx, top + 76.0f, 50.0f, 46.0f);
                        var slot = state.LauncherSlots[i];
                        bool configured = slot != null && !string.IsNullOrEmpty(slot.TargetPath);
                        float sHov = EaseHover(state, $"l-slot-h-{i}", r.IsHovered(sRect, state.PointerDipX, state.PointerDipY));
                        if (configured)
                        {
                            r.FillRoundedRect(sRect, 12.0f, Color.FromArgb((int)(18 + 14 * sHov), slot.BadgeColor.R, slot.BadgeColor.G, slot.BadgeColor.B));
                            r.FillCircle(sx + 25.0f, top + 95.0f, 11.0f, slot.BadgeColor);
                            r.DrawTextStr((i + 1).ToString(), new RectangleF(sx, top + 84.0f, 50.0f, 22.0f), 11.0f, true, Color.White, mono: true, hAlign: StringAlignment.Center);
                            r.DrawTextStr(slot.Label, new RectangleF(sx - 6.0f, top + 124.0f, 62.0f, 18.0f), 12.5f, false, theme.TextMuted, hAlign: StringAlignment.Center);
                        }
                        else
                        {
                            using var dash = new Pen(Color.FromArgb((int)(70 + 40 * sHov), theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B), 1.3f);
                            dash.DashStyle = DashStyle.Custom;
                            dash.DashPattern = new[] { 3.5f, 3.5f };
                            r.G.DrawPath(dash, RoundedRectPath(sRect, 12.0f));
                            r.DrawLucideIcon("plus", sx + 25.0f, top + 99.0f, 13.0f, theme.TextMuted);
                            r.DrawTextStr("Add", new RectangleF(sx - 6.0f, top + 124.0f, 62.0f, 18.0f), 13.5f, false,
                                Color.FromArgb(210, theme.TextPrimary.R, theme.TextPrimary.G, theme.TextPrimary.B), hAlign: StringAlignment.Center);
                        }
                        int slotIdx = i;
                        r.AddHitRegion($"l-slot-{i}", sRect, () => WindowsSystemHooks.ConfigureLauncherSlot(state, slotIdx));
                    }
                    break;
                }

                case PanelId.Calls:
                {
                    // Mic ring on the left, live meeting on the right: title + Join,
                    // Speakers and Microphone rows with their underline and Test pills,
                    // then Check camera.
                    float micCx = bodyLeft + 70.0f;
                    float micCy = bodyTop + 48.0f;
                    bool muted = state.GlobalMicMuted;
                    Color live = muted ? Color.FromArgb(255, 255, 69, 58) : Color.FromArgb(255, 42, 209, 91);
                    float clock = ConfigManager.GetTickMs() / 1000.0f;
                    float level = Math.Clamp(state.MicPeakLevel, 0.0f, 1.0f);
                    float pulse = muted ? 0.0f : 0.5f + 0.5f * MathF.Sin(clock * 2.6f);

                    r.FillCircle(micCx, micCy, 42.0f + level * 4.0f, Color.FromArgb((int)(20 + 12 * pulse), live.R, live.G, live.B));
                    r.FillCircle(micCx, micCy, 34.0f, Color.FromArgb(52, live.R, live.G, live.B));
                    r.DrawCircle(micCx, micCy, 34.0f, Color.FromArgb(230, live.R, live.G, live.B), 2.0f);
                    r.DrawLucideIcon("mic", micCx, micCy, 24.0f, theme.TextPrimary, 1.8f);
                    r.DrawTextStr(muted ? "Muted" : "Hearing you", new RectangleF(micCx - 62.0f, bodyTop + 91.0f, 124.0f, 17.0f), 12.5f, false, live, hAlign: StringAlignment.Center);
                    r.DrawTextStr(muted ? "Click to unmute" : "Click to mute", new RectangleF(micCx - 62.0f, bodyTop + 108.0f, 124.0f, 17.0f), 12.5f, false, theme.TextPrimary, hAlign: StringAlignment.Center);
                    r.AddHitRegion("calls-mic", new RectangleF(micCx - 42.0f, micCy - 42.0f, 84.0f, 84.0f), () => WindowsSystemHooks.ToggleGlobalMicMute(state));

                    float rx = bodyLeft + 142.0f;
                    float rw = 222.0f;
                    r.DrawTextStr(s.UpcomingMeetingTitle, new RectangleF(rx, bodyTop + 0.0f, 150.0f, 20.0f), 14.0f, false, theme.TextPrimary);

                    var joinBtn = new RectangleF(bodyLeft + 296.0f, bodyTop + 1.0f, 68.0f, 28.0f);
                    r.FillRoundedRect(joinBtn, 14.0f, Color.FromArgb(255, 42, 209, 91));
                    r.DrawLucideIcon("video", joinBtn.X + 15.0f, joinBtn.Y + 14.0f, 11.5f, Color.FromArgb(255, 12, 12, 14));
                    r.DrawTextStr("Join", new RectangleF(joinBtn.X + 24.0f, joinBtn.Y, 40.0f, 28.0f), 11.5f, true, Color.FromArgb(255, 12, 12, 14));
                    r.AddHitRegion("calls-join", joinBtn, () =>
                    {
                        if (string.IsNullOrWhiteSpace(s.UpcomingMeetingUrl)) return;
                        try { Process.Start(new ProcessStartInfo(s.UpcomingMeetingUrl) { UseShellExecute = true }); } catch { }
                    });

                    r.DrawLucideIcon("calendar", rx + 7.0f, bodyTop + 31.0f, 12.0f, theme.TextMuted);
                    r.DrawTextStr(s.UpcomingMeetingTime, new RectangleF(rx + 18.0f, bodyTop + 22.0f, 180.0f, 18.0f), 12.0f, false, theme.TextPrimary);

                    void DeviceRow(string id, float y, string icon, string label, Color tintFill, Color underline, Action test)
                    {
                        var row = new RectangleF(rx, bodyTop + y, rw, 25.0f);
                        r.FillRoundedRect(row, 9.0f, Color.FromArgb(22, 255, 255, 255));
                        var sel = new RectangleF(row.X, row.Y, 108.0f, row.Height);
                        r.FillRoundedRect(sel, 9.0f, tintFill);
                        r.FillRoundedRect(new RectangleF(sel.X + 4.0f, sel.Bottom - 2.5f, sel.Width - 6.0f, 2.5f), 1.2f, underline);
                        r.DrawLucideIcon(icon, sel.X + 16.0f, sel.Y + 12.5f, 13.5f, theme.TextPrimary);
                        r.DrawTextStr(label, new RectangleF(sel.X + 27.0f, sel.Y, 72.0f, 25.0f), 12.0f, false, theme.TextPrimary);
                        r.DrawLucideIcon("chevron-down", sel.Right - 6.0f, sel.Y + 12.5f, 9.0f, theme.TextMuted);
                        var testBtn = new RectangleF(row.Right - 42.0f, row.Y + 4.0f, 36.0f, 17.0f);
                        r.FillRoundedRect(testBtn, 8.5f, Color.FromArgb(24, 255, 255, 255));
                        r.DrawTextStr("Test", testBtn, 11.0f, false, theme.TextPrimary, hAlign: StringAlignment.Center);
                        r.AddHitRegion(id + "-test", testBtn, test);
                        r.AddHitRegion(id, sel, test);
                    }
                    DeviceRow("calls-spk", 48.0f, "headphones", "Speakers", Color.FromArgb(145, 28, 66, 86), Color.FromArgb(255, 68, 168, 232), () => System.Media.SystemSounds.Asterisk.Play());
                    DeviceRow("calls-mic-src", 80.0f, "mic", "Microphone", Color.FromArgb(145, 26, 68, 42), Color.FromArgb(255, 42, 209, 91), () => System.Media.SystemSounds.Asterisk.Play());

                    var camBtn = new RectangleF(rx, bodyTop + 112.0f, 115.0f, 24.0f);
                    r.FillRoundedRect(camBtn, 12.0f, Color.FromArgb(26, 255, 255, 255));
                    r.DrawLucideIcon("camera", camBtn.X + 16.0f, camBtn.Y + 12.0f, 14.0f, theme.TextPrimary);
                    r.DrawTextStr("Check camera", new RectangleF(camBtn.X + 26.0f, camBtn.Y, 88.0f, 24.0f), 11.5f, true, theme.TextPrimary);
                    r.AddHitRegion("calls-cam", camBtn, () => { try { Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true }); } catch { } });
                    break;
                }
            }
        }

        // The shelf's tiles are illustrations at 58x58: a photo with its horizon, a
        // video still with a play badge, a vinyl disc, and paper documents for
        // PDF/ZIP. Files that exist on disk still take their real shell icon above.
        // Maps a file extension to the demo's own captured tile art.
        private static string ShelfTileArt(string ext) => ext switch
        {
            "PNG" or "JPG" or "JPEG" or "GIF" or "WEBP" or "BMP" => "shelf-tile-1",
            "MP4" or "MOV" or "WEBM" => "shelf-tile-2",
            "MP3" or "WAV" or "M4A" or "FLAC" => "shelf-tile-3",
            "PDF" => "shelf-tile-4",
            "ZIP" or "RAR" or "7Z" => "shelf-tile-5",
            _ => "shelf-tile-5"
        };

        private static void DrawShelfThumb(LiquidGlassRenderer r, RectangleF tile, string ext)
        {
            Graphics g = r.G;
            switch (ext)
            {
                case "PNG": case "JPG": case "JPEG": case "GIF": case "WEBP": case "BMP":
                {
                    // Polaroid: paper margin around a sunset photo with its horizon.
                    r.FillRoundedRect(tile, 10.0f, Color.FromArgb(255, 242, 240, 236));
                    var photo = new RectangleF(tile.X + 4.0f, tile.Y + 4.0f, tile.Width - 8.0f, tile.Height - 13.0f);
                    using (var path = RoundedRectPath(photo, 5.0f))
                    {
                        using Region prev = g.Clip;
                        g.SetClip(path, CombineMode.Intersect);
                        // The demo's photo: a rose-to-amber sky, a cream sun half sunk at
                        // the horizon, a navy sea and short cream reflection lines.
                        using (var sky = new LinearGradientBrush(photo, Color.FromArgb(255, 206, 62, 138), Color.FromArgb(255, 255, 170, 74), 90f))
                        {
                            var blend = new System.Drawing.Drawing2D.ColorBlend(3)
                            {
                                Colors = new[] { Color.FromArgb(255, 188, 44, 138), Color.FromArgb(255, 247, 108, 108), Color.FromArgb(255, 255, 186, 92) },
                                Positions = new[] { 0.0f, 0.55f, 1.0f }
                            };
                            sky.InterpolationColors = blend;
                            g.FillRectangle(sky, photo);
                        }
                        float horizon = photo.Bottom - 11.0f;
                        using (var sea = new SolidBrush(Color.FromArgb(255, 24, 34, 84)))
                            g.FillRectangle(sea, photo.X, horizon, photo.Width, photo.Bottom - horizon);
                        r.FillCircle(photo.X + photo.Width * 0.5f, horizon, 7.4f, Color.FromArgb(255, 255, 226, 158));
                        using (var glint = new SolidBrush(Color.FromArgb(190, 255, 214, 160)))
                        {
                            g.FillRectangle(glint, photo.X + photo.Width * 0.5f - 8.0f, horizon + 2.5f, 16.0f, 1.2f);
                            g.FillRectangle(glint, photo.X + photo.Width * 0.5f - 5.0f, horizon + 5.5f, 10.0f, 1.0f);
                        }
                        g.Clip = prev;
                    }
                    break;
                }
                case "MP4": case "MOV": case "WEBM":
                {
                    // Film strip: blue frame, sprocket holes, white play badge.
                    r.FillRoundedRect(tile, 10.0f, Color.FromArgb(255, 47, 107, 255));
                    using (var hole = new SolidBrush(Color.FromArgb(200, 18, 24, 46)))
                    {
                        for (int i = 0; i < 4; ++i)
                        {
                            g.FillRectangle(hole, tile.X + 7.0f + i * 12.0f, tile.Y + 5.0f, 6.0f, 4.0f);
                            g.FillRectangle(hole, tile.X + 7.0f + i * 12.0f, tile.Bottom - 9.0f, 6.0f, 4.0f);
                        }
                    }
                    r.FillCircle(tile.X + 29.0f, tile.Y + 29.0f, 13.0f, Color.FromArgb(240, 255, 255, 255));
                    using (var tri = new SolidBrush(Color.FromArgb(255, 20, 46, 110)))
                        g.FillPolygon(tri, new[] { new PointF(tile.X + 25.0f, tile.Y + 23.0f), new PointF(tile.X + 34.0f, tile.Y + 29.0f), new PointF(tile.X + 25.0f, tile.Y + 35.0f) });
                    break;
                }
                case "MP3": case "WAV": case "M4A": case "FLAC":
                {
                    r.FillRoundedRect(tile, 10.0f, Color.FromArgb(255, 18, 18, 21));
                    r.DrawCircle(tile.X + 29.0f, tile.Y + 29.0f, 25.0f, Color.FromArgb(26, 255, 255, 255), 1.0f);
                    r.DrawCircle(tile.X + 29.0f, tile.Y + 29.0f, 20.0f, Color.FromArgb(22, 255, 255, 255), 1.0f);
                    r.DrawCircle(tile.X + 29.0f, tile.Y + 29.0f, 15.0f, Color.FromArgb(18, 255, 255, 255), 1.0f);
                    r.FillCircle(tile.X + 29.0f, tile.Y + 29.0f, 10.0f, Color.FromArgb(255, 232, 67, 111));
                    r.FillCircle(tile.X + 29.0f, tile.Y + 29.0f, 1.8f, Color.FromArgb(255, 14, 14, 16));
                    break;
                }
                default:
                {
                    bool zip = ext == "ZIP" || ext == "RAR" || ext == "7Z";
                    var page = new RectangleF(tile.X + 12.0f, tile.Y + 7.0f, 34.0f, 44.0f);
                    r.FillRoundedRect(page, 4.0f, zip ? Color.FromArgb(255, 236, 228, 210) : Color.FromArgb(255, 247, 210, 214));
                    using (var line = new SolidBrush(zip ? Color.FromArgb(120, 150, 130, 100) : Color.FromArgb(120, 170, 90, 100)))
                    {
                        g.FillRectangle(line, page.X + 7.0f, page.Y + 11.0f, 20.0f, 2.0f);
                        g.FillRectangle(line, page.X + 7.0f, page.Y + 17.0f, 15.0f, 2.0f);
                    }
                    var pill = new RectangleF(page.X + 3.0f, page.Bottom - 15.0f, page.Width - 6.0f, 11.0f);
                    r.FillRoundedRect(pill, 3.0f, zip ? Color.FromArgb(255, 196, 144, 46) : Color.FromArgb(255, 214, 58, 64));
                    r.DrawTextStr(zip ? "ZIP" : (ext.Length > 4 || ext.Length == 0 ? "FILE" : ext), pill, 7.0f, true,
                        Color.FromArgb(255, 255, 255, 255), mono: true, hAlign: StringAlignment.Center);
                    break;
                }
            }
        }

        // The demo's own artwork, embedded at capture resolution and blitted 1:1.
        // Loaded once, then reused; a missing resource just falls back to drawing.
        private static readonly Dictionary<string, Bitmap> ArtCache = new();

        private static readonly Dictionary<string, Bitmap> ShelfThumbCache = new(StringComparer.OrdinalIgnoreCase);

        private static Bitmap? LoadShelfImageThumb(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            lock (ShelfThumbCache)
            {
                if (ShelfThumbCache.TryGetValue(fullPath, out Bitmap? cached)) return cached;
                if (!File.Exists(fullPath)) return null;
                try
                {
                    using var src = Image.FromFile(fullPath);
                    var thumb = new Bitmap(96, 80);
                    using (var tg = Graphics.FromImage(thumb))
                    {
                        tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        // Center-crop cover fill so photos look crisp inside the Polaroid frame
                        float scale = MathF.Max(96.0f / Math.Max(1, src.Width), 80.0f / Math.Max(1, src.Height));
                        float dw = src.Width * scale, dh = src.Height * scale;
                        tg.DrawImage(src, (96.0f - dw) * 0.5f, (80.0f - dh) * 0.5f, dw, dh);
                    }
                    if (ShelfThumbCache.Count > 24) ShelfThumbCache.Clear();
                    ShelfThumbCache[fullPath] = thumb;
                    return thumb;
                }
                catch { return null; }
            }
        }

        private static Bitmap? Art(string name)
        {
            lock (ArtCache)
            {
                if (ArtCache.TryGetValue(name, out Bitmap? cached)) return cached;
                try
                {
                    using Stream? s = typeof(IslandTabsRenderer).Assembly
                        .GetManifestResourceStream("OpenPixel.Island." + name + ".png");
                    if (s == null) return null;
                    var bmp = new Bitmap(s);
                    ArtCache[name] = bmp;
                    return bmp;
                }
                catch { return null; }
            }
        }

        // The clipboard's file line truncates at the tail ("notchpixel-dem…"), the way
        // the demo's sample card does, rather than preserving the extension.
        private static string EndEllipsis(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name) || name.Length <= maxLength) return name ?? "";
            return name.Substring(0, Math.Max(1, maxLength - 1)) + "\u2026";
        }

        // Keeps both ends of a long file name visible, the way the shelf does.
        private static string MiddleEllipsis(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name) || name.Length <= maxLength) return name ?? "";
            int front = Math.Max(3, maxLength - 5);
            return name.Substring(0, front) + "\u2026" + name.Substring(name.Length - 3);
        }



        private static void RenderProgressBar(
            LiquidGlassRenderer r, GlobalAppState state,
            float barLeft, float barY, float barWidth, float progress01, ThemeSpec theme, float timeSec)
        {
            progress01 = Math.Clamp(progress01, 0.0f, 1.0f);
            var barBox = new RectangleF(barLeft, barY - 7.5f, barWidth, 15.0f);
            r.AddHitRegion("music-scrub", new RectangleF(barLeft, barY - 8.0f, barWidth, 16.0f), null,
                normX => state.Media.PositionSec = normX * Math.Max(1.0, state.Media.DurationSec));

            float head = barLeft + barWidth * progress01;
            float playT = Math.Clamp(state.PlaySpring.Value / 100.0f, 0.0f, 1.0f);
            bool playing = state.Media.IsPlaying;
            bool reduced = state.Settings.ReduceMotion;
            float sec = reduced ? 0.0f : timeSec;
            float hover = EaseHover(state, "music-bar", r.IsHovered(barBox, state.PointerDipX, state.PointerDipY));
            Color strong = state.Media.HasSession ? state.Media.DominantColor : theme.Accent;
            Color paper = theme.TextPrimary;
            Color white15 = Color.FromArgb(38, 255, 255, 255);

            switch (state.Settings.ProgressBar)
            {
                case OpenPixel.Core.ProgressBarStyle.Simple:
                {
                    float h = 4.0f + 4.0f * hover;
                    r.FillRoundedRect(new RectangleF(barLeft, barY - h * 0.5f, barWidth, h), h * 0.5f, white15);
                    if (progress01 > 0.0f)
                        r.FillRoundedRect(new RectangleF(barLeft, barY - h * 0.5f, Math.Max(h, head - barLeft), h), h * 0.5f, strong);
                    break;
                }
                case OpenPixel.Core.ProgressBarStyle.Pixels:
                {
                    float step = 4.5f, px = 3.0f;
                    int n = (int)MathF.Floor(barWidth / step) + 1, headK = -1, headStack = 1;
                    float hx = r.IsHovered(barBox, state.PointerDipX, state.PointerDipY) ? state.PointerDipX - barLeft : -1.0f;
                    for (int k = 0; k < n; ++k)
                    {
                        float x = barLeft + k * step, cx = x + px * 0.5f;
                        bool lit = cx <= head;
                        float near = hx >= 0.0f ? MathF.Exp(-MathF.Pow((cx - barLeft - hx) / 15.0f, 2.0f)) * hover : 0.0f;
                        int stack = 1 + (int)MathF.Round(near * 2.4f);
                        if (lit && cx + step > head) { headK = k; headStack = stack; continue; }
                        float lvl = lit
                            ? 0.62f + 0.38f * (0.5f + 0.5f * MathF.Sin(sec * 5.0f - k * 0.45f)) * playT + 0.2f * (1.0f - playT)
                            : 0.17f + near * 0.3f;
                        using var pxBrush = new SolidBrush(lit
                            ? Color.FromArgb((int)(Math.Min(1.0f, lvl) * 255 * r.Opacity), strong.R, strong.G, strong.B)
                            : Color.FromArgb((int)(Math.Min(1.0f, lvl) * 255 * r.Opacity), 255, 255, 255));
                        for (int p = 0; p < stack; ++p)
                            r.G.FillRectangle(pxBrush, x, barY + px * 0.5f - px - p * (px + 1.5f), px, px);
                    }
                    if (headK >= 0)
                    {
                        float hx2 = barLeft + headK * step - 0.5f;
                        using var headBrush = new SolidBrush(Color.FromArgb((int)(255 * r.Opacity), paper.R, paper.G, paper.B));
                        for (int q = 0; q < headStack; ++q)
                            r.G.FillRectangle(headBrush, hx2, barY + 2.0f - 4.0f - q * 4.5f, 4.0f, 4.0f);
                    }
                    break;
                }
                case OpenPixel.Core.ProgressBarStyle.Comet:
                {
                    float ch = 3.0f + 3.0f * hover;
                    r.FillRoundedRect(new RectangleF(barLeft, barY - ch * 0.5f, barWidth, ch), ch * 0.5f, Color.FromArgb(31, 255, 255, 255));
                    if (head > barLeft + 1.0f)
                    {
                        using var grad = new LinearGradientBrush(new RectangleF(barLeft, barY - ch * 0.5f, Math.Max(1.0f, head - barLeft), ch),
                            Color.FromArgb(20, strong.R, strong.G, strong.B), strong, 0f);
                        grad.InterpolationColors = new ColorBlend
                        {
                            Colors = new[] { Color.FromArgb(20, strong.R, strong.G, strong.B),
                                             Color.FromArgb(143, strong.R, strong.G, strong.B), strong },
                            Positions = new[] { 0.0f, 0.75f, 1.0f }
                        };
                        using var cometPath = RoundedRectPath(new RectangleF(barLeft, barY - ch * 0.5f, head - barLeft, ch), ch * 0.5f);
                        r.G.FillPath(grad, cometPath);
                    }
                    float beat = reduced ? 0.0f : MathF.Pow(MathF.Max(0.0f, MathF.Sin(sec * MathF.PI * 2.0f)), 10.0f) * playT;
                    using var headBrush = new SolidBrush(paper);
                    r.G.FillEllipse(headBrush, head - (3.4f + 1.6f * hover) * (1.0f + 0.3f * beat),
                        barY - (3.4f + 1.6f * hover) * (1.0f + 0.3f * beat),
                        (3.4f + 1.6f * hover) * (1.0f + 0.3f * beat) * 2.0f,
                        (3.4f + 1.6f * hover) * (1.0f + 0.3f * beat) * 2.0f);
                    break;
                }
                case OpenPixel.Core.ProgressBarStyle.SongShape:
                {
                    float[] bars = SongShapeBars(state.Media.HasSession ? state.Media.Title : "NotchPixel");
                    int cnt = bars.Length;
                    float st = barWidth / cnt, bw = Math.Max(1.6f, st - 1.4f);
                    float reach = r.IsHovered(barBox, state.PointerDipX, state.PointerDipY) ? state.PointerDipX : -1.0f;
                    for (int b = 0; b < cnt; ++b)
                    {
                        float bcx = barLeft + b * st + bw * 0.5f;
                        float bounce = reduced ? 0.0f
                            : Math.Max(0.0f, 1.0f - MathF.Abs(bcx - head) / 9.0f) * 0.4f * MathF.Abs(MathF.Sin(sec * 9.0f + b)) * playT;
                        float bh = Math.Max(2.2f, (bars[b] + bounce) * (12.0f + 4.0f * hover));
                        Color col = bcx <= head ? strong
                            : (bcx <= reach ? Color.FromArgb(112, strong.R, strong.G, strong.B) : Color.FromArgb(56, 255, 255, 255));
                        r.FillRoundedRect(new RectangleF(barLeft + b * st, barY - bh * 0.5f, bw, bh), bw * 0.5f, col);
                    }
                    break;
                }
                case OpenPixel.Core.ProgressBarStyle.Hearts:
                {
                    int n2 = Math.Max(2, (int)MathF.Floor(barWidth / 11.0f));
                    float gap = barWidth / (n2 - 1);
                    int hi = (int)MathF.Round(progress01 * (n2 - 1));
                    for (int j = 0; j < n2; ++j)
                    {
                        float hx = barLeft + j * gap;
                        if (j == hi)
                        {
                            float bt = 1.0f + 0.18f * MathF.Max(0.0f, MathF.Sin(sec * 7.5f)) * playT;
                            r.DrawLucideIcon("heart-filled", hx, barY, (7.5f + 3.0f) * bt, paper);
                            continue;
                        }
                        bool played = j < hi;
                        if (played)
                            r.DrawLucideIcon("heart-filled", hx, barY + MathF.Sin(sec * 4.0f - j * 0.5f) * 1.4f * playT, 7.5f, strong);
                        else
                            r.DrawLucideIcon("heart", hx, barY, 7.5f, white15);
                    }
                    break;
                }
                default:
                {
                    float lw = 2.6f + 1.4f * hover, amp = 2.8f * playT;
                    using var pen = new Pen(strong, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    if (head + 7.0f < barLeft + barWidth)
                    {
                        using var tailPen = new Pen(Color.FromArgb(46, paper.R, paper.G, paper.B), lw)
                        { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        r.G.DrawLine(tailPen, head + 7.0f, barY, barLeft + barWidth, barY);
                    }
                    if (head > barLeft + 1.0f)
                    {
                        var pts = new List<PointF>();
                        for (float wx = barLeft; wx <= head; wx += 0.75f)
                        {
                            float tp = Math.Min(1.0f, (head - wx) / 10.0f);
                            float wy = barY + MathF.Sin((wx - barLeft) / 20.0f * MathF.PI * 2.0f - sec * 4.4f) * amp * tp;
                            pts.Add(new PointF(wx, wy));
                        }
                        if (pts.Count > 1) r.G.DrawLines(pen, pts.ToArray());
                    }
                    float hh = 11.0f + 3.0f * hover, hw = 3.4f + hover;
                    r.FillRoundedRect(new RectangleF(head - hw * 0.5f, barY - hh * 0.5f, hw, hh), 2.0f, paper);
                    break;
                }
            }
        }

        // The demo's play/pause glyph morph and its solid skip triangles.
        private static readonly float[][][] PauseGlyph =
        {
            new[] { new[] { 6.0f, 4.5f }, new[] { 10.5f, 4.5f }, new[] { 10.5f, 19.5f }, new[] { 6.0f, 19.5f } },
            new[] { new[] { 13.5f, 4.5f }, new[] { 18.0f, 4.5f }, new[] { 18.0f, 19.5f }, new[] { 13.5f, 19.5f } }
        };
        private static readonly float[][][] PlayGlyph =
        {
            new[] { new[] { 6.5f, 3.8f }, new[] { 13.0f, 7.9f }, new[] { 13.0f, 16.1f }, new[] { 6.5f, 20.2f } },
            new[] { new[] { 13.0f, 7.9f }, new[] { 20.0f, 12.0f }, new[] { 20.0f, 12.0f }, new[] { 13.0f, 16.1f } }
        };

        private static void DrawPlayPauseGlyph(LiquidGlassRenderer r, float cx, float cy, float size, Color color, float morph01)
        {
            float e = morph01 * morph01 * (3.0f - 2.0f * morph01), k = size / 24.0f;
            using var path = new GraphicsPath();
            for (int sh = 0; sh < 2; ++sh)
            {
                for (int i = 0; i < 4; ++i)
                {
                    float px = PauseGlyph[sh][i][0], py = PauseGlyph[sh][i][1];
                    float qx = PlayGlyph[sh][i][0], qy = PlayGlyph[sh][i][1];
                    float x = cx + (px + (qx - px) * e - 12.0f) * k;
                    float y = cy + (py + (qy - py) * e - 12.0f) * k;
                    if (i == 0) { path.StartFigure(); path.AddLine(x, y, x, y); }
                    else path.AddLine(path.GetLastPoint(), new PointF(x, y));
                }
                path.CloseFigure();
            }
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 1.2f * k) { LineJoin = LineJoin.Round };
            r.G.FillPath(brush, path);
            r.G.DrawPath(pen, path);
        }

        private static void DrawSolidSkip(LiquidGlassRenderer r, float cx, float cy, float size, Color color, bool back)
        {
            float k = size / 24.0f;
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 1.2f * k) { LineJoin = LineJoin.Round };
            float ox = cx - size * 0.5f, oy = cy - size * 0.5f;
            var a = back
                ? new[] { new PointF(11, 12), new PointF(20, 5.5f), new PointF(20, 18.5f) }
                : new[] { new PointF(13, 12), new PointF(4, 5.5f), new PointF(4, 18.5f) };
            var b = back
                ? new[] { new PointF(2.5f, 12), new PointF(11, 5.5f), new PointF(11, 18.5f) }
                : new[] { new PointF(21.5f, 12), new PointF(13, 5.5f), new PointF(13, 18.5f) };
            foreach (var tri in new[] { a, b })
            {
                var pts = new PointF[3];
                for (int i = 0; i < 3; ++i) pts[i] = new PointF(ox + tri[i].X * k, oy + tri[i].Y * k);
                r.G.FillPolygon(brush, pts);
                r.G.DrawPolygon(pen, pts);
            }
        }

        private static GraphicsPath RoundedRectPath(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(0.1f, Math.Min(radius, Math.Min(rect.Width, rect.Height) * 0.5f)) * 2.0f;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static float[] SongShapeBars(string seed)
        {
            uint x = 7;
            foreach (char ch in seed) x = (uint)((int)x * 31 + ch);
            Func<float> next = () =>
            {
                x = (uint)((int)x * 1664525 + 1013904223);
                return x / 4294967296.0f;
            };
            var bars = new float[96];
            float v = 0.5f;
            for (int k = 0; k < 96; ++k)
            {
                v = Math.Min(1.0f, Math.Max(0.15f, v + (next() - 0.5f) * 0.45f));
                float env = Math.Min(1.0f, Math.Min((k + 2) / 10.0f, (96 - k) / 8.0f + 0.2f));
                bars[k] = Math.Max(0.14f, v * (0.6f + 0.4f * next()) * env);
            }
            return bars;
        }
    }
}
