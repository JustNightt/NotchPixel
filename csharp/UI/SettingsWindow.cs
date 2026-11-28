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
        private const float UiScaleBase = 1.15f;
        private const float RailWidth = 228.0f;
        private const float ContentLeft = 262.0f;
        private const float TopBarHeight = 46.0f;
        private const float HeaderBottom = 114.0f;
        private const float ViewTop = 122.0f;
        private const float ViewBottomPad = 18.0f;

        private const float SegH = 40.0f;
        private const float ToggleRowH = 44.0f;
        private const float SliderH = 32.0f;
        private const float HairH = 14.0f;

        private readonly GlobalAppState _state;
        private readonly Action _onSettingsChanged;
        private readonly Action _onOpenIslandPreview;
        private readonly Action _onOpenOnboarding;
        private readonly LiquidGlassRenderer _renderer = new();

        private bool _langDropdownOpen = false;
        private RectangleF _langAnchorRect = RectangleF.Empty;
        private Bitmap _wheelBitmap;
        private RectangleF _wheelRect = RectangleF.Empty;
        private RectangleF _valBarRect = RectangleF.Empty;
        private bool _draggingWheel = false;
        private bool _draggingValBar = false;
        private float _wheelHue = 0.0f;
        private float _wheelSat = 0.0f;
        private float _wheelVal = 1.0f;
        private int _wheelSyncArgb = 0;

        private static readonly (string Code, string Label)[] LanguageOptions =
        {
            ("system", "Same as Windows"),
            ("en",     "English"),
            ("es",     "Espa\u00F1ol"),
            ("fr",     "Fran\u00E7ais"),
            ("de",     "Deutsch"),
            ("pt",     "Portugu\u00EAs"),
            ("ja",     "\u65E5\u672C\u8A9E"),
            ("zh",     "\u4E2D\u6587"),
            ("ar",     "\u0627\u0644\u0639\u0631\u0628\u064A\u0629")
        };

        private static readonly byte[,] PixelN =
        {
            { 1, 0, 0, 0, 0, 0, 1 },
            { 1, 1, 0, 0, 0, 0, 1 },
            { 1, 0, 1, 0, 0, 0, 1 },
            { 1, 0, 0, 1, 0, 0, 1 },
            { 1, 0, 0, 0, 1, 0, 1 },
            { 1, 0, 0, 0, 0, 2, 1 },
            { 1, 0, 0, 0, 0, 0, 1 }
        };

        private static string AppVersionString
        {
            get
            {
                Version v = typeof(SettingsWindow).Assembly.GetName().Version;
                return v != null ? v.ToString(4) : "0.5.126.0";
            }
        }

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
            (SettingsCategory.Island,        "Island",        "island"),
            (SettingsCategory.Taskbar,       "Taskbar",       "taskbar"),
            (SettingsCategory.Panels,        "Panels",        "layout-grid"),
            (SettingsCategory.Widgets,       "Widgets",       "gauge"),
            (SettingsCategory.Wallpaper,     "Wallpaper",     "image"),
            (SettingsCategory.Calls,         "Calls",         "mic"),
            (SettingsCategory.Streaming,     "Streaming",     "radio"),
            (SettingsCategory.Gaming,        "Gaming",        "gamepad"),
            (SettingsCategory.AiAgents,      "AI agents",     "terminal"),
            (SettingsCategory.Tools,         "Tools",         "zap"),
            (SettingsCategory.Keys,          "Keys",          "keyboard"),
            (SettingsCategory.About,         "About",         "info")
        };

        private static string SearchKeywordsFor(SettingsCategory cat) => cat switch
        {
            SettingsCategory.General => "language startup run windows slim strip maximized last tab music processor cpu",
            SettingsCategory.Accessibility => "text scale contrast reduce motion notice duration",
            SettingsCategory.Appearance => "theme liquid glass acrylic blur accent corner radius pill size visualizer",
            SettingsCategory.Island => "monitor edge placement hover delay auto hide screenshot privacy",
            SettingsCategory.Taskbar => "notchpixel taskbar every screen own apps hide maximized window previews win key number",
            SettingsCategory.Panels => "notifications volume clipboard history timestamps tab",
            SettingsCategory.Widgets => "day clock desktop size colour color wheel hex calendar focus timer weather stats todo world clock photos batteries",
            SettingsCategory.Wallpaper => "live video wallpaper mp4 mkv mov webm hue saturation brightness contrast colours",
            SettingsCategory.Calls => "microphone mute voice waveform",
            SettingsCategory.Streaming => "twitch youtube chat obs recording",
            SettingsCategory.Gaming => "fps strip overlay fullscreen game",
            SettingsCategory.AiAgents => "claude codex antigravity coding agent",
            SettingsCategory.Tools => "pc deck phone qr stream deck",
            SettingsCategory.Keys => "shortcut hotkey keybinding",
            SettingsCategory.About => "version diagnostic welcome setup onboarding",
            _ => ""
        };

        private static readonly Color[] AccentChoices =
        {
            Color.FromArgb(255, 88, 166, 255), Color.FromArgb(255, 156, 113, 255),
            Color.FromArgb(255, 255, 105, 180), Color.FromArgb(255, 255, 159, 67),
            Color.FromArgb(255, 48, 209, 88), Color.FromArgb(255, 245, 245, 247)
        };

        public SettingsWindow(GlobalAppState state, Action onSettingsChanged, Action onOpenIslandPreview = null, Action onOpenOnboarding = null)
        {
            _state = state;
            _onSettingsChanged = onSettingsChanged;
            _onOpenIslandPreview = onOpenIslandPreview;
            _onOpenOnboarding = onOpenOnboarding;
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

        private bool _swallowing = false;
        private float _swallowProgress = 0.0f;
        private Rectangle _normalBounds = Rectangle.Empty;

        public void DismissWithSwallow()
        {
            FlushPendingSave();
            _langDropdownOpen = false;
            if (!Visible) return;
            if (_state.Settings.ReduceMotion)
            {
                Hide();
                return;
            }
            if (!_swallowing)
            {
                _normalBounds = Bounds;
                _swallowing = true;
                _swallowProgress = 0.0f;
                _lastAnimMs = Environment.TickCount64;
                if (!_animTimer.Enabled) _animTimer.Start();
            }
        }

        public void ToggleVisibility()
        {
            if (Visible && !_swallowing) DismissWithSwallow();
            else
            {
                _swallowing = false;
                _swallowProgress = 0.0f;
                if (!_normalBounds.IsEmpty) Bounds = _normalBounds;
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
                DismissWithSwallow();
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
                    DismissWithSwallow();
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

            if (_swallowing)
            {
                _swallowProgress = Math.Min(1.0f, _swallowProgress + dt * 4.8f);
                float t = _swallowProgress * _swallowProgress * (3.0f - 2.0f * _swallowProgress);
                Rectangle scr = Screen.FromControl(this).Bounds;
                int targetW = 190;
                int targetH = 38;
                int targetX = scr.Left + (scr.Width - targetW) / 2;
                int targetY = scr.Top;
                int nx = (int)MathF.Round(_normalBounds.X + (targetX - _normalBounds.X) * t);
                int ny = (int)MathF.Round(_normalBounds.Y + (targetY - _normalBounds.Y) * t);
                int nw = Math.Max(140, (int)MathF.Round(_normalBounds.Width + (targetW - _normalBounds.Width) * t));
                int nh = Math.Max(34, (int)MathF.Round(_normalBounds.Height + (targetH - _normalBounds.Height) * t));
                Bounds = new Rectangle(nx, ny, nw, nh);
                _windowReveal = Math.Clamp(1.0f - t, 0.0f, 1.0f);
                if (_swallowProgress >= 1.0f)
                {
                    _swallowing = false;
                    _swallowProgress = 0.0f;
                    _windowReveal = 1.0f;
                    Hide();
                    Bounds = _normalBounds;
                    return;
                }
                RenderNow();
                return;
            }

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

                // Window frame adopting the active global theme (Pure black, Pure white, Candy glass tint, Rosé/Lavender/Peach/Sakura, etc.)
                var frame = new RectangleF(0.5f, 0.5f, LW - 1.0f, LH - 1.0f);
                Color winFill = s.Theme switch
                {
                    SurfaceTheme.PureBlack => Color.FromArgb(255, 0, 0, 0),
                    SurfaceTheme.PureWhite => Color.FromArgb(255, 246, 246, 244),
                    SurfaceTheme.Graphite => Color.FromArgb(255, 24, 24, 26),
                    SurfaceTheme.CandyGlass => Color.FromArgb(255, 28, 13, 25),
                    SurfaceTheme.Rose => Color.FromArgb(255, 28, 15, 20),
                    SurfaceTheme.Lavender => Color.FromArgb(255, 20, 17, 30),
                    SurfaceTheme.Peach => Color.FromArgb(255, 30, 19, 14),
                    SurfaceTheme.Sakura => Color.FromArgb(255, 31, 16, 23),
                    _ => Color.FromArgb(255, 11, 11, 12)
                };
                Color railDivider = theme.IsLight ? Color.FromArgb(255, 224, 224, 226) : Color.FromArgb(32, 255, 255, 255);
                Color outerEdge = s.Theme == SurfaceTheme.CandyGlass
                    ? Color.FromArgb(145, 255, 158, 207)
                    : (theme.IsLight ? Color.FromArgb(255, 210, 210, 214) : Color.FromArgb(255, 34, 34, 38));
                bool useBlurBg = !_swallowing && (s.Theme == SurfaceTheme.LiquidGlass || s.Theme == SurfaceTheme.CandyGlass || s.Theme == SurfaceTheme.Acrylic);
                bool drewSettingsBlur = false;
                if (useBlurBg)
                {
                    using var gp = CreateRoundedPath(frame, 12);
                    Region prevC = _renderer.G.Clip;
                    _renderer.G.SetClip(gp, CombineMode.Replace);
                    // Settings uses a clean frosted blur background (NO LiquidGlass refraction or edge glow).
                    // Candy glass gets a soft pinkish frosted blur; LiquidGlass/Acrylic get a clean dark blur.
                    Color blurTint = s.Theme == SurfaceTheme.CandyGlass
                        ? Color.FromArgb(162, 58, 18, 46)
                        : (s.Theme == SurfaceTheme.Acrylic ? Color.FromArgb(178, 16, 16, 20) : Color.FromArgb(168, 11, 12, 15));
                    drewSettingsBlur = _renderer.RenderBlurBackdrop(Bounds, 0, 0, LW, LH, MathF.Max(18.0f, s.GlassBlurRadius), blurTint);
                    _renderer.G.Clip = prevC;
                }
                if (!drewSettingsBlur)
                {
                    _renderer.FillRoundedRect(frame, 12, winFill);
                    if (s.Theme == SurfaceTheme.CandyGlass)
                    {
                        using var candyGlow = new LinearGradientBrush(frame,
                            Color.FromArgb(46, 225, 85, 165), Color.FromArgb(16, 120, 45, 155), LinearGradientMode.ForwardDiagonal);
                        using var gp = CreateRoundedPath(frame, 12);
                        _renderer.G.FillPath(candyGlow, gp);
                    }
                }
                _renderer.DrawRoundedRect(frame, 12, outerEdge, 1.0f);

                // Top chrome bar: small square N badge + bold NotchPixel + mono version + close X
                DrawTopBarBadge(new RectangleF(18, 12, 22, 22));
                _renderer.DrawTextStr("NotchPixel", new RectangleF(48, 10, 96, 26), 13.0f, true, Color.FromArgb(255, 245, 245, 247));
                _renderer.DrawTextStr(AppVersionString, new RectangleF(134, 12, 92, 24), 9.0f, false, Color.FromArgb(255, 120, 120, 126), mono: true);

                var closeBtn = new RectangleF(LW - 38, 10, 26, 26);
                float closeHover = GetHover("btn-close", _renderer.IsHovered(closeBtn, _mouseX, _mouseY));
                if (closeHover > 0.01f)
                    _renderer.FillRoundedRect(closeBtn, 7, Color.FromArgb((int)(34 * closeHover), 255, 255, 255));
                DrawCloseGlyph(closeBtn, Mix(Color.FromArgb(255, 158, 158, 164), Color.White, closeHover));
                _renderer.AddHitRegion("settings-close", closeBtn, DismissWithSwallow);

                // Rounded pill search field at the top of the left rail
                var searchBox = new RectangleF(12, 50, RailWidth - 24, 32);
                bool searchHover = _renderer.IsHovered(searchBox, _mouseX, _mouseY);
                float searchGlow = GetHover("search", searchHover || !string.IsNullOrEmpty(_state.SettingsSearchQuery));
                _renderer.FillRoundedRect(searchBox, 16, Color.FromArgb(255, 16, 16, 18));
                _renderer.DrawRoundedRect(searchBox, 16, Mix(Color.FromArgb(255, 42, 42, 46), Color.FromArgb(255, 96, 96, 104), searchGlow), 1.0f);
                _renderer.DrawLucideIcon("search", searchBox.X + 16, searchBox.Y + 16, 11.0f, Color.FromArgb(255, 130, 130, 136), 1.4f);
                _renderer.DrawTextStr(string.IsNullOrEmpty(_state.SettingsSearchQuery) ? "Search settings" : _state.SettingsSearchQuery,
                    new RectangleF(searchBox.X + 30, searchBox.Y, searchBox.Width - 38, searchBox.Height),
                    10.2f, false, string.IsNullOrEmpty(_state.SettingsSearchQuery) ? Color.FromArgb(255, 130, 130, 136) : Color.FromArgb(255, 240, 240, 242));

                // Filter the rail live across tab names AND setting keywords
                _visibleCats.Clear();
                string query = (_state.SettingsSearchQuery ?? "").Trim();
                foreach (var entry in Categories)
                {
                    if (query.Length == 0 ||
                        entry.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        SearchKeywordsFor(entry.Cat).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _visibleCats.Add(entry);
                    }
                }

                float navTop = 94.0f;
                float navStep = Math.Clamp((LH - navTop - 16.0f) / Math.Max(1, _visibleCats.Count), 26.0f, 34.0f);
                float navH = navStep - 3.0f;

                // Slide the highlight pill to the active row
                int activeIndex = _visibleCats.FindIndex(c => c.Cat == _state.ActiveSettingsCat);
                if (activeIndex >= 0) _navTargetY = navTop + activeIndex * navStep;
                if (_navY <= 0.0f && activeIndex >= 0) _navY = _navTargetY;

                if (activeIndex >= 0)
                {
                    var pill = new RectangleF(12, _navY, RailWidth - 24, navH);
                    Color activePillFill = theme.IsLight ? Color.FromArgb(22, 12, 12, 14) : Color.FromArgb(26, 255, 255, 255);
                    _renderer.FillRoundedRect(pill, 8, activePillFill);
                    // Small 4x4 square dot on the right edge of the active item (themed accent on soft/candy themes)
                    Color dotCol = s.Theme == SurfaceTheme.CandyGlass ? Color.FromArgb(255, 255, 128, 184) : theme.TextPrimary;
                    using var dotBrush = new SolidBrush(dotCol);
                    _renderer.G.FillRectangle(dotBrush, pill.Right - 14.0f, pill.Y + (navH - 4.0f) * 0.5f, 4.0f, 4.0f);
                }

                for (int i = 0; i < _visibleCats.Count; ++i)
                {
                    var (cat, label, icon) = _visibleCats[i];
                    float rowY = navTop + i * navStep;
                    var row = new RectangleF(12, rowY, RailWidth - 24, navH);
                    bool hov = _renderer.IsHovered(row, _mouseX, _mouseY);
                    float hoverT = GetHover($"cat-{cat}", hov && cat != _state.ActiveSettingsCat);
                    if (hoverT > 0.01f) _renderer.FillRoundedRect(row, 8, Color.FromArgb((int)(16 * hoverT), 255, 255, 255));

                    bool active = cat == _state.ActiveSettingsCat;
                    float lift = active ? 0.0f : hoverT;
                    Color rowText = active
                        ? Color.FromArgb(255, 245, 245, 247)
                        : Mix(Color.FromArgb(255, 168, 168, 174), Color.FromArgb(255, 240, 240, 242), lift);
                    DrawSidebarIcon(icon, row.X + 18, row.Y + navH * 0.5f, 12.5f, rowText);
                    _renderer.DrawTextStr(label, new RectangleF(row.X + 36, row.Y, row.Width - 52, navH), 11.0f, active, rowText);

                    var capCat = cat;
                    _renderer.AddHitRegion($"cat-{i}", row, () =>
                    {
                        _langDropdownOpen = false;
                        if (_state.ActiveSettingsCat == capCat) return;
                        _state.ActiveSettingsCat = capCat;
                        _scrollTarget = 0.0f;
                        _scrollShown = 0.0f;
                        _contentFade = 1.0f;
                        _lastAnimMs = Environment.TickCount64;
                        _animTimer.Start();
                        RenderNow();
                    });
                }

                if (_visibleCats.Count == 0)
                {
                    _renderer.DrawTextStr("No matches", new RectangleF(20, navTop, RailWidth - 32, 24), 10.5f, false, Color.FromArgb(255, 120, 120, 126));
                }

                // Subtle vertical border between left rail and content
                _renderer.DrawLine(RailWidth, TopBarHeight + 2, RailWidth, LH - 2, railDivider, 1.0f);

                // ---- Content -------------------------------------------------
                float paneX = ContentLeft;
                float paneW = LW - paneX - 32.0f;
                if (paneW < 120.0f) paneW = 120.0f;

                var contentTheme = FadedSurface(theme, _contentFade);
                float fadeOffset = (1.0f - _contentFade) * 6.0f;

                bool appearancePage = _state.ActiveSettingsCat == SettingsCategory.Appearance;
                if (appearancePage)
                    RenderAppearancePreview(paneX, Math.Max(160.0f, paneW - 48.0f), 54.0f, islandTheme, s);

                var (_, titleLabel, _) = Array.Find(Categories, c => c.Cat == _state.ActiveSettingsCat);
                float titleTop = appearancePage ? 190.0f : 54.0f;
                float viewTop = appearancePage ? 256.0f : ViewTop;
                _renderer.DrawTextStr("SETTINGS", new RectangleF(paneX, titleTop - fadeOffset, 240, 14), 8.2f, false, Color.FromArgb(255, 116, 116, 122), mono: true);
                _renderer.DrawTextStr(titleLabel ?? "Settings", new RectangleF(paneX, titleTop + 16 - fadeOffset, 380, 36), 22.0f, true, Color.FromArgb(255, 245, 245, 247));

                float viewBottom = LH - ViewBottomPad;
                float viewH = Math.Max(40.0f, viewBottom - viewTop);

                var clipState = _renderer.G.Save();
                _renderer.G.SetClip(new RectangleF(paneX - 8, viewTop, paneW + 26, viewH), CombineMode.Intersect);

                float py = viewTop - _scrollShown + fadeOffset;
                _viewTop = viewTop;
                _viewBottom = viewBottom;
                _langAnchorRect = RectangleF.Empty;
                _wheelRect = RectangleF.Empty;
                _valBarRect = RectangleF.Empty;

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

                        // Exact 11 Surface Theme pills in 2 rows matching image_8
                        _renderer.DrawTextStr("Surface", new RectangleF(paneX + 16, cy, paneW - 32, 20), 11.4f, true, contentTheme.TextPrimary);
                        cy += 22.0f;
                        _renderer.DrawTextStr(
                            "Glass bends and softly blurs what's behind the island; Graphite is a lighter black; Acrylic lets the desktop show through; Pure black is true OLED black. Ros\u00E9, Lavender, Peach, Sakura and Candy glass are soft themes.",
                            new RectangleF(paneX + 16, cy, paneW - 32, 42), 9.4f, false, contentTheme.TextMuted, noWrap: false);
                        cy += 46.0f;

                        (string Label, SurfaceTheme Theme)[] row1Themes =
                        {
                            ("Black", SurfaceTheme.Black),
                            ("Pure black", SurfaceTheme.PureBlack),
                            ("Pure white", SurfaceTheme.PureWhite),
                            ("Graphite", SurfaceTheme.Graphite),
                            ("Glass", SurfaceTheme.LiquidGlass),
                            ("Acrylic", SurfaceTheme.Acrylic)
                        };
                        (string Label, SurfaceTheme Theme)[] row2Themes =
                        {
                            ("Ros\u00E9", SurfaceTheme.Rose),
                            ("Lavender", SurfaceTheme.Lavender),
                            ("Peach", SurfaceTheme.Peach),
                            ("Sakura", SurfaceTheme.Sakura),
                            ("Candy glass", SurfaceTheme.CandyGlass)
                        };

                        var themeBox = new RectangleF(paneX + 16, cy, paneW - 32, 72.0f);
                        _renderer.FillRoundedRect(themeBox, 12, Color.FromArgb(20, 255, 255, 255));
                        _renderer.DrawRoundedRect(themeBox, 12, hairline, 1.0f);
                        DrawThemePillRow(row1Themes, themeBox.X + 6, themeBox.Y + 6, themeBox.Width - 12, s.Theme, contentTheme, chosen =>
                        {
                            _state.QueueTheme(chosen);
                            _state.CommitPendingParametersOnRenderTick();
                            AuxiliaryWindows.UpdateBottomTaskbar(_state);
                            NotifyChanged();
                        });
                        DrawThemePillRow(row2Themes, themeBox.X + 6, themeBox.Y + 38, themeBox.Width - 12, s.Theme, contentTheme, chosen =>
                        {
                            _state.QueueTheme(chosen);
                            _state.CommitPendingParametersOnRenderTick();
                            AuxiliaryWindows.UpdateBottomTaskbar(_state);
                            NotifyChanged();
                        });
                        cy += 82.0f;

                        // Glass blur slider (0%-100%, default 50%)
                        int blurPct = Math.Clamp((int)MathF.Round((s.GlassBlurRadius / 40.0f) * 100.0f), 0, 100);
                        Slider("Glass blur", $"{blurPct}%", paneX + 16, ref cy, paneW - 32, blurPct / 100.0f, "blur", contentTheme, n =>
                        {
                            _state.QueueBlurRadius(n * 40.0f);
                            _state.CommitPendingParametersOnRenderTick();
                            AuxiliaryWindows.UpdateBottomTaskbar(_state);
                            NotifyChanged();
                        });

                        // Accent color picker
                        AccentPalette(paneX + 16, ref cy, paneW - 32, contentTheme, s.AccentColorArgb, argb =>
                        {
                            s.AccentColorArgb = argb;
                            AuxiliaryWindows.UpdateBottomTaskbar(_state);
                            NotifyChanged();
                        });

                        // Font selector (6 fonts: Geist default, Inter, Roboto, Noto Sans, Manrope, Nunito)
                        _renderer.DrawTextStr("Font", new RectangleF(paneX + 16, cy + 4, paneW - 32, 20), 11.4f, true, contentTheme.TextPrimary);
                        cy += 24.0f;
                        _renderer.DrawTextStr(
                            "The letters the island, the taskbar and this window write in. Every one writes all of NotchPixel's languages; numbers keep their own even-width font.",
                            new RectangleF(paneX + 16, cy, paneW - 32, 34), 9.4f, false, contentTheme.TextMuted, noWrap: false);
                        cy += 38.0f;
                        var fontBox = new RectangleF(paneX + 16, cy, Math.Min(paneW - 32, 360.0f), 68.0f);
                        _renderer.FillRoundedRect(fontBox, 12, Color.FromArgb(20, 255, 255, 255));
                        _renderer.DrawRoundedRect(fontBox, 12, hairline, 1.0f);
                        DrawFontPillRow(new[] { (0, "Geist"), (1, "Inter"), (2, "Roboto"), (3, "Noto Sans") },
                            fontBox.X + 6, fontBox.Y + 6, s.FontIndex, contentTheme, idx =>
                            {
                                s.FontIndex = idx;
                                UiFonts.EnsureFontAvailable(idx, () =>
                                {
                                    try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { RenderNow(); _onSettingsChanged?.Invoke(); })); } catch { }
                                });
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });
                        DrawFontPillRow(new[] { (4, "Manrope"), (5, "Nunito") },
                            fontBox.X + 6, fontBox.Y + 36, s.FontIndex, contentTheme, idx =>
                            {
                                s.FontIndex = idx;
                                UiFonts.EnsureFontAvailable(idx, () =>
                                {
                                    try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { RenderNow(); _onSettingsChanged?.Invoke(); })); } catch { }
                                });
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });
                        cy += 78.0f;

                        Segmented("Card background", "Use album colours or a plain surface", paneX + 16, ref cy, paneW - 32,
                            new[] { "Album art", "Plain" }, (int)s.CardBackground, "cardbackground", contentTheme,
                            idx => { s.CardBackground = (CardBackgroundMode)idx; NotifyChanged(); });
                        Hairline(paneX + 16, cy, paneW - 32, hairline, contentTheme); cy += HairH;
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
                        Section("LANGUAGE", paneX, ref py, contentTheme);
                        {
                            float leftW = paneW - 220.0f;
                            _renderer.DrawTextStr("Language", new RectangleF(paneX, py + 4, leftW, 20), 11.4f, true, contentTheme.TextPrimary);
                            _renderer.DrawTextStr("The island, its notices and this window. Changes apply straight away.",
                                new RectangleF(paneX, py + 26, leftW, 34), 9.6f, false, contentTheme.TextMuted, noWrap: false);

                            float ddW = 204.0f;
                            var ddRect = new RectangleF(paneX + paneW - ddW, py + 12, ddW, 34);
                            _langAnchorRect = ddRect;
                            float ddHov = GetHover("gen-lang", _renderer.IsHovered(ddRect, _mouseX, _mouseY) || _langDropdownOpen);
                            _renderer.FillRoundedRect(ddRect, 8, Mix(Color.FromArgb(255, 16, 16, 18), Color.FromArgb(255, 24, 24, 28), ddHov));
                            _renderer.DrawRoundedRect(ddRect, 8, Mix(Color.FromArgb(255, 44, 44, 48), Color.FromArgb(255, 76, 76, 82), ddHov), 1.0f);
                            _renderer.DrawTextStr(GetLanguageDisplayLabel(s.Language),
                                new RectangleF(ddRect.X + 12, ddRect.Y, ddRect.Width - 34, ddRect.Height), 10.2f, false, contentTheme.TextPrimary);
                            DrawChevronDown(ddRect.Right - 16, ddRect.Y + ddRect.Height * 0.5f, Color.FromArgb(255, 164, 164, 170));
                            if (ControlVisible(ddRect))
                            {
                                _renderer.AddHitRegion("gen-lang", ddRect, () =>
                                {
                                    _langDropdownOpen = !_langDropdownOpen;
                                    RenderNow();
                                });
                            }
                            py += 70.0f;
                            _renderer.DrawLine(paneX, py, paneX + paneW, py, Color.FromArgb(255, 30, 30, 32), 1.0f);
                            py += 20.0f;
                        }

                        Section("GENERAL", paneX, ref py, contentTheme);
                        FlatToggleRow(
                            "Run at startup",
                            "Start NotchPixel when you sign in to Windows.",
                            paneX, ref py, paneW, s.LaunchAtStartup, "startup", contentTheme, 58.0f, true,
                            () =>
                            {
                                s.LaunchAtStartup = !s.LaunchAtStartup;
                                ConfigManager.ApplyLaunchAtStartup(s.LaunchAtStartup);
                                NotifyChanged();
                            });

                        FlatToggleRow(
                            "Slim strip under maximized windows",
                            "Collapse to a thin line so the island never covers browser tabs. Rest the cursor on it to open.",
                            paneX, ref py, paneW, s.SlimStripUnderMaximized, "slimmax", contentTheme, 70.0f, true,
                            () =>
                            {
                                s.SlimStripUnderMaximized = !s.SlimStripUnderMaximized;
                                NotifyChanged();
                            });

                        // When the island opens (Last tab | Music)
                        {
                            float rowH = 92.0f;
                            float segW = 148.0f;
                            float leftW = paneW - segW - 20.0f;
                            _renderer.DrawTextStr("When the island opens", new RectangleF(paneX, py + 6, leftW, 20), 11.4f, true, contentTheme.TextPrimary);
                            _renderer.DrawTextStr(
                                "Last tab opens the card on the tab you were on when it closed, even after a restart. Music always opens on the music card, or on the widgets while nothing is playing. A tab you have turned off is skipped.",
                                new RectangleF(paneX, py + 28, leftW, 56), 9.5f, false, contentTheme.TextMuted, noWrap: false);

                            var trackRect = new RectangleF(paneX + paneW - segW, py + 26, segW, 30);
                            _renderer.FillRoundedRect(trackRect, 15, Color.FromArgb(255, 16, 16, 18));
                            _renderer.DrawRoundedRect(trackRect, 15, Color.FromArgb(255, 42, 42, 46), 1.0f);
                            int openIdx = s.ReopenOnLastTab ? 0 : 1;
                            float anim = GetSeg("openmode", openIdx);
                            float btnW = (segW - 6.0f) * 0.5f;
                            float pillX = trackRect.X + 3.0f + Math.Clamp(anim, 0.0f, 1.0f) * btnW;
                            _renderer.FillRoundedRect(new RectangleF(pillX, trackRect.Y + 3.0f, btnW, 24.0f), 12, Color.FromArgb(255, 237, 237, 237));
                            string[] openItems = { "Last tab", "Music" };
                            for (int i = 0; i < 2; ++i)
                            {
                                var r = new RectangleF(trackRect.X + 3.0f + i * btnW, trackRect.Y + 3.0f, btnW, 24.0f);
                                bool act = i == openIdx;
                                _renderer.DrawTextStr(openItems[i], r, 9.6f, act, act ? Color.FromArgb(255, 16, 16, 18) : Color.FromArgb(255, 205, 205, 210), hAlign: StringAlignment.Center);
                                int cap = i;
                                if (ControlVisible(r))
                                {
                                    _renderer.AddHitRegion($"openmode-{i}", r, () =>
                                    {
                                        _segTarget["openmode"] = cap;
                                        s.ReopenOnLastTab = cap == 0;
                                        NotifyChanged();
                                    });
                                }
                            }
                            py += rowH;
                            _renderer.DrawLine(paneX, py, paneX + paneW, py, Color.FromArgb(255, 30, 30, 32), 1.0f);
                            py += 8.0f;
                        }

                        FlatToggleRow(
                            "Draw with the processor",
                            "If a black box shows around the island, or it flickers, your graphics card's driver is the likely cause: the processor draws NotchPixel instead. It uses a little more of the processor. Takes effect the next time NotchPixel starts (quit it from the tray and open it again).",
                            paneX, ref py, paneW, s.DrawWithProcessor, "cpu", contentTheme, 96.0f, true,
                            () => { s.DrawWithProcessor = !s.DrawWithProcessor; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Taskbar:
                    {
                        Section("NOTCHPIXEL TASKBAR", paneX, ref py, contentTheme);
                        FlatToggleRow(
                            "NotchPixel taskbar",
                            "A notch along the bottom of your main screen in place of Windows' taskbar, in the island's look: Start, your pinned and open apps, the song playing and the timer, the tray, quick settings and the clock. Right-click an app to pin it. Windows' taskbar comes back as it was when you turn this off or NotchPixel closes.",
                            paneX, ref py, paneW, s.BottomTaskbarEnabled, "tb-master", contentTheme, 108.0f, true,
                            () =>
                            {
                                s.BottomTaskbarEnabled = !s.BottomTaskbarEnabled;
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });

                        bool tbOn = s.BottomTaskbarEnabled;
                        FlatToggleRow(
                            "On every screen",
                            "A taskbar along the bottom of each screen, with Start, your apps, the song, the timer, quick settings and the clock. The tray icons stay on the main screen's.",
                            paneX, ref py, paneW, s.TaskbarMultiMonitor, "tb-every", contentTheme, 74.0f, tbOn,
                            () =>
                            {
                                s.TaskbarMultiMonitor = !s.TaskbarMultiMonitor;
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });

                        FlatToggleRow(
                            "Each screen's own apps",
                            "Each taskbar shows the apps open on its own screen, with your pins on all of them, and an app started from one opens on its screen. Off, every taskbar shows every app, as Windows' does.",
                            paneX, ref py, paneW, s.TaskbarPerScreenApps, "tb-perscreen", contentTheme, 82.0f, tbOn,
                            () =>
                            {
                                s.TaskbarPerScreenApps = !s.TaskbarPerScreenApps;
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });

                        FlatToggleRow(
                            "Hide over maximized windows",
                            "Maximized windows get the whole screen, and NotchPixel's taskbar steps aside while one is in front. Push the pointer to the bottom edge to bring it back. Off, windows stop above the taskbar.",
                            paneX, ref py, paneW, s.TaskbarAutoHideMaximized, "tb-autohide", contentTheme, 82.0f, tbOn,
                            () =>
                            {
                                s.TaskbarAutoHideMaximized = !s.TaskbarAutoHideMaximized;
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });

                        FlatToggleRow(
                            "Window previews",
                            "Rest the pointer on an app to see each of its windows live, with its title. Click one to bring it forward, or close it with its X. Clicking an app with several windows shows them so you can choose. Off, a label names the app and a click goes to its next window.",
                            paneX, ref py, paneW, s.TaskbarLiveHoverPreviews, "tb-previews", contentTheme, 96.0f, tbOn,
                            () =>
                            {
                                s.TaskbarLiveHoverPreviews = !s.TaskbarLiveHoverPreviews;
                                AuxiliaryWindows.UpdateBottomTaskbar(_state);
                                NotifyChanged();
                            });

                        FlatToggleRow(
                            "Windows key + number opens the taskbar's apps",
                            "Win+1 to Win+9, and Win+0, bring forward the app at that place on NotchPixel's taskbar.",
                            paneX, ref py, paneW, s.TaskbarWinNumberHotkeys, "tb-winnum", contentTheme, 68.0f, tbOn,
                            () =>
                            {
                                s.TaskbarWinNumberHotkeys = !s.TaskbarWinNumberHotkeys;
                                NotifyChanged();
                            });
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
                        ToggleCard("Notifications in the island", "New notifications appear in the island and stay in one list", paneX, ref py, paneW,
                            s.NotificationsInIsland, "notifisland", cardBg, hairline, contentTheme,
                            () => { s.NotificationsInIsland = !s.NotificationsInIsland; NotifyChanged(); });
                        ToggleCard("Clock timestamps in notifications", "Show 14:32 instead of relative time", paneX, ref py, paneW,
                            s.ClockTimestampsInNotifications, "timestamps", cardBg, hairline, contentTheme,
                            () => { s.ClockTimestampsInNotifications = !s.ClockTimestampsInNotifications; NotifyChanged(); });
                        ToggleCard("Volume in the island", "Show volume level in the island popup", paneX, ref py, paneW,
                            s.VolumeInIsland, "volisland", cardBg, hairline, contentTheme,
                            () => { s.VolumeInIsland = !s.VolumeInIsland; NotifyChanged(); });
                        ToggleCard("Clipboard history", "Remember what you copy so you can paste it again locally", paneX, ref py, paneW,
                            s.ClipboardHistoryEnabled, "cliphist", cardBg, hairline, contentTheme,
                            () => { s.ClipboardHistoryEnabled = !s.ClipboardHistoryEnabled; NotifyChanged(); });
                        break;
                    }

                    case SettingsCategory.Widgets:
                    {
                        Section("DAY CLOCK", paneX, ref py, contentTheme);
                        bool dayClockPinned = AuxiliaryWindows.IsDesktopWidgetPinned(_state, WidgetType.DayClock);
                        FlatToggleRow(
                            "Day clock on the desktop",
                            "The day, date and time in big spaced letters on your wallpaper, with nothing behind them. Drag it to move it; scroll on it or right-click it to resize it and change its colour.",
                            paneX, ref py, paneW, dayClockPinned, "dayclock-on", contentTheme, 82.0f, true,
                            () =>
                            {
                                AuxiliaryWindows.ToggleDesktopWidget(_state, WidgetType.DayClock);
                                AuxiliaryWindows.ApplyDayClockSettings(_state);
                                NotifyChanged();
                            });

                        // Size slider (50% - 200%, default 100%)
                        int dcScale = Math.Clamp(s.DayClockScalePercent, 50, 200);
                        FlatSliderRow("Size", null, $"{dcScale}%", paneX, ref py, paneW, (dcScale - 50.0f) / 150.0f, "dc-size", contentTheme, n =>
                        {
                            s.DayClockScalePercent = Math.Clamp((int)MathF.Round(50.0f + n * 150.0f), 50, 200);
                            AuxiliaryWindows.ApplyDayClockSettings(_state);
                            NotifyChanged();
                        });

                        // Colour wheel + brightness bar + hex readout
                        RenderDayClockColorWheelRow(paneX, ref py, paneW, s, contentTheme);

                        py += 16.0f;
                        Section("WIDGETS", paneX, ref py, contentTheme);
                        s.IslandWidgets ??= new List<string> { "calendar", "focus", "weather", "stats" };
                        _renderer.DrawTextStr(
                            $"{s.IslandWidgets.Count} of 4 on the island. Click to add or remove; they show in the order you add them.",
                            new RectangleF(paneX, py, paneW, 22), 10.0f, false, contentTheme.TextMuted);
                        py += 28.0f;

                        RenderIslandWidgetCardsGrid(paneX, ref py, paneW, s, contentTheme);
                        break;
                    }

                    case SettingsCategory.Wallpaper:
                    {
                        Section("LIVE WALLPAPER", paneX, ref py, contentTheme);
                        FlatToggleRow(
                            "Play a live wallpaper",
                            "A video of your own plays behind the desktop icons. It plays only while you can see the desktop, on battery too: it pauses under windows and in games, and lets its memory go when nobody has seen it for a minute.",
                            paneX, ref py, paneW, s.LiveVideoWallpaperEnabled, "wp-play", contentTheme, 92.0f, true,
                            () =>
                            {
                                s.LiveVideoWallpaperEnabled = !s.LiveVideoWallpaperEnabled;
                                AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                                NotifyChanged();
                            });

                        // Video file picker row
                        {
                            float leftW = paneW - 256.0f;
                            _renderer.DrawTextStr("Video", new RectangleF(paneX, py + 6, leftW, 20), 11.4f, true, contentTheme.TextPrimary);
                            _renderer.DrawTextStr(
                                "MP4, MOV, MKV, WebM, WMV or AVI from your own files. It plays without sound, decoded by the graphics card. A video the size of your screen, at 30 frames a second, costs the least.",
                                new RectangleF(paneX, py + 28, leftW, 56), 9.5f, false, contentTheme.TextMuted, noWrap: false);

                            string fileLabel = string.IsNullOrWhiteSpace(s.LiveVideoWallpaperPath)
                                ? "No video chosen"
                                : Path.GetFileName(s.LiveVideoWallpaperPath);
                            var btnRect = new RectangleF(paneX + paneW - 134, py + 28, 134, 30);
                            _renderer.DrawTextStr(fileLabel, new RectangleF(btnRect.X - 118, py + 33, 110, 20), 9.6f, false, contentTheme.TextMuted, hAlign: StringAlignment.Far);

                            float btnHov = GetHover("wp-choose", _renderer.IsHovered(btnRect, _mouseX, _mouseY));
                            _renderer.FillRoundedRect(btnRect, 15, Mix(Color.FromArgb(255, 16, 16, 18), Color.FromArgb(255, 28, 28, 32), btnHov));
                            _renderer.DrawRoundedRect(btnRect, 15, Mix(Color.FromArgb(255, 48, 48, 54), Color.FromArgb(255, 84, 84, 92), btnHov), 1.0f);
                            _renderer.DrawTextStr("Choose a video...", btnRect, 9.8f, true, contentTheme.TextPrimary, hAlign: StringAlignment.Center);
                            if (ControlVisible(btnRect))
                            {
                                _renderer.AddHitRegion("wp-choose", btnRect, () =>
                                {
                                    using var dialog = new OpenFileDialog
                                    {
                                        Title = "Choose a video wallpaper",
                                        Filter = "Video files (*.mp4;*.mov;*.mkv;*.webm;*.wmv;*.avi)|*.mp4;*.mov;*.mkv;*.webm;*.wmv;*.avi|All files (*.*)|*.*",
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
                            }

                            py += 92.0f;
                            _renderer.DrawLine(paneX, py, paneX + paneW, py, Color.FromArgb(255, 30, 30, 32), 1.0f);
                            py += 12.0f;

                            string status = !s.LiveVideoWallpaperEnabled
                                ? "Off."
                                : string.IsNullOrWhiteSpace(s.LiveVideoWallpaperPath)
                                    ? "Ready — choose a video to start."
                                    : $"Playing {Path.GetFileName(s.LiveVideoWallpaperPath)}.";
                            _renderer.DrawTextStr(status, new RectangleF(paneX, py, paneW, 20), 9.6f, false, contentTheme.TextMuted);
                            py += 36.0f;
                        }

                        Section("COLOURS", paneX, ref py, contentTheme);
                        float hueDeg = Math.Clamp(s.WallpaperHue, -180.0f, 180.0f);
                        FlatSliderRow(
                            "Hue",
                            "Turns every colour of the video around the colour wheel: 180 makes blue orange.",
                            $"{(int)MathF.Round(hueDeg)}\u00B0",
                            paneX, ref py, paneW, (hueDeg + 180.0f) / 360.0f, "wp-hue", contentTheme, n =>
                            {
                                s.WallpaperHue = MathF.Round(-180.0f + n * 360.0f);
                                AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                                NotifyChanged();
                            });

                        float satPct = Math.Clamp(s.WallpaperSaturation, 0.0f, 200.0f);
                        FlatSliderRow(
                            "Saturation",
                            "Less colour to the left, more to the right. 100 is the video as it was made.",
                            $"{(int)MathF.Round(satPct)}%",
                            paneX, ref py, paneW, satPct / 200.0f, "wp-sat", contentTheme, n =>
                            {
                                s.WallpaperSaturation = MathF.Round(n * 200.0f);
                                AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                                NotifyChanged();
                            });

                        float brightOffset = Math.Clamp(s.WallpaperBrightness - 100.0f, -100.0f, 100.0f);
                        FlatSliderRow(
                            "Brightness",
                            "Darker below 0, lighter above it.",
                            $"{(int)MathF.Round(brightOffset)}",
                            paneX, ref py, paneW, (brightOffset + 100.0f) / 200.0f, "wp-bright", contentTheme, n =>
                            {
                                s.WallpaperBrightness = Math.Clamp(MathF.Round(n * 200.0f), 0.0f, 200.0f);
                                AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                                NotifyChanged();
                            });

                        float contrastPct = Math.Clamp(s.WallpaperContrast, 0.0f, 200.0f);
                        FlatSliderRow(
                            "Contrast",
                            "Lower softens highlights and shadows; 100 is original contrast.",
                            $"{(int)MathF.Round(contrastPct)}%",
                            paneX, ref py, paneW, contrastPct / 200.0f, "wp-contrast", contentTheme, n =>
                            {
                                s.WallpaperContrast = MathF.Round(n * 200.0f);
                                AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
                                NotifyChanged();
                            });
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
                        if (_onOpenOnboarding != null)
                        {
                            SmallButton("Run welcome setup", paneX + 228, cy, 160, contentTheme, () => _onOpenOnboarding());
                        }
                        py += aboutH + 16.0f;
                        break;
                    }
                }

                float totalContentBottom = py + _scrollShown - fadeOffset;
                _maxScroll = Math.Max(0.0f, totalContentBottom - (viewBottom - 28.0f));
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

                // Floating language dropdown menu (rendered above clip)
                if (_langDropdownOpen && _state.ActiveSettingsCat == SettingsCategory.General && !_langAnchorRect.IsEmpty)
                {
                    RenderLanguageDropdownPopup(_langAnchorRect);
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
            _renderer.DrawTextStr(title, new RectangleF(x, y, 360, 14), 8.2f, false, Color.FromArgb(255, 118, 118, 124), mono: true);
            y += 18.0f;
            _renderer.DrawLine(x, y, x + (LogicalWidth - ContentLeft - 32.0f), y, Color.FromArgb(255, 30, 30, 32), 1.0f);
            y += 10.0f;
        }

        private void FlatToggleRow(
            string title, string sub, float x, ref float y, float w,
            bool value, string key, ThemeSpec theme, float rowH, bool enabled, Action onToggle)
        {
            var row = new RectangleF(x, y, w, rowH);
            float hover = enabled ? GetHover($"ftog-{key}", _renderer.IsHovered(row, _mouseX, _mouseY)) : 0.0f;
            if (!enabled)
            {
                _renderer.FillRoundedRect(new RectangleF(x, y + 2, w, rowH - 10), 6, Color.FromArgb(255, 19, 19, 21));
            }
            Color titleCol = enabled ? Color.FromArgb(255, 245, 245, 247) : Color.FromArgb(255, 128, 128, 134);
            Color subCol = enabled ? Color.FromArgb(255, 152, 152, 158) : Color.FromArgb(255, 92, 92, 98);

            float textW = w - 74.0f;
            _renderer.DrawTextStr(title, new RectangleF(x + (enabled ? 0 : 8), y + 6, textW, 20), 11.2f, true, titleCol);
            _renderer.DrawTextStr(sub, new RectangleF(x + (enabled ? 0 : 8), y + 28, textW, Math.Max(20.0f, rowH - 36.0f)), 9.5f, false, subCol, noWrap: false);

            const float swW = 46.0f;
            const float swH = 26.0f;
            float swX = x + w - swW - (enabled ? 0 : 6);
            float swY = y + Math.Min(20.0f, (rowH - swH) * 0.42f);
            DrawSwitch(swX, swY, swW, swH, GetToggle(key, value), hover, theme, enabled);

            if (enabled && ControlVisible(row))
            {
                _renderer.AddHitRegion($"ftog-{key}", row, () =>
                {
                    onToggle();
                    _toggleTarget[key] = !value ? 1.0f : 0.0f;
                });
            }

            y += rowH;
            _renderer.DrawLine(x, y, x + w, y, Color.FromArgb(255, 30, 30, 32), 1.0f);
            y += 8.0f;
        }

        private void FlatSliderRow(
            string title, string sub, string valueText, float x, ref float y, float w,
            float norm01, string key, ThemeSpec theme, Action<float> onSlide)
        {
            bool hasSub = !string.IsNullOrEmpty(sub);
            float rowH = hasSub ? 66.0f : 44.0f;
            float leftW = hasSub ? w * 0.48f : 150.0f;

            _renderer.DrawTextStr(title, new RectangleF(x, y + (hasSub ? 6 : 10), leftW, 20), 11.2f, true, Color.FromArgb(255, 245, 245, 247));
            if (hasSub)
            {
                _renderer.DrawTextStr(sub, new RectangleF(x, y + 28, leftW, 34), 9.4f, false, Color.FromArgb(255, 152, 152, 158), noWrap: false);
            }

            float valW = 54.0f;
            float trackX = x + leftW + 18.0f;
            float trackW = Math.Max(80.0f, w - leftW - valW - 28.0f);
            float trackY = y + (hasSub ? 28.0f : 20.0f);

            var track = new RectangleF(trackX, trackY, trackW, 4.0f);
            _renderer.FillRoundedRect(track, 2.0f, Color.FromArgb(255, 44, 44, 48));
            float fill = Math.Clamp(norm01, 0.0f, 1.0f);
            if (fill > 0.001f)
            {
                _renderer.FillRoundedRect(new RectangleF(track.X, track.Y, Math.Max(4.0f, track.Width * fill), 4.0f), 2.0f, Color.FromArgb(255, 237, 237, 237));
            }

            float thumbCx = track.X + track.Width * fill;
            float thumbCy = track.Y + 2.0f;
            _renderer.FillCircle(thumbCx, thumbCy, 7.0f, Color.FromArgb(255, 245, 245, 247));
            _renderer.DrawTextStr(valueText, new RectangleF(x + w - valW, trackY - 8.0f, valW, 20), 10.0f, false, Color.FromArgb(255, 205, 205, 210), mono: true, hAlign: StringAlignment.Far);

            var slideArea = new RectangleF(track.X - 8, trackY - 14, track.Width + 16, 30);
            if (ControlVisible(slideArea)) _renderer.AddHitRegion($"fsld-{key}", slideArea, null, onSlide);

            y += rowH;
            _renderer.DrawLine(x, y, x + w, y, Color.FromArgb(255, 30, 30, 32), 1.0f);
            y += 8.0f;
        }

        private void RenderDayClockColorWheelRow(float x, ref float y, float w, AppSettings s, ThemeSpec theme)
        {
            if (_wheelSyncArgb != s.DayClockAccentColorArgb)
            {
                Color c = Color.FromArgb(s.DayClockAccentColorArgb);
                RgbToHsv(c, out _wheelHue, out _wheelSat, out _wheelVal);
                _wheelSyncArgb = s.DayClockAccentColorArgb;
            }

            float wheelSize = 148.0f;
            float wheelX = x + w - wheelSize - 8.0f;
            float wheelY = y + 8.0f;
            _wheelRect = new RectangleF(wheelX, wheelY, wheelSize, wheelSize);

            float leftW = Math.Max(140.0f, wheelX - x - 24.0f);
            _renderer.DrawTextStr("Colour", new RectangleF(x, wheelY + 52.0f, leftW, 20), 11.2f, true, Color.FromArgb(255, 245, 245, 247));
            _renderer.DrawTextStr(
                "Pick anywhere on the wheel. The middle is white; the bar under it goes from black to full brightness.",
                new RectangleF(x, wheelY + 74.0f, leftW, 50), 9.5f, false, Color.FromArgb(255, 152, 152, 158), noWrap: false);

            EnsureWheelBitmap();
            if (_wheelBitmap != null)
            {
                _renderer.G.DrawImage(_wheelBitmap, _wheelRect);
            }
            _renderer.DrawRoundedRect(_wheelRect, wheelSize * 0.5f, Color.FromArgb(60, 255, 255, 255), 1.0f);

            // Draggable double-ring selector reticle
            float rad = _wheelSat * (wheelSize * 0.5f - 2.0f);
            float angleRad = _wheelHue * MathF.PI / 180.0f;
            float selX = _wheelRect.X + wheelSize * 0.5f + MathF.Cos(angleRad) * rad;
            float selY = _wheelRect.Y + wheelSize * 0.5f + MathF.Sin(angleRad) * rad;
            using (var darkPen = new Pen(Color.FromArgb(180, 0, 0, 0), 2.5f))
                _renderer.G.DrawEllipse(darkPen, selX - 6.5f, selY - 6.5f, 13.0f, 13.0f);
            using (var whitePen = new Pen(Color.White, 1.8f))
                _renderer.G.DrawEllipse(whitePen, selX - 6.5f, selY - 6.5f, 13.0f, 13.0f);

            // Brightness gradient bar below wheel
            float barY = _wheelRect.Bottom + 14.0f;
            _valBarRect = new RectangleF(wheelX, barY, wheelSize, 12.0f);
            Color fullBright = HsvToRgb(_wheelHue, _wheelSat, 1.0f);
            using (var path = CreateRoundedPath(_valBarRect, 6.0f))
            using (var brush = new LinearGradientBrush(_valBarRect, Color.Black, fullBright, LinearGradientMode.Horizontal))
            {
                _renderer.G.FillPath(brush, path);
            }
            _renderer.DrawRoundedRect(_valBarRect, 6.0f, Color.FromArgb(255, 52, 52, 58), 1.0f);

            float thumbX = _valBarRect.X + Math.Clamp(_wheelVal, 0.0f, 1.0f) * _valBarRect.Width;
            float thumbY = _valBarRect.Y + _valBarRect.Height * 0.5f;
            _renderer.FillCircle(thumbX, thumbY, 7.0f, Color.FromArgb(255, 18, 18, 20));
            using (var whitePen = new Pen(Color.White, 2.0f))
                _renderer.G.DrawEllipse(whitePen, thumbX - 6.5f, thumbY - 6.5f, 13.0f, 13.0f);

            // Live hex readout underneath
            Color cur = Color.FromArgb(s.DayClockAccentColorArgb);
            string hex = $"#{cur.R:X2}{cur.G:X2}{cur.B:X2}";
            _renderer.DrawTextStr(hex, new RectangleF(wheelX, _valBarRect.Bottom + 8.0f, wheelSize, 18), 9.6f, false, Color.FromArgb(255, 220, 220, 226), mono: true, hAlign: StringAlignment.Center);

            y = _valBarRect.Bottom + 36.0f;
        }

        private void RenderIslandWidgetCardsGrid(float x, ref float y, float w, AppSettings s, ThemeSpec theme)
        {
            s.IslandWidgets ??= new List<string> { "calendar", "focus", "weather", "stats" };
            (string Id, string Title, string Desc, WidgetType? DesktopType)[] cards =
            {
                ("calendar",   "Today",        "The date and current week.",                       WidgetType.Calendar),
                ("focus",      "Focus timer",  "A Pomodoro timer you start on the island.",        WidgetType.FocusTimer),
                ("weather",    "Weather",      "Now, and today's high and low.",                   WidgetType.MinimalWeather),
                ("stats",      "System stats", "CPU, GPU and memory as they move.",                WidgetType.PcStats),
                ("clock",      "Clock",        "Your time, and the time in a city you pick.",      WidgetType.WorldClock),
                ("todo",       "To do",        "A short list you tick off.",                       WidgetType.TodoList),
                ("photos",     "Photos",       "Your pictures, a new one every half minute.",      WidgetType.Photos),
                ("batteries",  "Batteries",    "This PC, headphones, mouse and more.",             WidgetType.Batteries)
            };

            const float gap = 12.0f;
            float cardW = (w - gap) * 0.5f;
            const float cardH = 148.0f;

            for (int i = 0; i < cards.Length; ++i)
            {
                int col = i % 2;
                int row = i / 2;
                float cx = x + col * (cardW + gap);
                float cy = y + row * (cardH + gap);
                var rect = new RectangleF(cx, cy, cardW, cardH);

                var c = cards[i];
                int orderIdx = s.IslandWidgets.IndexOf(c.Id);
                bool selected = orderIdx >= 0;
                float hov = GetHover($"wcard-{c.Id}", _renderer.IsHovered(rect, _mouseX, _mouseY));

                _renderer.FillRoundedRect(rect, 12, Color.FromArgb(255, 14, 14, 16));
                Color borderCol = selected
                    ? Color.FromArgb(255, 237, 237, 237)
                    : Mix(Color.FromArgb(255, 36, 36, 40), Color.FromArgb(255, 86, 86, 94), hov);
                _renderer.DrawRoundedRect(rect, 12, borderCol, selected ? 1.5f : 1.0f);

                // Inner widget mockup surface
                var inner = new RectangleF(rect.X + 12, rect.Y + 10, rect.Width - 24, 86);
                _renderer.FillRoundedRect(inner, 10, Color.FromArgb(255, 24, 24, 27));
                _renderer.DrawRoundedRect(inner, 10, Color.FromArgb(255, 42, 42, 46), 1.0f);

                DrawWidgetPreviewMockup(c.Id == "clock" ? "worldclock" : c.Id, inner, s);

                // Title + description + desktop pin icon + order badge (1..4 or +)
                float footY = inner.Bottom + 8.0f;
                _renderer.DrawTextStr(c.Title, new RectangleF(rect.X + 12, footY, rect.Width - 78, 18), 10.2f, true, Color.FromArgb(255, 245, 245, 247));
                _renderer.DrawTextStr(c.Desc, new RectangleF(rect.X + 12, footY + 19, rect.Width - 24, 16), 8.6f, false, Color.FromArgb(255, 142, 142, 148));

                // Register whole-card island toggle FIRST so the desktop pin button wins on click
                if (ControlVisible(rect))
                {
                    string capturedId = c.Id;
                    _renderer.AddHitRegion($"wcard-{c.Id}", rect, () =>
                    {
                        if (s.IslandWidgets.Contains(capturedId))
                        {
                            s.IslandWidgets.Remove(capturedId);
                        }
                        else
                        {
                            if (s.IslandWidgets.Count >= 4) s.IslandWidgets.RemoveAt(s.IslandWidgets.Count - 1);
                            s.IslandWidgets.Add(capturedId);
                        }
                        NotifyChanged();
                    });
                }

                // Desktop pin monitor button (registered AFTER card so it always receives clicks)
                var pinBtn = new RectangleF(rect.Right - 60, footY - 1, 22, 22);
                bool pinned = c.DesktopType.HasValue && AuxiliaryWindows.IsDesktopWidgetPinned(_state, c.DesktopType.Value);
                _renderer.DrawCircle(pinBtn.X + 11, pinBtn.Y + 11, 10.0f, pinned ? Color.FromArgb(255, 237, 237, 237) : Color.FromArgb(255, 76, 76, 82), 1.0f);
                _renderer.DrawRoundedRect(new RectangleF(pinBtn.X + 6, pinBtn.Y + 6.5f, 10, 7), 1.2f, pinned ? Color.White : Color.FromArgb(255, 168, 168, 174), 1.1f);
                if (c.DesktopType.HasValue && ControlVisible(pinBtn))
                {
                    WidgetType dt = c.DesktopType.Value;
                    _renderer.AddHitRegion($"wpin-{c.Id}", pinBtn, () =>
                    {
                        AuxiliaryWindows.ToggleDesktopWidget(_state, dt);
                        NotifyChanged();
                    });
                }

                // Order badge circle (1-4 white fill or + outline)
                var badge = new RectangleF(rect.Right - 34, footY - 1, 22, 22);
                if (selected)
                {
                    _renderer.FillCircle(badge.X + 11, badge.Y + 11, 10.5f, Color.FromArgb(255, 237, 237, 237));
                    _renderer.DrawTextStr((orderIdx + 1).ToString(), badge, 9.6f, true, Color.FromArgb(255, 16, 16, 18), hAlign: StringAlignment.Center);
                }
                else
                {
                    _renderer.DrawCircle(badge.X + 11, badge.Y + 11, 10.0f, Color.FromArgb(255, 76, 76, 82), 1.0f);
                    _renderer.DrawTextStr("+", badge, 11.0f, false, Color.FromArgb(255, 168, 168, 174), hAlign: StringAlignment.Center);
                }
            }

            int totalRows = (cards.Length + 1) / 2;
            y += totalRows * (cardH + gap) + 10.0f;
        }

        private void DrawThemePillRow((string Label, SurfaceTheme Theme)[] items, float x, float y, float w, SurfaceTheme active, ThemeSpec theme, Action<SurfaceTheme> onPick)
        {
            float cx = x;
            for (int i = 0; i < items.Length; ++i)
            {
                var item = items[i];
                float pw = Math.Max(54.0f, item.Label.Length * 7.6f + 22.0f);
                var pill = new RectangleF(cx, y, pw, 26.0f);
                bool isAct = item.Theme == active;
                float hov = GetHover($"thm-{item.Theme}", _renderer.IsHovered(pill, _mouseX, _mouseY) && !isAct);
                if (isAct)
                {
                    _renderer.FillRoundedRect(pill, 13.0f, Color.FromArgb(255, 237, 237, 237));
                }
                else if (hov > 0.01f)
                {
                    _renderer.FillRoundedRect(pill, 13.0f, Color.FromArgb((int)(24 * hov), 255, 255, 255));
                }
                _renderer.DrawTextStr(item.Label, pill, 9.6f, isAct, isAct ? Color.FromArgb(255, 16, 16, 18) : theme.TextPrimary, hAlign: StringAlignment.Center);
                if (ControlVisible(pill))
                {
                    SurfaceTheme cap = item.Theme;
                    _renderer.AddHitRegion($"thm-{item.Theme}", pill, () => onPick(cap));
                }
                cx += pw + 4.0f;
            }
        }

        private void DrawFontPillRow((int Index, string Label)[] items, float x, float y, int activeIdx, ThemeSpec theme, Action<int> onPick)
        {
            float cx = x;
            for (int i = 0; i < items.Length; ++i)
            {
                var item = items[i];
                float pw = Math.Max(58.0f, item.Label.Length * 8.0f + 24.0f);
                var pill = new RectangleF(cx, y, pw, 26.0f);
                bool isAct = item.Index == activeIdx;
                float hov = GetHover($"fnt-{item.Index}", _renderer.IsHovered(pill, _mouseX, _mouseY) && !isAct);
                if (isAct)
                {
                    _renderer.FillRoundedRect(pill, 13.0f, Color.FromArgb(255, 237, 237, 237));
                }
                else if (hov > 0.01f)
                {
                    _renderer.FillRoundedRect(pill, 13.0f, Color.FromArgb((int)(24 * hov), 255, 255, 255));
                }
                _renderer.DrawTextStr(item.Label, pill, 9.8f, isAct, isAct ? Color.FromArgb(255, 16, 16, 18) : theme.TextPrimary, hAlign: StringAlignment.Center);
                if (ControlVisible(pill))
                {
                    int cap = item.Index;
                    _renderer.AddHitRegion($"fnt-{item.Index}", pill, () => onPick(cap));
                }
                cx += pw + 4.0f;
            }
        }

        private void DrawWidgetPreviewMockup(string id, RectangleF r, AppSettings s)
        {
            Color white = Color.FromArgb(255, 245, 245, 247);
            Color muted = Color.FromArgb(255, 148, 148, 154);

            switch (id)
            {
                case "calendar":
                {
                    DateTime now = DateTime.Now;
                    int week = System.Globalization.ISOWeek.GetWeekOfYear(now);
                    _renderer.DrawTextStr(now.Day.ToString(), new RectangleF(r.X + 10, r.Y + 6, 44, 30), 19.0f, true, white);
                    _renderer.DrawTextStr(now.ToString("ddd").ToUpperInvariant(), new RectangleF(r.X + 46, r.Y + 8, 44, 14), 8.2f, true, white);
                    _renderer.DrawTextStr(now.ToString("MMM").ToUpperInvariant(), new RectangleF(r.X + 46, r.Y + 20, 44, 14), 8.2f, false, muted);
                    _renderer.DrawTextStr($"Week {week}", new RectangleF(r.Right - 64, r.Y + 10, 54, 14), 8.0f, false, muted, hAlign: StringAlignment.Far);

                    string[] days = { "S", "M", "T", "W", "T", "F", "S" };
                    int todayDow = (int)now.DayOfWeek;
                    DateTime sunday = now.AddDays(-todayDow);
                    float colW = (r.Width - 16) / 7.0f;
                    for (int d = 0; d < 7; ++d)
                    {
                        float dx = r.X + 8 + d * colW;
                        bool isToday = d == todayDow;
                        if (isToday)
                        {
                            _renderer.FillRoundedRect(new RectangleF(dx + 1, r.Y + 42, colW - 2, 32), 6, Color.FromArgb(255, 58, 58, 64));
                        }
                        _renderer.DrawTextStr(days[d], new RectangleF(dx, r.Y + 44, colW, 13), 7.5f, isToday, isToday ? white : muted, hAlign: StringAlignment.Center);
                        _renderer.DrawTextStr(sunday.AddDays(d).Day.ToString(), new RectangleF(dx, r.Y + 58, colW, 14), 8.0f, isToday, isToday ? white : muted, hAlign: StringAlignment.Center);
                    }
                    break;
                }
                case "focus":
                {
                    float ringD = 54.0f;
                    var ring = new RectangleF(r.X + 12, r.Y + (r.Height - ringD) * 0.5f, ringD, ringD);
                    using (var pen = new Pen(Color.FromArgb(255, 52, 52, 58), 3.0f))
                        _renderer.G.DrawEllipse(pen, ring);
                    _renderer.DrawTextStr("25:00", ring, 9.2f, true, white, mono: true, hAlign: StringAlignment.Center);

                    float tx = ring.Right + 12;
                    _renderer.DrawTextStr("Focus timer", new RectangleF(tx, r.Y + 12, r.Right - tx - 8, 18), 10.2f, true, white);
                    _renderer.DrawTextStr("25 + 5 min", new RectangleF(tx, r.Y + 30, r.Right - tx - 8, 15), 8.5f, false, muted);
                    _renderer.FillCircle(tx + 12, r.Y + 60, 10.5f, white);
                    _renderer.DrawLucideIcon("play", tx + 13, r.Y + 60, 8.0f, Color.FromArgb(255, 16, 16, 18), 1.5f);
                    _renderer.FillCircle(tx + 38, r.Y + 60, 9.5f, Color.FromArgb(255, 38, 38, 42));
                    _renderer.DrawTextStr("-", new RectangleF(tx + 28, r.Y + 50, 20, 20), 10.0f, true, white, hAlign: StringAlignment.Center);
                    _renderer.FillCircle(tx + 62, r.Y + 60, 9.5f, Color.FromArgb(255, 38, 38, 42));
                    _renderer.DrawTextStr("+", new RectangleF(tx + 52, r.Y + 50, 20, 20), 10.0f, true, white, hAlign: StringAlignment.Center);
                    break;
                }
                case "weather":
                {
                    _renderer.DrawLucideIcon("sun", r.X + 24, r.Y + 28, 16.0f, Color.FromArgb(255, 255, 204, 64), 1.6f);
                    _renderer.DrawTextStr($"{s.WeatherTempC}\u00B0", new RectangleF(r.X + 44, r.Y + 12, 64, 30), 18.0f, true, white);
                    _renderer.DrawTextStr(string.IsNullOrWhiteSpace(s.WeatherCity) ? "Local" : s.WeatherCity, new RectangleF(r.X + 14, r.Y + 46, r.Width - 28, 16), 9.6f, true, white);
                    _renderer.DrawTextStr($"H:{s.WeatherHighC}\u00B0  L:{s.WeatherLowC}\u00B0", new RectangleF(r.X + 14, r.Y + 62, r.Width - 28, 14), 8.2f, false, muted);
                    break;
                }
                case "stats":
                {
                    _renderer.DrawTextStr("PC Stats", new RectangleF(r.X + 12, r.Y + 8, r.Width - 24, 16), 9.6f, true, white);
                    string[] labels = { "CPU", "GPU", "RAM" };
                    float[] pcts = { 0.28f, 0.16f, 0.54f };
                    for (int k = 0; k < 3; ++k)
                    {
                        float by = r.Y + 30 + k * 16;
                        _renderer.DrawTextStr(labels[k], new RectangleF(r.X + 12, by - 2, 34, 14), 8.0f, false, muted, mono: true);
                        _renderer.FillRoundedRect(new RectangleF(r.X + 46, by + 3, r.Width - 62, 5), 2.5f, Color.FromArgb(255, 42, 42, 48));
                        _renderer.FillRoundedRect(new RectangleF(r.X + 46, by + 3, (r.Width - 62) * pcts[k], 5), 2.5f, white);
                    }
                    break;
                }
                case "todo":
                {
                    _renderer.DrawTextStr("To-Do", new RectangleF(r.X + 12, r.Y + 8, r.Width - 24, 16), 9.6f, true, white);
                    string[] items = { "Review notes", "Ship release" };
                    for (int k = 0; k < 2; ++k)
                    {
                        float ty = r.Y + 32 + k * 20;
                        _renderer.DrawRoundedRect(new RectangleF(r.X + 12, ty + 2, 11, 11), 5.5f, muted, 1.1f);
                        _renderer.DrawTextStr(items[k], new RectangleF(r.X + 28, ty, r.Width - 36, 16), 8.6f, false, muted);
                    }
                    break;
                }
                case "worldclock":
                {
                    _renderer.DrawTextStr(string.IsNullOrWhiteSpace(s.WorldClockCity) ? "Tokyo" : s.WorldClockCity, new RectangleF(r.X + 12, r.Y + 12, r.Width - 24, 16), 9.6f, true, white);
                    DateTime world = DateTime.UtcNow.AddHours(s.WorldClockOffsetHours);
                    _renderer.DrawTextStr(world.ToString(s.Use24HourClock ? "HH:mm" : "h:mm tt"), new RectangleF(r.X + 12, r.Y + 34, r.Width - 24, 28), 16.0f, true, white, mono: true);
                    break;
                }
                case "photos":
                {
                    _renderer.DrawLucideIcon("image", r.X + 26, r.Y + 30, 16.0f, muted, 1.5f);
                    _renderer.DrawTextStr("Photos", new RectangleF(r.X + 46, r.Y + 18, r.Width - 56, 18), 10.0f, true, white);
                    _renderer.DrawTextStr("Local folder", new RectangleF(r.X + 46, r.Y + 38, r.Width - 56, 16), 8.4f, false, muted);
                    break;
                }
                case "batteries":
                {
                    _renderer.DrawLucideIcon("battery", r.X + 26, r.Y + 30, 16.0f, Color.FromArgb(255, 48, 209, 88), 1.5f);
                    _renderer.DrawTextStr("Batteries", new RectangleF(r.X + 46, r.Y + 18, r.Width - 56, 18), 10.0f, true, white);
                    _renderer.DrawTextStr("PC & devices", new RectangleF(r.X + 46, r.Y + 38, r.Width - 56, 16), 8.4f, false, muted);
                    break;
                }
            }
        }

        private void EnsureWheelBitmap()
        {
            if (_wheelBitmap != null) return;
            const int size = 160;
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            float center = (size - 1) * 0.5f;
            float radius = center - 1.0f;
            for (int y = 0; y < size; ++y)
            {
                for (int x = 0; x < size; ++x)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist > radius + 1.0f)
                    {
                        bmp.SetPixel(x, y, Color.Transparent);
                        continue;
                    }
                    float alpha = dist > radius ? Math.Clamp(1.0f - (dist - radius), 0.0f, 1.0f) : 1.0f;
                    float sat = Math.Clamp(dist / radius, 0.0f, 1.0f);
                    float hue = MathF.Atan2(dy, dx) * 180.0f / MathF.PI;
                    if (hue < 0.0f) hue += 360.0f;
                    Color c = HsvToRgb(hue, sat, 1.0f);
                    bmp.SetPixel(x, y, Color.FromArgb((int)(alpha * 255.0f), c.R, c.G, c.B));
                }
            }
            _wheelBitmap = bmp;
        }

        private static Color HsvToRgb(float h, float s, float v)
        {
            h = ((h % 360.0f) + 360.0f) % 360.0f;
            s = Math.Clamp(s, 0.0f, 1.0f);
            v = Math.Clamp(v, 0.0f, 1.0f);
            float c = v * s;
            float x = c * (1.0f - MathF.Abs((h / 60.0f) % 2.0f - 1.0f));
            float m = v - c;
            float r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(255,
                Math.Clamp((int)MathF.Round((r + m) * 255.0f), 0, 255),
                Math.Clamp((int)MathF.Round((g + m) * 255.0f), 0, 255),
                Math.Clamp((int)MathF.Round((b + m) * 255.0f), 0, 255));
        }

        private static void RgbToHsv(Color c, out float h, out float s, out float v)
        {
            float r = c.R / 255.0f, g = c.G / 255.0f, b = c.B / 255.0f;
            float max = MathF.Max(r, MathF.Max(g, b));
            float min = MathF.Min(r, MathF.Min(g, b));
            float delta = max - min;
            v = max;
            s = max <= 0.0001f ? 0.0f : delta / max;
            if (delta <= 0.0001f) { h = 0.0f; return; }
            if (max == r) h = 60.0f * (((g - b) / delta) % 6.0f);
            else if (max == g) h = 60.0f * (((b - r) / delta) + 2.0f);
            else h = 60.0f * (((r - g) / delta) + 4.0f);
            if (h < 0.0f) h += 360.0f;
        }

        private void UpdateWheelFromPoint(float lx, float ly)
        {
            float cx = _wheelRect.X + _wheelRect.Width * 0.5f;
            float cy = _wheelRect.Y + _wheelRect.Height * 0.5f;
            float r = _wheelRect.Width * 0.5f;
            float dx = lx - cx;
            float dy = ly - cy;
            _wheelSat = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) / r, 0.0f, 1.0f);
            float deg = MathF.Atan2(dy, dx) * 180.0f / MathF.PI;
            if (deg < 0.0f) deg += 360.0f;
            _wheelHue = deg;
            ApplyWheelColor();
        }

        private void UpdateValBarFromPoint(float lx)
        {
            _wheelVal = Math.Clamp((lx - _valBarRect.X) / Math.Max(1.0f, _valBarRect.Width), 0.0f, 1.0f);
            ApplyWheelColor();
        }

        private void ApplyWheelColor()
        {
            Color col = HsvToRgb(_wheelHue, _wheelSat, _wheelVal);
            int argb = col.ToArgb();
            _wheelSyncArgb = argb;
            _state.Settings.DayClockAccentColorArgb = argb;
            AuxiliaryWindows.ApplyDayClockSettings(_state);
            NotifyChanged();
        }

        private static string GetLanguageDisplayLabel(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "system", StringComparison.OrdinalIgnoreCase))
                return "Same as Windows";
            foreach (var opt in LanguageOptions)
                if (string.Equals(opt.Code, code, StringComparison.OrdinalIgnoreCase)) return opt.Label;
            return "Same as Windows";
        }

        private void RenderLanguageDropdownPopup(RectangleF anchor)
        {
            const float itemH = 28.0f;
            float popupH = LanguageOptions.Length * itemH + 8.0f;
            float maxBottom = LogicalHeight - 16.0f;
            float popupY = anchor.Bottom + 4.0f;
            if (popupY + popupH > maxBottom) popupY = Math.Max(52.0f, maxBottom - popupH);

            var popup = new RectangleF(anchor.X, popupY, anchor.Width, popupH);
            _renderer.FillRoundedRect(new RectangleF(popup.X + 2, popup.Y + 4, popup.Width, popup.Height), 9, Color.FromArgb(140, 0, 0, 0));
            _renderer.FillRoundedRect(popup, 8, Color.FromArgb(255, 20, 20, 23));
            _renderer.DrawRoundedRect(popup, 8, Color.FromArgb(255, 52, 52, 58), 1.0f);

            string activeCode = string.IsNullOrWhiteSpace(_state.Settings.Language) ? "system" : _state.Settings.Language;
            for (int i = 0; i < LanguageOptions.Length; ++i)
            {
                var opt = LanguageOptions[i];
                var item = new RectangleF(popup.X + 4, popup.Y + 4 + i * itemH, popup.Width - 8, itemH);
                bool sel = string.Equals(activeCode, opt.Code, StringComparison.OrdinalIgnoreCase);
                bool hov = _renderer.IsHovered(item, _mouseX, _mouseY);
                if (hov || sel)
                    _renderer.FillRoundedRect(item, 6, hov ? Color.FromArgb(255, 38, 38, 43) : Color.FromArgb(255, 28, 28, 32));
                _renderer.DrawTextStr(opt.Label, new RectangleF(item.X + 10, item.Y, item.Width - 26, item.Height), 10.0f, sel, Color.FromArgb(255, 242, 242, 245));
                if (sel) _renderer.FillCircle(item.Right - 12, item.Y + item.Height * 0.5f, 2.5f, Color.FromArgb(255, 237, 237, 237));
                string capCode = opt.Code;
                _renderer.AddHitRegion($"set-lang-{i}", item, () =>
                {
                    _state.Settings.Language = capCode;
                    _langDropdownOpen = false;
                    NotifyChanged();
                });
            }
        }

        private void DrawTopBarBadge(RectangleF rect)
        {
            _renderer.FillRoundedRect(rect, 5, Color.FromArgb(255, 24, 24, 27));
            _renderer.DrawRoundedRect(rect, 5, Color.FromArgb(255, 44, 44, 49), 1.0f);
            float cell = 1.25f;
            float startX = rect.X + (rect.Width - 7 * cell) * 0.5f;
            float startY = rect.Y + (rect.Height - 7 * cell) * 0.5f;
            var prev = _renderer.G.SmoothingMode;
            _renderer.G.SmoothingMode = SmoothingMode.None;
            using var wb = new SolidBrush(Color.FromArgb(255, 240, 240, 244));
            using var ab = new SolidBrush(Color.FromArgb(255, 120, 214, 255));
            for (int r = 0; r < 7; ++r)
                for (int c = 0; c < 7; ++c)
                    if (PixelN[r, c] != 0)
                        _renderer.G.FillRectangle(PixelN[r, c] == 2 ? ab : wb, startX + c * cell, startY + r * cell, cell, cell);
            _renderer.G.SmoothingMode = prev;
        }

        private void DrawCloseGlyph(RectangleF rect, Color color)
        {
            float cx = rect.X + rect.Width * 0.5f;
            float cy = rect.Y + rect.Height * 0.5f;
            using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            _renderer.G.DrawLine(pen, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
            _renderer.G.DrawLine(pen, cx + 4.5f, cy - 4.5f, cx - 4.5f, cy + 4.5f);
        }

        private void DrawChevronDown(float cx, float cy, Color color)
        {
            using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            _renderer.G.DrawLines(pen, new[] { new PointF(cx - 3.8f, cy - 1.8f), new PointF(cx, cy + 2.0f), new PointF(cx + 3.8f, cy - 1.8f) });
        }

        private void DrawSidebarIcon(string icon, float cx, float cy, float size, Color color)
        {
            using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float r = size * 0.45f;
            switch (icon)
            {
                case "island":
                    _renderer.DrawRoundedRect(new RectangleF(cx - r, cy - r * 0.72f, r * 2, r * 1.44f), 2.5f, color, 1.3f);
                    _renderer.G.DrawLine(pen, cx - r * 0.45f, cy - r * 0.3f, cx + r * 0.45f, cy - r * 0.3f);
                    return;
                case "taskbar":
                    _renderer.DrawRoundedRect(new RectangleF(cx - r, cy - r * 0.72f, r * 2, r * 1.44f), 2.5f, color, 1.3f);
                    _renderer.G.DrawLine(pen, cx - r + 1.5f, cy + r * 0.25f, cx + r - 1.5f, cy + r * 0.25f);
                    return;
                case "gauge":
                    _renderer.G.DrawArc(pen, cx - r, cy - r * 0.7f, r * 2, r * 2, 180, 180);
                    _renderer.G.DrawLine(pen, cx, cy + r * 0.3f, cx + r * 0.55f, cy - r * 0.25f);
                    return;
                case "radio":
                    _renderer.FillCircle(cx, cy, 1.6f, color);
                    _renderer.G.DrawArc(pen, cx - r * 0.65f, cy - r * 0.65f, r * 1.3f, r * 1.3f, -45, 90);
                    _renderer.G.DrawArc(pen, cx - r * 0.65f, cy - r * 0.65f, r * 1.3f, r * 1.3f, 135, 90);
                    _renderer.G.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -45, 90);
                    _renderer.G.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 135, 90);
                    return;
                case "terminal":
                    _renderer.G.DrawLines(pen, new[] { new PointF(cx - r * 0.75f, cy - r * 0.5f), new PointF(cx - r * 0.1f, cy), new PointF(cx - r * 0.75f, cy + r * 0.5f) });
                    _renderer.G.DrawLine(pen, cx + r * 0.1f, cy + r * 0.5f, cx + r * 0.8f, cy + r * 0.5f);
                    return;
                case "zap":
                    _renderer.G.DrawPolygon(pen, new[] { new PointF(cx + 1.0f, cy - r), new PointF(cx - r * 0.75f, cy + 0.5f), new PointF(cx, cy + 0.5f), new PointF(cx - 1.0f, cy + r), new PointF(cx + r * 0.75f, cy - 0.5f), new PointF(cx, cy - 0.5f) });
                    return;
                case "keyboard":
                    _renderer.DrawRoundedRect(new RectangleF(cx - r, cy - r * 0.65f, r * 2, r * 1.3f), 2.2f, color, 1.3f);
                    _renderer.G.DrawLine(pen, cx - r * 0.45f, cy + r * 0.25f, cx + r * 0.45f, cy + r * 0.25f);
                    return;
                default:
                    _renderer.DrawLucideIcon(icon, cx, cy, size, color, 1.45f);
                    return;
            }
        }

        private static GraphicsPath CreateRoundedPath(RectangleF rect, float radius)
        {
            float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) * 0.5f);
            float d = Math.Max(0.5f, r * 2.0f);
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
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

        private void DrawSwitch(float x, float y, float w, float h, float t, float hover, ThemeSpec theme, bool enabled = true)
        {
            var track = new RectangleF(x, y, w, h);
            if (!enabled)
            {
                Color disTrack = Mix(Color.FromArgb(255, 34, 34, 38), Color.FromArgb(255, 118, 118, 124), t);
                _renderer.FillRoundedRect(track, h * 0.5f, disTrack);
                float dPad = 4.0f;
                float dKnob = h - dPad * 2.0f;
                float dKx = x + dPad + (w - dPad * 2.0f - dKnob) * t;
                _renderer.FillCircle(dKx + dKnob * 0.5f, y + h * 0.5f, dKnob * 0.5f, Color.FromArgb(255, 22, 22, 24));
                return;
            }

            Color offTrack = Mix(Color.FromArgb(255, 20, 20, 22), Color.FromArgb(255, 30, 30, 34), hover);
            Color onTrack = Mix(Color.FromArgb(255, 237, 237, 237), Color.White, hover);
            _renderer.FillRoundedRect(track, h * 0.5f, Mix(offTrack, onTrack, t));
            if (t < 0.98f)
            {
                _renderer.DrawRoundedRect(track, h * 0.5f, Color.FromArgb((int)(255 * (1.0f - t)), 54, 54, 60), 1.0f);
            }

            float pad = 4.0f;
            float knobD = h - pad * 2.0f;
            float knobX = x + pad + (w - pad * 2.0f - knobD) * t;
            Color knobCol = Mix(Color.FromArgb(255, 118, 118, 124), Color.FromArgb(255, 16, 16, 18), t);
            _renderer.FillCircle(knobX + knobD * 0.5f, y + h * 0.5f, knobD * 0.5f, knobCol);
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
            if (e.Button == MouseButtons.Left && _state.ActiveSettingsCat == SettingsCategory.Widgets)
            {
                if (!_wheelRect.IsEmpty)
                {
                    float cx = _wheelRect.X + _wheelRect.Width * 0.5f;
                    float cy = _wheelRect.Y + _wheelRect.Height * 0.5f;
                    float r = _wheelRect.Width * 0.5f + 4.0f;
                    if ((lx - cx) * (lx - cx) + (ly - cy) * (ly - cy) <= r * r)
                    {
                        _draggingWheel = true;
                        Capture = true;
                        UpdateWheelFromPoint(lx, ly);
                        RenderNow();
                        return;
                    }
                }
                if (!_valBarRect.IsEmpty && lx >= _valBarRect.X - 6 && lx <= _valBarRect.Right + 6 && ly >= _valBarRect.Y - 8 && ly <= _valBarRect.Bottom + 8)
                {
                    _draggingValBar = true;
                    Capture = true;
                    UpdateValBarFromPoint(lx);
                    RenderNow();
                    return;
                }
            }
            try { handled = _renderer.DispatchClick(lx, ly); }
            catch { /* never let a control action crash the window */ }
            if (handled)
            {
                _lastAnimMs = Environment.TickCount64;
                _animTimer.Start();
                if (Visible) RenderNow();
                return;
            }
            if (_langDropdownOpen)
            {
                _langDropdownOpen = false;
                RenderNow();
                return;
            }
            if (e.Button == MouseButtons.Left && ly < TopBarHeight && lx < LogicalWidth - 44)
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
            if (_draggingWheel)
            {
                UpdateWheelFromPoint(lx, ly);
                RenderNow();
                return;
            }
            if (_draggingValBar)
            {
                UpdateValBarFromPoint(lx);
                RenderNow();
                return;
            }
            bool draggingControl = (e.Button & MouseButtons.Left) != 0;
            if (draggingControl)
            {
                try { _renderer.DispatchDrag(lx, ly); }
                catch { }
            }
            long now = Environment.TickCount64;
            int minInterval = draggingControl ? 12 : 16;
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
            _draggingWheel = false;
            _draggingValBar = false;
            Capture = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushPendingSave();
                _saveTimer.Dispose();
                _animTimer.Dispose();
                _wheelBitmap?.Dispose();
                _wheelBitmap = null;
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
