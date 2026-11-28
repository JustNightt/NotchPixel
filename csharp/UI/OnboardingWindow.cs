using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;

namespace OpenPixel.UI
{
    // ========================================================================
    // 3-step first-launch onboarding wizard for NotchPixel.
    // Pure black custom-chrome layered window matching the exact 3-page layout:
    //   Page 1: Welcome + Language selector
    //   Page 2: Start with Windows toggle
    //   Page 3: Optional extras (Notifications, Game strip, FPS counter,
    //           Volume in the island, Clipboard history)
    // ========================================================================
    public sealed class OnboardingWindow : Form
    {
        private const float BaseLogicalWidth = 560.0f;
        private const float BaseLogicalHeight = 620.0f;

        private readonly GlobalAppState _state;
        private readonly Action _onSettingsChanged;
        private readonly LiquidGlassRenderer _renderer = new();

        private float _uiScale = 1.0f;
        private float LogicalWidth => Width / _uiScale;
        private float LogicalHeight => Height / _uiScale;

        private int _page = 0; // 0 = Welcome, 1 = Startup, 2 = Optional extras
        private bool _langDropdownOpen = false;

        private float _mouseX = -1000.0f;
        private float _mouseY = -1000.0f;
        private bool _draggingTitlebar = false;
        private int _dragOffX, _dragOffY;

        private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 16 };
        private long _lastAnimMs;
        private float _windowReveal = 1.0f;
        private bool _rendering = false;
        private bool _repaintQueued = false;

        private readonly Dictionary<string, float> _toggleValue = new();
        private readonly Dictionary<string, float> _toggleTarget = new();
        private readonly Dictionary<string, float> _hoverValue = new();
        private readonly Dictionary<string, float> _hoverTarget = new();

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

        // 7x7 pixel-art 'N' matching the NotchPixel emblem.
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
                Version v = typeof(OnboardingWindow).Assembly.GetName().Version;
                return v != null ? v.ToString(4) : "0.5.126.0";
            }
        }

        public OnboardingWindow(GlobalAppState state, Action onSettingsChanged)
        {
            _state = state;
            _onSettingsChanged = onSettingsChanged;
            Text = "Welcome to NotchPixel";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); } catch { }
            TopMost = false;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;

            _animTimer.Tick += (_, _) => StepAnimations();

            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            float dpi = 1.0f;
            try
            {
                using var probe = Graphics.FromHwnd(IntPtr.Zero);
                dpi = Math.Clamp(probe.DpiX / 96.0f, 1.0f, 2.5f);
            }
            catch { }

            float maxScaleW = (work.Width * 0.92f) / BaseLogicalWidth;
            float maxScaleH = (work.Height * 0.92f) / BaseLogicalHeight;
            _uiScale = Math.Min(dpi, Math.Min(maxScaleW, maxScaleH));

            int w = (int)MathF.Round(BaseLogicalWidth * _uiScale);
            int h = (int)MathF.Round(BaseLogicalHeight * _uiScale);
            Bounds = new Rectangle(
                work.Left + (work.Width - w) / 2,
                work.Top + (work.Height - h) / 2,
                w, h);
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

        public void ShowFromStart()
        {
            _page = 0;
            _langDropdownOpen = false;
            _toggleValue.Clear();
            _toggleTarget.Clear();
            _windowReveal = _state.Settings.ReduceMotion ? 1.0f : 0.0f;
            _lastAnimMs = 0;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            RenderNow();
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
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                CompleteAndClose();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                if (_langDropdownOpen)
                {
                    _langDropdownOpen = false;
                    RequestRender();
                }
                else
                {
                    CompleteAndClose();
                }
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Enter && !_langDropdownOpen)
            {
                if (_page < 2)
                {
                    _page++;
                    RequestRender();
                }
                else
                {
                    CompleteAndClose();
                }
                e.Handled = true;
            }
        }

        private void CompleteAndClose()
        {
            lock (_state.SyncRoot)
            {
                _state.Settings.HasCompletedOnboarding = true;
            }
            ConfigManager.ApplyLaunchAtStartup(_state.Settings.LaunchAtStartup);
            ConfigManager.SaveAtomic(_state);
            _onSettingsChanged?.Invoke();
            _langDropdownOpen = false;
            Hide();
        }

        private void PersistSettingChange()
        {
            ConfigManager.SaveAtomic(_state);
            _onSettingsChanged?.Invoke();
            RequestRender();
        }

        private void RequestRender()
        {
            if (!Visible || IsDisposed) return;
            if (_rendering) { _repaintQueued = true; return; }
            RenderNow();
        }

        public void RenderNow()
        {
            if (!Visible || IsDisposed || !IsHandleCreated) return;
            _rendering = true;
            try
            {
                do
                {
                    _repaintQueued = false;
                    RenderFrame();
                }
                while (_repaintQueued && Visible && !IsDisposed);
            }
            finally
            {
                _rendering = false;
            }
        }

        private static float Approach(float current, float target, float dt, float rate)
        {
            float alpha = 1.0f - MathF.Exp(-rate * dt);
            float next = current + (target - current) * alpha;
            return MathF.Abs(next - target) < 0.002f ? target : next;
        }

        private void StepAnimations()
        {
            if (!Visible || IsDisposed) return;
            long now = Environment.TickCount64;
            float dt = _lastAnimMs == 0 ? 0.016f : Math.Clamp((now - _lastAnimMs) / 1000.0f, 0.004f, 0.05f);
            _lastAnimMs = now;

            bool reduce = _state.Settings.ReduceMotion;
            bool dirty = false;

            if (_windowReveal < 1.0f)
            {
                _windowReveal = reduce ? 1.0f : Approach(_windowReveal, 1.0f, dt, 18.0f);
                dirty = true;
            }

            foreach (var kv in new List<KeyValuePair<string, float>>(_toggleTarget))
            {
                float cur = _toggleValue.TryGetValue(kv.Key, out float v) ? v : kv.Value;
                if (MathF.Abs(cur - kv.Value) > 0.002f)
                {
                    _toggleValue[kv.Key] = reduce ? kv.Value : Approach(cur, kv.Value, dt, 20.0f);
                    dirty = true;
                }
            }

            foreach (var kv in new List<KeyValuePair<string, float>>(_hoverTarget))
            {
                float cur = _hoverValue.TryGetValue(kv.Key, out float v) ? v : kv.Value;
                if (MathF.Abs(cur - kv.Value) > 0.002f)
                {
                    _hoverValue[kv.Key] = reduce ? kv.Value : Approach(cur, kv.Value, dt, 22.0f);
                    dirty = true;
                }
            }

            if (dirty) RenderNow();
        }

        private float GetToggleAnim(string key, bool value)
        {
            float target = value ? 1.0f : 0.0f;
            _toggleTarget[key] = target;
            if (!_toggleValue.TryGetValue(key, out float cur)) _toggleValue[key] = cur = target;
            return cur;
        }

        private float GetHoverAnim(string key, bool hovered)
        {
            float target = hovered ? 1.0f : 0.0f;
            _hoverTarget[key] = target;
            if (!_hoverValue.TryGetValue(key, out float cur)) _hoverValue[key] = cur = target;
            return cur;
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

        private static string GetLanguageDisplayLabel(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "system", StringComparison.OrdinalIgnoreCase))
                return "Same as Windows";
            foreach (var opt in LanguageOptions)
            {
                if (string.Equals(opt.Code, code, StringComparison.OrdinalIgnoreCase))
                    return opt.Label;
            }
            return "Same as Windows";
        }

        private void RenderFrame()
        {
            lock (_state.SyncRoot)
            {
                if (!_renderer.BeginLayeredFrame(Width, Height, _uiScale)) return;
                _renderer.ClearHitRegions();

                float lw = LogicalWidth;
                float lh = LogicalHeight;
                const float padX = 28.0f;
                float contentW = lw - padX * 2.0f;

                Color bg = Color.FromArgb(255, 11, 11, 12);
                Color outerBorder = Color.FromArgb(255, 36, 36, 39);
                Color dividerColor = Color.FromArgb(255, 34, 34, 36);
                Color textPrimary = Color.FromArgb(255, 245, 245, 247);
                Color textSecondary = Color.FromArgb(255, 162, 162, 168);
                Color textMuted = Color.FromArgb(255, 122, 122, 128);

                // Window background
                var frame = new RectangleF(0.5f, 0.5f, lw - 1.0f, lh - 1.0f);
                _renderer.FillRoundedRect(frame, 12.0f, bg);
                _renderer.DrawRoundedRect(frame, 12.0f, outerBorder, 1.0f);

                // ----------------------------------------------------------------
                // Top Bar: small logo + bold "NotchPixel" + mono version + close X
                // ----------------------------------------------------------------
                DrawNotchPixelBadge(new RectangleF(20.0f, 15.0f, 22.0f, 22.0f), 5.0f, small: true);
                _renderer.DrawTextStr("NotchPixel", new RectangleF(50.0f, 13.0f, 100.0f, 26.0f), 13.5f, true, textPrimary);
                _renderer.DrawTextStr(AppVersionString, new RectangleF(138.0f, 15.0f, 110.0f, 24.0f), 9.5f, false, textMuted, mono: true);

                var closeRect = new RectangleF(lw - 42.0f, 12.0f, 28.0f, 28.0f);
                float closeHov = GetHoverAnim("close", _renderer.IsHovered(closeRect, _mouseX, _mouseY));
                if (closeHov > 0.01f)
                {
                    _renderer.FillRoundedRect(closeRect, 7.0f, Color.FromArgb((int)(36 * closeHov), 255, 255, 255));
                }
                DrawCloseGlyph(closeRect, Mix(Color.FromArgb(255, 158, 158, 164), Color.White, closeHov));
                _renderer.AddHitRegion("close", closeRect, CompleteAndClose);

                // ----------------------------------------------------------------
                // Page Content
                // ----------------------------------------------------------------
                RectangleF langPillRect = RectangleF.Empty;
                if (_page == 0)
                {
                    langPillRect = RenderWelcomePage(padX, contentW, textPrimary, textSecondary, dividerColor);
                }
                else if (_page == 1)
                {
                    RenderStartupPage(padX, contentW, textPrimary, textSecondary, dividerColor);
                }
                else
                {
                    RenderExtrasPage(padX, contentW, textPrimary, textSecondary, dividerColor);
                }

                // ----------------------------------------------------------------
                // Shared Bottom Footer: 3-step pagination + Back / Next / Done
                // ----------------------------------------------------------------
                float footerBtnH = 36.0f;
                float footerBtnY = lh - 56.0f;
                float indicatorY = footerBtnY + (footerBtnH - 5.0f) * 0.5f;

                float dotX = padX;
                for (int i = 0; i < 3; ++i)
                {
                    bool active = i == _page;
                    float dotW = active ? 22.0f : 5.0f;
                    var dotRect = new RectangleF(dotX, indicatorY, dotW, 5.0f);
                    _renderer.FillRoundedRect(dotRect, 2.5f, active
                        ? Color.FromArgb(255, 237, 237, 237)
                        : Color.FromArgb(255, 68, 68, 72));
                    int pageIdx = i;
                    var dotHit = new RectangleF(dotX - 3.0f, indicatorY - 8.0f, dotW + 6.0f, 21.0f);
                    _renderer.AddHitRegion($"step-{i}", dotHit, () =>
                    {
                        _page = pageIdx;
                        _langDropdownOpen = false;
                        RequestRender();
                    });
                    dotX += dotW + 7.0f;
                }

                // Primary button (Next / Done)
                string primaryLabel = _page < 2 ? "Next" : "Done";
                float primaryW = 84.0f;
                var primaryRect = new RectangleF(lw - padX - primaryW, footerBtnY, primaryW, footerBtnH);
                float primaryHov = GetHoverAnim("btn-primary", _renderer.IsHovered(primaryRect, _mouseX, _mouseY));
                Color primaryFill = Mix(Color.FromArgb(255, 237, 237, 237), Color.White, primaryHov);
                _renderer.FillRoundedRect(primaryRect, footerBtnH * 0.5f, primaryFill);
                _renderer.DrawTextStr(primaryLabel, primaryRect, 11.2f, true, Color.FromArgb(255, 16, 16, 18), hAlign: StringAlignment.Center);
                _renderer.AddHitRegion("btn-primary", primaryRect, () =>
                {
                    _langDropdownOpen = false;
                    if (_page < 2)
                    {
                        _page++;
                        RequestRender();
                    }
                    else
                    {
                        CompleteAndClose();
                    }
                });

                // Secondary Back button on pages 2 and 3
                if (_page > 0)
                {
                    float backW = 80.0f;
                    var backRect = new RectangleF(primaryRect.X - 12.0f - backW, footerBtnY, backW, footerBtnH);
                    float backHov = GetHoverAnim("btn-back", _renderer.IsHovered(backRect, _mouseX, _mouseY));
                    Color backFill = Mix(Color.FromArgb(255, 16, 16, 18), Color.FromArgb(255, 28, 28, 32), backHov);
                    Color backBorder = Mix(Color.FromArgb(255, 44, 44, 48), Color.FromArgb(255, 76, 76, 82), backHov);
                    _renderer.FillRoundedRect(backRect, footerBtnH * 0.5f, backFill);
                    _renderer.DrawRoundedRect(backRect, footerBtnH * 0.5f, backBorder, 1.0f);
                    _renderer.DrawTextStr("Back", backRect, 11.2f, true, textPrimary, hAlign: StringAlignment.Center);
                    _renderer.AddHitRegion("btn-back", backRect, () =>
                    {
                        _langDropdownOpen = false;
                        _page = Math.Max(0, _page - 1);
                        RequestRender();
                    });
                }

                // Render Language dropdown overlay last so it floats above everything
                if (_page == 0 && _langDropdownOpen && !langPillRect.IsEmpty)
                {
                    RenderLanguageDropdownPopup(langPillRect, textPrimary);
                }

                _renderer.EndLayeredFrame(Handle, Location, (byte)Math.Clamp((int)MathF.Round(255.0f * _windowReveal), 0, 255));
            }
        }

        // --------------------------------------------------------------------
        // Page 1: Welcome
        // --------------------------------------------------------------------
        private RectangleF RenderWelcomePage(float padX, float contentW, Color textPrimary, Color textSecondary, Color dividerColor)
        {
            var s = _state.Settings;

            // Large 68x68 rounded dark square badge with glossy pill + pixel N
            DrawNotchPixelBadge(new RectangleF(padX, 66.0f, 68.0f, 68.0f), 16.0f, small: false);

            // Title & subtitle
            _renderer.DrawTextStr("Welcome to NotchPixel", new RectangleF(padX, 158.0f, contentW, 34.0f), 22.0f, true, textPrimary);
            _renderer.DrawTextStr(
                "It lives at the top centre of your screen. Rest your pointer on it and it opens; move away and it closes.",
                new RectangleF(padX, 198.0f, contentW, 44.0f), 11.2f, false, textSecondary, noWrap: false);

            DrawDivider(padX, 254.0f, contentW, dividerColor);

            // Language row
            float rowTop = 270.0f;
            float leftW = contentW - 216.0f;
            _renderer.DrawTextStr("Language", new RectangleF(padX, rowTop, leftW, 22.0f), 11.8f, true, textPrimary);
            _renderer.DrawTextStr("The island, its notices and its windows.", new RectangleF(padX, rowTop + 23.0f, leftW, 20.0f), 10.2f, false, textSecondary);

            float pillW = 200.0f;
            float pillH = 36.0f;
            var pillRect = new RectangleF(padX + contentW - pillW, rowTop + 4.0f, pillW, pillH);
            float pillHov = GetHoverAnim("lang-pill", _renderer.IsHovered(pillRect, _mouseX, _mouseY) || _langDropdownOpen);
            Color pillFill = Mix(Color.FromArgb(255, 16, 16, 18), Color.FromArgb(255, 24, 24, 28), pillHov);
            Color pillBorder = Mix(Color.FromArgb(255, 44, 44, 48), Color.FromArgb(255, 76, 76, 82), pillHov);
            _renderer.FillRoundedRect(pillRect, 8.0f, pillFill);
            _renderer.DrawRoundedRect(pillRect, 8.0f, pillBorder, 1.0f);

            string currentLang = GetLanguageDisplayLabel(s.Language);
            _renderer.DrawTextStr(currentLang, new RectangleF(pillRect.X + 12.0f, pillRect.Y, pillRect.Width - 36.0f, pillRect.Height), 10.6f, false, textPrimary);
            DrawChevronDown(pillRect.Right - 18.0f, pillRect.Y + pillRect.Height * 0.5f, Color.FromArgb(255, 168, 168, 174));

            _renderer.AddHitRegion("lang-pill", pillRect, () =>
            {
                _langDropdownOpen = !_langDropdownOpen;
                RequestRender();
            });

            DrawDivider(padX, 330.0f, contentW, dividerColor);

            // Guidance paragraphs
            _renderer.DrawTextStr(
                "Try it now: move your pointer to the top of the screen and rest it on the island.",
                new RectangleF(padX, 352.0f, contentW, 26.0f), 10.8f, true, textPrimary, noWrap: false);

            _renderer.DrawTextStr(
                "Settings are one click away: the NotchPixel icon in the notification area, next to the clock.",
                new RectangleF(padX, 392.0f, contentW, 42.0f), 10.8f, false, textSecondary, noWrap: false);

            return pillRect;
        }

        private void RenderLanguageDropdownPopup(RectangleF anchor, Color textPrimary)
        {
            const float itemH = 30.0f;
            float popupH = LanguageOptions.Length * itemH + 8.0f;
            float maxBottom = LogicalHeight - 66.0f;
            float popupY = anchor.Bottom + 4.0f;
            if (popupY + popupH > maxBottom) popupY = Math.Max(52.0f, maxBottom - popupH);

            var popupRect = new RectangleF(anchor.X, popupY, anchor.Width, popupH);
            _renderer.FillRoundedRect(new RectangleF(popupRect.X + 2, popupRect.Y + 4, popupRect.Width, popupRect.Height), 9.0f, Color.FromArgb(140, 0, 0, 0));
            _renderer.FillRoundedRect(popupRect, 8.0f, Color.FromArgb(255, 20, 20, 23));
            _renderer.DrawRoundedRect(popupRect, 8.0f, Color.FromArgb(255, 52, 52, 58), 1.0f);

            string activeCode = string.IsNullOrWhiteSpace(_state.Settings.Language) ? "system" : _state.Settings.Language;

            for (int i = 0; i < LanguageOptions.Length; ++i)
            {
                var opt = LanguageOptions[i];
                var itemRect = new RectangleF(popupRect.X + 4.0f, popupRect.Y + 4.0f + i * itemH, popupRect.Width - 8.0f, itemH);
                bool selected = string.Equals(activeCode, opt.Code, StringComparison.OrdinalIgnoreCase);
                bool hovered = _renderer.IsHovered(itemRect, _mouseX, _mouseY);
                if (hovered || selected)
                {
                    _renderer.FillRoundedRect(itemRect, 6.0f, hovered
                        ? Color.FromArgb(255, 38, 38, 43)
                        : Color.FromArgb(255, 28, 28, 32));
                }
                _renderer.DrawTextStr(opt.Label, new RectangleF(itemRect.X + 10.0f, itemRect.Y, itemRect.Width - 28.0f, itemRect.Height), 10.4f, selected, textPrimary);
                if (selected)
                {
                    _renderer.FillCircle(itemRect.Right - 12.0f, itemRect.Y + itemRect.Height * 0.5f, 2.6f, Color.FromArgb(255, 237, 237, 237));
                }
                string capturedCode = opt.Code;
                _renderer.AddHitRegion($"lang-opt-{i}", itemRect, () =>
                {
                    _state.Settings.Language = capturedCode;
                    _langDropdownOpen = false;
                    PersistSettingChange();
                });
            }
        }

        // --------------------------------------------------------------------
        // Page 2: Startup
        // --------------------------------------------------------------------
        private void RenderStartupPage(float padX, float contentW, Color textPrimary, Color textSecondary, Color dividerColor)
        {
            var s = _state.Settings;

            _renderer.DrawTextStr("Start with Windows?", new RectangleF(padX, 66.0f, contentW, 34.0f), 22.0f, true, textPrimary);
            _renderer.DrawTextStr(
                "So the island is there every time you sign in. Without it, NotchPixel closes when you restart and stays closed until you open it again.",
                new RectangleF(padX, 108.0f, contentW, 44.0f), 11.2f, false, textSecondary, noWrap: false);

            DrawDivider(padX, 166.0f, contentW, dividerColor);

            float y = 182.0f;
            DrawToggleRow(
                "Start NotchPixel when you sign in",
                "You can change this any time in Settings, General.",
                padX, ref y, contentW,
                s.LaunchAtStartup, "onboarding-startup",
                textPrimary, textSecondary,
                multiLineSub: false,
                () =>
                {
                    s.LaunchAtStartup = !s.LaunchAtStartup;
                    ConfigManager.ApplyLaunchAtStartup(s.LaunchAtStartup);
                    PersistSettingChange();
                });

            DrawDivider(padX, y + 6.0f, contentW, dividerColor);
        }

        // --------------------------------------------------------------------
        // Page 3: Optional extras
        // --------------------------------------------------------------------
        private void RenderExtrasPage(float padX, float contentW, Color textPrimary, Color textSecondary, Color dividerColor)
        {
            var s = _state.Settings;

            _renderer.DrawTextStr("Optional extras", new RectangleF(padX, 66.0f, contentW, 34.0f), 22.0f, true, textPrimary);
            _renderer.DrawTextStr(
                "Turn on what you want. Skip the rest; all of it is in Settings later.",
                new RectangleF(padX, 106.0f, contentW, 24.0f), 11.2f, false, textSecondary);

            float y = 144.0f;
            DrawDivider(padX, y, contentW, dividerColor);
            y += 14.0f;

            // 1. Notifications in the island (default ON)
            DrawToggleRow(
                "Notifications in the island",
                "New notifications appear in the island and stay in one list. They never leave this PC.",
                padX, ref y, contentW,
                s.NotificationsInIsland, "extra-notif",
                textPrimary, textSecondary,
                multiLineSub: false,
                () =>
                {
                    s.NotificationsInIsland = !s.NotificationsInIsland;
                    PersistSettingChange();
                });
            DrawDivider(padX, y + 4.0f, contentW, dividerColor);
            y += 18.0f;

            // 2. Game strip (default OFF)
            bool gameStripOn = s.GameOverlayMasterEnabled && s.GameOverlayMode != GameOverlayMode.OffHideCompletely;
            DrawToggleRow(
                "Game strip",
                "In fullscreen games, a slim FPS, CPU, GPU and RAM strip instead of hiding. Set games to borderless or windowed fullscreen.",
                padX, ref y, contentW,
                gameStripOn, "extra-gamestrip",
                textPrimary, textSecondary,
                multiLineSub: true,
                () =>
                {
                    bool next = !gameStripOn;
                    s.GameOverlayMasterEnabled = next;
                    s.GameOverlayMode = next ? GameOverlayMode.SlimStrip : GameOverlayMode.OffHideCompletely;
                    PersistSettingChange();
                });
            DrawDivider(padX, y + 4.0f, contentW, dividerColor);
            y += 18.0f;

            // 3. FPS counter (status pill: green dot + Allowed)
            float fpsLeftW = contentW - 104.0f;
            _renderer.DrawTextStr("FPS counter", new RectangleF(padX, y, fpsLeftW, 22.0f), 11.6f, true, textPrimary);
            _renderer.DrawTextStr("Ready. The overlay counts the game's frames while it is on screen.", new RectangleF(padX, y + 22.0f, fpsLeftW, 20.0f), 10.1f, false, textSecondary);

            var badgeRect = new RectangleF(padX + contentW - 88.0f, y + 8.0f, 88.0f, 28.0f);
            _renderer.FillRoundedRect(badgeRect, 14.0f, Color.FromArgb(255, 24, 24, 27));
            _renderer.DrawRoundedRect(badgeRect, 14.0f, Color.FromArgb(255, 44, 44, 49), 1.0f);
            _renderer.FillCircle(badgeRect.X + 14.5f, badgeRect.Y + badgeRect.Height * 0.5f, 3.5f, Color.FromArgb(255, 48, 209, 88));
            _renderer.DrawTextStr("Allowed", new RectangleF(badgeRect.X + 24.0f, badgeRect.Y, badgeRect.Width - 28.0f, badgeRect.Height), 10.2f, true, textPrimary);

            y += 52.0f;
            DrawDivider(padX, y + 4.0f, contentW, dividerColor);
            y += 18.0f;

            // 4. Volume in the island (default ON)
            DrawToggleRow(
                "Volume in the island",
                "Shows the volume level in the island and hides the Windows volume popup.",
                padX, ref y, contentW,
                s.VolumeInIsland, "extra-volume",
                textPrimary, textSecondary,
                multiLineSub: false,
                () =>
                {
                    s.VolumeInIsland = !s.VolumeInIsland;
                    PersistSettingChange();
                });
            DrawDivider(padX, y + 4.0f, contentW, dividerColor);
            y += 18.0f;

            // 5. Clipboard history (default ON)
            DrawToggleRow(
                "Clipboard history",
                "Remembers what you copy so you can paste it again. Kept on this PC only.",
                padX, ref y, contentW,
                s.ClipboardHistoryEnabled, "extra-clipboard",
                textPrimary, textSecondary,
                multiLineSub: false,
                () =>
                {
                    s.ClipboardHistoryEnabled = !s.ClipboardHistoryEnabled;
                    PersistSettingChange();
                });
            DrawDivider(padX, y + 4.0f, contentW, dividerColor);
        }

        // --------------------------------------------------------------------
        // Drawing primitives
        // --------------------------------------------------------------------
        private void DrawToggleRow(
            string title, string sub, float x, ref float y, float w,
            bool value, string key, Color textPrimary, Color textSecondary,
            bool multiLineSub, Action onToggle)
        {
            float rowH = multiLineSub ? 66.0f : 50.0f;
            float leftW = w - 64.0f;
            _renderer.DrawTextStr(title, new RectangleF(x, y, leftW, 22.0f), 11.6f, true, textPrimary);
            _renderer.DrawTextStr(sub, new RectangleF(x, y + 22.0f, leftW, multiLineSub ? 38.0f : 20.0f), 10.1f, false, textSecondary, noWrap: !multiLineSub);

            const float swW = 46.0f;
            const float swH = 26.0f;
            float swX = x + w - swW;
            float swY = y + (multiLineSub ? 14.0f : 8.0f);
            var rowHit = new RectangleF(x, y - 4.0f, w, rowH + 4.0f);
            float hov = GetHoverAnim($"hov-{key}", _renderer.IsHovered(rowHit, _mouseX, _mouseY));
            float anim = GetToggleAnim(key, value);

            DrawOnboardingSwitch(swX, swY, swW, swH, anim, hov);
            _renderer.AddHitRegion(key, rowHit, onToggle);

            y += rowH;
        }

        private void DrawOnboardingSwitch(float x, float y, float w, float h, float t, float hover)
        {
            var track = new RectangleF(x, y, w, h);
            Color offTrack = Mix(Color.FromArgb(255, 22, 22, 25), Color.FromArgb(255, 32, 32, 36), hover);
            Color onTrack = Mix(Color.FromArgb(255, 237, 237, 237), Color.White, hover);
            Color trackFill = Mix(offTrack, onTrack, t);

            _renderer.FillRoundedRect(track, h * 0.5f, trackFill);
            if (t < 0.98f)
            {
                Color border = Color.FromArgb((int)(255 * (1.0f - t)), 54, 54, 60);
                _renderer.DrawRoundedRect(track, h * 0.5f, border, 1.0f);
            }

            float pad = 4.0f;
            float knobD = h - pad * 2.0f;
            float knobX = x + pad + (w - pad * 2.0f - knobD) * t;
            float knobY = y + pad;
            Color knobColor = Mix(Color.FromArgb(255, 114, 114, 120), Color.FromArgb(255, 16, 16, 18), t);
            _renderer.FillCircle(knobX + knobD * 0.5f, knobY + knobD * 0.5f, knobD * 0.5f, knobColor);
        }

        private void DrawDivider(float x, float y, float w, Color color)
        {
            using var pen = new Pen(color, 1.0f);
            _renderer.G.DrawLine(pen, x, y, x + w, y);
        }

        private void DrawCloseGlyph(RectangleF rect, Color color)
        {
            float cx = rect.X + rect.Width * 0.5f;
            float cy = rect.Y + rect.Height * 0.5f;
            float r = 4.5f;
            using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            _renderer.G.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
            _renderer.G.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
        }

        private void DrawChevronDown(float cx, float cy, Color color)
        {
            using var pen = new Pen(color, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            _renderer.G.DrawLines(pen, new[]
            {
                new PointF(cx - 4.0f, cy - 2.0f),
                new PointF(cx,        cy + 2.2f),
                new PointF(cx + 4.0f, cy - 2.0f)
            });
        }

        // Renders the rounded dark square icon containing the glossy pill + pixelated N logo.
        private void DrawNotchPixelBadge(RectangleF rect, float radius, bool small)
        {
            Graphics g = _renderer.G;
            using (var path = CreateRoundedPath(rect, radius))
            using (var bgBrush = new LinearGradientBrush(rect, Color.FromArgb(255, 29, 29, 32), Color.FromArgb(255, 16, 16, 18), LinearGradientMode.Vertical))
            {
                g.FillPath(bgBrush, path);
            }
            _renderer.DrawRoundedRect(rect, radius, Color.FromArgb(255, 44, 44, 49), 1.0f);

            // Inner glossy black pill
            float pillW = rect.Width * (small ? 0.74f : 0.72f);
            float pillH = rect.Height * (small ? 0.52f : 0.46f);
            var pill = new RectangleF(
                rect.X + (rect.Width - pillW) * 0.5f,
                rect.Y + (rect.Height - pillH) * 0.5f,
                pillW, pillH);
            float pillR = pillH * 0.48f;

            // Soft outer glow/shadow around pill
            if (!small)
            {
                _renderer.FillRoundedRect(new RectangleF(pill.X, pill.Y + 1.5f, pill.Width, pill.Height), pillR, Color.FromArgb(140, 0, 0, 0));
            }

            using (var pPath = CreateRoundedPath(pill, pillR))
            using (var pBrush = new LinearGradientBrush(pill, Color.FromArgb(255, 32, 32, 36), Color.FromArgb(255, 6, 6, 8), LinearGradientMode.Vertical))
            {
                g.FillPath(pBrush, pPath);
            }

            // Top specular highlight inside pill
            if (!small)
            {
                var spec = new RectangleF(pill.X + 2.0f, pill.Y + 1.5f, pill.Width - 4.0f, pill.Height * 0.42f);
                using var sPath = CreateRoundedPath(spec, pillR * 0.7f);
                using var sBrush = new LinearGradientBrush(spec, Color.FromArgb(58, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
                g.FillPath(sBrush, sPath);
            }

            // Subtle metallic rim on pill
            using (var rimPath = CreateRoundedPath(pill, pillR))
            using (var rimBrush = new LinearGradientBrush(pill, Color.FromArgb(135, 165, 165, 175), Color.FromArgb(55, 70, 70, 78), LinearGradientMode.Vertical))
            using (var rimPen = new Pen(rimBrush, small ? 0.8f : 1.15f))
            {
                g.DrawPath(rimPen, rimPath);
            }

            // Pixelated 'N' centered inside pill
            int cols = 7;
            int rows = 7;
            float cell = small ? 1.25f : 3.2f;
            float gridW = cols * cell;
            float gridH = rows * cell;
            float startX = pill.X + (pill.Width - gridW) * 0.5f;
            float startY = pill.Y + (pill.Height - gridH) * 0.5f;

            SmoothingMode prevMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using var whiteBrush = new SolidBrush(Color.FromArgb(255, 245, 245, 247));
            using var accentBrush = new SolidBrush(Color.FromArgb(255, 120, 214, 255));
            for (int r = 0; r < rows; ++r)
            {
                for (int c = 0; c < cols; ++c)
                {
                    byte v = PixelN[r, c];
                    if (v == 0) continue;
                    g.FillRectangle(v == 2 ? accentBrush : whiteBrush, startX + c * cell, startY + r * cell, cell, cell);
                }
            }
            g.SmoothingMode = prevMode;
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

        // --------------------------------------------------------------------
        // Mouse interaction
        // --------------------------------------------------------------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _mouseX = e.X / _uiScale;
            _mouseY = e.Y / _uiScale;

            bool handled = false;
            try { handled = _renderer.DispatchClick(_mouseX, _mouseY); }
            catch { }
            if (handled)
            {
                _lastAnimMs = Environment.TickCount64;
                if (!_animTimer.Enabled) _animTimer.Start();
                if (Visible) RequestRender();
                return;
            }

            if (_langDropdownOpen)
            {
                _langDropdownOpen = false;
                RequestRender();
                return;
            }

            if (_mouseY < 50.0f)
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
            if (_draggingTitlebar)
            {
                Point pt = Cursor.Position;
                Location = new Point(pt.X - _dragOffX, pt.Y - _dragOffY);
                return;
            }
            _mouseX = e.X / _uiScale;
            _mouseY = e.Y / _uiScale;
            RequestRender();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _draggingTitlebar = false;
            Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouseX = -1000.0f;
            _mouseY = -1000.0f;
            Cursor = Cursors.Default;
            RequestRender();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animTimer.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
