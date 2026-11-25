using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

namespace OpenPixel.UI
{
    // ========================================================================
    // Spacious, Uncluttered OpenPixel Settings UI (Matching openpixel.com)
    // - 13 Clean Categories with monochrome outline icons & Search bar:
    //   General, Accessibility, Appearance, Island, Panels, Widgets, Calls,
    //   Streaming, Gaming, AI agents, Tools, Keys, About
    // - Muted uppercase eyebrow (`SETTINGS`) + Bold Heading + Grouped Rounded Cards
    // - Mouse-wheel scrollable right canvas so controls are never crammed!
    // - Zero notch cutouts inside settings cards; Liquid Glass default preserved
    // - Always easy to close via '×', Escape key, WM_CLOSE, or tray icon
    // ========================================================================
    public sealed class SettingsWindow : Form
    {
        private readonly GlobalAppState _state;
        private readonly Action _onSettingsChanged;
        private readonly LiquidGlassRenderer _renderer = new();
        private float _mouseX = -1000;
        private float _mouseY = -1000;
        private float _scrollOffsetY = 0.0f;
        private float _maxScrollY = 0.0f;
        private bool _draggingTitlebar = false;
        private int _dragOffX, _dragOffY;

        // Slider/theme drag safety: never re-enter the layered surface mid-frame,
        // and never hammer the disk with a JSON write per mouse-move.
        // One number enlarges the entire menu: every control is laid out in DIPs and
        // drawn through the renderer's scale transform, so text, cards, pills, icons,
        // strokes and hit targets all grow together instead of drifting out of sync.
        private const float UiScale = 1.4f;
        private float _uiScale = UiScale;

        private float LogicalWidth => Width / _uiScale;
        private float LogicalHeight => Height / _uiScale;

        private readonly System.Windows.Forms.Timer _saveTimer = new System.Windows.Forms.Timer { Interval = 350 };
        private bool _savePending = false;
        private bool _rendering = false;
        private bool _repaintQueued = false;

        private static readonly (SettingsCategory Cat, string Label, string Icon)[] Categories =
        {
            (SettingsCategory.General,       "General",       "sliders-horizontal"),
            (SettingsCategory.Accessibility, "Accessibility", "eye"),
            (SettingsCategory.Appearance,    "Appearance",    "palette"),
            (SettingsCategory.Island,        "Island",        "layout-grid"),
            (SettingsCategory.Panels,        "Panels",        "folder"),
            (SettingsCategory.Widgets,       "Widgets",       "calendar"),
            (SettingsCategory.Calls,         "Calls",         "headphones"),
            (SettingsCategory.Streaming,     "Streaming",     "video"),
            (SettingsCategory.Gaming,        "Gaming",        "gamepad"),
            (SettingsCategory.AiAgents,      "AI agents",     "cpu"),
            (SettingsCategory.Tools,         "Tools",         "smartphone"),
            (SettingsCategory.Keys,          "Keys",          "key"),
            (SettingsCategory.About,         "About",         "info")
        };

        public SettingsWindow(GlobalAppState state, Action onSettingsChanged)
        {
            _state = state;
            _onSettingsChanged = onSettingsChanged;
            Text = "OpenPixel Settings";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            _saveTimer.Tick += (_, _) => FlushPendingSave();

            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            float dpi = 1.0f;
            try
            {
                using var probe = Graphics.FromHwnd(IntPtr.Zero);
                dpi = Math.Clamp(probe.DpiX / 96.0f, 1.0f, 2.5f);
            }
            catch { }
            _uiScale = Math.Min(UiScale * dpi, 4.0f);

            // Roomier window to match the larger menu (still capped to the work area).
            int w = Math.Min(Math.Max((int)(1000 * dpi), 720), (int)(work.Width * 0.94f));
            int h = Math.Min(Math.Max((int)(720 * dpi), 520), (int)(work.Height * 0.92f));
            Bounds = new Rectangle(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                return cp;
            }
        }

        public void ToggleVisibility()
        {
            if (Visible) Hide();
            else { Show(); Activate(); RenderNow(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                FlushPendingSave();
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                FlushPendingSave();
                Hide();
                e.Handled = true;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float delta = e.Delta > 0 ? -40.0f : 40.0f;
            if (_state.Settings.ReverseScrolling) delta = -delta;
            _scrollOffsetY = Math.Clamp(_scrollOffsetY + delta, 0.0f, Math.Max(0.0f, _maxScrollY));
            RenderNow();
        }

        private void NotifyChanged()
        {
            // Coalesced local save: dragging a slider no longer writes settings.json on every pixel.
            _savePending = true;
            _saveTimer.Stop();
            _saveTimer.Start();

            _onSettingsChanged?.Invoke();
            RequestRender();
        }

        private void FlushPendingSave()
        {
            _saveTimer.Stop();
            if (!_savePending) return;
            _savePending = false;
            ConfigManager.SaveAtomic(_state);
        }

        private void RequestRender()
        {
            if (IsDisposed || !IsHandleCreated || !Visible) return;
            if (_rendering) { _repaintQueued = true; return; }
            RenderNow();
        }

        private void ScheduleRepaint()
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(new Action(() => { if (!IsDisposed) RenderNow(); }));
            }
            catch { }
        }

        public void RenderNow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (_rendering) { _repaintQueued = true; return; }
            _rendering = true;
            try { RenderFrame(); }
            catch { /* A failed GDI+/theme frame must never take down the app. */ }
            finally
            {
                _rendering = false;
                if (_repaintQueued)
                {
                    _repaintQueued = false;
                    ScheduleRepaint();
                }
            }
        }

        private void RenderFrame()
        {
            lock (_state.SyncRoot)
            {
                if (!_renderer.BeginLayeredFrame(Width, Height, _uiScale)) return;
                _renderer.ClearHitRegions();

                float LW = LogicalWidth;
                float LH = LogicalHeight;

                var s = _state.Settings;
                ThemeSpec theme = s.Theme == SurfaceTheme.PureWhite
                    ? ThemeSpec.Get(SurfaceTheme.PureWhite)
                    : ThemeSpec.Get(SurfaceTheme.Black);

                Color cardBg = theme.IsLight ? Color.FromArgb(14, 12, 12, 14) : Color.FromArgb(16, 255, 255, 255);
                Color hairline = theme.IsLight ? Color.FromArgb(24, 12, 12, 14) : Color.FromArgb(20, 255, 255, 255);

                // Clean outer rounded rectangle (no notch cutouts inside Settings!)
                var frame = new RectangleF(1, 1, LW - 2, LH - 2);
                _renderer.FillRoundedRect(frame, 16, theme.Fill);
                _renderer.DrawRoundedRect(frame, 16, hairline, 1.0f);

                // Top Close Button (always reachable & never overlapped)
                var closeBtn = new RectangleF(LW - 42, 12, 30, 30);
                bool closeHov = _renderer.IsHovered(closeBtn, _mouseX, _mouseY);
                _renderer.FillRoundedRect(closeBtn, 9, closeHov ? Color.FromArgb(225, 255, 69, 58) : cardBg);
                _renderer.DrawTextStr("\u00D7", closeBtn, 17.0f, true, closeHov ? Color.White : theme.TextPrimary, hAlign: StringAlignment.Center);
                _renderer.AddHitRegion("settings-close", closeBtn, () => Hide());

                // Left Navigation Rail (168 DIP): Brand + Search pill + 13 monochrome icon categories
                _renderer.FillRoundedRect(new RectangleF(18, 16, 20, 20), 6, Color.FromArgb(255, 26, 122, 240));
                _renderer.DrawTextStr("OpenPixel", new RectangleF(45, 14, 118, 24), 13.5f, true, theme.TextPrimary);

                var searchBox = new RectangleF(16, 48, 146, 28);
                _renderer.FillRoundedRect(searchBox, 9, cardBg);
                _renderer.DrawRoundedRect(searchBox, 9, hairline, 1.0f);
                _renderer.DrawTextStr(string.IsNullOrEmpty(_state.SettingsSearchQuery) ? "Search settings" : _state.SettingsSearchQuery,
                    new RectangleF(26, 48, 126, 28), 11.0f, false, theme.TextFaint);

                // The rail fits all 13 categories into the window height instead of
                // spilling off the bottom edge on short displays.
                float sideY = 86.0f;
                float railStep = Math.Clamp((LH - sideY - 14.0f) / Categories.Length, 20.0f, 34.0f);
                float railRowH = railStep - 2.0f;
                for (int i = 0; i < Categories.Length; ++i)
                {
                    var (cat, label, icon) = Categories[i];
                    var cBtn = new RectangleF(16, sideY, 146, railRowH);
                    bool act = _state.ActiveSettingsCat == cat;
                    bool hov = _renderer.IsHovered(cBtn, _mouseX, _mouseY);
                    if (act || hov)
                    {
                        _renderer.FillRoundedRect(cBtn, 9, act ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(14, 255, 255, 255));
                    }
                    _renderer.DrawLucideIcon(icon, 33, sideY + railRowH * 0.5f, 13.5f, act ? theme.TextPrimary : theme.TextMuted, 1.6f);
                    _renderer.DrawTextStr(label, new RectangleF(48, sideY, 108, railRowH), 12.0f, act, act ? theme.TextPrimary : theme.TextMuted);
                    var capCat = cat;
                    _renderer.AddHitRegion($"cat-{i}", cBtn, () => { _state.ActiveSettingsCat = capCat; _scrollOffsetY = 0; RenderNow(); });
                    sideY += railStep;
                }

                _renderer.DrawLine(174, 16, 174, LH - 16, hairline, 1.0f);

                // Right Spacious Canvas (Clipped & Scrollable)
                float paneX = 196.0f;
                float paneW = LW - paneX - 24.0f;
                var frameState = _renderer.G.Save();
                _renderer.G.SetClip(new RectangleF(180, 14, LW - 188, LH - 28), System.Drawing.Drawing2D.CombineMode.Intersect);

                float py = 20.0f - _scrollOffsetY;

                // Muted uppercase eyebrow ("SETTINGS") + Bold Section Title
                string activeTitle = Array.Find(Categories, c => c.Cat == _state.ActiveSettingsCat).Label ?? "Settings";
                _renderer.DrawTextStr("SETTINGS", new RectangleF(paneX, py, 200, 16), 10.0f, true, theme.TextFaint, mono: true);
                py += 16.0f;
                _renderer.DrawTextStr(activeTitle, new RectangleF(paneX, py, 340, 32), 21.0f, true, theme.TextPrimary);
                py += 44.0f;

                switch (_state.ActiveSettingsCat)
                {
                    case SettingsCategory.Appearance:
                    {
                        DrawSectionHeader("SURFACE & MOTION", paneX, ref py, theme);
                        DrawSpaciousCard(paneX, py, paneW, 254, cardBg, hairline);
                        float cy = py + 14.0f;
                        DrawSegmented("Shape", "Floating pill or flush top notch", paneX + 16, ref cy, paneW - 32, new[] { "Island", "Notch" }, (int)s.Shape, theme, idx =>
                        {
                            s.Shape = (IslandShape)idx;
                            _state.NotchSpring.SetTarget(s.Shape == IslandShape.Notch ? 100.0f : 0.0f, s.ReduceMotion);
                            NotifyChanged();
                        });
                        DrawHairline(paneX + 16, cy, paneW - 32, hairline); cy += 10.0f;
                        DrawSegmented("Surface", "Liquid Glass is default with live refraction", paneX + 16, ref cy, paneW - 32,
                            new[] { "Liquid Glass", "Candy", "Acrylic", "Black", "OLED", "Graphite" },
                            (int)s.Theme < 6 ? (int)s.Theme : -1, theme, idx => { _state.QueueTheme((SurfaceTheme)idx); _state.CommitPendingParametersOnRenderTick(); NotifyChanged(); });
                        DrawSegmented("Soft themes", "Pure white, Ros\u00E9, Lavender, Peach, Sakura", paneX + 16, ref cy, paneW - 32,
                            new[] { "White", "Ros\u00E9", "Lavender", "Peach", "Sakura" },
                            (int)s.Theme >= 6 ? (int)s.Theme - 6 : -1, theme, idx => { _state.QueueTheme((SurfaceTheme)(idx + 6)); _state.CommitPendingParametersOnRenderTick(); NotifyChanged(); });
                        DrawHairline(paneX + 16, cy, paneW - 32, hairline); cy += 10.0f;
                        DrawSlider("Liquid Glass blur", $"{(int)s.GlassBlurRadius} px", paneX + 16, ref cy, paneW - 32, s.GlassBlurRadius / 40.0f, theme, n =>
                        {
                            _state.QueueBlurRadius(n * 40.0f);
                            _state.CommitPendingParametersOnRenderTick();
                            NotifyChanged();
                        });
                        DrawSlider("Corners", $"{(int)(s.CornerRadiusScale * 100)}%", paneX + 16, ref cy, paneW - 32, (s.CornerRadiusScale - 0.15f) / 0.85f, theme, n =>
                        {
                            _state.QueueCornerRadiusScale(0.15f + n * 0.85f);
                            _state.CommitPendingParametersOnRenderTick();
                            NotifyChanged();
                        });
                        py += 270.0f;

                        DrawSectionHeader("MUSIC VISUALS", paneX, ref py, theme);
                        DrawSpaciousCard(paneX, py, paneW, 108, cardBg, hairline);
                        cy = py + 14.0f;
                        DrawSegmented("Progress bar", "Choose scrub bar animation", paneX + 16, ref cy, paneW - 32, new[] { "Wave", "Pixels", "Comet", "Shape", "Simple", "Hearts" }, (int)s.ProgressBar, theme, idx => { s.ProgressBar = (OpenPixel.Core.ProgressBarStyle)idx; NotifyChanged(); });
                        DrawSegmented("Visualizer", "Closed pill equalizer style", paneX + 16, ref cy, paneW - 32, new[] { "Pixels", "Bars" }, (int)s.Visualizer, theme, idx => { s.Visualizer = (VisualizerStyle)idx; NotifyChanged(); });
                        py += 124.0f;
                        break;
                    }

                    case SettingsCategory.Island:
                    {
                        DrawSectionHeader("PLACEMENT & HOVER", paneX, ref py, theme);
                        DrawSpaciousCard(paneX, py, paneW, 210, cardBg, hairline);
                        float cy = py + 14.0f;
                        DrawSegmented("Edge", "Top center or side edge", paneX + 16, ref cy, paneW - 32, new[] { "Top center", "Left edge", "Right edge" }, (int)s.Edge, theme, idx => { s.Edge = (EdgePlacement)idx; NotifyChanged(); });
                        DrawSegmented("Monitor", "Choose display for the island", paneX + 16, ref cy, paneW - 32, new[] { "Primary (1)", "Display 2", "Display 3" }, Math.Clamp(s.MonitorIndex, 0, 2), theme, idx => { s.MonitorIndex = idx; NotifyChanged(); });
                        DrawSlider("Hover open delay", $"{s.HoverOpenDelaySec:F2}s", paneX + 16, ref cy, paneW - 32, s.HoverOpenDelaySec / 1.5f, theme, n => { s.HoverOpenDelaySec = Math.Clamp(MathF.Round(n * 15.0f) / 10.0f, 0.0f, 1.5f); NotifyChanged(); });
                        DrawToggleRow("Show song title in closed island", "Show active track between cover and bars", paneX + 16, ref cy, paneW - 32, s.ShowSongTitleInClosedPill, theme, () => { s.ShowSongTitleInClosedPill = !s.ShowSongTitleInClosedPill; NotifyChanged(); });
                        py += 226.0f;

                        DrawToggleCard("Hide from screenshots and recordings", "Glass & Candy glass always stay hidden from captures", paneX, ref py, paneW, s.HideFromScreenshots, cardBg, hairline, theme, () => { s.HideFromScreenshots = !s.HideFromScreenshots; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Gaming:
                    {
                        DrawSectionHeader("FULLSCREEN GAME OVERLAY", paneX, ref py, theme);
                        DrawToggleCard("Enable Game Overlay", "Turn off to completely disable or hide OpenPixel in fullscreen games", paneX, ref py, paneW, s.GameOverlayMasterEnabled, cardBg, hairline, theme, () =>
                        {
                            s.GameOverlayMasterEnabled = !s.GameOverlayMasterEnabled;
                            if (!s.GameOverlayMasterEnabled) s.GameOverlayMode = GameOverlayMode.OffHideCompletely;
                            else if (s.GameOverlayMode == GameOverlayMode.OffHideCompletely) s.GameOverlayMode = GameOverlayMode.SlimStrip;
                            NotifyChanged();
                        });
                        DrawSpaciousCard(paneX, py, paneW, 132, cardBg, hairline);
                        float cy = py + 14.0f;
                        DrawSegmented("Overlay mode", "Hide completely, slim telemetry strip, or normal", paneX + 16, ref cy, paneW - 32,
                            new[] { "Off / Hide", "Slim strip", "Normal" }, (int)s.GameOverlayMode, theme, idx =>
                            {
                                s.GameOverlayMode = (GameOverlayMode)idx;
                                s.GameOverlayMasterEnabled = s.GameOverlayMode != GameOverlayMode.OffHideCompletely;
                                NotifyChanged();
                            });
                        DrawHairline(paneX + 16, cy, paneW - 32, hairline); cy += 10.0f;
                        DrawToggleRow("Strip background", "Disable for lightweight numbers-only overlay", paneX + 16, ref cy, paneW - 32, s.GameStripShowBackground, theme, () => { s.GameStripShowBackground = !s.GameStripShowBackground; NotifyChanged(); });
                        py += 148.0f;
                        break;
                    }

                    case SettingsCategory.AiAgents:
                    {
                        DrawSectionHeader("CODING AGENT MONITOR", paneX, ref py, theme);
                        DrawSpaciousCard(paneX, py, paneW, 138, cardBg, hairline);
                        float cy = py + 14.0f;
                        _renderer.DrawTextStr("Supported Agents: Free Buff, Claude Code, Codex, Antigravity, Hermes & Custom", new RectangleF(paneX + 16, cy, paneW - 32, 22), 12.0f, true, theme.TextPrimary);
                        cy += 26.0f;
                        _renderer.DrawTextStr("Monitors local CLI processes, .freebuff/status.json, and loopback 127.0.0.1:47823/agent.\nHighlights when Free Buff is Almost done and nudges when it needs input or approval.",
                            new RectangleF(paneX + 16, cy, paneW - 32, 40), 11.0f, false, theme.TextMuted, noWrap: false);
                        cy += 48.0f;
                        var testFbBtn = new RectangleF(paneX + 16, cy, 190, 30);
                        _renderer.FillRoundedRect(testFbBtn, 9, theme.Accent);
                        _renderer.DrawTextStr("Simulate Free Buff Alert", testFbBtn, 11.0f, true, ThemeSpec.AdaptiveInkForBackground(theme.Accent), hAlign: StringAlignment.Center);
                        _renderer.AddHitRegion("ai-test-fb", testFbBtn, () =>
                        {
                            WindowsSystemHooks.UpsertAgentSession(_state, "Free Buff", "Approve final build?", "Needs input", 92, true, true);
                            NotifyChanged();
                        });
                        py += 154.0f;
                        break;
                    }

                    case SettingsCategory.Tools:
                    {
                        DrawSectionHeader("PC DECK & ANDROID APP", paneX, ref py, theme);
                        string deckDesc = _state.IsNetworkPrivate
                            ? $"Private Wi-Fi ({_state.LocalPrivateIp}:{s.PcDeckTlsPort}) \u00B7 2-min code: {_state.ActivePairingCode}"
                            : "Disabled: Switch Windows Wi-Fi profile to Private to enable PC Deck";
                        DrawToggleCard("PC Deck (Local Wi-Fi Remote)", deckDesc, paneX, ref py, paneW, s.PcDeckEnabled, cardBg, hairline, theme, () =>
                        {
                            if (LocalNetworkServers.DetectPrivateNetworkAndLocalIp(_state))
                            {
                                s.PcDeckEnabled = !s.PcDeckEnabled;
                                if (s.PcDeckEnabled) LocalNetworkServers.GenerateTwoMinutePairingCode(_state);
                            }
                            NotifyChanged();
                        });

                        if (s.PcDeckEnabled && _state.IsNetworkPrivate)
                        {
                            DrawSpaciousCard(paneX, py, paneW, 186, cardBg, hairline);
                            string qrPayload = LocalNetworkServers.BuildDeckPairingQrPayload(_state);
                            // 150 DIP keeps each QR module big enough for a phone camera to read.
                            _renderer.DrawQrCode(qrPayload, paneX + 14, py + 14, 150, Color.FromArgb(255, 12, 12, 14), Color.FromArgb(255, 239, 237, 232));
                            float deckTextX = paneX + 178;
                            float deckTextW = paneW - 192;
                            _renderer.DrawTextStr($"Pairing Code (2 min): {_state.ActivePairingCode}", new RectangleF(deckTextX, py + 14, deckTextW, 22), 13.0f, true, theme.Accent, mono: true);
                            _renderer.DrawTextStr("Scan with OpenPixel Deck on this Wi-Fi", new RectangleF(deckTextX, py + 40, deckTextW, 18), 10.5f, false, theme.TextMuted);
                            _renderer.DrawTextStr($"TLS SHA-256 pinned \u00B7 Paired phones: {_state.PairedDeckDevices.Count}", new RectangleF(deckTextX, py + 60, deckTextW, 18), 10.0f, false, theme.TextMuted);

                            var regenBtn = new RectangleF(deckTextX, py + 92, 130, 30);
                            _renderer.FillRoundedRect(regenBtn, 9, Color.FromArgb(26, 255, 255, 255));
                            _renderer.DrawTextStr("New 2-Min Code", regenBtn, 10.5f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
                            _renderer.AddHitRegion("deck-regen", regenBtn, () => { LocalNetworkServers.GenerateTwoMinutePairingCode(_state); RenderNow(); });

                            var revBtn = new RectangleF(deckTextX + 138, py + 92, 150, 30);
                            _renderer.FillRoundedRect(revBtn, 9, Color.FromArgb(50, 255, 69, 58));
                            _renderer.DrawTextStr("Revoke All Phones", revBtn, 10.5f, true, Color.FromArgb(255, 255, 105, 97), hAlign: StringAlignment.Center);
                            _renderer.AddHitRegion("deck-revoke", revBtn, () => { _state.PairedDeckDevices.Clear(); RenderNow(); });

                            _renderer.DrawTextStr("Allow prompt appears on this PC before any phone is trusted.", new RectangleF(deckTextX, py + 132, deckTextW, 18), 10.0f, false, theme.TextFaint);
                            py += 200.0f;
                        }

                        DrawSectionHeader("TASKBAR & WALLPAPER", paneX, ref py, theme);
                        DrawToggleCard("Custom Bottom Notch Taskbar", "Start, pinned/open apps, tray icons, clock & crash failsafe", paneX, ref py, paneW, s.BottomTaskbarEnabled, cardBg, hairline, theme, () =>
                        {
                            s.BottomTaskbarEnabled = !s.BottomTaskbarEnabled;
                            AuxiliaryWindows.UpdateBottomTaskbar(_state);
                            NotifyChanged();
                        });
                        DrawToggleCard("Live Video Wallpapers", "Looping desktop wallpaper with live Glass widget reaction", paneX, ref py, paneW, s.LiveVideoWallpaperEnabled, cardBg, hairline, theme, () =>
                        {
                            s.LiveVideoWallpaperEnabled = !s.LiveVideoWallpaperEnabled;
                            AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                            NotifyChanged();
                        });
                        break;
                    }

                    case SettingsCategory.Widgets:
                    {
                        DrawSectionHeader("DESKTOP WIDGETS & CLOCK", paneX, ref py, theme);
                        DrawToggleCard("24-hour clock format", "Applies to Day Clock, island, taskbar, and desktop widgets", paneX, ref py, paneW, s.Use24HourClock, cardBg, hairline, theme, () => { s.Use24HourClock = !s.Use24HourClock; NotifyChanged(); });
                        DrawToggleCard("Lock desktop widgets in place", "Or right-click any widget to toggle Lock / Colour / Unpin", paneX, ref py, paneW, s.LockDesktopWidgets, cardBg, hairline, theme, () => { s.LockDesktopWidgets = !s.LockDesktopWidgets; NotifyChanged(); });
                        DrawSpaciousCard(paneX, py, paneW, 64, cardBg, hairline);
                        float cy = py + 14.0f;
                        DrawSegmented("Pin widget", "Places glass widget directly onto desktop", paneX + 16, ref cy, paneW - 32, new[] { "Day Clock", "Clock", "Weather", "Calendar", "PC Stats" }, -1, theme, idx =>
                        {
                            WidgetType[] map = { WidgetType.DayClock, WidgetType.Clock, WidgetType.MinimalWeather, WidgetType.Calendar, WidgetType.PcStats };
                            AuxiliaryWindows.ToggleDesktopWidget(_state, map[idx]);
                        });
                        py += 80.0f;
                        break;
                    }

                    case SettingsCategory.General:
                    {
                        DrawSectionHeader("STARTUP & RENDERER", paneX, ref py, theme);
                        DrawToggleCard("Launch at Windows startup", "Start quietly in the notification area", paneX, ref py, paneW, s.LaunchAtStartup, cardBg, hairline, theme, () => { s.LaunchAtStartup = !s.LaunchAtStartup; NotifyChanged(); });
                        DrawToggleCard("Draw with the processor", "CPU fallback for NVIDIA RTX 50 or unsupported GPU drivers", paneX, ref py, paneW, s.DrawWithProcessor, cardBg, hairline, theme, () => { s.DrawWithProcessor = !s.DrawWithProcessor; NotifyChanged(); });
                        DrawToggleCard("Reverse scrolling", "Invert wheel scroll direction in island and settings", paneX, ref py, paneW, s.ReverseScrolling, cardBg, hairline, theme, () => { s.ReverseScrolling = !s.ReverseScrolling; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Accessibility:
                    {
                        DrawSectionHeader("READABILITY & MOTION", paneX, ref py, theme);
                        DrawSpaciousCard(paneX, py, paneW, 134, cardBg, hairline);
                        float cy = py + 14.0f;
                        DrawSlider("Text scale", $"{(int)(s.TextScale * 100)}%", paneX + 16, ref cy, paneW - 32, (s.TextScale - 1.0f) / 0.5f, theme, n => { s.TextScale = Math.Clamp(1.0f + n * 0.5f, 1.0f, 1.5f); NotifyChanged(); });
                        DrawToggleRow("Higher contrast", "Stronger borders and adaptive black/white label ink", paneX + 16, ref cy, paneW - 32, s.HigherContrast, theme, () => { s.HigherContrast = !s.HigherContrast; NotifyChanged(); });
                        DrawToggleRow("Reduce motion", "Snap transitions immediately without spring bounce", paneX + 16, ref cy, paneW - 32, s.ReduceMotion, theme, () => { s.ReduceMotion = !s.ReduceMotion; NotifyChanged(); });
                        py += 150.0f;
                        break;
                    }

                    case SettingsCategory.Panels:
                    {
                        DrawSectionHeader("TAB BEHAVIOR", paneX, ref py, theme);
                        DrawToggleCard("Reopen onto last-viewed tab", "Otherwise always collapses onto Music", paneX, ref py, paneW, s.ReopenOnLastTab, cardBg, hairline, theme, () => { s.ReopenOnLastTab = !s.ReopenOnLastTab; NotifyChanged(); });
                        DrawToggleCard("Clock timestamps in Notifications", "Display 14:32 instead of relative time", paneX, ref py, paneW, s.ClockTimestampsInNotifications, cardBg, hairline, theme, () => { s.ClockTimestampsInNotifications = !s.ClockTimestampsInNotifications; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Calls:
                    {
                        DrawSectionHeader("MICROPHONE & CALLS", paneX, ref py, theme);
                        DrawToggleCard("Global Ctrl+Alt+M mute notice", "Show red mic mute banner in top notch", paneX, ref py, paneW, s.ShowGlobalMuteNotice, cardBg, hairline, theme, () => { s.ShowGlobalMuteNotice = !s.ShowGlobalMuteNotice; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Streaming:
                    {
                        DrawSectionHeader("TWITCH, YOUTUBE & OBS", paneX, ref py, theme);
                        DrawToggleCard("Movable Stream Chat Drawer", "Show live Twitch/YouTube chat overlay & red Chat dot", paneX, ref py, paneW, s.StreamChatEnabled, cardBg, hairline, theme, () =>
                        {
                            s.StreamChatEnabled = !s.StreamChatEnabled;
                            _state.ChatDrawerVisible = s.StreamChatEnabled;
                            AuxiliaryWindows.UpdateDrawers(_state);
                            NotifyChanged();
                        });
                        DrawToggleCard("OBS Studio REC pill", "Show live recording timer pill when OBS records", paneX, ref py, paneW, s.ShowObsRecPill, cardBg, hairline, theme, () => { s.ShowObsRecPill = !s.ShowObsRecPill; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Keys:
                    {
                        DrawSectionHeader("GLOBAL SHORTCUTS", paneX, ref py, theme);
                        (string act, string hk)[] keys =
                        {
                            ("Open / Close Island", "Ctrl + Alt + N"),
                            ("Play / Pause Media",  "Ctrl + Alt + P"),
                            ("Mute Microphone",     "Ctrl + Alt + M"),
                            ("Hide / Show Island",  "Ctrl + Alt + H"),
                            ("Screenshot",          "Ctrl + Alt + S"),
                            ("Screen Colour Picker","Ctrl + Alt + C"),
                            ("Quick Launcher 1\u20136", "Ctrl + Alt + 1..6")
                        };
                        DrawSpaciousCard(paneX, py, paneW, keys.Length * 36 + 16, cardBg, hairline);
                        float ky = py + 10.0f;
                        foreach (var (act, hk) in keys)
                        {
                            _renderer.DrawTextStr(act, new RectangleF(paneX + 16, ky, paneW - 170, 32), 12.0f, true, theme.TextPrimary);
                            _renderer.DrawTextStr(hk, new RectangleF(paneX + paneW - 160, ky, 144, 32), 11.0f, true, theme.Accent, mono: true, hAlign: StringAlignment.Far);
                            ky += 36.0f;
                        }
                        py += keys.Length * 36 + 32;
                        break;
                    }

                    case SettingsCategory.About:
                    {
                        DrawSectionHeader("OPENPIXEL 0.5.126", paneX, ref py, theme);
                        DrawSpaciousCard(paneX, py, paneW, 130, cardBg, hairline);
                        _renderer.DrawTextStr("OpenPixel for Windows 10 & 11 \u00B7 Zero accounts, zero telemetry, zero ads.", new RectangleF(paneX + 16, py + 14, paneW - 32, 22), 12.0f, true, theme.TextPrimary);
                        _renderer.DrawTextStr("Android Companion App located in <repo-root>/App (OpenPixel Deck).", new RectangleF(paneX + 16, py + 40, paneW - 32, 20), 11.0f, false, theme.TextMuted);
                        var repBtn = new RectangleF(paneX + 16, py + 76, 190, 32);
                        _renderer.FillRoundedRect(repBtn, 9, theme.Accent);
                        _renderer.DrawTextStr("Save Diagnostic Report", repBtn, 11.0f, true, ThemeSpec.AdaptiveInkForBackground(theme.Accent), hAlign: StringAlignment.Center);
                        _renderer.AddHitRegion("about-report", repBtn, () => { WindowsSystemHooks.SaveDiagnosticReportToDesktop(_state); NotifyChanged(); });
                        py += 146.0f;
                        break;
                    }
                }

                float totalContentBottom = py + _scrollOffsetY;
                _maxScrollY = Math.Max(0.0f, totalContentBottom - LH + 24.0f);
                _renderer.G.Restore(frameState);
                _renderer.EndLayeredFrame(Handle, Location, 254);
            }
        }

        private void DrawSectionHeader(string title, float x, ref float y, ThemeSpec theme)
        {
            _renderer.DrawTextStr(title, new RectangleF(x + 4, y, 300, 18), 10.5f, true, theme.TextFaint, mono: true);
            y += 22.0f;
        }

        private void DrawSpaciousCard(float x, float y, float w, float h, Color bg, Color border)
        {
            var r = new RectangleF(x, y, w, h);
            _renderer.FillRoundedRect(r, 14, bg);
            _renderer.DrawRoundedRect(r, 14, border, 1.0f);
        }

        private void DrawHairline(float x, float y, float w, Color col) => _renderer.DrawLine(x, y, x + w, y, col, 1.0f);

        private void DrawToggleCard(string title, string sub, float x, ref float y, float w, bool val, Color bg, Color border, ThemeSpec theme, Action onToggle)
        {
            DrawSpaciousCard(x, y, w, 58, bg, border);
            var card = new RectangleF(x, y, w, 58);
            _renderer.DrawTextStr(title, new RectangleF(x + 16, y + 10, w - 82, 20), 12.5f, true, theme.TextPrimary);
            _renderer.DrawTextStr(sub, new RectangleF(x + 16, y + 30, w - 82, 18), 10.5f, false, theme.TextMuted);
            var pill = new RectangleF(x + w - 58, y + 18, 42, 22);
            _renderer.FillRoundedRect(pill, 11, val ? Color.FromArgb(255, 48, 209, 88) : Color.FromArgb(42, 255, 255, 255));
            _renderer.FillCircle(val ? pill.Right - 11 : pill.Left + 11, y + 29, 8.0f, Color.White);
            _renderer.AddHitRegion($"tcard-{title}", card, onToggle);
            y += 70.0f;
        }

        private void DrawToggleRow(string title, string sub, float x, ref float y, float w, bool val, ThemeSpec theme, Action onToggle)
        {
            var row = new RectangleF(x, y, w, 40);
            _renderer.DrawTextStr(title, new RectangleF(x, y + 2, w - 56, 18), 12.0f, true, theme.TextPrimary);
            _renderer.DrawTextStr(sub, new RectangleF(x, y + 20, w - 56, 16), 10.0f, false, theme.TextMuted);
            var pill = new RectangleF(x + w - 42, y + 9, 40, 20);
            _renderer.FillRoundedRect(pill, 10, val ? Color.FromArgb(255, 48, 209, 88) : Color.FromArgb(42, 255, 255, 255));
            _renderer.FillCircle(val ? pill.Right - 10 : pill.Left + 10, y + 19, 7.2f, Color.White);
            _renderer.AddHitRegion($"trow-{title}", row, onToggle);
            y += 44.0f;
        }

        private void DrawSegmented(string label, string sub, float x, ref float y, float w, string[] items, int activeIdx, ThemeSpec theme, Action<int> onSelect)
        {
            _renderer.DrawTextStr(label, new RectangleF(x, y, 130, 18), 12.0f, true, theme.TextPrimary);
            _renderer.DrawTextStr(sub, new RectangleF(x, y + 17, 150, 14), 9.5f, false, theme.TextFaint);
            float segL = x + 156.0f;
            float segW = w - 156.0f;
            float btnW = (segW - (items.Length - 1) * 4.0f) / items.Length;
            for (int i = 0; i < items.Length; ++i)
            {
                float bx = segL + i * (btnW + 4.0f);
                var bRect = new RectangleF(bx, y + 4, btnW, 24);
                bool isAct = i == activeIdx;
                _renderer.FillRoundedRect(bRect, 12, isAct ? theme.TextPrimary : Color.FromArgb(20, 255, 255, 255));
                Color fg = isAct ? ThemeSpec.AdaptiveInkForBackground(theme.TextPrimary) : theme.TextPrimary;
                _renderer.DrawTextStr(items[i], bRect, 10.0f, isAct, fg, hAlign: StringAlignment.Center);
                int capI = i;
                _renderer.AddHitRegion($"seg-{label}-{i}", bRect, () => onSelect(capI));
            }
            y += 40.0f;
        }

        private void DrawSlider(string label, string valText, float x, ref float y, float w, float norm01, ThemeSpec theme, Action<float> onSlide)
        {
            _renderer.DrawTextStr(label, new RectangleF(x, y, 148, 24), 12.0f, true, theme.TextPrimary);
            var track = new RectangleF(x + 156, y + 8, w - 216, 8);
            _renderer.FillRoundedRect(track, 4, Color.FromArgb(36, 255, 255, 255));
            float fillW = track.Width * Math.Clamp(norm01, 0.0f, 1.0f);
            if (fillW > 1) _renderer.FillRoundedRect(new RectangleF(track.X, track.Y, fillW, 8), 4, theme.Accent);
            _renderer.DrawTextStr(valText, new RectangleF(x + w - 54, y, 54, 24), 11.0f, true, theme.TextPrimary, mono: true, hAlign: StringAlignment.Far);
            _renderer.AddHitRegion($"sld-{label}", new RectangleF(track.X - 4, y, track.Width + 8, 24), null, onSlide);
            y += 32.0f;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            float lx = e.X / _uiScale;
            float ly = e.Y / _uiScale;
            _mouseX = lx;
            _mouseY = ly;
            bool handled = false;
            try { handled = _renderer.DispatchClick(lx, ly); }
            catch { /* never let a control action crash the window */ }
            if (handled)
            {
                if (Visible) RenderNow();
                return;
            }
            if (e.Button == MouseButtons.Left && ly < 48 && lx < LogicalWidth - 52)
            {
                Point pt = Cursor.Position;
                _dragOffX = pt.X - Left;
                _dragOffY = pt.Y - Top;
                _draggingTitlebar = true;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            float lx = e.X / _uiScale;
            float ly = e.Y / _uiScale;
            _mouseX = lx;
            _mouseY = ly;
            if (_draggingTitlebar)
            {
                Point pt = Cursor.Position;
                Location = new Point(pt.X - _dragOffX, pt.Y - _dragOffY);
                RenderNow();
                return;
            }
            if ((e.Button & MouseButtons.Left) != 0)
            {
                try { _renderer.DispatchDrag(lx, ly); }
                catch { }
            }
            RenderNow();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _draggingTitlebar = false;
            Capture = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushPendingSave();
                _saveTimer.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
