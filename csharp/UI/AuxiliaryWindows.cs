using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

namespace OpenPixel.UI
{
    // ========================================================================
    // AuxiliaryWindows Manager (C# .NET 10)
    // 1. Desktop Day Clock Widget & Desktop Widgets (Smooth Zero-Jump Dragging,
    //    Right-Click Lock/Recolor/Unpin Menu, Live Wallpaper Glass Sampling)
    // 2. Built-in Live Video Wallpapers (Looping animated/video desktop wallpaper
    //    behind windows with Hue/Sat/Bright/Contrast & fullscreen auto-pause)
    // 3. Custom Bottom Notch Taskbar (with out-of-process Watchdog failsafe)
    // 4. Detachable Drawers (Synced Lyrics, Movable Twitch/YT Chat, No-Focus Web Drawer)
    // ========================================================================
    public static class AuxiliaryWindows
    {
        private static readonly List<DesktopWidgetForm> _widgets = new();
        private static LiveVideoWallpaperForm _wallpaperForm;
        private static BottomTaskbarForm _taskbarForm;
        private static DrawerForm _lyricsDrawer;
        private static DrawerForm _chatDrawer;
        private static DrawerForm _webDrawer;

        public static IReadOnlyList<DesktopWidgetForm> ActiveWidgets => _widgets;

        public static void ToggleDesktopWidget(GlobalAppState state, WidgetType type)
        {
            var existing = _widgets.Find(w => w.WidgetType == type);
            if (existing != null)
            {
                _widgets.Remove(existing);
                existing.Close();
                existing.Dispose();
                return;
            }

            var form = new DesktopWidgetForm(state, type, 48 + _widgets.Count * 240, 72);
            _widgets.Add(form);
            form.Show();
            form.RenderNow();
        }

        public static void RefreshAllDesktopWidgets(GlobalAppState state)
        {
            foreach (var w in _widgets)
            {
                if (!w.IsDisposed && w.Visible) w.RenderNow();
            }
            if (_wallpaperForm != null && !_wallpaperForm.IsDisposed && _wallpaperForm.Visible)
            {
                _wallpaperForm.StepAndRenderFrame();
            }
            if (_taskbarForm != null && !_taskbarForm.IsDisposed && _taskbarForm.Visible)
            {
                _taskbarForm.RenderNow();
            }
        }

        public static void UpdateLiveVideoWallpaper(GlobalAppState state)
        {
            if (state.Settings.LiveVideoWallpaperEnabled)
            {
                if (_wallpaperForm == null || _wallpaperForm.IsDisposed)
                {
                    _wallpaperForm = new LiveVideoWallpaperForm(state);
                }
                _wallpaperForm.Show();
                _wallpaperForm.StepAndRenderFrame();
            }
            else if (_wallpaperForm != null && !_wallpaperForm.IsDisposed)
            {
                _wallpaperForm.Close();
                _wallpaperForm.Dispose();
                _wallpaperForm = null;
            }
        }

        public static void UpdateBottomTaskbar(GlobalAppState state)
        {
            if (state.Settings.BottomTaskbarEnabled)
            {
                if (_taskbarForm == null || _taskbarForm.IsDisposed)
                {
                    _taskbarForm = new BottomTaskbarForm(state);
                    IntPtr hTray = Win32Layered.FindWindowW("Shell_TrayWnd", null);
                    if (hTray != IntPtr.Zero) Win32Layered.ShowWindow(hTray, 0); // SW_HIDE
                    SpawnTaskbarWatchdogProcess();
                }
                _taskbarForm.Show();
                _taskbarForm.RenderNow();
            }
            else
            {
                if (_taskbarForm != null && !_taskbarForm.IsDisposed)
                {
                    _taskbarForm.Close();
                    _taskbarForm.Dispose();
                    _taskbarForm = null;
                }
                RestoreWindowsNativeTaskbar();
            }
        }

        public static void RestoreWindowsNativeTaskbar()
        {
            IntPtr hTray = Win32Layered.FindWindowW("Shell_TrayWnd", null);
            if (hTray != IntPtr.Zero) Win32Layered.ShowWindow(hTray, 5); // SW_SHOW
            IntPtr hSec = Win32Layered.FindWindowW("Shell_SecondaryTrayWnd", null);
            if (hSec != IntPtr.Zero) Win32Layered.ShowWindow(hSec, 5);
        }

        public static void SpawnTaskbarWatchdogProcess()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
            }
            catch { }
        }

        public static void UpdateDrawers(GlobalAppState state)
        {
            if (state.LyricsDrawerVisible)
            {
                _lyricsDrawer ??= new DrawerForm(state, 0);
                _lyricsDrawer.Show();
                _lyricsDrawer.RenderNow();
            }
            else if (_lyricsDrawer != null) _lyricsDrawer.Hide();

            if (state.ChatDrawerVisible)
            {
                _chatDrawer ??= new DrawerForm(state, 1);
                _chatDrawer.Show();
                _chatDrawer.RenderNow();
            }
            else if (_chatDrawer != null) _chatDrawer.Hide();

            if (state.WebDrawerVisible)
            {
                _webDrawer ??= new DrawerForm(state, 2);
                _webDrawer.Show();
                _webDrawer.RenderNow();
            }
            else if (_webDrawer != null)
            {
                // Stay 100% dormant when closed!
                _webDrawer.Close();
                _webDrawer.Dispose();
                _webDrawer = null;
            }
        }

        public static void CloseAll(GlobalAppState state)
        {
            foreach (var w in _widgets) { try { w.Close(); w.Dispose(); } catch { } }
            _widgets.Clear();
            try { _wallpaperForm?.Close(); _wallpaperForm?.Dispose(); } catch { }
            try { _taskbarForm?.Close(); _taskbarForm?.Dispose(); } catch { }
            try { _lyricsDrawer?.Close(); _lyricsDrawer?.Dispose(); } catch { }
            try { _chatDrawer?.Close(); _chatDrawer?.Dispose(); } catch { }
            try { _webDrawer?.Close(); _webDrawer?.Dispose(); } catch { }
            RestoreWindowsNativeTaskbar();
        }
    }

    // ========================================================================
    // 1. DesktopWidgetForm (Includes Desktop Day Clock Widget + 12 Widgets)
    //    - Smooth frame-by-frame pointer-delta dragging (Zero jump/teleport!)
    //    - Anchored at HWND_BOTTOM below normal windows & survives Show Desktop
    //    - Liquid Glass dynamically samples live wallpaper (including Live Video Wallpapers)
    //    - Right-click context menu: Lock widgets, Colour wheel tint, Resize, Unpin
    // ========================================================================
    public sealed class DesktopWidgetForm : Form
    {
        private readonly GlobalAppState _state;
        private readonly LiquidGlassRenderer _renderer = new();
        public WidgetType WidgetType { get; }
        public int StyleMode { get; set; } = 0; // 0 = Liquid Glass, 1 = Dark, 2 = Light
        public Color CustomAccent { get; set; } = Color.FromArgb(255, 239, 237, 232);

        private bool _isDragging = false;
        private int _dragOffsetX = 0;
        private int _dragOffsetY = 0;

        public DesktopWidgetForm(GlobalAppState state, WidgetType type, int x, int y)
        {
            _state = state;
            WidgetType = type;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = type == WidgetType.DayClock
                ? new Rectangle(x, y, 280, 134)
                : new Rectangle(x, y, 210, 110);
            CustomAccent = Color.FromArgb(_state.Settings.DayClockAccentColorArgb);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void RenderNow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            Win32Layered.SetWindowDisplayAffinity(Handle, Win32Layered.WDA_EXCLUDEFROMCAPTURE);
            // Keep widget layered at HWND_BOTTOM underneath normal windows
            Win32Layered.SetWindowPos(Handle, new IntPtr(1), Left, Top, Width, Height, 0x0010 | 0x0001);

            if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;

            var box = new RectangleF(2, 2, Width - 4, Height - 4);
            bool drewGlass = false;
            if (StyleMode == 0 && !_state.Settings.DrawWithProcessor)
            {
                drewGlass = _renderer.RenderLiquidGlassBackdrop(Bounds, 2, 2, Width - 4, Height - 4, _state.Settings.GlassBlurRadius, false);
            }
            if (!drewGlass)
            {
                Color bg = StyleMode == 2 ? Color.FromArgb(240, 247, 247, 245) : Color.FromArgb(224, 16, 18, 24);
                _renderer.FillRoundedRect(box, 18, bg);
            }
            _renderer.DrawRoundedRect(box, 18, Color.FromArgb(56, 255, 255, 255), 1.0f);

            Color ink = StyleMode == 2 ? Color.FromArgb(255, 12, 12, 14) : CustomAccent;
            Color muted = StyleMode == 2 ? Color.FromArgb(255, 82, 82, 88) : Color.FromArgb(255, 195, 192, 186);
            DateTime now = DateTime.Now;

            switch (WidgetType)
            {
                case WidgetType.DayClock:
                {
                    // Newly Shipped Desktop Day Clock Widget (Large Weekday, Date, and Time typography!)
                    string weekday = now.ToString("dddd").ToUpperInvariant();
                    string fullDate = now.ToString("dd MMMM yyyy").ToUpperInvariant();
                    string timeStr = _state.Settings.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm tt");

                    _renderer.DrawTextStr(weekday, new RectangleF(16, 12, Width - 32, 42), 28.0f, true, ink, hAlign: StringAlignment.Center);
                    _renderer.DrawTextStr(fullDate, new RectangleF(16, 56, Width - 32, 24), 12.5f, true, muted, mono: true, hAlign: StringAlignment.Center);
                    _renderer.DrawTextStr(timeStr, new RectangleF(16, 82, Width - 32, 38), 22.0f, true, ink, mono: true, hAlign: StringAlignment.Center);
                    break;
                }
                case WidgetType.Clock:
                case WidgetType.WorldClock:
                {
                    string tStr = _state.Settings.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm tt");
                    _renderer.DrawTextStr(tStr, new RectangleF(16, 14, Width - 32, 40), 26.0f, true, ink, mono: true);
                    DateTime worldTime = DateTime.UtcNow.AddHours(_state.Settings.WorldClockOffsetHours);
                    _renderer.DrawTextStr($"{_state.Settings.WorldClockCity} {worldTime:HH:mm}", new RectangleF(16, 58, Width - 32, 30), 12.5f, false, muted);
                    break;
                }
                case WidgetType.MinimalWeather:
                case WidgetType.PlainWeather:
                {
                    bool night = now.Hour < 6 || now.Hour >= 19;
                    _renderer.DrawTextStr($"{(night ? "\u263D" : "\u2600")} {_state.Settings.WeatherTempC}\u00B0C", new RectangleF(16, 14, Width - 32, 40), 24.0f, true, ink);
                    _renderer.DrawTextStr($"{_state.Settings.WeatherCity} \u00B7 H {_state.Settings.WeatherHighC}\u00B0 L {_state.Settings.WeatherLowC}\u00B0", new RectangleF(16, 58, Width - 32, 30), 12.0f, false, muted);
                    break;
                }
                case WidgetType.Calendar:
                {
                    _renderer.DrawTextStr(now.ToString("MMMM yyyy"), new RectangleF(16, 12, Width - 32, 24), 14.0f, true, ink);
                    _renderer.DrawTextStr($"Today: {now:dddd, MMM d}\n{_state.Settings.UpcomingMeetingTitle}", new RectangleF(16, 40, Width - 32, 58), 11.5f, false, muted, noWrap: false);
                    break;
                }
                default:
                {
                    _renderer.DrawTextStr($"CPU {(int)_state.Telemetry.CpuPercent}% \u00B7 GPU {(int)_state.Telemetry.GpuPercent}%", new RectangleF(16, 18, Width - 32, 32), 15.5f, true, ink, mono: true);
                    _renderer.DrawTextStr($"RAM {(int)_state.Telemetry.RamPercent}% \u00B7 Disk {(int)_state.Telemetry.StoragePercent}%", new RectangleF(16, 54, Width - 32, 32), 12.0f, false, muted, mono: true);
                    break;
                }
            }

            _renderer.EndLayeredFrame(Handle, Location, 246);
        }

        // Smooth Per-Frame Pointer-Delta Dragging (Zero initial jump/teleport!)
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !_state.Settings.LockDesktopWidgets)
            {
                Point cursorScreen = Cursor.Position;
                _dragOffsetX = cursorScreen.X - Left;
                _dragOffsetY = cursorScreen.Y - Top;
                _isDragging = true;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging && !_state.Settings.LockDesktopWidgets)
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0)
                {
                    _isDragging = false;
                    Capture = false;
                    return;
                }
                Point cursorScreen = Cursor.Position;
                int nx = cursorScreen.X - _dragOffsetX;
                int ny = cursorScreen.Y - _dragOffsetY;
                Location = new Point(nx, ny);
                Win32Layered.SetWindowPos(Handle, new IntPtr(1), nx, ny, Width, Height, 0x0010);
                RenderNow();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _isDragging)
            {
                _isDragging = false;
                Capture = false;
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowWidgetContextMenu();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_state.Settings.LockDesktopWidgets)
            {
                int delta = e.Delta > 0 ? 16 : -16;
                Width = Math.Clamp(Width + delta, 160, 420);
                Height = Math.Clamp(Height + (int)(delta * 0.55f), 96, 260);
                RenderNow();
            }
        }

        private void ShowWidgetContextMenu()
        {
            var menu = new ContextMenuStrip();
            var lockItem = new ToolStripMenuItem("Lock widgets in place") { Checked = _state.Settings.LockDesktopWidgets };
            lockItem.Click += (_, _) =>
            {
                _state.Settings.LockDesktopWidgets = !_state.Settings.LockDesktopWidgets;
                ConfigManager.SaveAtomic(_state);
                RenderNow();
            };
            menu.Items.Add(lockItem);
            menu.Items.Add(new ToolStripSeparator());

            var colorItem = new ToolStripMenuItem("Choose Day Clock / Widget Colour...");
            colorItem.Click += (_, _) =>
            {
                using var cd = new ColorDialog { Color = CustomAccent, FullOpen = true };
                if (cd.ShowDialog() == DialogResult.OK)
                {
                    CustomAccent = cd.Color;
                    _state.Settings.DayClockAccentColorArgb = cd.Color.ToArgb();
                    ConfigManager.SaveAtomic(_state);
                    RenderNow();
                }
            };
            menu.Items.Add(colorItem);

            menu.Items.Add("Style: Liquid Glass (Live Wallpaper)", null, (_, _) => { StyleMode = 0; RenderNow(); });
            menu.Items.Add("Style: Dark Surface", null, (_, _) => { StyleMode = 1; RenderNow(); });
            menu.Items.Add("Style: Pure White", null, (_, _) => { StyleMode = 2; RenderNow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Unpin widget from desktop", null, (_, _) => AuxiliaryWindows.ToggleDesktopWidget(_state, WidgetType));
            menu.Show(Cursor.Position);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _renderer.Dispose();
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // 2. LiveVideoWallpaperForm (Newly Shipped 0.5.110 – 0.5.123 Feature)
    //    - Renders a smooth looping animated/video desktop wallpaper behind
    //      desktop windows (anchored at HWND_BOTTOM)
    //    - Supports Hue/Saturation/Brightness/Contrast settings
    //    - Auto-pauses when a fullscreen game is active
    //    - Desktop widgets' Liquid Glass dynamically samples the live frames!
    // ========================================================================
    public sealed class LiveVideoWallpaperForm : Form
    {
        private readonly GlobalAppState _state;
        private readonly LiquidGlassRenderer _renderer = new();
        private float _phase = 0.0f;

        public LiveVideoWallpaperForm(GlobalAppState state)
        {
            _state = state;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Rectangle scr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            // Render a desktop-anchored live video wallpaper strip/canvas at HWND_BOTTOM
            Bounds = new Rectangle(scr.Left, scr.Top, Math.Min(scr.Width, 640), Math.Min(scr.Height, 360));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000020; // LAYERED | TOOLWINDOW | NOACTIVATE | TRANSPARENT
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void StepAndRenderFrame()
        {
            if (IsDisposed || !IsHandleCreated) return;
            // Auto-pause live video wallpaper when a fullscreen game covers the screen (0.5.110)
            if (_state.IsFullscreenGameActive) return;

            _phase += 0.08f;
            Win32Layered.SetWindowPos(Handle, new IntPtr(1), Left, Top, Width, Height, 0x0010 | 0x0001);

            if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;

            float hueShift = _state.Settings.WallpaperHue;
            int r1 = Math.Clamp((int)(28 + 24 * MathF.Sin(_phase + hueShift)), 10, 140);
            int g1 = Math.Clamp((int)(72 + 48 * MathF.Cos(_phase * 0.7f)), 20, 190);
            int b1 = Math.Clamp((int)(168 + 60 * MathF.Sin(_phase * 0.5f)), 80, 245);

            int r2 = Math.Clamp((int)(96 + 50 * MathF.Cos(_phase * 0.9f)), 30, 210);
            int g2 = Math.Clamp((int)(32 + 28 * MathF.Sin(_phase * 1.1f)), 15, 150);
            int b2 = Math.Clamp((int)(142 + 55 * MathF.Cos(_phase * 0.6f)), 60, 230);

            using (var grad = new LinearGradientBrush(
                new PointF(0, 0), new PointF(Width, Height),
                Color.FromArgb(210, r1, g1, b1),
                Color.FromArgb(210, r2, g2, b2)))
            {
                _renderer.G.FillRectangle(grad, 0, 0, Width, Height);
            }

            // Animated aurora orbs so Glass desktop widgets dynamically refract the live changing colors
            for (int i = 0; i < 4; ++i)
            {
                float ox = Width * (0.2f + 0.2f * i) + MathF.Sin(_phase * 1.3f + i) * 45.0f;
                float oy = Height * 0.5f + MathF.Cos(_phase * 0.9f + i * 1.7f) * 35.0f;
                _renderer.FillCircle(ox, oy, 55.0f + i * 12.0f, Color.FromArgb(65, 94, 210, 255));
            }

            _renderer.EndLayeredFrame(Handle, Location, 220);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _renderer.Dispose();
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // 3. BottomTaskbarForm (Optional Custom Bottom Notch Taskbar)
    // ========================================================================
    public sealed class BottomTaskbarForm : Form
    {
        private readonly GlobalAppState _state;
        private readonly LiquidGlassRenderer _renderer = new();
        private float _mouseX = -1000;
        private float _mouseY = -1000;

        public BottomTaskbarForm(GlobalAppState state)
        {
            _state = state;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Rectangle scr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            Bounds = new Rectangle((scr.Width - 680) / 2, scr.Bottom - 48, 680, 48);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void RenderNow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (_state.IsFullscreenGameActive)
            {
                Hide();
                return;
            }
            if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;
            _renderer.ClearHitRegions();

            ThemeSpec theme = ThemeSpec.Get(_state.Settings.Theme);
            using GraphicsPath path = _renderer.CreateBottomTaskbarNotchPath(4, 2, Width - 8, Height - 2, 18 * _state.Settings.CornerRadiusScale);
            Region prev = _renderer.G.Clip;
            _renderer.G.SetClip(path, CombineMode.Replace);

            bool drewGlass = false;
            if (!_state.Settings.DrawWithProcessor && theme.IsGlass)
            {
                drewGlass = _renderer.RenderLiquidGlassBackdrop(Bounds, 0, 0, Width, Height, _state.Settings.GlassBlurRadius, _state.Settings.Theme == SurfaceTheme.CandyGlass);
            }
            if (!drewGlass)
            {
                _renderer.FillRoundedRect(new RectangleF(0, 0, Width, Height), 16, theme.IsGlass ? Color.FromArgb(235, 14, 16, 22) : theme.Fill);
            }

            float cy = Height * 0.5f;
            // Start Button
            var startBtn = new RectangleF(16, cy - 15, 30, 30);
            _renderer.FillRoundedRect(startBtn, 9, Color.FromArgb(28, 255, 255, 255));
            _renderer.DrawLucideIcon("layout-grid", 31, cy, 15, Color.FromArgb(255, 78, 179, 255));
            _renderer.AddHitRegion("tb-start", startBtn, () =>
            {
                Win32Layered.keybd_event(0x5B, 0, 0, UIntPtr.Zero); // VK_LWIN
                Win32Layered.keybd_event(0x5B, 0, 2, UIntPtr.Zero);
            });

            // Pinned & Running Apps
            (string title, string cmd, Color col)[] pins =
            {
                ("Explorer", "explorer.exe", Color.FromArgb(255, 250, 189, 47)),
                ("Terminal", "wt.exe", Color.FromArgb(255, 78, 179, 255)),
                ("Notepad", "notepad.exe", Color.FromArgb(255, 114, 137, 218)),
                ("Task Mgr", "taskmgr.exe", Color.FromArgb(255, 46, 204, 113)),
                ("Browser", "https://openpixel.com", Color.FromArgb(255, 58, 130, 247))
            };

            float ax = 58.0f;
            for (int i = 0; i < pins.Length; ++i)
            {
                var slot = new RectangleF(ax, cy - 16, 34, 32);
                bool hov = _renderer.IsHovered(slot, _mouseX, _mouseY);
                float mag = (_state.Settings.TaskbarDockMagnification && hov) ? 3.0f : 0.0f;
                var box = new RectangleF(slot.X - mag, slot.Y - mag, slot.Width + mag * 2, slot.Height + mag * 2);
                _renderer.FillRoundedRect(box, 9, Color.FromArgb(hov ? 44 : 22, 255, 255, 255));
                _renderer.FillCircle(ax + 17, cy, 9 + mag * 0.4f, pins[i].col);
                _renderer.DrawTextStr(pins[i].title.Substring(0, 1), box, 11.0f, true, Color.White, hAlign: StringAlignment.Center);
                string target = pins[i].cmd;
                _renderer.AddHitRegion($"tb-p-{i}", slot, () => { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } });
                ax += 42.0f;
            }

            // Now Playing / Timer Pill
            var mediaPill = new RectangleF(320, cy - 14, 150, 28);
            _renderer.FillRoundedRect(mediaPill, 14, Color.FromArgb(20, 255, 255, 255));
            string mText = _state.Media.HasSession ? _state.Media.Title : $"Focus {IslandTabsRenderer.FormatTimeSec(_state.FocusTimerRemainingSec)}";
            _renderer.DrawTextStr(mText, mediaPill, 11.0f, false, theme.TextPrimary, hAlign: StringAlignment.Center);

            // 12/24h Clock & Date
            DateTime now = DateTime.Now;
            string tStr = _state.Settings.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm tt");
            _renderer.DrawTextStr(tStr, new RectangleF(Width - 110, 6, 94, 18), 11.0f, true, theme.TextPrimary, mono: true, hAlign: StringAlignment.Far);
            _renderer.DrawTextStr(now.ToString("M/d/yyyy"), new RectangleF(Width - 110, 24, 94, 16), 9.5f, false, theme.TextMuted, mono: true, hAlign: StringAlignment.Far);

            _renderer.G.Clip = prev;
            using var pen = new Pen(theme.Edge, 1.0f);
            _renderer.G.DrawPath(pen, path);
            _renderer.EndLayeredFrame(Handle, Location, 252);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouseX = e.X;
            _mouseY = e.Y;
            RenderNow();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _renderer.DispatchClick(e.X, e.Y);
            RenderNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _renderer.Dispose();
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // 4. DrawerForm (Synced Lyrics, Movable Stream Chat, Lazy No-Focus Web Drawer)
    // ========================================================================
    public sealed class DrawerForm : Form
    {
        private readonly GlobalAppState _state;
        private readonly int _kind; // 0 = Lyrics, 1 = Stream Chat, 2 = Lazy Web Drawer
        private readonly LiquidGlassRenderer _renderer = new();
        private bool _dragging = false;
        private int _offX, _offY;

        public DrawerForm(GlobalAppState state, int kind)
        {
            _state = state;
            _kind = kind;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            int scrW = Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            Bounds = kind == 1
                ? new Rectangle(scrW - 340, 90, 310, 200)
                : new Rectangle((scrW - 400) / 2, 244, 400, 210);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; // WS_EX_NOACTIVATE so game focus is never stolen!
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void RenderNow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;

            ThemeSpec theme = ThemeSpec.Get(_state.Settings.Theme);
            var box = new RectangleF(2, 2, Width - 4, Height - 4);
            _renderer.FillRoundedRect(box, 18, theme.IsGlass ? Color.FromArgb(238, 14, 16, 22) : theme.Fill);
            _renderer.DrawRoundedRect(box, 18, theme.Edge, 1.0f);

            if (_kind == 0)
            {
                _renderer.DrawTextStr("Synced Lyrics (LRCLIB)", new RectangleF(16, 10, Width - 32, 22), 12.5f, true, theme.Accent);
                string body = _state.Media.HasSession ? $"Playing: {_state.Media.Title} - {_state.Media.Artist}" : "Play a track to display synchronized time-coded lyrics.";
                _renderer.DrawTextStr(body, new RectangleF(16, 44, Width - 32, Height - 60), 12.0f, false, theme.TextMuted, hAlign: StringAlignment.Center, noWrap: false);
            }
            else if (_kind == 1)
            {
                _renderer.DrawTextStr("\u25CF LIVE CHAT DRAWER (Drag anywhere)", new RectangleF(16, 10, Width - 32, 22), 12.0f, true, Color.FromArgb(255, 255, 69, 58));
                string msg = _state.ChatMessages.Count > 0 ? $"{_state.ChatMessages[0].Username}: {_state.ChatMessages[0].Message}" : "Connected \u00B7 Waiting for new Twitch / YouTube messages...";
                _renderer.DrawTextStr(msg, new RectangleF(16, 44, Width - 32, Height - 60), 11.5f, false, theme.TextMuted, noWrap: false);
            }
            else
            {
                _renderer.DrawTextStr($"Web Drawer \u00B7 {_state.CurrentWebDrawerTitle} (No-Focus Overlay)", new RectangleF(16, 10, Width - 32, 22), 12.5f, true, theme.TextPrimary);
                _renderer.DrawTextStr(_state.CurrentWebDrawerUrl, new RectangleF(16, 32, Width - 32, 18), 10.5f, false, theme.Accent, mono: true);
                _renderer.DrawTextStr("Opens over apps & fullscreen games with WS_EX_NOACTIVATE\nStays 100% dormant (0 MB) when closed.", new RectangleF(16, 66, Width - 32, Height - 80), 11.5f, false, theme.TextMuted, hAlign: StringAlignment.Center, noWrap: false);
            }

            _renderer.EndLayeredFrame(Handle, Location, 248);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Point pt = Cursor.Position;
                _offX = pt.X - Left;
                _offY = pt.Y - Top;
                _dragging = true;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                Point pt = Cursor.Position;
                Location = new Point(pt.X - _offX, pt.Y - _offY);
                RenderNow();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _renderer.Dispose();
            base.Dispose(disposing);
        }
    }
}
