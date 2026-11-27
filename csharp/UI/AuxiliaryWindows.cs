using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

namespace OpenPixel.UI
{
    // ========================================================================
    // Auxiliary window manager.
    //   1. Desktop widgets (desktop-layer hosted, per-type renderers)
    //   2. Live .mp4/.mkv video wallpaper (Media Foundation decode)
    //   3. Bottom notch taskbar (real icons, tray, status cluster)
    //   4. Detachable drawers (lyrics, stream chat, no-focus web)
    // ========================================================================
    public static class AuxiliaryWindows
    {
        private static readonly List<DesktopWidgetForm> _widgets = new();
        private static LiveVideoWallpaperForm _wallpaperForm;
        private static BottomTaskbarForm _taskbarForm;
        private static DrawerForm _lyricsDrawer;
        private static DrawerForm _chatDrawer;
        private static WebViewWindow _webWindow;

        public static IReadOnlyList<DesktopWidgetForm> ActiveWidgets => _widgets;
        public static LiveVideoWallpaperForm WallpaperForm => _wallpaperForm;

        public static bool TryGetWallpaperColor(out Color color)
        {
            color = Color.Black;
            LiveVideoWallpaperForm form = _wallpaperForm;
            if (form == null || form.IsDisposed) return false;
            return form.TryGetAverageColor(out color);
        }

        public static void ToggleDesktopWidget(GlobalAppState state, WidgetType type)
        {
            DesktopWidgetForm existing = _widgets.Find(w => w.WidgetType == type);
            if (existing != null)
            {
                _widgets.Remove(existing);
                existing.Close();
                existing.Dispose();
                state.Settings.PinnedWidgets.RemoveAll(p => p.Type == (int)type);
                ConfigManager.SaveAtomic(state);
                return;
            }

            var form = new DesktopWidgetForm(state, type, 48 + _widgets.Count * 40, 96 + _widgets.Count * 30);
            _widgets.Add(form);
            form.Show();
            form.AttachAndRender();
            state.Settings.PinnedWidgets.RemoveAll(p => p.Type == (int)type);
            state.Settings.PinnedWidgets.Add(new WidgetPinRecord
            {
                Type = (int)type,
                X = form.Left,
                Y = form.Top,
                ScalePercent = form.ScalePercent
            });
            ConfigManager.SaveAtomic(state);
        }

        // Re-creates the desktop widgets recorded in settings on launch.
        public static void RestorePinnedWidgets(GlobalAppState state)
        {
            foreach (WidgetPinRecord pin in new List<WidgetPinRecord>(state.Settings.PinnedWidgets))
            {
                if (pin == null || !Enum.IsDefined(typeof(WidgetType), pin.Type)) continue;
                WidgetType type = (WidgetType)pin.Type;
                if (_widgets.Exists(w => w.WidgetType == type)) continue;

                var form = new DesktopWidgetForm(state, type, pin.X, pin.Y);
                if (pin.ScalePercent != 100) form.ApplyScale(pin.ScalePercent);
                _widgets.Add(form);
                form.Show();
                form.AttachAndRender();
            }
        }

        public static void NotifyWidgetLayoutChanged(GlobalAppState state, DesktopWidgetForm form)
        {
            WidgetPinRecord record = state.Settings.PinnedWidgets.Find(p => p.Type == (int)form.WidgetType);
            if (record == null)
            {
                record = new WidgetPinRecord { Type = (int)form.WidgetType };
                state.Settings.PinnedWidgets.Add(record);
            }
            record.X = form.Left;
            record.Y = form.Top;
            record.ScalePercent = form.ScalePercent;
        }

        // Display mode / DPI changes re-clamp every desktop surface instead of
        // leaving widgets at stale coordinates.
        public static void HookDisplayReclamp(GlobalAppState state)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            {
                try
                {
                    foreach (DesktopWidgetForm w in _widgets)
                    {
                        if (!w.IsDisposed) w.RenderNow();
                    }
                    UpdateBottomTaskbar(state);
                }
                catch { }
            };
        }

        public static void RefreshAllDesktopWidgets(GlobalAppState state)
        {
            TelemetryHistory.Sample(state.Telemetry);
            StockQuotes.Refresh(state.Settings.StockSymbols);

            for (int i = _widgets.Count - 1; i >= 0; --i)
            {
                DesktopWidgetForm w = _widgets[i];
                if (w.IsDisposed) { _widgets.RemoveAt(i); continue; }
                if (w.Visible) w.RefreshIfStale();
            }
        }

        public static void UpdateLiveVideoWallpaper(GlobalAppState state)
        {
            if (state.Settings.LiveVideoWallpaperEnabled)
            {
                string path = state.Settings.LiveVideoWallpaperPath ?? "";
                if (_wallpaperForm == null || _wallpaperForm.IsDisposed)
                {
                    _wallpaperForm = new LiveVideoWallpaperForm(state);
                    _wallpaperForm.Show();
                }
                _wallpaperForm.UpdateSource(path);
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

        private static bool _watchdogSpawned;

        public static void SpawnTaskbarWatchdogProcess()
        {
            // One watchdog per run: re-creating the dock used to start a new
            // process every time, leaving stale copies behind.
            if (_watchdogSpawned) return;
            _watchdogSpawned = true;
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
                if (_webWindow == null || _webWindow.IsDisposed) _webWindow = new WebViewWindow(state);
                _webWindow.Navigate(state.CurrentWebDrawerUrl, state.CurrentWebDrawerTitle);
                _webWindow.Present();
            }
            else if (_webWindow != null)
            {
                // Stay completely dormant (and free) when closed.
                _webWindow.Close();
                _webWindow.Dispose();
                _webWindow = null;
            }
        }

        public static void CloseAll(GlobalAppState state)
        {
            foreach (DesktopWidgetForm w in _widgets) { try { w.Close(); w.Dispose(); } catch { } }
            _widgets.Clear();
            try { _wallpaperForm?.Close(); _wallpaperForm?.Dispose(); } catch { }
            try { _taskbarForm?.Close(); _taskbarForm?.Dispose(); } catch { }
            try { _lyricsDrawer?.Close(); _lyricsDrawer?.Dispose(); } catch { }
            try { _chatDrawer?.Close(); _chatDrawer?.Dispose(); } catch { }
            try { _webWindow?.Close(); _webWindow?.Dispose(); } catch { }
            _wallpaperForm = null;
            _taskbarForm = null;
            _lyricsDrawer = null;
            _chatDrawer = null;
            _webWindow = null;
            RestoreWindowsNativeTaskbar();
        }
    }

    // ========================================================================
    // Live video wallpaper
    // ========================================================================
    public sealed class LiveVideoWallpaperForm : Form
    {
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);

        private readonly GlobalAppState _state;
        private readonly LiquidGlassRenderer _renderer = new();
        private readonly VideoWallpaperDecoder _decoder = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
        private string _path = "";
        private float _phase;
        private long _lastPaintMs;
        private int _pausePollMs;
        private bool _paused;

        public LiveVideoWallpaperForm(GlobalAppState state)
        {
            _state = state;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Rectangle scr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            Bounds = new Rectangle(scr.Left, scr.Top, scr.Width, scr.Height);
            _timer.Tick += (_, _) => StepAndRenderFrame();
            _timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000020; // LAYERED|TOOLWINDOW|NOACTIVATE|TRANSPARENT
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DesktopLayer.Attach(Handle, asWallpaper: true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x007E || m.Msg == 0x001A)
            {
                DesktopLayer.Reset();
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed) DesktopLayer.Attach(Handle, asWallpaper: true);
                }));
            }
            base.WndProc(ref m);
        }

        public void UpdateSource(string path)
        {
            path ??= "";
            if (path == _path) return;
            _path = path;

            if (path.Length > 0 && File.Exists(path))
            {
                _decoder.Start(path);
            }
            else
            {
                _decoder.Stop();
            }
        }

        public bool TryGetAverageColor(out Color color)
        {
            if (_decoder.HasVideo && _decoder.TryGetAverageColor(out color)) return true;
            color = Color.Black;
            return false;
        }

        private bool ShouldPause()
        {
            if (_state.IsFullscreenGameActive) return true;
            if (_state.Settings.PauseEffectsOnBattery &&
                System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online) return true;
            if (_state.Settings.PauseVideoWallpaperWhenCovered && IsCoveredByMaximizedWindow()) return true;
            if (ExternalWallpaperToolRunning()) return true;
            return false;
        }

        private bool IsCoveredByMaximizedWindow()
        {
            IntPtr fg = Win32Layered.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            Win32Layered.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;
            if (!IsZoomed(fg)) return false;
            return true;
        }

        private static long _externalCheckMs;
        private static bool _externalRunning;

        // Lively / Wallpaper Engine own the desktop layer themselves; running ours
        // on top of them would flicker between the two renderers.
        private static bool ExternalWallpaperToolRunning()
        {
            if (Environment.TickCount64 - _externalCheckMs < 10000) return _externalRunning;
            _externalCheckMs = Environment.TickCount64;
            _externalRunning = false;
            foreach (string name in new[] { "Lively", "livelywpf", "wallpaper32", "wallpaper64" })
            {
                try
                {
                    if (Process.GetProcessesByName(name).Length > 0) { _externalRunning = true; break; }
                }
                catch { }
            }
            return _externalRunning;
        }

        public void StepAndRenderFrame()
        {
            if (IsDisposed || !IsHandleCreated) return;
            long now = Environment.TickCount64;
            int minInterval = _paused ? 400 : 28;
            if (now - _lastPaintMs < minInterval) return;
            _lastPaintMs = now;

            _paused = ShouldPause();
            if (_paused)
            {
                _decoder.Pause();
            }
            else
            {
                _decoder.Resume();
            }

            if (_state.IsFullscreenGameActive)
            {
                if (Visible) Hide();
                return;
            }
            if (!Visible) Show();
            DesktopLayer.Attach(Handle, asWallpaper: true);
            Win32Layered.SetWindowPos(Handle, new IntPtr(1), Left, Top, Width, Height, 0x0010 | 0x0001);

            if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;
            Graphics g = _renderer.G;

            bool drewFrame = _decoder.HasVideo &&
                             _decoder.DrawFrame(g, new Rectangle(0, 0, Width, Height), BuildColorMatrix());

            if (!drewFrame)
            {
                DrawFallbackGradient(g, now);
            }
            else
            {
                DrawHueTint(g);
            }

            _renderer.EndLayeredFrame(Handle, Location, 255);
        }

        private void DrawFallbackGradient(Graphics g, long now)
        {
            _phase += 0.08f;
            float hueShift = _state.Settings.WallpaperHue;
            int r1 = Math.Clamp((int)(16 + 14 * MathF.Sin(_phase + hueShift)), 6, 60);
            int g1 = Math.Clamp((int)(24 + 20 * MathF.Cos(_phase * 0.7f)), 8, 80);
            int b1 = Math.Clamp((int)(42 + 26 * MathF.Sin(_phase * 0.5f)), 16, 110);
            int r2 = Math.Clamp((int)(38 + 20 * MathF.Cos(_phase * 0.9f)), 12, 90);
            int g2 = Math.Clamp((int)(14 + 12 * MathF.Sin(_phase * 1.1f)), 6, 60);
            int b2 = Math.Clamp((int)(52 + 24 * MathF.Cos(_phase * 0.6f)), 20, 110);

            using (var grad = new LinearGradientBrush(
                new PointF(0, 0), new PointF(Width, Height),
                Color.FromArgb(255, r1, g1, b1),
                Color.FromArgb(255, r2, g2, b2)))
            {
                g.FillRectangle(grad, 0, 0, Width, Height);
            }

            for (int i = 0; i < 4; ++i)
            {
                float ox = Width * (0.2f + 0.2f * i) + MathF.Sin(_phase * 1.3f + i) * 45.0f;
                float oy = Height * 0.5f + MathF.Cos(_phase * 0.9f + i * 1.7f) * 35.0f;
                _renderer.FillCircle(ox, oy, 55.0f + i * 12.0f, Color.FromArgb(26, 94, 210, 255));
            }
        }

        // Brightness / contrast / saturation run as a single GDI+ colour matrix
        // while the frame is blitted, so adjusting them costs no extra pass.
        private ColorMatrix BuildColorMatrix()
        {
            AppSettings s = _state.Settings;
            float sat = Math.Clamp(s.WallpaperSaturation, 0.0f, 200.0f) / 100.0f;
            float bright = Math.Clamp(s.WallpaperBrightness, 20.0f, 200.0f) / 100.0f;
            float contrast = Math.Clamp(s.WallpaperContrast, 20.0f, 200.0f) / 100.0f;
            if (Math.Abs(sat - 1.0f) < 0.01f && Math.Abs(bright - 1.0f) < 0.01f && Math.Abs(contrast - 1.0f) < 0.01f)
                return null;

            float scale = bright * contrast;
            float translate = 0.5f * (1.0f - contrast) * bright;
            const float lr = 0.213f, lg = 0.715f, lb = 0.072f;
            float inv = 1.0f - sat;

            return new ColorMatrix(new[]
            {
                new[] { (lr * inv + sat) * scale, lr * inv * scale, lr * inv * scale, 0.0f, 0.0f },
                new[] { lg * inv * scale, (lg * inv + sat) * scale, lg * inv * scale, 0.0f, 0.0f },
                new[] { lb * inv * scale, lb * inv * scale, (lb * inv + sat) * scale, 0.0f, 0.0f },
                new[] { 0.0f, 0.0f, 0.0f, 1.0f, 0.0f },
                new[] { translate, translate, translate, 0.0f, 1.0f }
            });
        }

        // Hue is a translucent wash on top of the corrected frame: it shifts the
        // perceived colour without a per-pixel pass on every decoded frame.
        private void DrawHueTint(Graphics g)
        {
            float hue = ((_state.Settings.WallpaperHue % 360.0f) + 360.0f) % 360.0f;
            if (hue < 1.0f) return;
            Color tint = HsvToRgb(hue, 0.85f, 1.0f);
            using var brush = new SolidBrush(Color.FromArgb(26, tint));
            g.FillRectangle(brush, 0, 0, Width, Height);
        }

        private static Color HsvToRgb(float hueDegrees, float saturation, float value)
        {
            float c = value * saturation;
            float h = hueDegrees / 60.0f;
            float x = c * (1.0f - Math.Abs(h % 2.0f - 1.0f));
            float m = value - c;
            float r, g, b;
            if (h < 1.0f) { r = c; g = x; b = 0; }
            else if (h < 2.0f) { r = x; g = c; b = 0; }
            else if (h < 3.0f) { r = 0; g = c; b = x; }
            else if (h < 4.0f) { r = 0; g = x; b = c; }
            else if (h < 5.0f) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb(255, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                try { if (IsHandleCreated) DesktopLayer.Detach(Handle); } catch { }
                _decoder.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // Bottom notch taskbar
    // ========================================================================
    public sealed class BottomTaskbarForm : Form
    {
        private const int BarHeight = 46;
        private const int BottomMargin = 0;

        private readonly GlobalAppState _state;
        private readonly LiquidGlassRenderer _renderer = new();
        private float _mouseX = -1000;
        private float _mouseY = -1000;

        private readonly Dictionary<string, float> _animValues = new();
        private readonly Dictionary<string, float> _animTargets = new();
        private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 15 };
        private readonly System.Windows.Forms.Timer _visibilityTimer = new() { Interval = 140 };
        private readonly System.Windows.Forms.Timer _dragHoverTimer = new() { Interval = 450 };
        private readonly List<(RectangleF Rect, TaskbarApp App, bool IsShowDesktop)> _dragZones = new();
        private TaskbarApp _dragHoverApp;
        private bool _dragHoverShowDesktop;
        private StartMenuForm _startMenu;
        private long _lastAnimMs;
        private long _lastPaintMs;
        private bool _paintPending;
        private int _clockSecond = -1;
        private bool _forceHidden;

        private static long _netCheckMs;
        private static bool _netWired;
        private static bool _netWireless;
        private static bool _netAvailable;

        public BottomTaskbarForm(GlobalAppState state)
        {
            _state = state;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AllowDrop = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, 542, BarHeight);

            _animTimer.Tick += (_, _) => StepTaskbarAnimations();
            _animTimer.Start();
            _visibilityTimer.Tick += (_, _) => TickVisibility();
            _visibilityTimer.Start();
            _dragHoverTimer.Tick += (_, _) =>
            {
                _dragHoverTimer.Stop();
                if (_dragHoverShowDesktop)
                {
                    TriggerShowDesktop();
                    _dragHoverShowDesktop = false;
                }
                else if (_dragHoverApp != null)
                {
                    TaskbarApps.Activate(_dragHoverApp);
                    _dragHoverApp = null;
                }
            };
        }

        private MonitorInfo TargetMonitor()
        {
            Screen[] screens = Screen.AllScreens;
            int idx = Math.Clamp(_state.Settings.MonitorIndex, 0, Math.Max(0, screens.Length - 1));
            Rectangle b = screens.Length > 0 ? screens[idx].Bounds : new Rectangle(0, 0, 1920, 1080);
            return new MonitorInfo { Bounds = b };
        }

        private struct MonitorInfo { public Rectangle Bounds; }

        private float Animate(string key, float target)
        {
            _animTargets[key] = target;
            if (!_animValues.TryGetValue(key, out float value))
            {
                _animValues[key] = target;
                return target;
            }
            return value;
        }

        private void StepTaskbarAnimations()
        {
            if (IsDisposed) { _animTimer.Stop(); return; }
            long now = Environment.TickCount64;
            float dt = _lastAnimMs > 0 ? Math.Clamp((now - _lastAnimMs) / 1000.0f, 0.001f, 0.08f) : 0.016f;
            _lastAnimMs = now;

            bool moved = false;
            foreach (KeyValuePair<string, float> pair in _animTargets)
            {
                _animValues.TryGetValue(pair.Key, out float current);
                float next = current + (pair.Value - current) * Math.Min(1.0f, dt * 15.0f);
                if (MathF.Abs(next - pair.Value) < 0.004f)
                {
                    next = pair.Value;
                    if (MathF.Abs(current - pair.Value) > 0.001f) moved = true;
                }
                else moved = true;
                _animValues[pair.Key] = next;
            }

            int second = DateTime.Now.Second;
            if (second != _clockSecond) { _clockSecond = second; moved = true; }

            if ((moved || _paintPending) && Environment.TickCount64 - _lastPaintMs >= 28)
                RenderNow();
        }

        private bool _autoHideRevealed = false;
        private long _autoHideLeaveMs = 0;

        private void TickVisibility()
        {
            if (IsDisposed) { _visibilityTimer.Stop(); return; }

            if (_state.IsFullscreenGameActive)
            {
                if (Visible) Hide();
                _forceHidden = true;
                _autoHideRevealed = false;
                return;
            }

            _forceHidden = false;
            bool maximized = IsForegroundMaximized();
            bool autoHide = maximized && _state.Settings.TaskbarAutoHideMaximized;

            if (!autoHide)
            {
                _autoHideRevealed = false;
                _autoHideLeaveMs = 0;
                if (!Visible)
                {
                    Show();
                    RenderNow();
                }
                return;
            }

            // Hysteresis zone so moving around inside the revealed taskbar NEVER flashes:
            // trigger reveal in the bottom 4 px, keep revealed while cursor stays inside
            // BarHeight + 18 px, and wait 320 ms after leaving before hiding again.
            int monBottom = TargetMonitor().Bounds.Bottom;
            int cy = Cursor.Position.Y;
            long nowMs = Environment.TickCount64;
            bool inTrigger = cy >= monBottom - 4;
            bool inKeepZone = cy >= monBottom - (BarHeight + 18);

            if (inTrigger)
            {
                _autoHideRevealed = true;
                _autoHideLeaveMs = 0;
            }
            else if (_autoHideRevealed)
            {
                if (inKeepZone)
                {
                    _autoHideLeaveMs = 0;
                }
                else
                {
                    if (_autoHideLeaveMs == 0) _autoHideLeaveMs = nowMs;
                    else if (nowMs - _autoHideLeaveMs >= 320)
                    {
                        _autoHideRevealed = false;
                        _autoHideLeaveMs = 0;
                    }
                }
            }

            if (!_autoHideRevealed)
            {
                if (Visible) Hide();
                return;
            }

            if (!Visible)
            {
                Show();
                RenderNow();
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private bool IsForegroundMaximized()
        {
            IntPtr fg = Win32Layered.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle || !IsWindowVisible(fg)) return false;
            Win32Layered.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string cname = cls.ToString();
            if (cname == "Progman" || cname == "WorkerW" || cname == "Shell_TrayWnd" ||
                cname == "Shell_SecondaryTrayWnd" || cname == "NotifyIconOverflowWindow" ||
                cname == "tooltips_class32" || cname == "Windows.UI.Core.CoreWindow")
                return false;
            if (!Win32Layered.GetWindowRect(fg, out Win32Layered.RECT r)) return false;
            Rectangle b = TargetMonitor().Bounds;
            // Require true fullscreen coverage (over the taskbar area, not just a normal maximized work-area window)
            return r.left <= b.Left + 2 && r.top <= b.Top + 2 && r.right >= b.Right - 2 && r.bottom >= b.Bottom - 2;
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

        private int MeasureWidth(int apps, int tray)
        {
            int width = 16;               // left margin + flare allowance
            width += 30 + 8;              // start
            width += apps * 38;           // app slots
            if (_state.Media.HasSession) width += 48; // inline mini album art + play/pause
            width += 6 + 1 + 6;           // divider
            width += tray * 22;           // tray icons
            width += 22;                  // overflow chevron
            width += 22 + 22 + 24;        // wifi + volume + battery
            width += 8 + 86;              // clock block
            width += 8 + 1 + 8;           // divider
            width += 26 + 14;             // bell + show desktop strip + right flare
            return width;
        }

        public void RenderNow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            _lastPaintMs = Environment.TickCount64;
            _paintPending = false;

            if (_state.IsFullscreenGameActive || _forceHidden)
            {
                if (Visible) Hide();
                return;
            }

            TaskbarApps.Refresh();
            IReadOnlyList<Bitmap> tray = TrayIcons.Snapshot();
            int trayCount = Math.Min(tray.Count, 5);
            Rectangle monitor = TargetMonitor().Bounds;
            int maxFitApps = Math.Max(4, Math.Min(16, (monitor.Width - 360) / 38));
            List<TaskbarApp> dockApps = TaskbarApps.DockItems(maxFitApps);

            int width = MeasureWidth(dockApps.Count, trayCount);
            int x = monitor.Left + (monitor.Width - width) / 2;
            int y = monitor.Bottom - BarHeight - BottomMargin;
            var target = new Rectangle(x, y, width, BarHeight);
            if (Bounds != target) Bounds = target;

            if (!Visible) Show();
            if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;
            _renderer.ClearHitRegions();
            _dragZones.Clear();

            ThemeSpec theme = ThemeSpec.Get(_state.Settings.Theme);
            using GraphicsPath path = _renderer.CreateBottomTaskbarNotchPath(8, 2, Width - 16, Height - 2, 16 * _state.Settings.CornerRadiusScale);
            Graphics g = _renderer.G;
            Region prev = g.Clip;
            g.SetClip(path, CombineMode.Replace);

            bool drewGlass = false;
            if (!_state.Settings.DrawWithProcessor && theme.IsGlass)
            {
                drewGlass = _renderer.RenderLiquidGlassBackdrop(Bounds, 0, 0, Width, Height,
                    _state.Settings.GlassBlurRadius, _state.Settings.Theme == SurfaceTheme.CandyGlass);
            }
            if (!drewGlass)
            {
                using var fillBrush = new SolidBrush(theme.IsGlass ? Color.FromArgb(242, 10, 11, 14) : theme.Fill);
                g.FillPath(fillBrush, path);
            }

            float cy = Height * 0.5f;
            float cursor = 18.0f;

            // Start (crisp white 2x2 grid matching reference screenshot)
            var startBtn = new RectangleF(cursor, cy - 15, 30, 30);
            float startHover = Animate("tb-start", _renderer.IsHovered(startBtn, _mouseX, _mouseY) ? 1.0f : 0.0f);
            if (startHover > 0.01f)
                _renderer.FillRoundedRect(startBtn, 8, Color.FromArgb((int)(28 * startHover), 255, 255, 255));
            _renderer.DrawLucideIcon("layout-grid", startBtn.X + 15, cy, 16, theme.TextPrimary);
            _renderer.AddHitRegion("tb-start", startBtn, OpenStartMenu);
            cursor += 30 + 8;

            // Pinned + running apps with their real icons, live indicators & unread badges
            for (int i = 0; i < dockApps.Count; ++i)
            {
                TaskbarApp app = dockApps[i];
                var slot = new RectangleF(cursor, cy - 16, 34, 32);
                _dragZones.Add((slot, app, false));
                float hover = Animate($"dock-{i}", _renderer.IsHovered(slot, _mouseX, _mouseY) ? 1.0f : 0.0f);
                float mag = (_state.Settings.TaskbarDockMagnification ? 4.0f : 0.0f) * hover;
                var box = new RectangleF(slot.X - mag, slot.Y - mag * 1.3f, slot.Width + mag * 2, slot.Height + mag * 1.8f);
                if (hover > 0.01f || app.IsActive)
                {
                    int bgAlpha = app.IsActive ? 26 : (int)(24 * hover);
                    _renderer.FillRoundedRect(box, 8 + hover, Color.FromArgb(bgAlpha, 255, 255, 255));
                }

                Bitmap icon = AppIcons.ForPinnedShortcut(app.LaunchPath, app.Window);
                if (icon != null)
                {
                    float side = 23.0f + mag * 1.1f;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(icon, slot.X + (slot.Width - side) * 0.5f, slot.Y + (slot.Height - side) * 0.5f - mag * 0.35f, side, side);
                }
                else
                {
                    _renderer.FillCircle(slot.X + 17, slot.Y + 16 - mag * 0.4f, 7.0f + mag * 0.5f,
                        Color.FromArgb(120, 200, 200, 206));
                }

                if (app.Running)
                {
                    float pillW = app.IsActive ? 14.0f : 6.0f;
                    Color indCol = app.IsActive ? theme.Accent : Color.FromArgb(200, 195, 198, 204);
                    _renderer.FillRoundedRect(new RectangleF(slot.X + (slot.Width - pillW) * 0.5f, slot.Y + 32.5f, pillW, 2.5f), 1.25f, indCol);
                }

                if (app.UnreadCount > 0)
                {
                    string badge = app.UnreadCount > 9 ? "9+" : app.UnreadCount.ToString();
                    var bRect = new RectangleF(slot.Right - 13, slot.Top, 13, 13);
                    _renderer.FillRoundedRect(bRect, 6.5f, Color.FromArgb(255, 255, 69, 58));
                    _renderer.DrawTextStr(badge, bRect, 7.5f, true, Color.White, mono: true, hAlign: StringAlignment.Center);
                }

                TaskbarApp captured = app;
                _renderer.AddHitRegion($"tb-p-{i}", slot, () => TaskbarApps.Activate(captured));
                cursor += 38;
            }

            // Inline mini music widget (album art + play/pause glyph, matching notchpixel.com Taskbar)
            if (_state.Media.HasSession)
            {
                var artRect = new RectangleF(cursor + 2, cy - 11, 22, 22);
                if (_state.Media.AlbumArt != null)
                {
                    using var artClip = _renderer.CreateIslandOrNotchPath(artRect.X, artRect.Y, artRect.Width, artRect.Height, 6, 0, EdgePlacement.TopCenter);
                    Region prevArt = g.Clip;
                    g.SetClip(artClip, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    try { g.DrawImage(_state.Media.AlbumArt, artRect); } catch { }
                    g.Clip = prevArt;
                }
                else
                {
                    _renderer.FillRoundedRect(artRect, 6, _state.Media.DominantColor);
                }
                var ppBtn = new RectangleF(cursor + 26, cy - 11, 20, 22);
                _renderer.DrawLucideIcon(_state.Media.IsPlaying ? "pause" : "play", ppBtn.X + 10, cy, 11.5f, theme.TextPrimary);
                _renderer.AddHitRegion("tb-media-toggle", new RectangleF(cursor, cy - 14, 46, 28), () =>
                {
                    WindowsSystemHooks.MediaTogglePlayPause(_state);
                    RenderNow();
                });
                cursor += 48;
            }

            // Divider
            cursor += 6;
            _renderer.DrawLine(cursor, cy - 10, cursor, cy + 10, Color.FromArgb(34, 255, 255, 255), 1.0f);
            cursor += 1 + 6;

            // Real notification-area icons + overflow chevron
            for (int i = 0; i < trayCount; ++i)
            {
                try
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(tray[i], cursor + 1, cy - 10, 20, 20);
                }
                catch { }
                cursor += 22;
            }

            var chevron = new RectangleF(cursor, cy - 11, 18, 22);
            _renderer.DrawTextStr("\u2039", chevron, 14.0f, true, theme.TextMuted, hAlign: StringAlignment.Center);
            _renderer.AddHitRegion("tb-overflow", chevron, OpenTrayOverflow);
            cursor += 22;

            // Status cluster: Wi-Fi / Ethernet, volume, battery
            RefreshNetworkState();
            var wifi = new RectangleF(cursor, cy - 9, 18, 18);
            DrawNetworkIcon(wifi, theme.TextPrimary);
            _renderer.AddHitRegion("tb-network", wifi, () => OpenUri("ms-availablenetworks:"));
            cursor += 22;

            var volume = new RectangleF(cursor, cy - 9, 18, 18);
            DrawVolumeIcon(volume, theme.TextPrimary, _state.MasterOutputMuted ? 0 : _state.MasterVolume);
            _renderer.AddHitRegion("tb-volume", volume, () => OpenUri("ms-settings:sound"));
            cursor += 22;

            if (_state.Telemetry.HasBattery)
            {
                var battery = new RectangleF(cursor, cy - 7, 20, 14);
                DrawBatteryIcon(battery, theme.TextPrimary, _state.Telemetry.BatteryPercent,
                    System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online);
                _renderer.AddHitRegion("tb-battery", battery, () => OpenUri("ms-settings:batterysaver"));
            }
            cursor += 24;

            // Clock: two stacked lines in smooth Arial matching the top notch.
            cursor += 8;
            DateTime now = DateTime.Now;
            string timeText = _state.Settings.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm tt");
            _renderer.DrawTextStr(timeText, new RectangleF(cursor, cy - 15, 86, 16), 10.5f, true, theme.TextPrimary, mono: false, hAlign: StringAlignment.Far);
            _renderer.DrawTextStr(now.ToString("M/d/yyyy"), new RectangleF(cursor, cy + 1, 86, 14), 8.5f, false, theme.TextMuted, mono: false, hAlign: StringAlignment.Far);
            cursor += 86;

            cursor += 8;
            _renderer.DrawLine(cursor, cy - 10, cursor, cy + 10, Color.FromArgb(34, 255, 255, 255), 1.0f);
            cursor += 1 + 6;

            var bell = new RectangleF(cursor, cy - 13, 26, 26);
            float bellHover = Animate("tb-bell", _renderer.IsHovered(bell, _mouseX, _mouseY) ? 1.0f : 0.0f);
            if (bellHover > 0.01f) _renderer.FillRoundedRect(bell, 8, Color.FromArgb((int)(30 * bellHover), 255, 255, 255));
            _renderer.DrawLucideIcon("bell", bell.X + 13, cy, 14, theme.TextPrimary);
            if (_state.Notifications.Count > 0)
                _renderer.FillCircle(bell.Right - 6, bell.Y + 5, 3.2f, Color.FromArgb(255, 255, 69, 58));
            _renderer.AddHitRegion("tb-bell", bell, OpenActionCenter);
            cursor += 28;

            // Show Desktop edge strip (click or drag-over reveals desktop)
            var showDesk = new RectangleF(cursor, cy - 14, 10, 28);
            _dragZones.Add((showDesk, null, true));
            _renderer.DrawLine(showDesk.X + 4, cy - 8, showDesk.X + 4, cy + 8, Color.FromArgb(46, 255, 255, 255), 1.0f);
            _renderer.AddHitRegion("tb-showdesktop", showDesk, TriggerShowDesktop);

            g.Clip = prev;
            using var pen = new Pen(theme.Edge, 1.0f);
            g.DrawPath(pen, path);
            _renderer.EndLayeredFrame(Handle, Location, 252);
        }

        private void OpenStartMenu()
        {
            try
            {
                _startMenu?.Close();
                _startMenu = new StartMenuForm(_state);
                Rectangle monitor = TargetMonitor().Bounds;
                _startMenu.ShowAt(new Point(monitor.Left + (monitor.Width - 600) / 2, monitor.Bottom - BarHeight - BottomMargin - 344));
            }
            catch { }
        }

        private void OpenTrayOverflow()
        {
            try
            {
                IntPtr overflow = Win32Layered.FindWindowW("NotifyIconOverflowWindow", null);
                if (overflow == IntPtr.Zero) return;
                Rectangle monitor = TargetMonitor().Bounds;
                Win32Layered.SetWindowPos(overflow, new IntPtr(-1), monitor.Right - 340,
                    monitor.Bottom - BarHeight - BottomMargin - 260, 0, 0, 0x0001 | 0x0002); // NOSIZE|NOMOVE
                Win32Layered.ShowWindow(overflow, 4); // SW_SHOWNOACTIVATE
                SetForegroundWindow(overflow);
            }
            catch { }
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static void OpenActionCenter()
        {
            try
            {
                Win32Layered.keybd_event(0x5B, 0, 0, UIntPtr.Zero);      // LWIN down
                Win32Layered.keybd_event(0x41, 0, 0, UIntPtr.Zero);      // A down
                Win32Layered.keybd_event(0x41, 0, 2, UIntPtr.Zero);      // A up
                Win32Layered.keybd_event(0x5B, 0, 2, UIntPtr.Zero);      // LWIN up
            }
            catch { }
        }

        private static void OpenUri(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch { }
        }

        private static void RefreshNetworkState()
        {
            if (Environment.TickCount64 - _netCheckMs < 5000) return;
            _netCheckMs = Environment.TickCount64;
            try
            {
                _netAvailable = NetworkInterface.GetIsNetworkAvailable();
                _netWired = false;
                _netWireless = false;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) _netWireless = true;
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                             ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet) _netWired = true;
                }
            }
            catch { }
        }

        private void DrawNetworkIcon(RectangleF r, Color color)
        {
            if (_netWireless)
            {
                Graphics g = _renderer.G;
                using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                float cx = r.X + r.Width * 0.5f;
                float bottom = r.Bottom - 3;
                for (int i = 2; i >= 0; --i)
                {
                    float radius = 3.4f + i * 4.4f;
                    g.DrawArc(pen, cx - radius, bottom - radius, radius * 2, radius * 2, 225, 90);
                }
                _renderer.FillCircle(cx, bottom, 1.7f, color);
            }
            else
            {
                // Ethernet: a plug with a cable stub.
                _renderer.DrawRoundedRect(new RectangleF(r.X + 2, r.Y + 4, 14, 10), 2.5f, color, 1.4f);
                _renderer.DrawLine(r.X + 5, r.Y + 4, r.X + 5, r.Y + 1, color, 1.4f);
                _renderer.DrawLine(r.X + 13, r.Y + 4, r.X + 13, r.Y + 1, color, 1.4f);
            }
        }

        private void DrawVolumeIcon(RectangleF r, Color color, float volumePercent)
        {
            Graphics g = _renderer.G;
            using var brush = new SolidBrush(color);
            float cy = r.Y + r.Height * 0.5f;
            g.FillPolygon(brush, new[]
            {
                new PointF(r.X + 2, cy - 3),
                new PointF(r.X + 6, cy - 3),
                new PointF(r.X + 10, cy - 7),
                new PointF(r.X + 10, cy + 7),
                new PointF(r.X + 6, cy + 3),
                new PointF(r.X + 2, cy + 3)
            });
            if (volumePercent > 0)
            {
                using var pen = new Pen(color, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, r.X + 6, cy - 5, 8, 10, -55, 110);
                if (volumePercent > 60) g.DrawArc(pen, r.X + 6, cy - 8, 13, 16, -50, 100);
            }
            else
            {
                _renderer.DrawLine(r.X + 12, cy - 4, r.X + 17, cy + 4, Color.FromArgb(255, 255, 105, 97), 1.5f);
                _renderer.DrawLine(r.X + 17, cy - 4, r.X + 12, cy + 4, Color.FromArgb(255, 255, 105, 97), 1.5f);
            }
        }

        private void DrawBatteryIcon(RectangleF r, Color color, int percent, bool charging)
        {
            _renderer.DrawRoundedRect(r, 3.0f, color, 1.2f);
            _renderer.FillRoundedRect(new RectangleF(r.Right, r.Y + 4, 2, r.Height - 8), 1.0f, color);
            float fillWidth = Math.Max(0, (r.Width - 4) * Math.Clamp(percent, 0, 100) / 100.0f);
            if (fillWidth > 1)
                _renderer.FillRoundedRect(new RectangleF(r.X + 2, r.Y + 2, fillWidth, r.Height - 4), 2.0f,
                    charging ? Color.FromArgb(255, 120, 220, 150) : color);
            if (charging)
                _renderer.DrawTextStr("\u26A1", new RectangleF(r.X - 2, r.Y - 8, r.Width + 4, 10), 7.0f, false, color, hAlign: StringAlignment.Center);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouseX = e.X;
            _mouseY = e.Y;
            _paintPending = true;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouseX = -1000;
            _mouseY = -1000;
            _paintPending = true;
        }

        private static void TriggerShowDesktop()
        {
            try
            {
                Win32Layered.keybd_event(0x5B, 0, 0, UIntPtr.Zero); // LWIN down
                Win32Layered.keybd_event(0x44, 0, 0, UIntPtr.Zero); // D down
                Win32Layered.keybd_event(0x44, 0, 2, UIntPtr.Zero); // D up
                Win32Layered.keybd_event(0x5B, 0, 2, UIntPtr.Zero); // LWIN up
            }
            catch { }
        }

        protected override void OnDragOver(DragEventArgs drgevent)
        {
            base.OnDragOver(drgevent);
            drgevent.Effect = DragDropEffects.Move;
            Point client = PointToClient(new Point(drgevent.X, drgevent.Y));
            foreach (var z in _dragZones)
            {
                if (!z.Rect.Contains(client.X, client.Y)) continue;
                if (z.IsShowDesktop)
                {
                    if (!_dragHoverShowDesktop)
                    {
                        _dragHoverShowDesktop = true;
                        _dragHoverApp = null;
                        _dragHoverTimer.Stop();
                        _dragHoverTimer.Start();
                    }
                    return;
                }
                if (z.App != null && !ReferenceEquals(_dragHoverApp, z.App))
                {
                    _dragHoverApp = z.App;
                    _dragHoverShowDesktop = false;
                    _dragHoverTimer.Stop();
                    _dragHoverTimer.Start();
                }
                return;
            }
            _dragHoverTimer.Stop();
            _dragHoverApp = null;
            _dragHoverShowDesktop = false;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _dragHoverTimer.Stop();
            _dragHoverApp = null;
            _dragHoverShowDesktop = false;
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            _dragHoverTimer.Stop();
            _dragHoverApp = null;
            _dragHoverShowDesktop = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _renderer.DispatchClick(e.X, e.Y);
            RenderNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animTimer.Dispose();
                _visibilityTimer.Dispose();
                _dragHoverTimer.Dispose();
                _startMenu?.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // Start panel, anchored above the notch so no window is pushed around.
    // ========================================================================
    public sealed class StartMenuForm : Form
    {
        private const float PanelW = 680.0f;
        private const float PanelH = 520.0f;

        private sealed class StartAppEntry
        {
            public string Name = "";
            public string LaunchPath = "";
            public TaskbarApp RunningApp;
            public bool IsPinnedOrRunning;
            public Color Accent = Color.FromArgb(255, 10, 132, 255);
        }

        private readonly GlobalAppState _state;
        private readonly LiquidGlassRenderer _renderer = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
        private readonly Dictionary<string, float> _hover = new();
        private readonly Dictionary<string, float> _hoverTarget = new();
        private readonly List<StartAppEntry> _allApps = new();
        private readonly List<StartAppEntry> _filtered = new();
        private string _searchQuery = "";
        private float _scrollY = 0.0f;
        private long _openedMs;
        private long _lastMs;
        private float _mouseX = -1000.0f;
        private float _mouseY = -1000.0f;

        public StartMenuForm(GlobalAppState state)
        {
            _state = state;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, (int)PanelW, (int)PanelH);

            LoadAllInstalledApps();
            ApplyFilter();
            _openedMs = Environment.TickCount64;
            _timer.Tick += (_, _) => Step();
            _timer.Start();
            Shown += (_, _) => RenderNow();
        }

        private void LoadAllInstalledApps()
        {
            _allApps.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Pinned & running taskbar apps first
            TaskbarApps.Refresh();
            foreach (var app in TaskbarApps.All(24))
            {
                if (string.IsNullOrWhiteSpace(app.Label)) continue;
                if (seen.Add(app.Label))
                {
                    _allApps.Add(new StartAppEntry
                    {
                        Name = app.Label,
                        LaunchPath = app.LaunchPath ?? "",
                        RunningApp = app,
                        IsPinnedOrRunning = true,
                        Accent = app.Accent
                    });
                }
            }

            // 2. Every installed application shortcut from Windows Start Menu (All Users + Current User)
            var installed = new List<StartAppEntry>();
            string[] roots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
            };
            Color[] palette =
            {
                Color.FromArgb(255, 10, 132, 255), Color.FromArgb(255, 48, 209, 88),
                Color.FromArgb(255, 255, 159, 10), Color.FromArgb(255, 191, 90, 242),
                Color.FromArgb(255, 255, 69, 58),  Color.FromArgb(255, 100, 210, 255)
            };

            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(lnk);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string lower = name.ToLowerInvariant();
                        if (lower.Contains("uninstall") || lower.Contains("readme") || lower.Contains("help") ||
                            lower.Contains("license") || lower.Contains("website") || lower.Contains("manual"))
                            continue;
                        if (!seen.Add(name)) continue;
                        installed.Add(new StartAppEntry
                        {
                            Name = name,
                            LaunchPath = lnk,
                            IsPinnedOrRunning = false,
                            Accent = palette[Math.Abs(name.GetHashCode()) % palette.Length]
                        });
                    }
                }
                catch { }
            }

            installed.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            _allApps.AddRange(installed);
        }

        private void ApplyFilter()
        {
            _filtered.Clear();
            string q = _searchQuery.Trim();
            foreach (var app in _allApps)
            {
                if (string.IsNullOrEmpty(q) || app.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    _filtered.Add(app);
            }
            _scrollY = 0.0f;
        }

        public void ShowAt(Point location)
        {
            Rectangle wa = Screen.FromPoint(location).WorkingArea;
            int x = Math.Clamp(location.X, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - Width - 8));
            int y = Math.Clamp(location.Y, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - Height - 8));
            Location = new Point(x, y);
            Show();
            Activate();
            RenderNow();
        }

        private float OpenProgress
        {
            get
            {
                float raw = Math.Clamp((Environment.TickCount64 - _openedMs) / 200.0f, 0.0f, 1.0f);
                return SpringPresets.CubicEaseOut(raw);
            }
        }

        private void Step()
        {
            if (IsDisposed || !Visible) { _timer.Stop(); return; }
            long now = Environment.TickCount64;
            float dt = _lastMs > 0 ? Math.Clamp((now - _lastMs) / 1000.0f, 0.001f, 0.08f) : 0.016f;
            _lastMs = now;

            bool moved = OpenProgress < 1.0f;
            foreach (KeyValuePair<string, float> pair in _hoverTarget)
            {
                _hover.TryGetValue(pair.Key, out float current);
                float next = current + (pair.Value - current) * Math.Min(1.0f, dt * 15.0f);
                if (MathF.Abs(next - pair.Value) < 0.004f) { next = pair.Value; if (MathF.Abs(current - pair.Value) > 0.001f) moved = true; }
                else moved = true;
                _hover[pair.Key] = next;
            }
            if (moved) RenderNow();
        }

        private float Hover(string key, bool hovered)
        {
            _hoverTarget[key] = hovered ? 1.0f : 0.0f;
            if (!_hover.ContainsKey(key)) _hover[key] = 0.0f;
            return _hover[key];
        }

        public void RenderNow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;
            _renderer.ClearHitRegions();

            ThemeSpec theme = _state.CurrentTheme;
            float t = OpenProgress;
            float offsetY = (1.0f - t) * 14.0f;
            var bgView = new ThemeSpec
            {
                Name = theme.Name,
                Fill = Color.FromArgb(0, 0, 0, 0),
                Edge = Color.FromArgb(0, 0, 0, 0),
                Accent = Color.FromArgb(0, 0, 0, 0),
                TextPrimary = Color.FromArgb(0, 0, 0, 0),
                TextMuted = Color.FromArgb(0, 0, 0, 0),
                TextFaint = Color.FromArgb(0, 0, 0, 0),
                DefaultBar = theme.DefaultBar,
                IsGlass = theme.IsGlass,
                IsLight = theme.IsLight
            };
            ThemeSpec fx = ThemeSpec.Lerp(bgView, theme, t);

            var panel = new RectangleF(4, offsetY + 4, Width - 8, Height - 12);
            bool glass = false;
            if (!_state.Settings.DrawWithProcessor && theme.IsGlass)
                glass = _renderer.RenderLiquidGlassBackdrop(Bounds, 4, 4, Width - 8, Height - 12,
                    _state.Settings.GlassBlurRadius, _state.Settings.Theme == SurfaceTheme.CandyGlass);
            if (!glass) _renderer.FillRoundedRect(panel, 20, theme.IsGlass ? Color.FromArgb(246, 14, 15, 20) : theme.Fill);
            _renderer.DrawRoundedRect(panel, 20, fx.Edge, 1.0f);

            // Header + Live Search bar
            var searchBox = new RectangleF(24, offsetY + 20, Width - 48, 36);
            _renderer.FillRoundedRect(searchBox, 12, Color.FromArgb(24, 255, 255, 255));
            _renderer.DrawRoundedRect(searchBox, 12, Color.FromArgb(40, 255, 255, 255), 1.0f);
            _renderer.DrawLucideIcon("search", searchBox.X + 18, searchBox.Y + 18, 14, fx.TextMuted);
            string searchDisplay = string.IsNullOrEmpty(_searchQuery)
                ? $"Search {_allApps.Count} installed apps..."
                : _searchQuery + "|";
            _renderer.DrawTextStr(searchDisplay, new RectangleF(searchBox.X + 36, searchBox.Y + 7, searchBox.Width - 48, 22),
                11.5f, false, string.IsNullOrEmpty(_searchQuery) ? fx.TextFaint : fx.TextPrimary);

            _renderer.DrawTextStr(string.IsNullOrEmpty(_searchQuery) ? "All Apps" : $"Results ({_filtered.Count})",
                new RectangleF(24, offsetY + 66, 260, 20), 11.5f, true, fx.TextMuted);
            _renderer.DrawTextStr("Scroll to browse  \u00B7  Esc to close",
                new RectangleF(Width - 260, offsetY + 67, 236, 18), 9.0f, false, fx.TextFaint, hAlign: StringAlignment.Far);

            // Scrollable 5-column All-Apps grid
            float gridTop = offsetY + 94;
            float gridBottom = Height - 20;
            float gridH = Math.Max(60.0f, gridBottom - gridTop);
            int cols = 5;
            float tileW = 118.0f;
            float tileH = 76.0f;
            float gap = 10.0f;
            int totalRows = (_filtered.Count + cols - 1) / cols;
            float contentH = totalRows * (tileH + gap);
            float maxScroll = Math.Max(0.0f, contentH - gridH);
            _scrollY = Math.Clamp(_scrollY, 0.0f, maxScroll);

            Region prevClip = _renderer.G.Clip;
            _renderer.G.SetClip(new RectangleF(20, gridTop, Width - 40, gridH), CombineMode.Replace);

            for (int i = 0; i < _filtered.Count; ++i)
            {
                int col = i % cols;
                int row = i / cols;
                float tx = 24 + col * (tileW + gap);
                float ty = gridTop + row * (tileH + gap) - _scrollY;
                if (ty + tileH < gridTop - 8 || ty > gridBottom + 8) continue;

                StartAppEntry entry = _filtered[i];
                var tile = new RectangleF(tx, ty, tileW, tileH);
                float hover = Hover($"tile-{i}", _renderer.IsHovered(tile, _mouseX, _mouseY));
                _renderer.FillRoundedRect(tile, 12, Color.FromArgb((int)(12 + 22 * hover), 255, 255, 255));

                Bitmap icon = AppIcons.ForPinnedShortcut(entry.LaunchPath, entry.RunningApp?.Window ?? IntPtr.Zero);
                if (icon != null)
                {
                    _renderer.G.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    _renderer.G.DrawImage(icon, tx + (tileW - 30) * 0.5f, ty + 10, 30, 30);
                }
                else
                {
                    _renderer.FillCircle(tx + tileW * 0.5f, ty + 25, 14, entry.Accent);
                    string ini = entry.Name.Length > 0 ? entry.Name.Substring(0, 1).ToUpperInvariant() : "?";
                    _renderer.DrawTextStr(ini, new RectangleF(tx + tileW * 0.5f - 14, ty + 13, 28, 24), 11.5f, true, Color.White, hAlign: StringAlignment.Center);
                }

                string label = entry.Name.Length > 15 ? entry.Name.Substring(0, 14) + "\u2026" : entry.Name;
                _renderer.DrawTextStr(label, new RectangleF(tx + 4, ty + 46, tileW - 8, 20), 9.5f, false, fx.TextPrimary, hAlign: StringAlignment.Center);

                StartAppEntry captured = entry;
                _renderer.AddHitRegion($"start-{i}", tile, () =>
                {
                    if (captured.RunningApp != null)
                    {
                        TaskbarApps.Activate(captured.RunningApp);
                    }
                    else if (!string.IsNullOrWhiteSpace(captured.LaunchPath))
                    {
                        try { Process.Start(new ProcessStartInfo(captured.LaunchPath) { UseShellExecute = true }); } catch { }
                    }
                    Close();
                });
            }

            _renderer.G.Clip = prevClip;

            if (_filtered.Count == 0)
            {
                _renderer.DrawTextStr("No matching apps found", new RectangleF(24, gridTop + 20, Width - 48, 24), 11.5f, false, fx.TextMuted, hAlign: StringAlignment.Center);
            }

            // Subtle vertical scroll indicator
            if (maxScroll > 1.0f)
            {
                float barH = Math.Max(28.0f, gridH * (gridH / contentH));
                float barY = gridTop + (_scrollY / maxScroll) * (gridH - barH);
                _renderer.FillRoundedRect(new RectangleF(Width - 14, barY, 4, barH), 2, Color.FromArgb(95, 255, 255, 255));
            }

            _renderer.EndLayeredFrame(Handle, Location, 254);
        }

        protected override bool ShowWithoutActivation => false;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (e.KeyCode == Keys.Back && _searchQuery.Length > 0)
            {
                _searchQuery = _searchQuery.Substring(0, _searchQuery.Length - 1);
                ApplyFilter();
                RenderNow();
            }
            else if (e.KeyCode == Keys.Enter && _filtered.Count > 0)
            {
                var first = _filtered[0];
                if (first.RunningApp != null) TaskbarApps.Activate(first.RunningApp);
                else if (!string.IsNullOrWhiteSpace(first.LaunchPath))
                    try { Process.Start(new ProcessStartInfo(first.LaunchPath) { UseShellExecute = true }); } catch { }
                Close();
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!char.IsControl(e.KeyChar) && _searchQuery.Length < 48)
            {
                _searchQuery += e.KeyChar;
                ApplyFilter();
                RenderNow();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scrollY = Math.Max(0.0f, _scrollY - e.Delta * 0.5f);
            RenderNow();
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
            _mouseX = e.X;
            _mouseY = e.Y;
            bool handled = false;
            try { handled = _renderer.DispatchClick(e.X, e.Y); } catch { }
            if (!handled) Close();
            else RenderNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // Detachable drawers: synced lyrics, movable stream chat
    // ========================================================================
    public sealed class DrawerForm : Form
    {
        private readonly GlobalAppState _state;
        private readonly int _kind;
        private readonly LiquidGlassRenderer _renderer = new();
        private bool _dragging;
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
                ? new Rectangle(scrW - 340, 90, 310, 210)
                : new Rectangle((scrW - 400) / 2, 244, 400, 210);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; // NOACTIVATE: never steals game focus
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
                _renderer.DrawTextStr("Synced Lyrics (LRCLIB)", new RectangleF(16, 12, Width - 32, 22), 12.5f, true, theme.Accent, hAlign: StringAlignment.Center);
                string body = _state.Media.HasSession
                    ? $"Playing: {_state.Media.Title} - {_state.Media.Artist}"
                    : "Play a track to display synchronized time-coded lyrics.";
                _renderer.DrawTextStr(body, new RectangleF(16, 48, Width - 32, Height - 64), 12.0f, false, theme.TextMuted, hAlign: StringAlignment.Center, noWrap: false);
            }
            else
            {
                string title = "LIVE CHAT DRAWER";
                float titleW = title.Length * 7.6f;
                float groupX = (Width - (12.0f + 8.0f + titleW)) * 0.5f;
                _renderer.FillCircle(groupX + 5.0f, 22.0f, 4.5f, Color.FromArgb(255, 255, 69, 58));
                _renderer.DrawTextStr(title, new RectangleF(groupX + 16.0f, 11.0f, titleW + 24.0f, 22.0f), 11.5f, true, Color.FromArgb(255, 255, 69, 58));
                string msg = _state.ChatMessages.Count > 0
                    ? $"{_state.ChatMessages[0].Username}: {_state.ChatMessages[0].Message}"
                    : "Connected \u00B7 Waiting for new Twitch / YouTube messages...";
                _renderer.DrawTextStr(msg, new RectangleF(18, 48, Width - 36, Height - 64), 11.5f, false, theme.TextMuted, hAlign: StringAlignment.Center, noWrap: false);
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
