using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

namespace OpenPixel.UI
{
    // ========================================================================
    // NotchPixel Settings - a calm, spacious menu:
    // - Inter typeface (bundled) with a single consistent type scale
    // - Animated navigation pill, hover fades, sliding segmented pills,
    //   springing toggle knobs, eased scrolling and a content fade/slide
    // - Every visible control is wired to real behaviour
    // ========================================================================
    public sealed class SettingsWindow : Form
    {
        private const float UiScaleBase = 1.4f;
        private const float RailWidth = 190.0f;
        private const float ContentLeft = 214.0f;
        private const float HeaderBottom = 84.0f;
        private const float ViewTop = 96.0f;
        private const float ViewBottomPad = 18.0f;

        private const float SegH = 40.0f;
        private const float ToggleRowH = 44.0f;
        private const float SliderH = 32.0f;
        private const float HairH = 14.0f;

        private readonly GlobalAppState _state;
        private readonly Action _onSettingsChanged;
        private readonly Action _onOpenIslandPreview;
        private readonly LiquidGlassRenderer _renderer = new();

        private float _uiScale = UiScaleBase;
        private float LogicalWidth => Width / _uiScale;
        private float LogicalHeight => Height / _uiScale;

        private float _mouseX = -1000.0f;
        private float _mouseY = -1000.0f;
        private float _scrollShown = 0.0f;
        private float _scrollTarget = 0.0f;
        private float _previewTimeSec = 0.0f;
        private float _maxScroll = 0.0f;

        private bool _draggingTitlebar = false;
        private int _dragOffX, _dragOffY;

        private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 350 };
        private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 15 };
        private long _lastAnimMs;
        private bool _savePending = false;
        private bool _rendering = false;
        private bool _repaintQueued = false;

        // Animated state
        private float _navY = 0.0f;
        private float _navTargetY = 0.0f;
        private float _contentFade = 1.0f;
        private float _windowReveal = 1.0f;
        private float _scrollBarFade = 0.0f;
        private readonly Dictionary<string, float> _toggleValue = new();
        private readonly Dictionary<string, float> _toggleTarget = new();
        private readonly Dictionary<string, float> _segValue = new();
        private readonly Dictionary<string, float> _segTarget = new();
        private readonly Dictionary<string, float> _hoverValue = new();
        private readonly Dictionary<string, float> _hoverTarget = new();
        private readonly List<(SettingsCategory Cat, string Label, string Icon)> _visibleCats = new();

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
            (SettingsCategory.Keys,          "Shortcuts",     "command"),
            (SettingsCategory.About,         "About",         "info")
        };

        private static readonly Color[] AccentChoices =
        {
            Color.FromArgb(255, 88, 166, 255), Color.FromArgb(255, 156, 113, 255),
            Color.FromArgb(255, 255, 105, 180), Color.FromArgb(255, 255, 159, 67),
            Color.FromArgb(255, 48, 209, 88), Color.FromArgb(255, 245, 245, 247)
        };

        public SettingsWindow(GlobalAppState state, Action onSettingsChanged, Action onOpenIslandPreview = null)
        {
            _state = state;
            _onSettingsChanged = onSettingsChanged;
            _onOpenIslandPreview = onOpenIslandPreview;
            Text = "NotchPixel Settings";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); } catch { }
            TopMost = false;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            _saveTimer.Tick += (_, _) => FlushPendingSave();
            _animTimer.Tick += (_, _) => StepAnimations();

            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            float dpi = 1.0f;
            try
            {
                using var probe = Graphics.FromHwnd(IntPtr.Zero);
                dpi = Math.Clamp(probe.DpiX / 96.0f, 1.0f, 2.5f);
            }
            catch { }
            _uiScale = Math.Min(UiScaleBase * dpi, 4.0f);

            int w = Math.Min(Math.Max((int)(1000 * dpi), 720), (int)(work.Width * 0.94f));
            int h = Math.Min(Math.Max((int)(740 * dpi), 540), (int)(work.Height * 0.92f));
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
            else
            {
                _windowReveal = _state.Settings.ReduceMotion ? 1.0f : 0.0f;
                _lastAnimMs = 0;
                Show();
                Activate();
                RenderNow();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                _lastAnimMs = 0;
                _animTimer.Start();
                RenderNow();
            }
            else
            {
                _animTimer.Stop();
                FlushPendingSave();
            }
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

        private int _recordingShortcutIdx = -1;

        private string GetShortcutValue(int idx) => idx switch
        {
            0 => _state.Settings.HotkeyOpenIsland,
            1 => _state.Settings.HotkeyPlayPause,
            2 => _state.Settings.HotkeyMuteMic,
            3 => _state.Settings.HotkeyHideIsland,
            4 => _state.Settings.HotkeyScreenshot,
            5 => _state.Settings.HotkeyColorPicker,
            _ => "Ctrl+Alt+N"
        };

        private void SetShortcutValue(int idx, string val)
        {
            switch (idx)
            {
                case 0: _state.Settings.HotkeyOpenIsland = val; break;
                case 1: _state.Settings.HotkeyPlayPause = val; break;
                case 2: _state.Settings.HotkeyMuteMic = val; break;
                case 3: _state.Settings.HotkeyHideIsland = val; break;
                case 4: _state.Settings.HotkeyScreenshot = val; break;
                case 5: _state.Settings.HotkeyColorPicker = val; break;
            }
            NotifyChanged();
            IslandMainForm.ReRegisterGlobalHotkeys();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_recordingShortcutIdx >= 0)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    _recordingShortcutIdx = -1;
                    RequestRender();
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey ||
                    e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey ||
                    e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey ||
                    e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu ||
                    e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
                {
                    e.Handled = true;
                    return;
                }
                var parts = new List<string>();
                if (e.Control) parts.Add("Ctrl");
                if (e.Alt) parts.Add("Alt");
                if (e.Shift) parts.Add("Shift");
                parts.Add(e.KeyCode.ToString());
                string combo = string.Join("+", parts);
                int idx = _recordingShortcutIdx;
                _recordingShortcutIdx = -1;
                SetShortcutValue(idx, combo);
                RequestRender();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                if (!string.IsNullOrEmpty(_state.SettingsSearchQuery))
                {
                    _state.SettingsSearchQuery = "";
                    RequestRender();
                }
                else
                {
                    FlushPendingSave();
                    Hide();
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Back && !string.IsNullOrEmpty(_state.SettingsSearchQuery))
            {
                _state.SettingsSearchQuery = _state.SettingsSearchQuery.Substring(0, _state.SettingsSearchQuery.Length - 1);
                RequestRender();
                e.Handled = true;
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (char.IsControl(e.KeyChar)) return;
            if (_state.SettingsSearchQuery.Length >= 24) return;
            _state.SettingsSearchQuery += e.KeyChar;
            RequestRender();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float delta = e.Delta > 0 ? -60.0f : 60.0f;
            if (_state.Settings.ReverseScrolling) delta = -delta;
            _scrollTarget = Math.Clamp(_scrollTarget + delta, 0.0f, Math.Max(0.0f, _maxScroll));
        }

        private void NotifyChanged()
        {
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

        private bool _repaintScheduled;

        // Toggles used to paint the whole panel synchronously, so every click paid
        // for a full Liquid Glass capture. Coalesce bursts of changes into one
        // paint on the next message loop turn instead.
        private void RequestRender()
        {
            if (IsDisposed || !IsHandleCreated || !Visible) return;
            if (_rendering) { _repaintQueued = true; return; }
            if (_repaintScheduled) return;
            _repaintScheduled = true;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _repaintScheduled = false;
                    if (!IsDisposed && Visible) RenderNow();
                }));
            }
            catch
            {
                _repaintScheduled = false;
            }
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
            catch { /* A failed GDI+ frame must never take down the app. */ }
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

        // --------------------------------------------------------------------
        // Animation clock
        // --------------------------------------------------------------------
        private static float Approach(float value, float target, float dt, float rate)
            => value + (target - value) * Math.Min(1.0f, dt * rate);

        private static bool EaseMap(Dictionary<string, float> values, Dictionary<string, float> targets, float dt, float rate)
        {
            bool moved = false;
            foreach (var pair in targets)
            {
                if (!values.TryGetValue(pair.Key, out float current)) current = pair.Value;
                float target = pair.Value;
                float next = current + (target - current) * Math.Min(1.0f, dt * rate);
                if (MathF.Abs(next - target) < 0.004f)
                {
                    next = target;
                    moved |= MathF.Abs(current - target) > 0.0001f;
                }
                else moved = true;
                values[pair.Key] = next;
            }
            return moved;
        }

        private void StepAnimations()
        {
            if (!Visible)
            {
                _animTimer.Stop();
                return;
            }

            long now = Environment.TickCount64;
            float dt = _lastAnimMs > 0 ? Math.Clamp((now - _lastAnimMs) / 1000.0f, 0.001f, 0.08f) : 0.016f;
            _lastAnimMs = now;

            bool moved = false;

            float nav = Approach(_navY, _navTargetY, dt, 15.0f);
            if (MathF.Abs(nav - _navTargetY) > 0.15f) moved = true; else nav = _navTargetY;
            _navY = nav;

            float reveal = Approach(_windowReveal, 1.0f, dt, 13.0f);
            if (MathF.Abs(reveal - 1.0f) > 0.004f) moved = true; else reveal = 1.0f;
            _windowReveal = reveal;

            if (_contentFade < 1.0f)
            {
                _contentFade = Math.Min(1.0f, _contentFade + dt * 5.0f);
                moved = true;
            }

            float scroll = Approach(_scrollShown, _scrollTarget, dt, 17.0f);
            if (MathF.Abs(scroll - _scrollTarget) > 0.15f) moved = true; else scroll = _scrollTarget;
            _scrollShown = scroll;

            float barFadeTarget = _maxScroll > 1.0f && _scrollShown > 0.5f ? 1.0f : 0.35f;
            _scrollBarFade = Approach(_scrollBarFade, barFadeTarget, dt, 10.0f);

            moved |= EaseMap(_toggleValue, _toggleTarget, dt, 16.0f);
            moved |= EaseMap(_segValue, _segTarget, dt, 18.0f);
            moved |= EaseMap(_hoverValue, _hoverTarget, dt, 14.0f);

            if (_state.ActiveSettingsCat == SettingsCategory.Appearance && !_state.Settings.ReduceMotion)
            {
                _previewTimeSec += dt;
                if (now - _lastHoverPaintMs >= 66)
                {
                    _lastHoverPaintMs = now;
                    moved = true;
                }
            }

            if (moved) RenderNow();
        }

        private float GetToggle(string key, bool value)
        {
            float target = value ? 1.0f : 0.0f;
            if (!_toggleTarget.ContainsKey(key)) { _toggleTarget[key] = target; _toggleValue[key] = target; }
            return _toggleValue.TryGetValue(key, out float v) ? v : target;
        }

        private float GetSeg(string key, int index)
        {
            if (!_segTarget.ContainsKey(key)) { _segTarget[key] = index; _segValue[key] = index; }
            return _segValue.TryGetValue(key, out float v) ? v : index;
        }

        private float GetHover(string key, bool hovered)
        {
            _hoverTarget[key] = hovered ? 1.0f : 0.0f;
            if (!_hoverValue.ContainsKey(key)) _hoverValue[key] = 0.0f;
            return _hoverValue[key];
        }

        private static Color Mix(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0.0f, 1.0f);
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static ThemeSpec FadedSurface(ThemeSpec theme, float alpha)
        {
            if (alpha >= 0.999f) return theme;
            var bg = new ThemeSpec
            {
                Name = theme.Name,
                Fill = theme.Fill,
                Edge = theme.Fill,
                Accent = theme.Fill,
                TextPrimary = theme.Fill,
                TextMuted = theme.Fill,
                TextFaint = theme.Fill,
                DefaultBar = theme.DefaultBar,
                IsGlass = theme.IsGlass,
                IsLight = theme.IsLight
            };
            return ThemeSpec.Lerp(bg, theme, alpha);
        }

        private static ThemeSpec SettingsSurfaceTheme(ThemeSpec theme)
        {
            if (!theme.IsGlass) return theme;
            return new ThemeSpec
            {
                Name = theme.Name,
                Fill = Color.FromArgb(242, theme.Fill.R, theme.Fill.G, theme.Fill.B),
                Edge = theme.Edge,
                Accent = theme.Accent,
                TextPrimary = theme.TextPrimary,
                TextMuted = theme.TextMuted,
                TextFaint = theme.TextFaint,
                DefaultBar = theme.DefaultBar,
                IsGlass = theme.IsGlass,
                IsLight = theme.IsLight
            };
        }

        // --------------------------------------------------------------------
        // Frame
        // --------------------------------------------------------------------
        private void RenderFrame()
        {
            lock (_state.SyncRoot)
            {
                if (!_renderer.BeginLayeredFrame(Width, Height, _uiScale)) return;
                _renderer.ClearHitRegions();

                float LW = LogicalWidth;
                float LH = LogicalHeight;
                var s = _state.Settings;
                ThemeSpec islandTheme = _state.CurrentTheme;
                ThemeSpec theme = SettingsSurfaceTheme(islandTheme);

                Color cardBg = theme.IsLight ? Color.FromArgb(14, 12, 12, 14) : Color.FromArgb(17, 255, 255, 255);
                Color cardBg2 = theme.IsLight ? Color.FromArgb(9, 12, 12, 14) : Color.FromArgb(10, 255, 255, 255);
                Color hairline = theme.IsLight ? Color.FromArgb(26, 12, 12, 14) : Color.FromArgb(24, 255, 255, 255);

                _scrollShown = Math.Clamp(_scrollShown, 0.0f, Math.Max(0.0f, _maxScroll));

                // Window frame (deep obsidian #0B0B0C matching notchpixel.com Windows Settings app)
                var frame = new RectangleF(1, 1, LW - 2, LH - 2);
                Color winFill = theme.IsLight ? theme.Fill : Color.FromArgb(255, 11, 11, 12);
                _renderer.FillRoundedRect(frame, 14, winFill);
                _renderer.DrawRoundedRect(frame, 14, hairline, 1.0f);

                // Subtle minimize + close buttons in top-right corner
                var minBtn = new RectangleF(LW - 66, 12, 24, 24);
                float minHover = GetHover("btn-min", _renderer.IsHovered(minBtn, _mouseX, _mouseY));
                if (minHover > 0.01f)
                    _renderer.FillRoundedRect(minBtn, 8, Color.FromArgb((int)(42 * minHover), 255, 255, 255));
                _renderer.DrawTextStr("\u2014", minBtn, 11.0f, true, minHover > 0.5f ? Color.White : theme.TextMuted, hAlign: StringAlignment.Center);
                _renderer.AddHitRegion("settings-min", minBtn, () => WindowState = FormWindowState.Minimized);

                var closeBtn = new RectangleF(LW - 36, 12, 24, 24);
                float closeHover = GetHover("btn-close", _renderer.IsHovered(closeBtn, _mouseX, _mouseY));
                if (closeHover > 0.01f)
                    _renderer.FillRoundedRect(closeBtn, 8, Color.FromArgb((int)(210 * closeHover), 255, 69, 58));
                _renderer.DrawTextStr("\u00D7", closeBtn, 15.0f, false, closeHover > 0.5f ? Color.White : theme.TextMuted, hAlign: StringAlignment.Center);
                _renderer.AddHitRegion("settings-close", closeBtn, () => Hide());

                // Search field right at the top of the left rail (exact match to step_58_shot_4.png)
                var searchBox = new RectangleF(18, 16, RailWidth - 28, 30);
                bool searchHover = _renderer.IsHovered(searchBox, _mouseX, _mouseY);
                float searchGlow = GetHover("search", searchHover || !string.IsNullOrEmpty(_state.SettingsSearchQuery));
                _renderer.FillRoundedRect(searchBox, 10, Color.FromArgb(22, 255, 255, 255));
                _renderer.DrawRoundedRect(searchBox, 10, Mix(hairline, Color.FromArgb(130, 239, 237, 232), searchGlow), 1.0f);
                _renderer.DrawLucideIcon("search", searchBox.X + 14, searchBox.Y + 15, 11.0f, theme.TextFaint, 1.4f);
                _renderer.DrawTextStr(string.IsNullOrEmpty(_state.SettingsSearchQuery) ? "Search settings" : _state.SettingsSearchQuery,
                    new RectangleF(searchBox.X + 26, searchBox.Y, searchBox.Width - 32, searchBox.Height),
                    10.5f, false, string.IsNullOrEmpty(_state.SettingsSearchQuery) ? theme.TextFaint : theme.TextPrimary);

                // Filter the rail live as the user types
                _visibleCats.Clear();
                string query = (_state.SettingsSearchQuery ?? "").Trim();
                foreach (var entry in Categories)
                {
                    if (query.Length == 0 || entry.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        _visibleCats.Add(entry);
                }

                float navTop = 58.0f;
                float navStep = Math.Clamp((LH - navTop - 16.0f) / Math.Max(1, _visibleCats.Count), 26.0f, 34.0f);
                float navH = navStep - 4.0f;

                // Slide the highlight pill to the active row
                int activeIndex = _visibleCats.FindIndex(c => c.Cat == _state.ActiveSettingsCat);
                if (activeIndex >= 0) _navTargetY = navTop + activeIndex * navStep;
                if (_navY <= 0.0f && activeIndex >= 0) _navY = _navTargetY;

                if (activeIndex >= 0)
                {
                    var pill = new RectangleF(18, _navY, RailWidth - 26, navH);
                    _renderer.FillRoundedRect(pill, 9, Color.FromArgb(26, 255, 255, 255));
                    // Cyan/blue indicator dot on the right edge of the active rail pill
                    _renderer.FillCircle(pill.Right - 10, pill.Y + navH * 0.5f, 2.4f, Color.FromArgb(255, 59, 130, 246));
                }

                for (int i = 0; i < _visibleCats.Count; ++i)
                {
                    var (cat, label, icon) = _visibleCats[i];
                    float rowY = navTop + i * navStep;
                    var row = new RectangleF(18, rowY, RailWidth - 26, navH);
                    bool hov = _renderer.IsHovered(row, _mouseX, _mouseY);
                    float hoverT = GetHover($"cat-{cat}", hov && cat != _state.ActiveSettingsCat);
                    if (hoverT > 0.01f) _renderer.FillRoundedRect(row, 10, Color.FromArgb((int)(15 * hoverT), 255, 255, 255));

                    bool active = cat == _state.ActiveSettingsCat;
                    float lift = active ? 0.0f : hoverT;
                    Color rowText = active ? theme.TextPrimary : Mix(theme.TextMuted, theme.TextPrimary, lift);
                    _renderer.DrawLucideIcon(icon, row.X + 17, row.Y + navH * 0.5f, 12.5f, rowText, 1.55f);
                    _renderer.DrawTextStr(label, new RectangleF(row.X + 34, row.Y, row.Width - 42, navH), 11.5f, active, rowText);

                    var capCat = cat;
                    _renderer.AddHitRegion($"cat-{i}", row, () =>
                    {
                        if (_state.ActiveSettingsCat == capCat) return;
                        _state.ActiveSettingsCat = capCat;
                        _scrollTarget = 0.0f;
                        _scrollShown = 0.0f;
                        _contentFade = 0.0f;
                        _lastAnimMs = Environment.TickCount64;
                        _animTimer.Start();
                        RenderNow();
                    });
                }

                if (_visibleCats.Count == 0)
                {
                    _renderer.DrawTextStr("No matches", new RectangleF(24, navTop, RailWidth - 36, 24), 11.0f, false, theme.TextFaint);
                }

                // Divider between rail and canvas
                _renderer.FillRoundedRect(new RectangleF(RailWidth, 22, 1, LH - 44), 0.5f, hairline);

                // ---- Content -------------------------------------------------
                float paneX = ContentLeft;
                float paneW = LW - paneX - 30.0f;
                if (paneW < 120.0f) paneW = 120.0f;

                var contentTheme = FadedSurface(theme, _contentFade);
                float fadeOffset = (1.0f - _contentFade) * 8.0f;

                bool appearancePage = _state.ActiveSettingsCat == SettingsCategory.Appearance;
                if (appearancePage)
                    RenderAppearancePreview(paneX, Math.Max(160.0f, paneW - 48.0f), 12.0f, islandTheme, s);

                var (_, titleLabel, _) = Array.Find(Categories, c => c.Cat == _state.ActiveSettingsCat);
                float titleTop = appearancePage ? 154.0f : 20.0f;
                float headerBottom = appearancePage ? 218.0f : HeaderBottom;
                _renderer.DrawTextStr("SETTINGS", new RectangleF(paneX, titleTop - fadeOffset, 240, 14), 8.5f, true, contentTheme.TextFaint, mono: true);
                _renderer.DrawTextStr(titleLabel ?? "Settings", new RectangleF(paneX, titleTop + 16 - fadeOffset, 360, 34), 21.0f, true, contentTheme.TextPrimary);
                _renderer.DrawTextStr(SubtitleFor(_state.ActiveSettingsCat), new RectangleF(paneX, titleTop + 46 - fadeOffset, paneW, 16), 10.0f, false, contentTheme.TextFaint);
                _renderer.FillRoundedRect(new RectangleF(paneX, headerBottom, paneW, 1), 0.5f, hairline);

                float viewTop = appearancePage ? 230.0f : ViewTop;
                float viewBottom = LH - ViewBottomPad;
                float viewH = Math.Max(40.0f, viewBottom - viewTop);

                var clipState = _renderer.G.Save();
                _renderer.G.SetClip(new RectangleF(paneX - 8, viewTop, paneW + 26, viewH), CombineMode.Intersect);

                float py = viewTop - _scrollShown + fadeOffset;
                _viewTop = viewTop;
                _viewBottom = viewBottom;

                switch (_state.ActiveSettingsCat)
                {
                    case SettingsCategory.Appearance:
                    {
                        Section("APPEARANCE", paneX, ref py, contentTheme);
                        ToggleCard("Auto-hide top notch", "Off keeps the notch always visible (default)", paneX, ref py, paneW,
                            s.NotchAutoHide, "notch_autohide", cardBg, hairline, contentTheme, () =>
                            {
                                s.NotchAutoHide = !s.NotchAutoHide;
                                NotifyChanged();
                            });
                        float surfaceH = CardHeight(SegH, SliderH, SliderH, SliderH, SegH, HairH, SegH, SegH, SegH, SegH, HairH, SliderH, SliderH);
                        Card(py, surfaceH, paneX, paneW, cardBg, hairline);
                        float cy = py + 14.0f;
                        Segmented("Shape", "Floating island or notch joined to the screen edge", paneX + 16, ref cy, paneW - 32,
                            new[] { "Island", "Notch" }, (int)s.Shape, "shape", contentTheme, idx =>
                            {
                                s.Shape = (IslandShape)idx;
                                _state.NotchSpring.SetTarget(s.Shape == IslandShape.Notch ? 100.0f : 0.0f, s.ReduceMotion);
                                NotifyChanged();
                            });
                        Slider("Size", $"{(int)s.PillSizePercent}%", paneX + 16, ref cy, paneW - 32,
                            (s.PillSizePercent - 70.0f) / 70.0f, "pillsize", contentTheme,
                            n => { s.PillSizePercent = Math.Clamp(70.0f + n * 70.0f, 70.0f, 140.0f); NotifyChanged(); });
                        Slider("Open size", $"{(int)s.OpenSizePercent}%", paneX + 16, ref cy, paneW - 32,
                            (s.OpenSizePercent - 70.0f) / 80.0f, "opensize", contentTheme,
                            n => { s.OpenSizePercent = Math.Clamp(70.0f + n * 80.0f, 70.0f, 150.0f); NotifyChanged(); });
                        Slider("Font size", $"{(int)Math.Round(s.TextScale * 100.0f)}%", paneX + 16, ref cy, paneW - 32,
                            (s.TextScale - 0.80f) / 0.60f, "textsize", contentTheme,
                            n => { s.TextScale = Math.Clamp(0.80f + n * 0.60f, 0.80f, 1.40f); NotifyChanged(); });
                        int motionIndex = s.MotionSpeed switch
                        {
                            MotionSpeed.Quick => 0,
                            MotionSpeed.Normal => 1,
                            _ => 2
                        };
                        Segmented("Motion", "Fluid, balanced or lower-power movement", paneX + 16, ref cy, paneW - 32,
                            new[] { "Fluid", "Balanced", "Battery saver" }, motionIndex, "motionfeel", contentTheme,
                            idx =>
                            {
                                s.MotionSpeed = idx switch
                                {
                                    0 => MotionSpeed.Quick,
                                    1 => MotionSpeed.Normal,
                                    _ => MotionSpeed.Calm
                                };
                                NotifyChanged();
                            });
                        Hairline(paneX + 16, cy, paneW - 32, hairline, contentTheme); cy += HairH;
                        Segmented("Surface", "Liquid Glass is the default with live refraction", paneX + 16, ref cy, paneW - 32,
                            new[] { "Liquid Glass", "Candy", "Acrylic", "OLED", "White" },
                            s.Theme switch
                            {
                                SurfaceTheme.LiquidGlass => 0,
                                SurfaceTheme.CandyGlass => 1,
                                SurfaceTheme.Acrylic => 2,
                                SurfaceTheme.PureBlack => 3,
                                SurfaceTheme.PureWhite => 4,
                                _ => -1
                            }, "surface", contentTheme, idx =>
                            {
                                SurfaceTheme[] map = { SurfaceTheme.LiquidGlass, SurfaceTheme.CandyGlass, SurfaceTheme.Acrylic, SurfaceTheme.PureBlack, SurfaceTheme.PureWhite };
                                _state.QueueTheme(map[idx]);
                                _state.CommitPendingParametersOnRenderTick();
                                NotifyChanged();
                            });
                        Segmented("Solid themes", "Black, Graphite and the four soft accents", paneX + 16, ref cy, paneW - 32,
                            new[] { "Black", "Graphite", "Ros\u00E9", "Lavender", "Peach", "Sakura" },
                            s.Theme switch
                            {
                                SurfaceTheme.Black => 0,
                                SurfaceTheme.Graphite => 1,
                                SurfaceTheme.Rose => 2,
                                SurfaceTheme.Lavender => 3,
                                SurfaceTheme.Peach => 4,
                                SurfaceTheme.Sakura => 5,
                                _ => -1
                            }, "soft", contentTheme, idx =>
                            {
                                SurfaceTheme[] map = { SurfaceTheme.Black, SurfaceTheme.Graphite, SurfaceTheme.Rose, SurfaceTheme.Lavender, SurfaceTheme.Peach, SurfaceTheme.Sakura };
                                _state.QueueTheme(map[idx]);
                                _state.CommitPendingParametersOnRenderTick();
                                NotifyChanged();
                            });
                        AccentPalette(paneX + 16, ref cy, paneW - 32, contentTheme, s.AccentColorArgb, argb =>
                        {
                            s.AccentColorArgb = argb;
                            NotifyChanged();
                        });
                        Segmented("Card background", "Use album colours or a plain surface", paneX + 16, ref cy, paneW - 32,
                            new[] { "Album art", "Plain" }, (int)s.CardBackground, "cardbackground", contentTheme,
                            idx => { s.CardBackground = (CardBackgroundMode)idx; NotifyChanged(); });
                        Hairline(paneX + 16, cy, paneW - 32, hairline, contentTheme); cy += HairH;
                        Slider("Liquid Glass blur", $"{(int)s.GlassBlurRadius} px", paneX + 16, ref cy, paneW - 32, s.GlassBlurRadius / 40.0f, "blur", contentTheme, n =>
                        {
                            _state.QueueBlurRadius(n * 40.0f);
                            _state.CommitPendingParametersOnRenderTick();
                            NotifyChanged();
                        });
                        Slider("Corners", $"{(int)(s.CornerRadiusScale * 100)}%", paneX + 16, ref cy, paneW - 32, (s.CornerRadiusScale - 0.15f) / 0.85f, "corners", contentTheme, n =>
                        {
                            _state.QueueCornerRadiusScale(0.15f + n * 0.85f);
                            _state.CommitPendingParametersOnRenderTick();
                            NotifyChanged();
                        });
                        py += surfaceH + 20.0f;

                        ToggleCard("Sync UI colours to playing audio", "Tints buttons, accents, and sub-colours to match your current song", paneX, ref py, paneW,
                            s.SyncThemeToAudioColor, "syncaudiocol", cardBg, hairline, contentTheme,
                            () => { s.SyncThemeToAudioColor = !s.SyncThemeToAudioColor; NotifyChanged(); });

                        Section("MUSIC VISUALS", paneX, ref py, contentTheme);
                        float visualsH = CardHeight(SegH, SegH);
                        Card(py, visualsH, paneX, paneW, cardBg, hairline);
                        cy = py + 14.0f;
                        Segmented("Progress bar", "Scrub bar animation", paneX + 16, ref cy, paneW - 32,
                            new[] { "Wave", "Pixels", "Comet", "Shape", "Simple", "Hearts" }, (int)s.ProgressBar, "bar", contentTheme,
                            idx => { s.ProgressBar = (OpenPixel.Core.ProgressBarStyle)idx; NotifyChanged(); });
                        Segmented("Visualizer", "Closed pill equaliser style", paneX + 16, ref cy, paneW - 32,
                            new[] { "Pixels", "Bars" }, (int)s.Visualizer, "viz", contentTheme,
                            idx => { s.Visualizer = (VisualizerStyle)idx; NotifyChanged(); });
                        py += visualsH + 20.0f;
                        break;
                    }

                    case SettingsCategory.Island:
                    {
                        Section("PLACEMENT", paneX, ref py, contentTheme);
                        float placementH = CardHeight(SegH, SegH, SliderH, ToggleRowH);
                        Card(py, placementH, paneX, paneW, cardBg, hairline);
                        float cy = py + 14.0f;
                        Segmented("Edge", "Top centre or a side edge", paneX + 16, ref cy, paneW - 32,
                            new[] { "Top centre", "Left edge", "Right edge" }, (int)s.Edge, "edge", contentTheme,
                            idx => { s.Edge = (EdgePlacement)idx; NotifyChanged(); });
                        Segmented("Monitor", "Display that hosts the island", paneX + 16, ref cy, paneW - 32,
                            new[] { "Primary", "Display 2", "Display 3" }, Math.Clamp(s.MonitorIndex, 0, 2), "monitor", contentTheme,
                            idx => { s.MonitorIndex = idx; NotifyChanged(); });
                        Slider("Hover open delay", $"{s.HoverOpenDelaySec:F2}s", paneX + 16, ref cy, paneW - 32,
                            s.HoverOpenDelaySec / 1.5f, "hoverdelay", contentTheme,
                            n => { s.HoverOpenDelaySec = Math.Clamp(MathF.Round(n * 15.0f) / 10.0f, 0.0f, 1.5f); NotifyChanged(); });
                        ToggleRow("Show song title in the closed island", "Track name between cover and bars", paneX + 16, ref cy, paneW - 32,
                            s.ShowSongTitleInClosedPill, "songtitle", contentTheme,
                            () => { s.ShowSongTitleInClosedPill = !s.ShowSongTitleInClosedPill; NotifyChanged(); });
                        py += placementH + 20.0f;

                        Section("PRIVACY", paneX, ref py, contentTheme);
                        ToggleCard("Hide from screenshots and recordings", "Glass and Candy glass always stay out of captures", paneX, ref py, paneW,
                            s.HideFromScreenshots, "hideshots", cardBg, hairline, contentTheme, () =>
                            {
                                s.HideFromScreenshots = !s.HideFromScreenshots;
                                if (IsHandleCreated) WindowsSystemHooks.ApplyWindowCaptureAffinity(Handle, _state);
                                NotifyChanged();
                            });
                        break;
                    }

                    case SettingsCategory.General:
                    {
                        Section("STARTUP", paneX, ref py, contentTheme);
                        ToggleCard("Launch at Windows startup", "Start quietly in the notification area", paneX, ref py, paneW,
                            s.LaunchAtStartup, "startup", cardBg, hairline, contentTheme, () =>
                            {
                                s.LaunchAtStartup = !s.LaunchAtStartup;
                                ConfigManager.ApplyLaunchAtStartup(s.LaunchAtStartup);
                                NotifyChanged();
                            });

                        Section("PERFORMANCE", paneX, ref py, contentTheme);
                        ToggleCard("Draw with the processor", "CPU fallback for unsupported GPU drivers", paneX, ref py, paneW,
                            s.DrawWithProcessor, "cpu", cardBg, hairline, contentTheme,
                            () => { s.DrawWithProcessor = !s.DrawWithProcessor; NotifyChanged(); });
                        ToggleCard("Pause effects on battery", "Stops ambient theme motion while on battery power", paneX, ref py, paneW,
                            s.PauseEffectsOnBattery, "batteryfx", cardBg, hairline, contentTheme,
                            () => { s.PauseEffectsOnBattery = !s.PauseEffectsOnBattery; NotifyChanged(); });

                        Section("INPUT", paneX, ref py, contentTheme);
                        ToggleCard("Reverse scrolling", "Invert the wheel direction in the island and settings", paneX, ref py, paneW,
                            s.ReverseScrolling, "reversescroll", cardBg, hairline, contentTheme,
                            () => { s.ReverseScrolling = !s.ReverseScrolling; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Accessibility:
                    {
                        Section("TEXT & CONTRAST", paneX, ref py, contentTheme);
                        float textH = CardHeight(SliderH, ToggleRowH);
                        Card(py, textH, paneX, paneW, cardBg, hairline);
                        float cy = py + 14.0f;
                        Slider("Text scale", $"{(int)(s.TextScale * 100)}%", paneX + 16, ref cy, paneW - 32, (s.TextScale - 1.0f) / 0.5f, "textscale", contentTheme,
                            n => { s.TextScale = Math.Clamp(1.0f + n * 0.5f, 1.0f, 1.5f); NotifyChanged(); });
                        ToggleRow("Higher contrast", "Stronger borders and adaptive ink", paneX + 16, ref cy, paneW - 32,
                            s.HigherContrast, "contrast", contentTheme,
                            () => { s.HigherContrast = !s.HigherContrast; NotifyChanged(); });
                        py += textH + 20.0f;

                        Section("ANIMATION ACCESSIBILITY", paneX, ref py, contentTheme);
                        float motionH = CardHeight(ToggleRowH, SliderH);
                        Card(py, motionH, paneX, paneW, cardBg, hairline);
                        cy = py + 14.0f;
                        ToggleRow("Reduce motion", "Snap transitions without spring bounce", paneX + 16, ref cy, paneW - 32,
                            s.ReduceMotion, "reducemotion", contentTheme,
                            () => { s.ReduceMotion = !s.ReduceMotion; NotifyChanged(); });
                        Slider("Notice duration", $"{s.NoticeDurationSec:F1}s", paneX + 16, ref cy, paneW - 32,
                            (s.NoticeDurationSec - 1.0f) / 9.0f, "noticedur", contentTheme,
                            n => { s.NoticeDurationSec = Math.Clamp(1.0f + n * 9.0f, 1.0f, 10.0f); NotifyChanged(); });
                        py += motionH + 20.0f;
                        break;
                    }

                    case SettingsCategory.Panels:
                    {
                        Section("TAB BEHAVIOUR", paneX, ref py, contentTheme);
                        ToggleCard("Reopen onto the last-viewed tab", "Otherwise always collapses onto Music", paneX, ref py, paneW,
                            s.ReopenOnLastTab, "lasttab", cardBg, hairline, contentTheme,
                            () => { s.ReopenOnLastTab = !s.ReopenOnLastTab; NotifyChanged(); });
                        ToggleCard("Clock timestamps in notifications", "Show 14:32 instead of relative time", paneX, ref py, paneW,
                            s.ClockTimestampsInNotifications, "timestamps", cardBg, hairline, contentTheme,
                            () => { s.ClockTimestampsInNotifications = !s.ClockTimestampsInNotifications; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Widgets:
                    {
                        Section("DESKTOP WIDGETS", paneX, ref py, contentTheme);
                        ToggleCard("24-hour clock", "Applies to the island, taskbar and every widget", paneX, ref py, paneW,
                            s.Use24HourClock, "clock24", cardBg, hairline, contentTheme,
                            () => { s.Use24HourClock = !s.Use24HourClock; NotifyChanged(); });
                        ToggleCard("Lock widgets in place", "Right-click any widget to unlock, recolour or unpin", paneX, ref py, paneW,
                            s.LockDesktopWidgets, "lockwidgets", cardBg, hairline, contentTheme,
                            () => { s.LockDesktopWidgets = !s.LockDesktopWidgets; NotifyChanged(); });

                        Section("ADD A WIDGET", paneX, ref py, contentTheme);
                        float pinH = CardHeight(SegH);
                        Card(py, pinH, paneX, paneW, cardBg, hairline);
                        float cy = py + 14.0f;
                        Segmented("Pin widget", "Places a glass widget on the desktop", paneX + 16, ref cy, paneW - 32,
                            new[] { "Day Clock", "Analog", "To-Do", "Weather", "Calendar", "PC Stats", "Stocks" }, -1, "pinwidget", contentTheme, idx =>
                            {
                                WidgetType[] map =
                                {
                                    WidgetType.DayClock, WidgetType.AnalogClock, WidgetType.TodoList,
                                    WidgetType.MinimalWeather, WidgetType.Calendar, WidgetType.PcStats, WidgetType.StockPrices
                                };
                                AuxiliaryWindows.ToggleDesktopWidget(_state, map[idx]);
                            });
                        py += pinH + 20.0f;

                        Section("WIDGET SOURCES", paneX, ref py, contentTheme);
                        float srcH = 88.0f;
                        Card(py, srcH, paneX, paneW, cardBg, hairline);
                        string photos = string.IsNullOrEmpty(s.PhotosFolder) ? "Pictures folder (default)" : s.PhotosFolder;
                        _renderer.DrawTextStr("Photos: " + photos, new RectangleF(paneX + 16, py + 10, paneW - 32, 18), 9.5f, false, contentTheme.TextMuted);
                        SmallButton("Photos folder\u2026", paneX + 16, py + 34, 150, contentTheme, () =>
                        {
                            using var dialog = new FolderBrowserDialog { Description = "Choose a folder of photos" };
                            if (dialog.ShowDialog() == DialogResult.OK)
                            {
                                s.PhotosFolder = dialog.SelectedPath;
                                NotifyChanged();
                            }
                        });
                        SmallButton("Stock symbols\u2026", paneX + 174, py + 34, 150, contentTheme, () =>
                        {
                            string current = string.Join(",", s.StockSymbols ?? new System.Collections.Generic.List<string>());
                            string entered = TextPromptForm.Ask("Stock symbols", "Comma-separated tickers, e.g. AAPL, MSFT, NVDA", current);
                            if (entered == null) return;
                            s.StockSymbols = new System.Collections.Generic.List<string>();
                            foreach (string raw in entered.Split(','))
                            {
                                string symbol = raw.Trim().ToUpperInvariant();
                                if (symbol.Length > 0 && !s.StockSymbols.Contains(symbol)) s.StockSymbols.Add(symbol);
                            }
                            if (s.StockSymbols.Count == 0) s.StockSymbols.Add("AAPL");
                            StockQuotes.Refresh(s.StockSymbols);
                            NotifyChanged();
                        });
                        py += srcH + 20.0f;
                        break;
                    }

                    case SettingsCategory.Calls:
                    {
                        Section("MICROPHONE", paneX, ref py, contentTheme);
                        ToggleCard("Global Ctrl+Alt+M mute notice", "Red microphone banner in the top notch", paneX, ref py, paneW,
                            s.ShowGlobalMuteNotice, "micnotice", cardBg, hairline, contentTheme,
                            () => { s.ShowGlobalMuteNotice = !s.ShowGlobalMuteNotice; NotifyChanged(); });
                        ToggleCard("Top voice waveform bar", "Smooth curved voice line along the top of the notch when speaking", paneX, ref py, paneW,
                            s.ShowVoiceWaveBar, "voicewavebar", cardBg, hairline, contentTheme,
                            () => { s.ShowVoiceWaveBar = !s.ShowVoiceWaveBar; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Streaming:
                    {
                        Section("CHAT & CAPTURE", paneX, ref py, contentTheme);
                        ToggleCard("Movable stream chat drawer", "Live Twitch and YouTube chat overlay", paneX, ref py, paneW,
                            s.StreamChatEnabled, "streamchat", cardBg, hairline, contentTheme, () =>
                            {
                                s.StreamChatEnabled = !s.StreamChatEnabled;
                                _state.ChatDrawerVisible = s.StreamChatEnabled;
                                AuxiliaryWindows.UpdateDrawers(_state);
                                NotifyChanged();
                            });
                        ToggleCard("OBS recording pill", "Live recording timer while OBS records", paneX, ref py, paneW,
                            s.ShowObsRecPill, "obspill", cardBg, hairline, contentTheme,
                            () => { s.ShowObsRecPill = !s.ShowObsRecPill; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Gaming:
                    {
                        Section("FULLSCREEN GAME OVERLAY", paneX, ref py, contentTheme);
                        ToggleCard("Enable game overlay", "Off hides NotchPixel completely in fullscreen games", paneX, ref py, paneW,
                            s.GameOverlayMasterEnabled, "overlay", cardBg, hairline, contentTheme, () =>
                            {
                                s.GameOverlayMasterEnabled = !s.GameOverlayMasterEnabled;
                                if (!s.GameOverlayMasterEnabled) s.GameOverlayMode = GameOverlayMode.OffHideCompletely;
                                else if (s.GameOverlayMode == GameOverlayMode.OffHideCompletely) s.GameOverlayMode = GameOverlayMode.SlimStrip;
                                NotifyChanged();
                            });

                        float modeH = CardHeight(SegH, HairH, ToggleRowH);
                        Card(py, modeH, paneX, paneW, cardBg, hairline);
                        float cy = py + 14.0f;
                        Segmented("Mode", "Hide, slim telemetry strip, or normal", paneX + 16, ref cy, paneW - 32,
                            new[] { "Off / Hide", "Slim strip", "Normal" }, (int)s.GameOverlayMode, "overlaymode", contentTheme, idx =>
                            {
                                s.GameOverlayMode = (GameOverlayMode)idx;
                                s.GameOverlayMasterEnabled = s.GameOverlayMode != GameOverlayMode.OffHideCompletely;
                                NotifyChanged();
                            });
                        Hairline(paneX + 16, cy, paneW - 32, hairline, contentTheme); cy += HairH;
                        ToggleRow("Strip background", "Disable for lightweight numbers only", paneX + 16, ref cy, paneW - 32,
                            s.GameStripShowBackground, "stripbg", contentTheme,
                            () => { s.GameStripShowBackground = !s.GameStripShowBackground; NotifyChanged(); });
                        py += modeH + 20.0f;
                        break;
                    }

                    case SettingsCategory.AiAgents:
                    {
                        Section("CODING AGENT MONITOR", paneX, ref py, contentTheme);
                        float agentH = CardHeight(26.0f, 50.0f, 42.0f);
                        Card(py, agentH, paneX, paneW, cardBg, hairline);
                        float cy = py + 14.0f;
                        _renderer.DrawTextStr("Claude Code, Codex, Antigravity, Hermes, OpenClaw & Free Buff", new RectangleF(paneX + 16, cy, paneW - 32, 22), 11.5f, true, contentTheme.TextPrimary);
                        cy += 26.0f;
                        _renderer.DrawTextStr("Connected via NotchAI \u00B7 Monitors local CLI agents and the loopback API.\nNotifies you when an agent finishes or needs input.",
                            new RectangleF(paneX + 16, cy, paneW - 32, 42), 10.5f, false, contentTheme.TextMuted, noWrap: false);
                        cy += 50.0f;
                        AccentButton("Simulate AI alert", paneX + 16, cy, 180, contentTheme, () =>
                        {
                            WindowsSystemHooks.UpsertAgentSession(_state, "NotchAI", "Task ready for review", "Needs input", 94, true, true);
                            NotifyChanged();
                        });
                        py += agentH + 16.0f;
                        break;
                    }

                    case SettingsCategory.Tools:
                    {
                        Section("PC DECK & PHONE APP", paneX, ref py, contentTheme);
                        string deckDesc = _state.IsNetworkPrivate
                            ? $"Private Wi-Fi \u00B7 {_state.LocalPrivateIp}:{s.PcDeckTlsPort} \u00B7 code {_state.ActivePairingCode}"
                            : "Switch the Windows Wi-Fi profile to Private to enable PC Deck";
                        ToggleCard("PC Deck (local Wi-Fi remote)", deckDesc, paneX, ref py, paneW,
                            s.PcDeckEnabled, "pcdeck", cardBg, hairline, contentTheme, () =>
                            {
                                if (LocalNetworkServers.DetectPrivateNetworkAndLocalIp(_state))
                                {
                                    s.PcDeckEnabled = !s.PcDeckEnabled;
                                    if (s.PcDeckEnabled) LocalNetworkServers.GenerateTwoMinutePairingCode(_state);
                                    LocalNetworkServers.ApplyDeckEnabled(_state);
                                }
                                NotifyChanged();
                            });

                        if (s.PcDeckEnabled && _state.IsNetworkPrivate)
                        {
                            Card(py, 180.0f, paneX, paneW, cardBg, hairline);
                            string qrPayload = LocalNetworkServers.BuildDeckPairingQrPayload(_state);
                            _renderer.DrawQrCode(qrPayload, paneX + 16, py + 16, 148, Color.FromArgb(255, 12, 12, 14), Color.FromArgb(255, 239, 237, 232));
                            float deckTextX = paneX + 180;
                            float deckTextW = paneW - 196;
                            _renderer.DrawTextStr($"Pairing code \u00B7 2 min", new RectangleF(deckTextX, py + 16, deckTextW, 16), 9.0f, true, contentTheme.TextFaint, mono: true);
                            _renderer.DrawTextStr(_state.ActivePairingCode, new RectangleF(deckTextX, py + 32, deckTextW, 30), 19.0f, true, contentTheme.Accent, mono: true);
                            _renderer.DrawTextStr("Scan with NotchPixel Deck on the same Wi-Fi", new RectangleF(deckTextX, py + 64, deckTextW, 18), 10.0f, false, contentTheme.TextMuted);
                            _renderer.DrawTextStr($"TLS pinned \u00B7 {_state.PairedDeckDevices.Count} phone(s)", new RectangleF(deckTextX, py + 82, deckTextW, 18), 9.5f, false, contentTheme.TextFaint);

                            SmallButton("New code", deckTextX, py + 106, 96, contentTheme, () =>
                            {
                                LocalNetworkServers.GenerateTwoMinutePairingCode(_state);
                                RenderNow();
                            });
                            SmallButton("Revoke all", deckTextX + 104, py + 106, 96, contentTheme, () =>
                            {
                                _state.PairedDeckDevices.Clear();
                                RenderNow();
                            });
                            py += 188.0f;
                        }

                        // Stream Deck App (.exe) & Custom PNG Icon Manager (synced live to Android Deck)
                        Section("STREAM DECK KEYS (PHONE APPS)", paneX, ref py, contentTheme);
                        for (int slotIdx = 0; slotIdx < 6; ++slotIdx)
                        {
                            int capSlot = slotIdx;
                            var slot = _state.LauncherSlots[slotIdx];
                            string slotLabel = !string.IsNullOrEmpty(slot.TargetPath) ? slot.Label : $"Slot {slotIdx + 1} (Empty)";
                            string slotSub = !string.IsNullOrEmpty(slot.IconPath)
                                ? $"{Path.GetFileName(slot.TargetPath)} \u00B7 Icon: {Path.GetFileName(slot.IconPath)}"
                                : (!string.IsNullOrEmpty(slot.TargetPath) ? Path.GetFileName(slot.TargetPath) : "Pick an .exe and optional .png icon");

                            _renderer.DrawTextStr(slotLabel, new RectangleF(paneX + 4, py + 4, paneW - 236, 18), 11.0f, true, contentTheme.TextPrimary);
                            _renderer.DrawTextStr(slotSub, new RectangleF(paneX + 4, py + 22, paneW - 236, 15), 9.0f, false, contentTheme.TextMuted);

                            SmallButton("Pick .exe", paneX + paneW - 224, py + 7, 74, contentTheme, () =>
                            {
                                using var dlg = new OpenFileDialog
                                {
                                    Title = $"Choose app for Deck Slot {capSlot + 1}",
                                    Filter = "Apps (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*",
                                    CheckFileExists = true
                                };
                                if (dlg.ShowDialog() == DialogResult.OK)
                                {
                                    _state.LauncherSlots[capSlot].TargetPath = dlg.FileName;
                                    _state.LauncherSlots[capSlot].Label = Path.GetFileNameWithoutExtension(dlg.FileName);
                                    ConfigManager.SyncLauncherSlotsToSettings(_state);
                                    NotifyChanged();
                                }
                            });
                            SmallButton("Pick .png", paneX + paneW - 144, py + 7, 74, contentTheme, () =>
                            {
                                using var dlg = new OpenFileDialog
                                {
                                    Title = $"Choose PNG icon for Deck Slot {capSlot + 1}",
                                    Filter = "PNG / Image (*.png;*.jpg;*.ico)|*.png;*.jpg;*.jpeg;*.ico|All files (*.*)|*.*",
                                    CheckFileExists = true
                                };
                                if (dlg.ShowDialog() == DialogResult.OK)
                                {
                                    _state.LauncherSlots[capSlot].IconPath = dlg.FileName;
                                    ConfigManager.SyncLauncherSlotsToSettings(_state);
                                    NotifyChanged();
                                }
                            });
                            SmallButton("Clear", paneX + paneW - 64, py + 7, 60, contentTheme, () =>
                            {
                                _state.LauncherSlots[capSlot].TargetPath = "";
                                _state.LauncherSlots[capSlot].IconPath = "";
                                _state.LauncherSlots[capSlot].Label = "+ Add";
                                ConfigManager.SyncLauncherSlotsToSettings(_state);
                                NotifyChanged();
                            });
                            _renderer.DrawLine(paneX, py + 44, paneX + paneW, py + 44, Color.FromArgb(18, 255, 255, 255), 1.0f);
                            py += 46.0f;
                        }
                        py += 8.0f;

                        Section("TASKBAR & WALLPAPER", paneX, ref py, contentTheme);
                        ToggleCard("NotchPixel bottom taskbar", "Start, pinned apps, tray icons and clock", paneX, ref py, paneW,
                            s.BottomTaskbarEnabled, "taskbar", cardBg, hairline, contentTheme, () =>
                            {
                                s.BottomTaskbarEnabled = !s.BottomTaskbarEnabled;
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });
                        ToggleCard("Auto-hide taskbar when maximized", "Off keeps the taskbar always visible with zero flashing", paneX, ref py, paneW,
                            s.TaskbarAutoHideMaximized, "taskbar_autohide", cardBg, hairline, contentTheme, () =>
                            {
                                s.TaskbarAutoHideMaximized = !s.TaskbarAutoHideMaximized;
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });
                        ToggleCard("Live video wallpapers", "Looping .mp4 / .mkv desktop wallpaper with glass widget reaction", paneX, ref py, paneW,
                            s.LiveVideoWallpaperEnabled, "wallpaper", cardBg, hairline, contentTheme, () =>
                            {
                                s.LiveVideoWallpaperEnabled = !s.LiveVideoWallpaperEnabled;
                                AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                                NotifyChanged();
                            });

                        float wpH = 92.0f;
                        Card(py, wpH, paneX, paneW, cardBg, hairline);
                        string wpName = string.IsNullOrEmpty(s.LiveVideoWallpaperPath)
                            ? "No video selected \u2014 the engine shows its animated backdrop"
                            : System.IO.Path.GetFileName(s.LiveVideoWallpaperPath);
                        _renderer.DrawTextStr("Wallpaper source", new RectangleF(paneX + 16, py + 10, paneW - 32, 18), 10.5f, true, contentTheme.TextPrimary);
                        _renderer.DrawTextStr(wpName, new RectangleF(paneX + 16, py + 28, paneW - 32, 18), 9.5f, false, contentTheme.TextMuted);
                        SmallButton("Choose .mp4 / .mkv\u2026", paneX + 16, py + 50, 172, contentTheme, () =>
                        {
                            using var dialog = new OpenFileDialog
                            {
                                Title = "Choose a video wallpaper",
                                Filter = "Video files (*.mp4;*.mkv)|*.mp4;*.mkv|All files (*.*)|*.*",
                                CheckFileExists = true
                            };
                            if (dialog.ShowDialog() == DialogResult.OK)
                            {
                                s.LiveVideoWallpaperPath = dialog.FileName;
                                s.LiveVideoWallpaperEnabled = true;
                                AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                                NotifyChanged();
                                RenderNow();
                            }
                        });
                        SmallButton("Clear", paneX + 196, py + 50, 72, contentTheme, () =>
                        {
                            s.LiveVideoWallpaperPath = "";
                            AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                            NotifyChanged();
                            RenderNow();
                        });
                        py += wpH + 12.0f;

                        ToggleCard("Pause wallpaper under maximized windows", "Stops decoding when a game or maximized app covers the desktop", paneX, ref py, paneW,
                            s.PauseVideoWallpaperWhenCovered, "wpcovered", cardBg, hairline, contentTheme, () =>
                            {
                                s.PauseVideoWallpaperWhenCovered = !s.PauseVideoWallpaperWhenCovered;
                                NotifyChanged();
                            });
                        break;
                    }

                    case SettingsCategory.Keys:
                    {
                        Section("GLOBAL SHORTCUTS (CLICK ANY ROW TO EDIT)", paneX, ref py, contentTheme);
                        string[] labels =
                        {
                            "Open / close island",
                            "Play / pause media",
                            "Mute microphone",
                            "Hide / show island",
                            "Screenshot",
                            "Screen colour picker"
                        };
                        float keysH = CardHeight(20.0f, labels.Length * 42.0f + 38.0f);
                        Card(py, keysH, paneX, paneW, cardBg2, hairline);
                        float ky = py + 10.0f;
                        for (int i = 0; i < labels.Length; ++i)
                        {
                            int cap = i;
                            if (i > 0) Hairline(paneX + 16, ky, paneW - 32, hairline, contentTheme);
                            var rowRect = new RectangleF(paneX + 12, ky + 3, paneW - 24, 36);
                            bool rec = _recordingShortcutIdx == i;
                            float hov = GetHover($"hk-{i}", _renderer.IsHovered(rowRect, _mouseX, _mouseY));
                            if (rec || hov > 0.01f)
                                _renderer.FillRoundedRect(rowRect, 8, rec ? Color.FromArgb(46, contentTheme.Accent.R, contentTheme.Accent.G, contentTheme.Accent.B) : Color.FromArgb((int)(16 * hov), 255, 255, 255));
                            _renderer.DrawTextStr(labels[i], new RectangleF(paneX + 20, ky + 9, paneW - 200, 24), 11.5f, false, contentTheme.TextPrimary);
                            string val = rec ? "Press keys\u2026 (Esc cancels)" : GetShortcutValue(i).Replace("+", " + ");
                            var pill = new RectangleF(paneX + paneW - 184, ky + 7, 164, 26);
                            _renderer.FillRoundedRect(pill, 8, Color.FromArgb(24, 255, 255, 255));
                            _renderer.DrawTextStr(val, pill, 10.0f, true, rec ? Color.White : contentTheme.Accent, mono: true, hAlign: StringAlignment.Center);
                            if (ControlVisible(rowRect))
                                _renderer.AddHitRegion($"hk-{i}", rowRect, () => { _recordingShortcutIdx = cap; RequestRender(); });
                            ky += 42.0f;
                        }
                        Hairline(paneX + 16, ky, paneW - 32, hairline, contentTheme);
                        _renderer.DrawTextStr("Quick launcher 1\u20136", new RectangleF(paneX + 20, ky + 8, paneW - 200, 24), 11.5f, false, contentTheme.TextMuted);
                        _renderer.DrawTextStr("Ctrl + Alt + 1..6", new RectangleF(paneX + paneW - 184, ky + 8, 164, 24), 10.0f, true, contentTheme.TextFaint, mono: true, hAlign: StringAlignment.Center);
                        py += keysH + 20.0f;
                        break;
                    }

                    case SettingsCategory.About:
                    {
                        Section("NOTCHPIXEL 0.5.126", paneX, ref py, contentTheme);
                        float aboutH = CardHeight(26.0f, 56.0f, 42.0f);
                        Card(py, aboutH, paneX, paneW, cardBg, hairline);
                        float cy = py + 16.0f;
                        _renderer.DrawTextStr("NotchPixel for Windows 10 & 11", new RectangleF(paneX + 16, cy, paneW - 32, 24), 13.0f, true, contentTheme.TextPrimary);
                        cy += 26.0f;
                        _renderer.DrawTextStr("Zero accounts, zero telemetry, zero ads. Inter typeface by Rasmus Andersson (OFL).\nAndroid companion: NotchPixel Deck.", new RectangleF(paneX + 16, cy, paneW - 32, 44), 10.5f, false, contentTheme.TextMuted, noWrap: false);
                        cy += 56.0f;
                        AccentButton("Save diagnostic report", paneX + 16, cy, 200, contentTheme, () =>
                        {
                            WindowsSystemHooks.SaveDiagnosticReportToDesktop(_state);
                            NotifyChanged();
                        });
                        py += aboutH + 16.0f;
                        break;
                    }
                }

                float contentBottom = py;
                _maxScroll = Math.Max(0.0f, contentBottom - (viewBottom - 8.0f));
                if (_scrollTarget > _maxScroll) _scrollTarget = _maxScroll;
                _renderer.G.Restore(clipState);

                // Slim scroll indicator
                if (_maxScroll > 1.0f)
                {
                    float trackTop = viewTop + 2.0f;
                    float trackH = viewH - 4.0f;
                    float thumbH = Math.Max(36.0f, trackH * (viewH / (viewH + _maxScroll)));
                    float thumbY = trackTop + (trackH - thumbH) * (_maxScroll <= 0.0f ? 0.0f : Math.Clamp(_scrollShown / _maxScroll, 0.0f, 1.0f));
                    _renderer.FillRoundedRect(new RectangleF(LW - 12, thumbY, 3.0f, thumbH), 1.5f,
                        Color.FromArgb((int)(90 * Math.Max(_scrollBarFade, 0.35f)), 255, 255, 255));
                }

                _renderer.EndLayeredFrame(Handle, Location, (byte)Math.Clamp((int)MathF.Round(254.0f * _windowReveal), 0, 254));
            }
        }

        private bool _previewResting = false;
        private bool _previewPlaying = true;
        private float _previewProgress = 0.46f;
        private int _previewTrackIdx = 0;
        private static readonly (string Title, string Artist)[] _previewTracks =
        {
            ("Neon Harbor", "Kavi Rao"),
            ("Midnight Glass", "Solara"),
            ("After Rain", "Milo Vance")
        };

        private void RenderAppearancePreview(float x, float w, float y, ThemeSpec theme, AppSettings settings)
        {
            // Deep navy-to-violet wallpaper backdrop matching NotchPixel Settings preview (step_160_shot_1 & step_94_shot_2)
            var banner = new RectangleF(x, y, w, 128.0f);
            using (var bGrad = new LinearGradientBrush(banner, Color.FromArgb(255, 24, 48, 98), Color.FromArgb(255, 86, 30, 94), 16.0f))
            {
                bGrad.InterpolationColors = new ColorBlend
                {
                    Colors = new[]
                    {
                        Color.FromArgb(255, 24, 52, 108),
                        Color.FromArgb(255, 44, 44, 96),
                        Color.FromArgb(255, 92, 32, 98)
                    },
                    Positions = new[] { 0.0f, 0.52f, 1.0f }
                };
                using var bPath = _renderer.CreateIslandOrNotchPath(banner.X, banner.Y, banner.Width, banner.Height, 14.0f, 0.0f, EdgePlacement.TopCenter);
                _renderer.G.FillPath(bGrad, bPath);
            }
            _renderer.DrawRoundedRect(banner, 14.0f, Color.FromArgb(24, 255, 255, 255), 1.0f);

            float notch = settings.Shape == IslandShape.Notch ? 1.0f : 0.0f;
            if (_previewResting)
            {
                // Miniature desktop icon silhouettes on the left (matches step_94_shot_2.png)
                _renderer.FillRoundedRect(new RectangleF(x + 14, y + 20, 22, 22), 5.0f, Color.FromArgb(85, 160, 175, 210));
                _renderer.FillRoundedRect(new RectangleF(x + 12, y + 46, 26, 3.0f), 1.5f, Color.FromArgb(75, 220, 225, 240));
                _renderer.FillRoundedRect(new RectangleF(x + 48, y + 20, 22, 22), 5.0f, Color.FromArgb(190, 215, 168, 72));
                _renderer.FillRoundedRect(new RectangleF(x + 46, y + 46, 26, 3.0f), 1.5f, Color.FromArgb(75, 220, 225, 240));

                float pillW = 132.0f;
                float pillH = 26.0f;
                float pillX = x + (w - pillW) * 0.5f;
                float pillY = y + (settings.Shape == IslandShape.Notch ? 0.0f : 6.0f);
                using GraphicsPath pPath = _renderer.CreateIslandOrNotchPath(pillX, pillY, pillW, pillH, 13.0f, notch, EdgePlacement.TopCenter);
                using (var fill = new SolidBrush(Color.FromArgb(240, 12, 12, 14))) _renderer.G.FillPath(fill, pPath);
                using (var edge = new Pen(theme.Edge, 1.0f)) _renderer.G.DrawPath(edge, pPath);
                _renderer.FillRoundedRect(new RectangleF(pillX + 10, pillY + 5, 16, 16), 4.0f, Color.FromArgb(255, 232, 92, 96));
                for (int b = 0; b < 4; ++b)
                {
                    float bh = 4.0f + 5.0f * MathF.Abs(MathF.Sin(_previewTimeSec * 4.2f + b * 1.1f));
                    _renderer.FillRoundedRect(new RectangleF(pillX + pillW - 28 + b * 4.5f, pillY + 13 - bh * 0.5f, 3.0f, bh), 1.0f, Color.FromArgb(255, 240, 120, 140));
                }
            }
            else
            {
                const float hudH = 96.0f;
                float hudW = Math.Min(w - 28.0f, 344.0f);
                float hudX = x + (w - hudW) * 0.5f;
                float hudY = y + (settings.Shape == IslandShape.Notch ? 0.0f : 6.0f);
                using GraphicsPath path = _renderer.CreateIslandOrNotchPath(hudX, hudY, hudW, hudH, 22.0f, notch, EdgePlacement.TopCenter);
                Color fillCol = theme.IsGlass ? Color.FromArgb(200, 14, 15, 20) : theme.Fill;
                using (var fill = new SolidBrush(fillCol)) _renderer.G.FillPath(fill, path);
                using (var edge = new Pen(theme.Edge, 1.0f)) _renderer.G.DrawPath(edge, path);

                string[] icons = { "music", "globe", "bell", "message-circle", "folder", "clipboard", "layout-grid", "sliders-horizontal", "headphones", "settings" };
                float iconY = hudY + 13.0f;
                float iconLeft = hudX + 24.0f;
                float iconRight = hudX + hudW - 40.0f;
                float gap = (iconRight - iconLeft) / (icons.Length - 1);
                for (int i = 0; i < icons.Length; ++i)
                {
                    Color iconColor = i == 0 ? theme.TextPrimary : theme.TextFaint;
                    _renderer.DrawLucideIcon(icons[i], iconLeft + i * gap, iconY, 10.5f, iconColor, 1.35f);
                }
                _renderer.FillRoundedRect(new RectangleF(iconLeft - 2.0f, hudY + 23.0f, 4.0f, 2.0f), 1.0f, theme.Accent);

                var art = new RectangleF(hudX + 18.0f, hudY + 32.0f, 44.0f, 44.0f);
                if (_state.Media.HasSession && _state.Media.AlbumArt is Bitmap albumArt)
                {
                    try { _renderer.G.DrawImage(albumArt, art); }
                    catch { _renderer.FillRoundedRect(art, 8.0f, Color.FromArgb(255, 75, 73, 102)); }
                }
                else
                {
                    using (var artGrad = new LinearGradientBrush(art, Color.FromArgb(255, 255, 106, 61), Color.FromArgb(255, 58, 15, 92), 90.0f))
                        _renderer.G.FillRectangle(artGrad, art);
                    _renderer.FillCircle(art.X + 22.0f, art.Y + 20.0f, 10.0f, Color.FromArgb(255, 255, 209, 102));
                    _renderer.FillRoundedRect(new RectangleF(art.X + 8, art.Y + 29, 28, 3), 1.5f, Color.FromArgb(255, 58, 15, 92));
                    _renderer.FillRoundedRect(new RectangleF(art.X + 11, art.Y + 35, 22, 2), 1.0f, Color.FromArgb(255, 58, 15, 92));
                }

                string title = _state.Media.HasSession && !string.IsNullOrWhiteSpace(_state.Media.Title)
                    ? _state.Media.Title : _previewTracks[_previewTrackIdx].Title;
                string artist = _state.Media.HasSession && !string.IsNullOrWhiteSpace(_state.Media.Artist)
                    ? _state.Media.Artist : _previewTracks[_previewTrackIdx].Artist;
                bool isPlayingNow = _state.Media.HasSession ? _state.Media.IsPlaying : _previewPlaying;
                _renderer.DrawTextStr(title, new RectangleF(hudX + 72, hudY + 36, Math.Max(80, hudW * 0.42f), 20), 12.5f, true, theme.TextPrimary);
                _renderer.DrawTextStr(artist, new RectangleF(hudX + 72, hudY + 55, Math.Max(80, hudW * 0.42f), 16), 10.0f, false, Color.FromArgb(255, 240, 148, 168));

                float controlsX = hudX + hudW - 86.0f;
                float controlsY = hudY + 54.0f;
                _renderer.DrawLucideIcon("skip-back", controlsX, controlsY, 13.0f, theme.TextMuted, 1.7f);
                _renderer.DrawLucideIcon(isPlayingNow ? "pause" : "play", controlsX + 26, controlsY, 17.0f, theme.TextPrimary, 1.8f);
                _renderer.DrawLucideIcon("skip-forward", controlsX + 52, controlsY, 13.0f, theme.TextMuted, 1.7f);
                _renderer.AddHitRegion("preview-media-previous", new RectangleF(controlsX - 10, controlsY - 10, 20, 20), () =>
                {
                    if (_state.Media.HasSession) WindowsSystemHooks.MediaPreviousTrack(_state);
                    else { _previewTrackIdx = (_previewTrackIdx + _previewTracks.Length - 1) % _previewTracks.Length; _previewProgress = 0.12f; RequestRender(); }
                });
                _renderer.AddHitRegion("preview-media-toggle", new RectangleF(controlsX + 16, controlsY - 11, 20, 22), () =>
                {
                    if (_state.Media.HasSession) WindowsSystemHooks.MediaTogglePlayPause(_state);
                    else { _previewPlaying = !_previewPlaying; RequestRender(); }
                });
                _renderer.AddHitRegion("preview-media-next", new RectangleF(controlsX + 42, controlsY - 10, 20, 20), () =>
                {
                    if (_state.Media.HasSession) WindowsSystemHooks.MediaNextTrack(_state);
                    else { _previewTrackIdx = (_previewTrackIdx + 1) % _previewTracks.Length; _previewProgress = 0.08f; RequestRender(); }
                });

                float progressX = hudX + 18.0f;
                float progressY = hudY + 84.0f;
                float progressW = hudW - 36.0f;
                _renderer.FillRoundedRect(new RectangleF(progressX, progressY, progressW, 2.5f), 1.25f, Color.FromArgb(45, 255, 255, 255));
                float progress = _state.Media.HasSession && _state.Media.DurationSec > 0.0
                    ? (float)Math.Clamp(_state.Media.PositionSec / _state.Media.DurationSec, 0.0, 1.0)
                    : _previewProgress;
                float fillW = Math.Clamp(progressW * progress, 3.0f, progressW);
                for (int i = 0; i < 18; ++i)
                {
                    float px = progressX + fillW * i / 18.0f;
                    float py = progressY + 1.2f + (isPlayingNow ? MathF.Sin(_previewTimeSec * 3.2f + i * 0.75f) * 1.3f : 0.0f);
                    if (px <= progressX + fillW) _renderer.FillCircle(px, py, 1.4f, Color.FromArgb(255, 255, 118, 128));
                }
                _renderer.AddHitRegion("preview-media-seek", new RectangleF(progressX, progressY - 7.0f, progressW, 17.0f), null, norm =>
                {
                    if (_state.Media.HasSession && _state.Media.DurationSec > 0.1)
                        WindowsSystemHooks.SeekMediaPosition(_state, norm * _state.Media.DurationSec);
                    else
                    {
                        _previewProgress = Math.Clamp(norm, 0.0f, 1.0f);
                        RequestRender();
                    }
                });
            }

            _renderer.DrawTextStr("Live preview. Point at the island, play, skip, scrub.", new RectangleF(x + 12, y + 104, w - 124, 18), 8.8f, false, Color.FromArgb(200, 239, 237, 232));
            var pillBg = new RectangleF(x + w - 106, y + 102, 96, 21);
            _renderer.FillRoundedRect(pillBg, 10.5f, Color.FromArgb(180, 11, 11, 12));
            var restBtn = new RectangleF(pillBg.X + 2, pillBg.Y + 2, 46, 17);
            var openBtn = new RectangleF(pillBg.X + 48, pillBg.Y + 2, 46, 17);
            if (_previewResting)
                _renderer.FillRoundedRect(restBtn, 8.5f, Color.White);
            else
                _renderer.FillRoundedRect(openBtn, 8.5f, Color.White);

            _renderer.DrawTextStr("Resting", restBtn, 8.8f, _previewResting, _previewResting ? Color.FromArgb(255, 11, 11, 12) : Color.FromArgb(195, 239, 237, 232), hAlign: StringAlignment.Center);
            _renderer.DrawTextStr("Open", openBtn, 8.8f, !_previewResting, !_previewResting ? Color.FromArgb(255, 11, 11, 12) : Color.FromArgb(195, 239, 237, 232), hAlign: StringAlignment.Center);
            _renderer.AddHitRegion("appearance-preview-resting", restBtn, () => { _previewResting = true; RequestRender(); });
            _renderer.AddHitRegion("appearance-preview-open", openBtn, () => { _previewResting = false; RequestRender(); });
        }

        private void AccentPalette(float x, ref float y, float w, ThemeSpec theme, int selectedArgb, Action<int> onSelect)
        {
            _renderer.DrawTextStr("Accent", new RectangleF(x, y + 2, 160, 20), 11.5f, true, theme.TextPrimary);
            _renderer.DrawTextStr("Highlight colour across the island", new RectangleF(x, y + 20, 180, 14), 9.0f, false, theme.TextFaint);

            float left = x + 190.0f;
            var themeChip = new RectangleF(left, y + 4, 38, 24);
            bool themeSelected = selectedArgb == 0;
            _renderer.FillRoundedRect(themeChip, 12.0f, themeSelected ? theme.TextPrimary : Color.FromArgb(18, 255, 255, 255));
            _renderer.DrawTextStr("Theme", themeChip, 8.3f, true,
                themeSelected ? ThemeSpec.AdaptiveInkForBackground(theme.TextPrimary) : theme.TextMuted,
                hAlign: StringAlignment.Center);
            _renderer.AddHitRegion("accent-theme", themeChip, () => onSelect(0));

            for (int i = 0; i < AccentChoices.Length; ++i)
            {
                float cx = left + 54.0f + i * 27.0f;
                var swatch = new RectangleF(cx, y + 5.0f, 20.0f, 20.0f);
                bool selected = selectedArgb == AccentChoices[i].ToArgb();
                if (selected) _renderer.DrawCircle(swatch.X + 10.0f, swatch.Y + 10.0f, 9.0f, theme.TextPrimary, 1.4f);
                _renderer.FillCircle(swatch.X + 10.0f, swatch.Y + 10.0f, selected ? 6.6f : 7.5f, AccentChoices[i]);
                int color = AccentChoices[i].ToArgb();
                _renderer.AddHitRegion($"accent-color-{i}", swatch, () => onSelect(color));
            }
            y += SegH;
        }

        private float _viewTop = 0.0f;
        private float _viewBottom = 0.0f;

        private bool ControlVisible(RectangleF r) => r.Bottom >= _viewTop - 2.0f && r.Top <= _viewBottom + 2.0f;

        private static float CardHeight(params float[] rows)
        {
            float h = 28.0f;
            foreach (float row in rows) h += row;
            return h;
        }

        private static string SubtitleFor(SettingsCategory cat) => cat switch
        {
            SettingsCategory.General => "Startup, performance and input",
            SettingsCategory.Accessibility => "Readability, contrast and reduced motion",
            SettingsCategory.Appearance => "Shape, size, themes and live preview",
            SettingsCategory.Island => "Placement, hover behaviour and privacy",
            SettingsCategory.Panels => "Tab behaviour and notification detail",
            SettingsCategory.Widgets => "Desktop widgets and clock format",
            SettingsCategory.Calls => "Microphone notices",
            SettingsCategory.Streaming => "Chat overlay and capture status",
            SettingsCategory.Gaming => "Fullscreen game overlay",
            SettingsCategory.AiAgents => "Local coding agent awareness",
            SettingsCategory.Tools => "PC Deck, taskbar and wallpapers",
            SettingsCategory.Keys => "Keyboard shortcuts",
            SettingsCategory.About => "Version and support",
            _ => ""
        };

        // --------------------------------------------------------------------
        // Controls
        // --------------------------------------------------------------------
        private void Section(string title, float x, ref float y, ThemeSpec theme)
        {
            _renderer.DrawTextStr(title, new RectangleF(x + 2, y, 320, 14), 8.8f, true, theme.TextFaint, mono: true);
            y += 24.0f;
        }

        private void Card(float y, float h, float x, float w, Color bg, Color border)
        {
            // Flat borderless section layout matching NotchPixel Settings (step_160_shot_1)
            if (h <= 2.0f) return;
            _renderer.DrawLine(x, y + h, x + w, y + h, Color.FromArgb(24, 255, 255, 255), 1.0f);
        }

        private void Hairline(float x, float y, float w, Color color, ThemeSpec theme)
            => _renderer.DrawLine(x, y, x + w, y, color, 1.0f);

        private void ToggleCard(string title, string sub, float x, ref float y, float w, bool value, string key,
            Color bg, Color border, ThemeSpec theme, Action onToggle)
        {
            var card = new RectangleF(x, y, w, 56);
            float hover = GetHover($"tcard-{key}", _renderer.IsHovered(card, _mouseX, _mouseY));
            if (hover > 0.01f)
                _renderer.FillRoundedRect(card, 10, Color.FromArgb((int)(10 * hover), 255, 255, 255));
            _renderer.DrawLine(x, y + 56, x + w, y + 56, Color.FromArgb(20, 255, 255, 255), 1.0f);
            _renderer.DrawTextStr(title, new RectangleF(x + 4, y + 8, w - 82, 22), 11.5f, true, theme.TextPrimary);
            _renderer.DrawTextStr(sub, new RectangleF(x + 4, y + 29, w - 82, 18), 9.6f, false, theme.TextMuted);
            DrawSwitch(x + w - 58, y + 16, 52, 24, GetToggle(key, value), hover, theme);
            if (ControlVisible(card))
            {
                _renderer.AddHitRegion($"tcard-{key}", card, () =>
                {
                    onToggle();
                    _toggleTarget[key] = !value ? 1.0f : 0.0f;
                });
            }
            y += 60.0f;
        }

        private void ToggleRow(string title, string sub, float x, ref float y, float w, bool value, string key,
            ThemeSpec theme, Action onToggle)
        {
            var row = new RectangleF(x, y, w, 44);
            float hover = GetHover($"trow-{key}", _renderer.IsHovered(row, _mouseX, _mouseY));
            if (hover > 0.01f)
                _renderer.FillRoundedRect(new RectangleF(row.X - 6, row.Y - 2, row.Width + 12, row.Height), 9, Color.FromArgb((int)(10 * hover), 255, 255, 255));
            _renderer.DrawTextStr(title, new RectangleF(x, y + 3, w - 70, 20), 11.5f, true, theme.TextPrimary);
            _renderer.DrawTextStr(sub, new RectangleF(x, y + 22, w - 70, 16), 9.6f, false, theme.TextMuted);
            DrawSwitch(x + w - 52, y + 10, 52, 24, GetToggle(key, value), hover, theme);
            if (ControlVisible(row))
            {
                _renderer.AddHitRegion($"trow-{key}", row, () =>
                {
                    onToggle();
                    _toggleTarget[key] = !value ? 1.0f : 0.0f;
                });
            }
            y += ToggleRowH;
        }

        // Liquid Glass Toggle modeled on KaranocaVe/LiquidGlassAvaloniaUI:
        // Translucent vibrant glass track with inner hairline rim + curved refractive capsule thumb
        // featuring soft depth shadow, optical dome gradient, and chromatic edge highlight.
        private void DrawSwitch(float x, float y, float w, float h, float t, float hover, ThemeSpec theme)
        {
            var track = new RectangleF(x, y, w, h);
            Color off = Color.FromArgb(88, 120, 124, 138);
            Color on = Color.FromArgb(225, 48, 209, 88);
            _renderer.FillRoundedRect(track, h * 0.5f, Mix(off, on, t));
            // Inner frosted glass track border
            _renderer.DrawRoundedRect(track, h * 0.5f, Color.FromArgb(54, 255, 255, 255), 1.0f);

            float pad = 2.2f;
            float stretch = 1.0f + 0.12f * hover;
            float thumbW = (w - pad * 2.0f) * 0.58f * stretch;
            float thumbH = (h - pad * 2.0f) * (1.0f + 0.06f * hover);
            float travel = Math.Max(0.0f, w - pad * 2.0f - thumbW);
            float tx = x + pad + travel * t;
            float ty = y + (h - thumbH) * 0.5f;
            var thumb = new RectangleF(tx, ty, thumbW, thumbH);

            // Soft drop shadow beneath the glass knob
            _renderer.FillRoundedRect(new RectangleF(tx, ty + 1.5f, thumbW, thumbH), thumbH * 0.5f, Color.FromArgb(55, 0, 0, 0));

            // Translucent frosted glass knob body (turns more optical/refractive on hover)
            int fillAlpha = (int)(242 - 96 * hover);
            _renderer.FillRoundedRect(thumb, thumbH * 0.5f, Color.FromArgb(fillAlpha, 248, 251, 255));

            // Top specular crescent inside the knob
            var sheen = new RectangleF(tx + 3.0f, ty + 1.5f, thumbW - 6.0f, thumbH * 0.44f);
            _renderer.FillRoundedRect(sheen, sheen.Height * 0.5f, Color.FromArgb((int)(120 + 75 * hover), 255, 255, 255));

            // Chromatic refraction edge highlight (cyan-white)
            _renderer.DrawRoundedRect(thumb, thumbH * 0.5f,
                Color.FromArgb((int)(110 + 110 * hover), 175, 232, 255), 1.15f);
        }

        // Liquid Glass Slider (NotchPixel white track + Kyant0/AndroidLiquidGlass capsule glass thumb & pressed halo)
        private void Slider(string label, string value, float x, ref float y, float w, float norm01, string key,
            ThemeSpec theme, Action<float> onSlide)
        {
            var row = new RectangleF(x, y, w, SliderH);
            float hover = GetHover($"sld-{key}", _renderer.IsHovered(row, _mouseX, _mouseY));
            _renderer.DrawTextStr(label, new RectangleF(x, y + 2, 170, 22), 11.5f, true, theme.TextPrimary);
            _renderer.DrawTextStr(value, new RectangleF(x + w - 54, y + 2, 54, 22), 10.5f, false, theme.TextMuted, mono: true, hAlign: StringAlignment.Far);

            var track = new RectangleF(x + 186, y + 12, w - 252, 4.5f);
            if (track.Width < 30) track = new RectangleF(x + 160, y + 12, w - 220, 4.5f);
            _renderer.FillRoundedRect(track, 2.25f, Color.FromArgb(52, 255, 255, 255));
            float fill = Math.Clamp(norm01, 0.0f, 1.0f);
            if (fill > 0.001f)
                _renderer.FillRoundedRect(new RectangleF(track.X, track.Y, Math.Max(4.0f, track.Width * fill), 4.5f), 2.25f, Color.White);

            float thumbCx = track.X + track.Width * fill;
            float thumbCy = track.Y + 2.25f;
            float pillW = 14.0f + 12.0f * hover;
            float pillH = 14.0f + 3.0f * hover;
            var thumbRect = new RectangleF(thumbCx - pillW * 0.5f, thumbCy - pillH * 0.5f, pillW, pillH);

            if (hover > 0.02f)
            {
                // Concentric Liquid Glass refraction halo on hover/drag (step_160_shot_1 + Kyant0 lens)
                var haloRect = new RectangleF(thumbRect.X - 3.5f, thumbRect.Y - 3.5f, thumbRect.Width + 7.0f, thumbRect.Height + 7.0f);
                _renderer.DrawRoundedRect(haloRect, haloRect.Height * 0.5f, Color.FromArgb((int)(135 * hover), 255, 255, 255), 1.4f);
            }
            int thumbAlpha = (int)(255 - 125 * hover);
            _renderer.FillRoundedRect(thumbRect, thumbRect.Height * 0.5f, Color.FromArgb(thumbAlpha, 255, 255, 255));
            if (hover > 0.02f)
            {
                _renderer.DrawRoundedRect(thumbRect, thumbRect.Height * 0.5f, Color.FromArgb((int)(170 * hover), 180, 235, 255), 1.1f);
            }

            var slideArea = new RectangleF(track.X - 8, y, track.Width + 16, SliderH);
            if (ControlVisible(slideArea)) _renderer.AddHitRegion($"sld-{key}", slideArea, null, onSlide);
            y += SliderH;
        }

        private void Segmented(string label, string sub, float x, ref float y, float w, string[] items, int activeIdx, string key,
            ThemeSpec theme, Action<int> onSelect)
        {
            _renderer.DrawTextStr(label, new RectangleF(x, y + 2, 160, 20), 11.5f, true, theme.TextPrimary);
            _renderer.DrawTextStr(sub, new RectangleF(x, y + 20, 180, 14), 9.0f, false, theme.TextFaint);
            float segL = x + 186.0f;
            float segW = Math.Max(60.0f, w - 186.0f);
            float pad = 3.0f;
            float innerW = segW - pad * 2.0f;
            float gap = 3.0f;
            float btnW = (innerW - (items.Length - 1) * gap) / items.Length;

            // Encased capsule track matching notchpixel.com Settings segmented pills
            var trackRect = new RectangleF(segL, y + 2, segW, 28);
            _renderer.FillRoundedRect(trackRect, 14, theme.IsLight ? Color.FromArgb(14, 12, 12, 14) : Color.FromArgb(255, 20, 20, 22));
            _renderer.DrawRoundedRect(trackRect, 14, theme.IsLight ? Color.FromArgb(24, 12, 12, 14) : Color.FromArgb(24, 255, 255, 255), 1.0f);

            float anim = activeIdx >= 0 ? GetSeg(key, activeIdx) : -1.0f;
            if (activeIdx >= 0)
            {
                float pillX = segL + pad + Math.Clamp(anim, 0.0f, items.Length - 1) * (btnW + gap);
                _renderer.FillRoundedRect(new RectangleF(pillX, y + 5, btnW, 22), 11, theme.TextPrimary);
            }

            for (int i = 0; i < items.Length; ++i)
            {
                float bx = segL + pad + i * (btnW + gap);
                var rect = new RectangleF(bx, y + 5, btnW, 22);
                bool act = i == activeIdx;
                float hov = GetHover($"seg-{key}-{i}", _renderer.IsHovered(rect, _mouseX, _mouseY) && !act);
                if (!act && hov > 0.01f)
                    _renderer.FillRoundedRect(rect, 11, Color.FromArgb((int)(18 * hov), 255, 255, 255));
                Color fg = act ? ThemeSpec.AdaptiveInkForBackground(theme.TextPrimary) : Mix(theme.TextMuted, theme.TextPrimary, hov);
                _renderer.DrawTextStr(items[i], rect, 9.8f, act, fg, hAlign: StringAlignment.Center);
                int cap = i;
                if (ControlVisible(rect))
                {
                    _renderer.AddHitRegion($"seg-{key}-{i}", rect, () =>
                    {
                        _segTarget[key] = cap;
                        onSelect(cap);
                    });
                }
            }
            y += SegH;
        }

        private void AccentButton(string label, float x, float y, float w, ThemeSpec theme, Action onClick)
        {
            var rect = new RectangleF(x, y, w, 32);
            float hover = GetHover($"btn-{label}", _renderer.IsHovered(rect, _mouseX, _mouseY));
            _renderer.FillRoundedRect(rect, 9, Mix(theme.Accent, Color.White, hover * 0.35f));
            _renderer.DrawTextStr(label, rect, 10.5f, true, ThemeSpec.AdaptiveInkForBackground(theme.Accent), hAlign: StringAlignment.Center);
            if (ControlVisible(rect)) _renderer.AddHitRegion($"btn-{label}", rect, onClick);
        }

        private void SmallButton(string label, float x, float y, float w, ThemeSpec theme, Action onClick)
        {
            var rect = new RectangleF(x, y, w, 30);
            float hover = GetHover($"btn-{label}", _renderer.IsHovered(rect, _mouseX, _mouseY));
            _renderer.FillRoundedRect(rect, 8, Color.FromArgb((int)(22 + 14 * hover), 255, 255, 255));
            _renderer.DrawTextStr(label, rect, 10.0f, true, theme.TextPrimary, hAlign: StringAlignment.Center);
            if (ControlVisible(rect)) _renderer.AddHitRegion($"btn-{label}", rect, onClick);
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
                _lastAnimMs = Environment.TickCount64;
                _animTimer.Start();
                if (Visible) RenderNow();
                return;
            }
            if (e.Button == MouseButtons.Left && ly < 52 && lx < LogicalWidth - 56)
            {
                Point pt = Cursor.Position;
                _dragOffX = pt.X - Left;
                _dragOffY = pt.Y - Top;
                _draggingTitlebar = true;
                Capture = true;
            }
        }

        private long _lastHoverPaintMs = 0;

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
                return;
            }
            bool draggingControl = (e.Button & MouseButtons.Left) != 0;
            if (draggingControl)
            {
                try { _renderer.DispatchDrag(lx, ly); }
                catch { }
            }
            long now = Environment.TickCount64;
            int minInterval = draggingControl ? 16 : 36;
            if (now - _lastHoverPaintMs >= minInterval)
            {
                _lastHoverPaintMs = now;
                _lastAnimMs = now;
                if (!_animTimer.Enabled) _animTimer.Start();
                RenderNow();
            }
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
                _animTimer.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
