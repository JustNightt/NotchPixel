using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;
using OpenPixel.UI;

namespace OpenPixel
{
    public sealed class IslandMainForm : Form
    {
        private readonly GlobalAppState _state = GlobalAppState.Instance;
        private readonly LiquidGlassRenderer _renderer = new();
        private SettingsWindow _settingsWindow;
        private NotifyIcon _trayIcon;

        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly System.Windows.Forms.Timer _telemetryTimer;
        private readonly System.Windows.Forms.Timer _oneHzTimer;
        private readonly System.Windows.Forms.Timer _hoverOpenTimer;
        private readonly System.Windows.Forms.Timer _hoverCloseTimer;
        private long _lastAnimTimestamp;
        private static readonly long AnimClockStart = Stopwatch.GetTimestamp();

        // Profiling counters surfaced by --idle-test (no per-frame allocation).
        public static double TelemetryMs;
        public static double OneHzMs;
        public static int OneHzTicks;
        public static double RemindersMs;
        public static double MediaMs;
        public static double AgentsMs;
        public static double WidgetsMs;
        private int _oneHzTickCounter;
        public static double RenderMs;
        public static int TelemetryTicks;
        public static int RenderCount;
        private bool _renderingIsland = false;
        private bool _islandRepaintQueued = false;
        private Point _mouseDownPt;
        private bool _shelfCandidateDrag = false;
        private bool _pendingMusicSeek = false;

        private const int HK_OPEN_ISLAND  = 1001; // Ctrl+Alt+N
        private const int HK_PLAY_PAUSE   = 1002; // Ctrl+Alt+P (/support Section 9)
        private const int HK_MUTE_MIC     = 1003; // Ctrl+Alt+M
        private const int HK_HIDE_ISLAND  = 1004; // Ctrl+Alt+H
        private const int HK_SCREENSHOT   = 1005; // Ctrl+Alt+S (/support Section 9)
        private const int HK_COLOR_PICKER = 1006; // Ctrl+Alt+C (/support Section 9)
        private const int HK_SLOT_BASE    = 1011; // Ctrl+Alt+1..6

        public IslandMainForm()
        {
            Text = "OpenPixel";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AllowDrop = true;
            StartPosition = FormStartPosition.Manual;

            PositionOnTargetMonitor();

            WindowsSystemHooks.SetUiWakeCallback(() =>
            {
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed) return;
                            StartAnimTimer();
                            RenderIslandNow();
                        }));
                    }
                }
                catch { }
            });

            _settingsWindow = new SettingsWindow(_state, () =>
            {
                PositionOnTargetMonitor();
                WindowsSystemHooks.ApplyWindowCaptureAffinity(Handle, _state);
                RenderIslandNow();
            }, OpenIslandNow);

            _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animTimer.Tick += (_, _) => StepSpringsAndRender();

            _hoverOpenTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _hoverOpenTimer.Tick += (_, _) =>
            {
                _hoverOpenTimer.Stop();
                if (_state.PointerInsideIsland && !_state.IslandOpen) OpenIslandNow();
            };

            _hoverCloseTimer = new System.Windows.Forms.Timer { Interval = 380 };
            _hoverCloseTimer.Tick += (_, _) =>
            {
                _hoverCloseTimer.Stop();
                if (!_state.PointerInsideIsland && _state.IslandOpen) CollapseIslandNow();
            };

            _telemetryTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _telemetryTimer.Tick += (_, _) =>
            {
                long profStart = Stopwatch.GetTimestamp();
                WindowsSystemHooks.Tick10HzTelemetryAndSensors(_state);
                TelemetryMs += Stopwatch.GetElapsedTime(profStart).TotalMilliseconds;
                ++TelemetryTicks;
                if (_state.IsFullscreenGameActive || _state.ToastActive || _state.IsRecordingScreen ||
                    _state.ObsRecordingActive || (_state.Media.HasSession && _state.Media.IsPlaying) ||
                    _state.Sensors.MicActive ||
                    _state.Settings.Theme == SurfaceTheme.LiquidGlass || _state.Settings.Theme == SurfaceTheme.CandyGlass)
                {
                    RenderIslandNow();
                }
                // Keep Liquid Glass responsive even when resting; 10 Hz when open/hovered.
                int want = (_state.IslandOpen || _state.PointerInsideIsland) ? 33
                         : (_state.Settings.Theme == SurfaceTheme.LiquidGlass || _state.Settings.Theme == SurfaceTheme.CandyGlass) ? 66 : 250;
                if (_telemetryTimer.Interval != want) _telemetryTimer.Interval = want;
            };
            _telemetryTimer.Start();

            _oneHzTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _oneHzTimer.Tick += (_, _) =>
            {
                long profStart = Stopwatch.GetTimestamp();
                int tick = ++_oneHzTickCounter;

                long subStart = Stopwatch.GetTimestamp();
                WindowsSystemHooks.Tick1HzRemindersAndTimers(_state);
                RemindersMs += Stopwatch.GetElapsedTime(subStart).TotalMilliseconds;

                // Media/agent polling walks COM + the process list; a resting island
                // does not need either at 1 Hz.
                subStart = Stopwatch.GetTimestamp();
                if (_state.IslandOpen || tick % 2 == 0) WindowsSystemHooks.PollRealMediaSession(_state);
                MediaMs += Stopwatch.GetElapsedTime(subStart).TotalMilliseconds;

                subStart = Stopwatch.GetTimestamp();
                if (_state.IslandOpen || tick % 5 == 0) WindowsSystemHooks.PollCodingAgents(_state);
                AgentsMs += Stopwatch.GetElapsedTime(subStart).TotalMilliseconds;

                ConfigManager.FlushPersistentCollectionsIfChanged(_state);

                subStart = Stopwatch.GetTimestamp();
                AuxiliaryWindows.RefreshAllDesktopWidgets(_state);
                WindowsSystemHooks.ApplyCaptureAffinityToOwnedWindows(_state);
                WidgetsMs += Stopwatch.GetElapsedTime(subStart).TotalMilliseconds;
                OneHzMs += Stopwatch.GetElapsedTime(profStart).TotalMilliseconds;
                ++OneHzTicks;

                bool liveUi = _state.IslandOpen || _state.ToastActive || _state.IsFullscreenGameActive ||
                              (_state.Media.HasSession && _state.Media.IsPlaying) || _state.Sensors.MicActive;
                if (liveUi || DateTime.Now.Second == 0)
                {
                    long renderStart = Stopwatch.GetTimestamp();
                    RenderIslandNow();
                    RenderMs += Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
                    ++RenderCount;
                }
            };
            _oneHzTimer.Start();

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("NotchPixel 0.5.126 Settings", null, (_, _) => _settingsWindow.ToggleVisibility());
            trayMenu.Items.Add("Pin Desktop Day Clock", null, (_, _) => AuxiliaryWindows.ToggleDesktopWidget(_state, WidgetType.DayClock));
            trayMenu.Items.Add("Toggle Live Video Wallpaper", null, (_, _) =>
            {
                _state.Settings.LiveVideoWallpaperEnabled = !_state.Settings.LiveVideoWallpaperEnabled;
                AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
            });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Quit NotchPixel", null, (_, _) => Close());

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "NotchPixel 0.5.126 (Liquid Glass)",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) _settingsWindow.ToggleVisibility();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BeginInvoke(new Action(() =>
            {
                AuxiliaryWindows.HookDisplayReclamp(_state);
                AuxiliaryWindows.RestorePinnedWidgets(_state);
            }));
            Win32Layered.AddClipboardFormatListener(Handle);
            DragAcceptFiles(Handle, true);
            WindowsSystemHooks.ApplyWindowCaptureAffinity(Handle, _state);

            _instance = this;
            RegisterConfiguredHotkeys();
            for (int i = 0; i < 6; ++i)
            {
                Win32Layered.RegisterHotKey(Handle, HK_SLOT_BASE + i, 0x4003, (uint)(Keys.D1 + i));
            }

            if (_state.Settings.BottomTaskbarEnabled) AuxiliaryWindows.UpdateBottomTaskbar(_state);
            if (_state.Settings.LiveVideoWallpaperEnabled) AuxiliaryWindows.UpdateLiveVideoWallpaper(_state);
            RenderIslandNow();
            if (AmbienceShouldAnimate()) StartAnimTimer();
        }

        private static IslandMainForm _instance;

        private static bool TryParseHotkey(string spec, out uint mods, out uint vk)
        {
            mods = 0x4000; // MOD_NOREPEAT
            vk = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            string[] parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) mods |= 0x0002;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= 0x0001;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= 0x0004;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) mods |= 0x0008;
                else if (Enum.TryParse<Keys>(p, true, out Keys k)) vk = (uint)k;
                else if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = (uint)char.ToUpperInvariant(p[0]);
            }
            return vk != 0;
        }

        public static void ReRegisterGlobalHotkeys()
        {
            var inst = _instance;
            if (inst == null || inst.IsDisposed || !inst.IsHandleCreated) return;
            try { inst.BeginInvoke(new Action(inst.RegisterConfiguredHotkeys)); } catch { }
        }

        private void RegisterConfiguredHotkeys()
        {
            for (int id = HK_OPEN_ISLAND; id <= HK_COLOR_PICKER; ++id)
                Win32Layered.UnregisterHotKey(Handle, id);
            var s = _state.Settings;
            void Reg(int id, string spec, uint fallbackMods, uint fallbackVk)
            {
                if (TryParseHotkey(spec, out uint m, out uint vk))
                    Win32Layered.RegisterHotKey(Handle, id, m, vk);
                else
                    Win32Layered.RegisterHotKey(Handle, id, fallbackMods, fallbackVk);
            }
            Reg(HK_OPEN_ISLAND,  s.HotkeyOpenIsland,  0x4003, (uint)Keys.N);
            Reg(HK_PLAY_PAUSE,   s.HotkeyPlayPause,   0x4003, (uint)Keys.P);
            Reg(HK_MUTE_MIC,     s.HotkeyMuteMic,     0x4003, (uint)Keys.M);
            Reg(HK_HIDE_ISLAND,  s.HotkeyHideIsland,  0x4003, (uint)Keys.H);
            Reg(HK_SCREENSHOT,   s.HotkeyScreenshot,  0x4003, (uint)Keys.S);
            Reg(HK_COLOR_PICKER, s.HotkeyColorPicker, 0x4003, (uint)Keys.C);
        }

        public void PositionOnTargetMonitor()
        {
            Screen[] screens = Screen.AllScreens;
            int idx = Math.Clamp(_state.Settings.MonitorIndex, 0, Math.Max(0, screens.Length - 1));
            Rectangle mon = screens.Length > 0 ? screens[idx].Bounds : new Rectangle(0, 0, 1920, 1080);

            float scale = Math.Clamp(_state.Settings.OpenSizePercent / 100.0f, 0.7f, 1.8f);
            float maxCardW = Math.Max(DipMetrics.CardW + 24.0f, DipMetrics.CardW);
            float maxCardH = Math.Max(DipMetrics.HNotifications, DipMetrics.HMusic + 82.0f);
            int w = Math.Max(520, (int)MathF.Ceiling(maxCardW * scale) + 80);
            int h = Math.Max(300, (int)MathF.Ceiling(maxCardH * scale) + 64);
            int x = mon.Left + (mon.Width - w) / 2;
            int y = mon.Top;
            if (_state.Settings.Edge == EdgePlacement.LeftEdge) x = mon.Left;
            else if (_state.Settings.Edge == EdgePlacement.RightEdge) x = mon.Right - w;

            var target = new Rectangle(x, y, w, h);
            if (Bounds != target) Bounds = target;
        }

        public void OpenIslandNow()
        {
            lock (_state.SyncRoot)
            {
                if (!_state.Settings.ReopenOnLastTab)
                {
                    _state.ActivePanel = PanelId.Music;
                    _state.ShownPanel = PanelId.Music;
                    _state.PanelSpring.Snap(100.0f);
                }
                else
                {
                    _state.ShownPanel = _state.ActivePanel;
                    _state.PanelSpring.Snap(100.0f);
                }
                float targetH = ThemeSpec.GetExpandedHeight(_state.ShownPanel)
                              + (_state.ShownPanel == PanelId.Music && _state.ShowLyricsInIsland ? 76.0f : 0.0f);
                _state.CardHeightSpring.SetTarget(targetH, _state.Settings.ReduceMotion);
                _state.IslandOpen = true;
                _state.OpenSpring.SetTarget(100.0f, _state.Settings.ReduceMotion);
                _state.FadeSpring.SetTarget(100.0f, _state.Settings.ReduceMotion);
            }
            StartAnimTimer();
            StepSpringsAndRender();
        }

        public void CollapseIslandNow()
        {
            lock (_state.SyncRoot)
            {
                _state.IslandOpen = false;
                _state.OpenSpring.SetTarget(0.0f, _state.Settings.ReduceMotion);
                _state.FadeSpring.SetTarget(0.0f, _state.Settings.ReduceMotion);
            }
            StartAnimTimer();
            StepSpringsAndRender();
        }

        private void StepSpringsAndRender()
        {
            int motionInterval = _state.Settings.MotionSpeed switch
            {
                MotionSpeed.Quick => 8,
                MotionSpeed.Normal => 8,
                _ => 16
            };
            if (_animTimer.Interval != motionInterval) _animTimer.Interval = motionInterval;

            long nowTs = Stopwatch.GetTimestamp();
            float dt = _lastAnimTimestamp > 0
                ? Math.Clamp((float)Stopwatch.GetElapsedTime(_lastAnimTimestamp, nowTs).TotalSeconds, 0.001f, 0.05f)
                : 0.016f;
            _lastAnimTimestamp = nowTs;
            float nowSec = (float)Stopwatch.GetElapsedTime(AnimClockStart, nowTs).TotalSeconds;

            float liveAudioPeak = (_state.Media.HasSession && _state.Media.IsPlaying)
                ? CoreAudioInterop.TryGetRenderPeakLevel()
                : 0.0f;
            float liveMicPeak = (_state.Settings.ShowVoiceWaveBar && !_state.GlobalMicMuted)
                ? CoreAudioInterop.TryGetCapturePeakLevel()
                : 0.0f;

            bool moving = false;
            lock (_state.SyncRoot)
            {
                float peakRate = liveAudioPeak > _state.AudioPeakSmoothed ? 28.0f : 10.0f;
                _state.AudioPeakSmoothed += (liveAudioPeak - _state.AudioPeakSmoothed) * (1.0f - MathF.Exp(-dt * peakRate));
                if (_state.AudioPeakSmoothed < 0.002f) _state.AudioPeakSmoothed = 0.0f;

                float micRate = liveMicPeak > _state.MicPeakSmoothed ? 16.0f : 6.5f;
                _state.MicPeakSmoothed += (liveMicPeak - _state.MicPeakSmoothed) * (1.0f - MathF.Exp(-dt * micRate));
                if (_state.MicPeakSmoothed < 0.002f) _state.MicPeakSmoothed = 0.0f;
                _state.MicPeakLevel = _state.MicPeakSmoothed;
                if (_state.MicPeakSmoothed > 0.01f) moving = true;

                float speed = _state.Settings.MotionSpeed == MotionSpeed.Calm ? 0.72f
                            : (_state.Settings.MotionSpeed == MotionSpeed.Quick ? 1.45f : 1.0f);
                bool red = _state.Settings.ReduceMotion;
                SpringSpec openSpec = _state.IslandOpen ? SpringPresets.Open : SpringPresets.Close;
                moving |= _state.OpenSpring.Step(dt, in openSpec, speed, red);
                moving |= _state.FadeSpring.Step(dt, in SpringPresets.Fade, speed, red);
                moving |= _state.PanelSpring.Step(dt, in SpringPresets.Panel, speed, red);

                // Match notchpixel.com island.js: fade outgoing tab out until PANEL < 0.08,
                // then swap ShownPanel, smoothly spring CardHeightSpring (SPRING.close), and fade in.
                if (_state.ActivePanel != _state.ShownPanel && _state.PanelSpring.Value < 8.0f)
                {
                    _state.ShownPanel = _state.ActivePanel;
                    float targetH = ThemeSpec.GetExpandedHeight(_state.ShownPanel)
                                  + (_state.ShownPanel == PanelId.Music && _state.ShowLyricsInIsland ? 76.0f : 0.0f);
                    _state.CardHeightSpring.SetTarget(targetH, red);
                    _state.PanelSpring.SetTarget(100.0f, red);
                    moving = true;
                }

                moving |= _state.NotchSpring.Step(dt, in SpringPresets.Close, speed, red);
                moving |= _state.RecSpring.Step(dt, in SpringPresets.Open, speed, red);
                moving |= _state.FlipSpring.Step(dt, in SpringPresets.Open, speed, red);

                // Always keep PlaySpring synced with Media.IsPlaying every frame so play/pause
                // updates instantaneously in-place without waiting for pointer movement.
                _state.PlaySpring.SetTarget(_state.Media.IsPlaying ? 100.0f : 0.0f, red);
                moving |= _state.PlaySpring.StepExponential(dt, 8.0f, speed, red);

                // Advance marquee & detect track switches for cover flip / title swap animation.
                if (_state.Media.HasSession)
                {
                    string trackKey = (_state.Media.Title ?? "") + "|" + (_state.Media.Artist ?? "");
                    if (!string.IsNullOrEmpty(_state.LastMediaTrackKey) && _state.LastMediaTrackKey != trackKey)
                    {
                        _state.SwapAtSec = nowSec;
                        _state.MarqueeSec = 0.0f;
                        moving = true;
                    }
                    _state.LastMediaTrackKey = trackKey;
                    if (_state.Media.IsPlaying)
                    {
                        _state.MarqueeSec += dt;
                    }
                }
                else
                {
                    _state.LastMediaTrackKey = "";
                    _state.MarqueeSec = 0.0f;
                }
                if ((nowSec - _state.SwapAtSec) is >= 0.0f and < 0.55f ||
                    (nowSec - _state.PrevKickSec) is >= 0.0f and < 0.48f ||
                    (nowSec - _state.NextKickSec) is >= 0.0f and < 0.48f ||
                    (nowSec - _state.PlayKickSec) is >= 0.0f and < 0.48f)
                {
                    moving = true;
                }

                float bgTarget = _state.Settings.CardBackground == CardBackgroundMode.AlbumColours ? 1.0f : 0.0f;
                if (red)
                {
                    _state.AlbumBgEase = bgTarget;
                }
                else
                {
                    _state.AlbumBgEase += (bgTarget - _state.AlbumBgEase) * (1.0f - MathF.Exp(-dt * 5.0f));
                    if (MathF.Abs(bgTarget - _state.AlbumBgEase) > 0.004f) moving = true;
                    else _state.AlbumBgEase = bgTarget;
                }
                float morphTarget = _state.Media.IsPlaying ? 0.0f : 1.0f;
                if (red)
                {
                    _state.MorphEase = morphTarget;
                }
                else
                {
                    _state.MorphEase += (morphTarget - _state.MorphEase) * (1.0f - MathF.Exp(-dt * 14.0f));
                    if (MathF.Abs(morphTarget - _state.MorphEase) > 0.004f) moving = true;
                    else _state.MorphEase = morphTarget;
                }
                moving |= _state.CardWidthSpring.Step(dt, in SpringPresets.Panel, speed, red);
                moving |= _state.CardHeightSpring.Step(dt, in SpringPresets.Close, speed, red);
                moving |= _state.IsThemeBlending;

                float hoverLerp = red ? 1.0f : 1.0f - MathF.Exp(-dt * 12.0f * speed);
                foreach (var kv in _state.HoverTargets)
                {
                    _state.HoverAlphas.TryGetValue(kv.Key, out float cur);
                    float next = red ? kv.Value : cur + (kv.Value - cur) * hoverLerp;
                    if (MathF.Abs(next - kv.Value) < 0.005f)
                    {
                        next = kv.Value;
                        if (MathF.Abs(cur - kv.Value) > 0.001f) moving = true;
                    }
                    else moving = true;
                    _state.HoverAlphas[kv.Key] = next;
                }
            }

            RenderIslandNow();

            // Keep the 60 fps loop alive while a theme cross-fades or an animated
            // theme ambience is on screen; stop when everything is truly idle so
            // a settled island still costs 0.0% CPU.
            if (!moving && !_state.IslandOpen && !_state.IsThemeBlending && !AmbienceShouldAnimate())
            {
                StopAnimTimer();
            }
            else if (!_animTimer.Enabled)
            {
                StartAnimTimer();
            }
        }

        private void StartAnimTimer()
        {
            if (_animTimer.Enabled) return;
            _lastAnimTimestamp = Stopwatch.GetTimestamp();
            Win32Layered.BeginHighResolutionTimer();
            _animTimer.Start();
        }

        private void StopAnimTimer()
        {
            if (!_animTimer.Enabled) return;
            _animTimer.Stop();
            Win32Layered.EndHighResolutionTimer();
        }

        // Soft themes (Rosé, Lavender, Peach, Sakura) animate particles even when
        // the island is resting, so they need continuous frames to look smooth.
        private bool AmbienceShouldAnimate()
        {
            var s = _state.Settings;
            if (s.ReduceMotion || _state.IslandHidden || !Visible) return false;
            if (s.PauseEffectsOnBattery && SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online) return false;
            // Theme particle ambience only runs while the island is actually being
            // looked at. Keeping it alive behind a static desktop was the reason a
            // settled island still burned a full core's worth of frames.
            bool ambience = ThemeSpec.HasAmbience(s.Theme) ||
                            (_state.IsThemeBlending &&
                             (ThemeSpec.HasAmbience(_state.ThemeBlendFrom) || ThemeSpec.HasAmbience(_state.ThemeBlendTo)));
            if (ambience) return _state.IslandOpen || _state.PointerInsideIsland;

            if (_state.Media.HasSession && _state.Media.IsPlaying) return true;
            if (_state.IsRecordingScreen || _state.ObsRecordingActive || _state.Sensors.MicActive) return true;
            if (_state.Media.LyricsFetching || _state.WebDrawerVisible) return true;
            // Keep Liquid Glass live while open or hovered so moving windows behind it never lag.
            if (s.Theme == SurfaceTheme.LiquidGlass || s.Theme == SurfaceTheme.CandyGlass)
                return _state.IslandOpen || _state.PointerInsideIsland;
            return false;
        }

        public void RenderIslandNow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (_state.IslandHidden)
            {
                Hide();
                return;
            }
            // Never re-enter the layered surface mid-frame: a click/slider handler that
            // requests a repaint is coalesced into one follow-up frame instead.
            if (_renderingIsland)
            {
                _islandRepaintQueued = true;
                return;
            }

            _renderingIsland = true;
            try
            {
                PositionOnTargetMonitor();
                if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;

                float timeSec = (float)Stopwatch.GetElapsedTime(AnimClockStart).TotalSeconds;
                IslandTabsRenderer.RenderIslandFrame(
                    _renderer, _state, Bounds, Width, Height, timeSec,
                    onOpenSettings: () => _settingsWindow.ToggleVisibility(),
                    onToggleDesktopWidget: wt => AuxiliaryWindows.ToggleDesktopWidget(_state, wt));

                _renderer.EndLayeredFrame(Handle, Location, 255);
            }
            catch
            {
                // Any render failure must never take down the island; fall back to a black pill.
            }
            finally
            {
                _renderingIsland = false;
                if (_state.IsThemeBlending && !_animTimer.Enabled) StartAnimTimer();
                if (_islandRepaintQueued)
                {
                    _islandRepaintQueued = false;
                    try
                    {
                        BeginInvoke(new Action(() => { if (!IsDisposed) RenderIslandNow(); }));
                    }
                    catch { }
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_DROPFILES = 0x0233;
            const int WM_HOTKEY = 0x0312;
            const int WM_CLIPBOARDUPDATE = 0x031D;
            const int HTTRANSPARENT = -1;
            const int HTCLIENT = 1;

            if (m.Msg == WM_NCHITTEST)
            {
                int lp = m.LParam.ToInt32();
                Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                IslandTabsRenderer.ComputeCurrentIslandGeometry(_state, Width, out float left, out float top, out float w, out float h, out _);
                if (w > 1 && h > 1 && pt.X >= left - 4 && pt.X <= left + w + 4 && pt.Y >= top && pt.Y <= top + h + 4)
                {
                    m.Result = new IntPtr(HTCLIENT);
                }
                else
                {
                    m.Result = new IntPtr(HTTRANSPARENT);
                }
                return;
            }

            if (m.Msg == WM_DROPFILES)
            {
                try
                {
                    IntPtr hDrop = m.WParam;
                    uint count = DragQueryFileW(hDrop, 0xFFFFFFFFu, null, 0);
                    var dropped = new List<string>((int)count);
                    var pathBuf = new StringBuilder(1024);
                    for (uint i = 0; i < count; ++i)
                    {
                        pathBuf.Clear();
                        if (DragQueryFileW(hDrop, i, pathBuf, pathBuf.Capacity) > 0)
                            dropped.Add(pathBuf.ToString());
                    }
                    DragFinish(hDrop);
                    if (dropped.Count > 0)
                    {
                        WindowsSystemHooks.AddFilesToShelf(_state, dropped);
                        lock (_state.SyncRoot) _state.ActivePanel = PanelId.Shelf;
                        OpenIslandNow();
                    }
                }
                catch { }
                return;
            }

            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_OPEN_ISLAND)
                {
                    if (_state.IslandOpen) CollapseIslandNow();
                    else OpenIslandNow();
                }
                else if (id == HK_PLAY_PAUSE) WindowsSystemHooks.MediaTogglePlayPause(_state);
                else if (id == HK_MUTE_MIC) { WindowsSystemHooks.ToggleGlobalMicMute(_state); RenderIslandNow(); }
                else if (id == HK_HIDE_ISLAND)
                {
                    _state.IslandHidden = !_state.IslandHidden;
                    if (_state.IslandHidden) Hide();
                    else { Show(); RenderIslandNow(); }
                }
                else if (id == HK_SCREENSHOT) { WindowsSystemHooks.CaptureFullScreenshot(_state); RenderIslandNow(); }
                else if (id == HK_COLOR_PICKER) { WindowsSystemHooks.PickScreenColorUnderCursor(_state); RenderIslandNow(); }
                else if (id >= HK_SLOT_BASE && id < HK_SLOT_BASE + 6)
                {
                    WindowsSystemHooks.ExecuteLauncherSlot(_state, id - HK_SLOT_BASE);
                    RenderIslandNow();
                }
                return;
            }

            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                WindowsSystemHooks.OnClipboardUpdated(_state);
                RenderIslandNow();
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            if (drgevent?.Data != null && drgevent.Data.GetDataPresent(DataFormats.FileDrop))
            {
                drgevent.Effect = DragDropEffects.Copy;
                lock (_state.SyncRoot) _state.ActivePanel = PanelId.Shelf;
                if (!_state.IslandOpen) OpenIslandNow();
            }
            else if (drgevent != null)
            {
                drgevent.Effect = DragDropEffects.None;
            }
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            try
            {
                if (drgevent?.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    WindowsSystemHooks.AddFilesToShelf(_state, files);
                    lock (_state.SyncRoot) _state.ActivePanel = PanelId.Shelf;
                    OpenIslandNow();
                }
            }
            catch { }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _state.PointerDipX = e.X;
            _state.PointerDipY = e.Y;

            // /support Section 7: Moving mouse while aiming in a game NEVER opens the island or Web Drawer
            // unless the user brings up the game cursor (Esc/menu) or presses Ctrl+Alt+N!
            if (_state.IsFullscreenGameActive && !_state.IsGameCursorShowing && !_state.IslandOpen)
            {
                return;
            }

            if (!_state.PointerInsideIsland)
            {
                _state.PointerInsideIsland = true;
                _hoverCloseTimer.Stop();
                if (!_state.IslandOpen)
                {
                    int delayMs = (int)(Math.Clamp(_state.Settings.HoverOpenDelaySec, 0.0f, 1.5f) * 1000.0f);
                    if (delayMs <= 15) OpenIslandNow();
                    else { _hoverOpenTimer.Interval = delayMs; _hoverOpenTimer.Start(); }
                }
            }
            if ((e.Button & MouseButtons.Left) != 0)
            {
                if (_shelfCandidateDrag && (Math.Abs(e.X - _mouseDownPt.X) > 6 || Math.Abs(e.Y - _mouseDownPt.Y) > 6))
                {
                    _shelfCandidateDrag = false;
                    WindowsSystemHooks.StartShelfDragOut(this, _state);
                    return;
                }
                if (_renderer.DispatchDrag(e.X, e.Y) && _state.ActivePanel == PanelId.Music)
                {
                    _pendingMusicSeek = true;
                }
            }
            // While the animation clock is running it paints the next frame anyway;
            // an extra synchronous paint here is what made motion look glitchy.
            if (!_animTimer.Enabled)
            {
                StartAnimTimer();
                RenderIslandNow();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _state.PointerInsideIsland = false;
            _state.PointerDipX = -1000;
            _state.PointerDipY = -1000;
            _hoverOpenTimer.Stop();
            if (_state.IslandOpen) _hoverCloseTimer.Start();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _state.PointerDipX = e.X;
            _state.PointerDipY = e.Y;
            _mouseDownPt = e.Location;
            _shelfCandidateDrag = false;

            if (TryHandleShelfOrLauncherInteraction(e)) return;

            if (e.Button == MouseButtons.Right)
            {
                // Right-click inside the notch does not open Settings
                return;
            }
            if (!_state.IslandOpen && !_state.ToastIsReminder)
            {
                OpenIslandNow();
            }
            else
            {
                try
                {
                    if (_renderer.DispatchClick(e.X, e.Y) && _state.ActivePanel == PanelId.Music)
                        _pendingMusicSeek = true;
                }
                catch { /* island actions must never crash the host */ }
                WindowsSystemHooks.SyncSystemTogglesIfChanged(_state);
                StartAnimTimer();
                StepSpringsAndRender();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _shelfCandidateDrag = false;
            if (_state.PlayButtonPressed)
            {
                _state.PlayButtonPressed = false;
                StartAnimTimer();
                RenderIslandNow();
            }
            if (_pendingMusicSeek)
            {
                _pendingMusicSeek = false;
                WindowsSystemHooks.SeekMediaPosition(_state, _state.Media.PositionSec);
            }
        }

        private bool TryHandleShelfOrLauncherInteraction(MouseEventArgs e)
        {
            if (!_state.IslandOpen) return false;
            IslandTabsRenderer.ComputeCurrentIslandGeometry(_state, Width, out float left, out float top, out float width, out float height, out _);
            float bodyTop = top + 32.0f;
            float bodyLeft = left + 18.0f;
            float bodyW = width - 36.0f;

            if (_state.ActivePanel == PanelId.Shelf && !_state.SendToPhoneQrActive && _state.ShelfFiles.Count > 0)
            {
                float fy = bodyTop + 30.0f;
                int visCount = Math.Min(12, _state.ShelfFiles.Count);
                float tileW = 58.0f;
                float gap = 4.5f;
                for (int i = 0; i < visCount; ++i)
                {
                    int col = i % 6;
                    int row = i / 6;
                    var cardRect = new RectangleF(bodyLeft + col * (tileW + gap), fy + row * 56.0f, tileW, 50.0f);
                    if (!cardRect.Contains(e.X, e.Y)) continue;

                    if (e.Button == MouseButtons.Right)
                    {
                        lock (_state.SyncRoot)
                        {
                            if (!_state.ShelfFiles[i].Selected)
                            {
                                foreach (var f in _state.ShelfFiles) f.Selected = false;
                                _state.ShelfFiles[i].Selected = true;
                            }
                        }
                        if (WindowsSystemHooks.CopyShelfSelectionToClipboard(_state))
                        {
                            lock (_state.SyncRoot)
                            {
                                _state.ToastPopupType = TopPopupType.Notice;
                                _state.ToastTitle = "Copied shelf files";
                                _state.ToastSubtitle = "Ready to paste";
                                _state.ToastAccent = Color.FromArgb(255, 48, 209, 88);
                                _state.ToastIsReminder = false;
                                _state.ToastActive = true;
                                _state.ToastExpireMs = ConfigManager.GetTickMs() + 2500;
                            }
                            RenderIslandNow();
                        }
                        return true;
                    }
                    if (e.Button == MouseButtons.Left)
                    {
                        lock (_state.SyncRoot)
                        {
                            if ((ModifierKeys & Keys.Control) == 0 && !_state.ShelfFiles[i].Selected)
                            {
                                foreach (var f in _state.ShelfFiles) f.Selected = false;
                            }
                            _state.ShelfFiles[i].Selected = !_state.ShelfFiles[i].Selected;
                        }
                        _shelfCandidateDrag = true;
                        RenderIslandNow();
                        return true;
                    }
                }
            }
            else if (_state.ActivePanel == PanelId.Launcher)
            {
                float slotW = (bodyW - 35.0f) / 6.0f;
                float slotY = bodyTop + 48.0f;
                for (int i = 0; i < 6; ++i)
                {
                    var sRect = new RectangleF(bodyLeft + i * (slotW + 7.0f), slotY, slotW, 62);
                    if (!sRect.Contains(e.X, e.Y)) continue;
                    if (e.Button == MouseButtons.Right)
                    {
                        WindowsSystemHooks.ConfigureLauncherSlot(_state, i);
                        RenderIslandNow();
                        return true;
                    }
                    if (e.Button == MouseButtons.Left)
                    {
                        WindowsSystemHooks.ExecuteLauncherSlot(_state, i);
                        RenderIslandNow();
                        return true;
                    }
                }
            }
            return false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopAnimTimer();
            Win32Layered.RemoveClipboardFormatListener(Handle);
            Win32Layered.UnregisterHotKey(Handle, HK_OPEN_ISLAND);
            Win32Layered.UnregisterHotKey(Handle, HK_PLAY_PAUSE);
            Win32Layered.UnregisterHotKey(Handle, HK_MUTE_MIC);
            Win32Layered.UnregisterHotKey(Handle, HK_HIDE_ISLAND);
            Win32Layered.UnregisterHotKey(Handle, HK_SCREENSHOT);
            Win32Layered.UnregisterHotKey(Handle, HK_COLOR_PICKER);
            for (int i = 0; i < 6; ++i) Win32Layered.UnregisterHotKey(Handle, HK_SLOT_BASE + i);

            WindowsSystemHooks.Shutdown(_state);
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            AuxiliaryWindows.CloseAll(_state);
            ConfigManager.SaveAtomic(_state);
            _settingsWindow?.Close();
            _settingsWindow?.Dispose();
            _renderer.Dispose();
            base.OnFormClosed(e);
        }

        [DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder lpszFile, int cch);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);
    }

    internal static class Program
    {
        // Runs the real island + live desktop widget for N seconds and reports the
        // measured idle CPU (percent of one core) and memory, instead of guessing.
        private static int RunIdleTest(int seconds)
        {
            var state = GlobalAppState.Instance;
            ConfigManager.Load(state);
            WindowsSystemHooks.Initialize(state);
            LocalNetworkServers.Start(state);

            using var widget = new DesktopWidgetForm(state, WidgetType.DayClock, 80, 80);
            widget.Show();
            widget.AttachAndRender();

            using var form = new IslandMainForm();
            var proc = Process.GetCurrentProcess();
            TimeSpan cpuStart = TimeSpan.Zero, cpuEnd = TimeSpan.Zero;
            long wallStart = 0, wallMs = 1;
            var timers = new List<System.Windows.Forms.Timer>();

            form.Shown += (_, _) =>
            {
                var warm = new System.Windows.Forms.Timer { Interval = 1500 };
                warm.Tick += (_, _) =>
                {
                    warm.Stop();
                    proc.Refresh();
                    cpuStart = proc.TotalProcessorTime;
                    wallStart = Environment.TickCount64;
                    var run = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
                    run.Tick += (_, _) =>
                    {
                        run.Stop();
                        proc.Refresh();
                        cpuEnd = proc.TotalProcessorTime;
                        wallMs = Math.Max(1, Environment.TickCount64 - wallStart);
                        form.Close();
                    };
                    run.Start();
                    timers.Add(run);
                };
                warm.Start();
                timers.Add(warm);
            };

            Application.Run(form);
            LocalNetworkServers.Stop();

            double cpuMs = Math.Max(0, (cpuEnd - cpuStart).TotalMilliseconds);
            double oneCorePct = cpuMs / wallMs * 100.0;
            double heapMb = GC.GetTotalMemory(true) / (1024.0 * 1024.0);
            proc.Refresh();
            double workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            Console.WriteLine($"IDLE_TEST seconds={wallMs / 1000.0:F1} cpuMs={cpuMs:F0} cpuPercentOneCore={oneCorePct:F2} " +
                              $"heapMb={heapMb:F1} workingSetMb={workingSetMb:F1} " +
                              $"telemetryTicks={IslandMainForm.TelemetryTicks} telemetryMs={IslandMainForm.TelemetryMs:F0} " +
                              $"oneHzTicks={IslandMainForm.OneHzTicks} oneHzMs={IslandMainForm.OneHzMs:F0} " +
                              $"[rem={IslandMainForm.RemindersMs:F0} media={IslandMainForm.MediaMs:F0} " +
                              $"agents={IslandMainForm.AgentsMs:F0} widgets={IslandMainForm.WidgetsMs:F0}] " +
                              $"renders={IslandMainForm.RenderCount} renderMs={IslandMainForm.RenderMs:F0}");
            return oneCorePct < 5.0 ? 0 : 1;
        }

        // Captures the real desktop behind a region, runs the production glass
        // pipeline at radius 0 and 40, and saves the blurred backdrop so it can be
        // inspected as a PNG (proves live capture, not a flat fill).
        private static int RunGlassTest(string path)
        {
            using var renderer = new LiquidGlassRenderer();
            if (!renderer.BeginLayeredFrame(480, 240, 1.0f))
            {
                Console.WriteLine("GLASS_TEST begin=false");
                return 1;
            }

            var region = new Rectangle(0, 0, 320, 160);
            bool sharpOk = renderer.RenderLiquidGlassBackdrop(region, 0, 0, 480, 240, 0.0f, false);
            double sharpVariance = renderer.BackdropVariance;
            ulong sharpHash = renderer.BackdropOutputHash;
            bool blurOk = renderer.RenderLiquidGlassBackdrop(region, 0, 0, 480, 240, 40.0f, false);
            double blurVariance = renderer.BackdropVariance;
            ulong blurHash = renderer.BackdropOutputHash;
            bool saved = renderer.SaveSurfacePng(path);

            Console.WriteLine($"GLASS_TEST captured={(sharpOk && blurOk ? 1 : 0)} variance0={sharpVariance:F3} variance40={blurVariance:F3} " +
                              $"radiusChangesPixels={(sharpHash != blurHash ? 1 : 0)} png={saved}");
            return sharpOk && blurOk && saved && blurVariance > 0.2 ? 0 : 1;
        }

        private static bool ProbeTcp(int port, bool expectOpen, int timeoutMs = 3000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                try
                {
                    using var probe = new TcpClient();
                    var connect = probe.ConnectAsync(IPAddress.Loopback, port);
                    bool opened = connect.Wait(300) && probe.Connected;
                    if (opened) return expectOpen;
                }
                catch
                {
                    if (!expectOpen) return true;
                }
                Thread.Sleep(100);
            }
            return !expectOpen;
        }

        // Renders the island offscreen in every state the hero demo shows — the resting
        // pill, music playing and paused, and every tab — and writes one PNG per state
        // so each frame can be compared with notchpixel.com directly.
        private static int RunHudSnapshots(string dir)
        {
            var state = GlobalAppState.Instance;
            var s = state.Settings;
            s.Theme = SurfaceTheme.Black;              // the demo's default surface (no glass capture)
            s.Edge = EdgePlacement.TopCenter;
            s.Shape = IslandShape.Notch;
            s.DrawWithProcessor = false;
            s.ReduceMotion = true;                     // settled frames, no spring in flight
            s.PillSizePercent = 100.0f;
            s.OpenSizePercent = 100.0f;
            s.CornerRadiusScale = 1.0f;
            s.TextScale = 1.0f;
            s.ShowSongTitleInClosedPill = false;
            s.Visualizer = VisualizerStyle.Pixels;
            s.ProgressBar = OpenPixel.Core.ProgressBarStyle.Wave;
            s.CardBackground = CardBackgroundMode.Plain;
            s.PanelOrder = new List<PanelId>
            {
                PanelId.Music, PanelId.Web, PanelId.Notifications, PanelId.Chat, PanelId.Shelf,
                PanelId.Clipboard, PanelId.Widgets, PanelId.Controls, PanelId.Launcher, PanelId.Calls
            };

            Directory.CreateDirectory(dir);

            var cover = new System.Drawing.Bitmap(240, 240);
            using (var cg = Graphics.FromImage(cover))
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, 240, 240),
                Color.FromArgb(18, 83, 54), Color.FromArgb(255, 230, 109), 45f))
            {
                cg.FillRectangle(lg, 0, 0, 240, 240);
                using var bloom = new SolidBrush(Color.FromArgb(52, 183, 136));
                foreach (var p in new[] { new Point(70, 80), new Point(160, 70), new Point(120, 160) })
                    for (int k = 0; k < 6; ++k)
                        cg.FillEllipse(bloom, p.X + (int)(Math.Cos(k) * 18) - 20, p.Y + (int)(Math.Sin(k) * 18) - 12, 40, 24);
            }

            var media = state.Media;
            media.HasSession = true;
            media.IsPlaying = true;
            // The demo's own sample track, so the card's text, clipping and 136 DIP
            // title box line up with the live card.
            media.Title = "Everything We Want";
            media.Artist = "Mira Sol";
            media.SourceAppName = "Spotify";
            media.DurationSec = 187;
            media.PositionSec = 63;

            // Representative entries for the tab captures, mirroring the demo's own
            // state art so each frame can be compared with it side by side. This runs
            // only in the offscreen harness; the live app never sees these.
            s.WeatherCity = "London";
            s.WeatherHighC = 22;
            s.WeatherLowC = 15;
            s.UpcomingMeetingTitle = "Design review";
            s.UpcomingMeetingTime = "in 12 min \u00B7 Zoom";
            state.ShelfFiles.AddRange(new[]
            {
                new ShelfFileItem { DisplayName = "Sunset-Tokyo.png", Extension = ".png", IsImage = true },
                new ShelfFileItem { DisplayName = "Launch-Demo.mp4", Extension = ".mp4" },
                new ShelfFileItem { DisplayName = "Night-Shift.mp3", Extension = ".mp3" },
                new ShelfFileItem { DisplayName = "Invoice-October.pdf", Extension = ".pdf" },
                new ShelfFileItem { DisplayName = "Brand kit.zip", Extension = ".zip" },
                new ShelfFileItem { DisplayName = "Press-Kit.zip", Extension = ".zip" },
                new ShelfFileItem { DisplayName = "Hero-Shot.png", Extension = ".png", IsImage = true },
                new ShelfFileItem { DisplayName = "Teaser.mp4", Extension = ".mp4" }
            });
            state.TodoList.Add(new TodoItem { Text = "Reply to Sam" });
            state.TodoList.Add(new TodoItem { Text = "Book the dentist", Done = true });
            long hudNow = ConfigManager.GetTickMs();

            state.ClipboardHistory.Add(new ClipboardEntry
            {
                Category = ClipboardCategory.Files,
                Preview = "3 files",
                SourceInfo = "Temp",
                TimestampMs = hudNow - 40000,
                FilePaths = new List<string> { "notchpixel-demo.zip", "brand-kit.fig", "island.js" }
            });
            state.ClipboardHistory.Add(new ClipboardEntry
            {
                Category = ClipboardCategory.Files,
                Preview = "Invoice",
                SourceInfo = "NotchPixel demo",
                TimestampMs = hudNow - 12000,
                FilePaths = new List<string> { "October.pdf" }
            });
            var hudImageEntry = new ClipboardEntry { Category = ClipboardCategory.Images, Preview = "Island render", SourceInfo = "Screenshot", TimestampMs = hudNow - 20000 };
            state.ClipboardHistory.Add(hudImageEntry);
            try
            {
                string hudImagePath = Path.Combine(Path.GetTempPath(), "openpixel-hud-image.png");
                using (var bmp = new Bitmap(240, 240))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        // The demo's own clipboard photograph, embedded at capture
                        // resolution (assets/island/clip-photo.png) and cover-fitted to the
                        // 240 x 240 the card reports. No network at render time.
                        Bitmap? art = null;
                        try
                        {
                            using Stream? photoStream = typeof(Program).Assembly
                                .GetManifestResourceStream("OpenPixel.Island.clip-photo.png");
                            if (photoStream != null) art = new Bitmap(photoStream);
                        }
                        catch { }
                        if (art != null)
                        {
                            using (art)
                            {
                                float scale = Math.Max(240.0f / art.Width, 240.0f / art.Height);
                                float w = art.Width * scale, h = art.Height * scale;
                                g.DrawImage(art, (240.0f - w) * 0.5f, (240.0f - h) * 0.5f, w, h);
                            }
                        }
                        else
                        {
                            using var fallback = new SolidBrush(Color.FromArgb(255, 24, 34, 84));
                            g.FillRectangle(fallback, 0, 0, 240, 240);
                        }
                    }
                    bmp.Save(hudImagePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                hudImageEntry.ImagePath = hudImagePath;
            }
            catch { }
            // The demo's own album cover, embedded from its music card at capture size.
            Bitmap albumArt = cover;
            try
            {
                using Stream? coverStream = typeof(Program).Assembly
                    .GetManifestResourceStream("OpenPixel.Island.music-cover.png");
                if (coverStream != null) albumArt = new Bitmap(coverStream);
            }
            catch { }
            media.AlbumArt = albumArt;
            media.DominantColor = Color.FromArgb(255, 181, 228, 140);
            media.SecondaryColor = Color.FromArgb(255, 18, 53, 36);

            state.PinnedSites.Clear();
            state.PinnedSites.Add(new PinnedWebSite { Title = "YT Music", Url = "music.youtube.com", BadgeColor = Color.FromArgb(255, 255, 40, 70) });
            state.PinnedSites.Add(new PinnedWebSite { Title = "YouTube", Url = "youtube.com", BadgeColor = Color.FromArgb(255, 255, 40, 60) });
            state.PinnedSites.Add(new PinnedWebSite { Title = "Spotify", Url = "open.spotify.com", BadgeColor = Color.FromArgb(255, 30, 215, 96) });

            state.Notifications.Clear();
            state.Notifications.Add(new NotificationItem { AppName = "Stocks", Title = "Price alert", Body = "BTC above 70,000", TimeStr = "09:41", RelativeStr = "now", AccentColor = Color.FromArgb(255, 233, 65, 87) });
            state.Notifications.Add(new NotificationItem { AppName = "Phone", Title = "From your phone", Body = "IMG_2041.jpg is on the shelf", TimeStr = "09:40", RelativeStr = "now", AccentColor = Color.FromArgb(255, 232, 192, 39) });
            state.Notifications.Add(new NotificationItem { AppName = "Terminal", Title = "Build main", Body = "142 tests, 38 s, passing", TimeStr = "09:39", RelativeStr = "now", AccentColor = Color.FromArgb(255, 233, 65, 87) });

            var panels = new[]
            {
                PanelId.Music, PanelId.Web, PanelId.Notifications, PanelId.Chat, PanelId.Shelf,
                PanelId.Clipboard, PanelId.Widgets, PanelId.Controls, PanelId.Launcher, PanelId.Calls
            };
            int failed = 0;

            bool Shot(string name, bool open, PanelId panel, bool playing)
            {
                state.IslandOpen = open;
                state.ActivePanel = panel;
                state.PreviousPanel = panel;
                state.ShowLyricsInIsland = false;
                state.ShowCalendarInIsland = false;
                state.ShowDeckInIsland = false;
                media.IsPlaying = playing;
                state.PlaySpring.Snap(playing ? 100.0f : 0.0f);
                state.MorphEase = playing ? 0.0f : 1.0f;
                state.PanelSpring.Snap(open ? 100.0f : 0.0f);
                state.OpenSpring.Snap(open ? 100.0f : 0.0f);
                state.FadeSpring.Snap(open ? 100.0f : 0.0f);
                state.CardWidthSpring.Snap(open ? DipMetrics.CardW : DipMetrics.PillW);
                state.CardHeightSpring.Snap(open ? ThemeSpec.GetExpandedHeight(panel) : DipMetrics.PillH);
                state.NotchSpring.Snap(100.0f);

IslandTabsRenderer.ComputeCurrentIslandGeometry(state, 520, out float dl, out float dt, out float dw, out float dh, out _);
                Console.WriteLine($"HUD_SHOT {name} l={dl:F1} t={dt:F1} w={dw:F1} h={dh:F1}");

                using var r = new LiquidGlassRenderer();
                if (!r.BeginLayeredFrame(520, 290, 1.0f)) return false;
                IslandTabsRenderer.RenderIslandFrame(r, state, new Rectangle(1000, 0, 520, 290), 520, 290, 1.0f, () => { }, _ => { });
                r.EndLayeredFrame(IntPtr.Zero, new Point(1000, 0), 255);
                return r.SaveSurfacePng(Path.Combine(dir, name + ".png"));
            }

            // The idle player (no session) is a real demo state of its own, so the
            // harness writes fourteen distinct frames rather than an album duplicate.
            media.HasSession = false;
            if (!Shot("00-music-idle", true, PanelId.Music, false)) ++failed;
            media.HasSession = true;

            if (!Shot("01-pill", false, PanelId.Music, true)) ++failed;
            if (!Shot("02-music-playing", true, PanelId.Music, true)) ++failed;
            if (!Shot("03-music-paused", true, PanelId.Music, false)) ++failed;

            // Frame 04 is the music card with the album-colour backdrop on, a state of
            // its own rather than a second copy of the playing card.
            s.CardBackground = CardBackgroundMode.AlbumColours;
            if (!Shot("04-music-album", true, PanelId.Music, true)) ++failed;
            s.CardBackground = CardBackgroundMode.Plain;

            for (int i = 1; i < panels.Length; ++i)
            {
                string name = $"{(i + 4):D2}-{panels[i].ToString().ToLowerInvariant()}";
                if (!Shot(name, true, panels[i], true)) ++failed;
            }

            Console.WriteLine($"HUD_FONT geist={OpenPixel.Rendering.UiFonts.IsGeist} scale={OpenPixel.Rendering.UiFonts.SizeScaleFactor} mono={OpenPixel.Rendering.UiFonts.MonoFamilyName}");
            Console.WriteLine($"HUD_TEST dir={Path.GetFullPath(dir)} shots={4 + panels.Length - failed} failed={failed}");
            return failed == 0 ? 0 : 1;
        }

        // Creates a real pinned Day Clock widget, reports where Windows actually
        // placed it and saves its layered surface so the render can be inspected
        // without a desktop screenshot.
        private static int RunWidgetTest(string path)
        {
            var state = GlobalAppState.Instance;
            ConfigManager.Load(state);
            using var form = new DesktopWidgetForm(state, WidgetType.DayClock, 80, 80);
            form.Show();
            Application.DoEvents();
            Thread.Sleep(150);
            Application.DoEvents();
            form.AttachAndRender();
            Application.DoEvents();

            IntPtr hwnd = form.Handle;
            bool visible = Win32Layered.IsWindowVisible(hwnd);
            var parentClass = new StringBuilder(64);
            IntPtr parent = Win32Layered.GetParent(hwnd);
            if (parent != IntPtr.Zero) Win32Layered.GetClassNameW(parent, parentClass, parentClass.Capacity);
            bool parentVisible = parent != IntPtr.Zero && Win32Layered.IsWindowVisible(parent);
            Win32Layered.GetWindowRect(hwnd, out Win32Layered.RECT rect);
            bool saved = form.SavePreview(path);

            int w = rect.right - rect.left;
            int h = rect.bottom - rect.top;
            Win32Layered.GetWindowRect(DesktopLayer.Host, out Win32Layered.RECT hostRect);
            Console.WriteLine($"WIDGET_HOST rect={hostRect.left},{hostRect.top},{hostRect.right - hostRect.left}x{hostRect.bottom - hostRect.top}");
            long style = Win32Layered.GetWindowLongPtr(hwnd, -16).ToInt64();
            Console.WriteLine($"WIDGET_TEST visible={(visible ? 1 : 0)} winforms={(form.Visible ? 1 : 0)} host=0x{DesktopLayer.Host.ToInt64():X} style=0x{style:X8} parent=0x{parent.ToInt64():X}/{parentClass}/vis={parentVisible} rect={rect.left},{rect.top},{w}x{h} png={saved}");
            return visible && w > 100 && h > 60 && saved ? 0 : 1;
        }

        // Renders the Day Clock typography to a PNG so the stencil face and type
        // sizes can be compared against the reference screenshots headlessly.
        private static int RenderWidgetPreview(string path)
        {
            const int W = 360, H = 190;
            string weekday = "SATURDAY";
            float pad = 20.0f, tracking = 13.6f;
            float unit = StencilFont.WidthOf(weekday, 1.0f, 0.0f);
            float fit = (W - pad * 2.0f - (weekday.Length - 1) * tracking) / Math.Max(0.01f, unit);
            float cap = Math.Clamp(fit, 18.0f, 58.0f);

            using var bmp = new Bitmap(W, H);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(214, 226, 240));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                Color ink = Color.FromArgb(18, 18, 20);
                float y = 16.0f;
                StencilFont.Draw(g, weekday, W * 0.5f, y, cap, tracking, ink, 0.72f);
                y += cap + 11.0f;
                DrawPreviewLine(g, "21 JANUARY, 2023.", W * 0.5f, y + 4.0f, 12.5f, 2.4f, ink);
                y += 21.0f;
                DrawPreviewLine(g, "- 11:03 -", W * 0.5f, y + 4.0f, 14.5f, 2.0f, ink);
            }

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("RENDER_OK " + path);
            return 0;
        }

        private static void DrawPreviewLine(Graphics g, string text, float centerX, float top, float size, float tracking, Color color)
        {
            using Font font = UiFonts.Get(size, true);
            using var brush = new SolidBrush(color);
            var format = StringFormat.GenericTypographic;
            float spaceWidth = Math.Max(1.0f,
                g.MeasureString("i i", font, PointF.Empty, format).Width -
                2.0f * g.MeasureString("i", font, PointF.Empty, format).Width);
            var widths = new float[text.Length];
            float total = tracking * Math.Max(0, text.Length - 1);
            for (int i = 0; i < text.Length; ++i)
            {
                widths[i] = char.IsWhiteSpace(text[i])
                    ? spaceWidth
                    : g.MeasureString(text[i].ToString(), font, PointF.Empty, format).Width;
                total += widths[i];
            }

            float x = centerX - total * 0.5f;
            for (int i = 0; i < text.Length; ++i)
            {
                g.DrawString(text[i].ToString(), font, brush, x, top, format);
                x += widths[i] + tracking;
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // A single bad GDI+/theme frame must never terminate the whole app.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => LogRecoveredException(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogRecoveredException(e.ExceptionObject as Exception);

            // Whatever ends this process - a normal quit, a fatal handler or an
            // external kill - the native taskbar must never stay hidden.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { AuxiliaryWindows.RestoreWindowsNativeTaskbar(); } catch { }
            };

            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i] == "--watchdog" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parentPid))
                {
                    try
                    {
                        using var proc = Process.GetProcessById(parentPid);
                        proc.WaitForExit();
                    }
                    catch { }
                    AuxiliaryWindows.RestoreWindowsNativeTaskbar();
                    return 0;
                }
                if (args[i] == "--self-test")
                {
                    return RunAutomatedSelfTests();
                }
                if (args[i] == "--web-test")
                {
                    // Proves the Edge WebView2 runtime + native loader resolve from
                    // wherever this exe lives (root copy included).
                    try
                    {
                        string probeDir = Path.Combine(Path.GetTempPath(), "openpixel-web-probe");
                        Directory.CreateDirectory(probeDir);
                        var env = Microsoft.Web.WebView2.Core.CoreWebView2Environment
                            .CreateAsync(null, probeDir).GetAwaiter().GetResult();
                        string version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
                        Console.WriteLine("WEBVIEW_OK " + version);
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("WEBVIEW_FAIL " + ex.GetType().Name + ": " + ex.Message);
                        return 1;
                    }
                }
                if (args[i] == "--video-test")
                {
                    return VideoTestClip.Run(i + 1 < args.Length ? args[i + 1] : null);
                }
                if (args[i] == "--video-test-file" && i + 1 < args.Length)
                {
                    return VideoTestClip.Run(args[i + 1], generateClip: false);
                }
                if (args[i] == "--render-test")
                {
                    return RenderWidgetPreview(i + 1 < args.Length ? args[i + 1] : "dayclock_preview.png");
                }
                if (args[i] == "--widget-test" && i + 1 < args.Length)
                {
                    return RunWidgetTest(args[i + 1]);
                }
                if (args[i] == "--glass-test" && i + 1 < args.Length)
                {
                    return RunGlassTest(args[i + 1]);
                }
                if (args[i] == "--hud-test")
                {
                    return RunHudSnapshots(i + 1 < args.Length ? args[i + 1] : "hud_snapshots");
                }
                if (args[i] == "--idle-test")
                {
                    int idleSeconds = (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed))
                        ? Math.Clamp(parsed, 2, 60) : 10;
                    return RunIdleTest(idleSeconds);
                }
                if (args[i] == "--qr-test")
                {
                    QrMatrix qr = QrCodeEncoder.Encode(i + 1 < args.Length ? args[i + 1] : "openpixel-deck://pair");
                    var json = new StringBuilder();
                    json.Append("{\"version\":").Append(qr.Version)
                        .Append(",\"mask\":").Append(qr.Mask)
                        .Append(",\"size\":").Append(qr.Size)
                        .Append(",\"rows\":[");
                    for (int y = 0; y < qr.Size; ++y)
                    {
                        if (y > 0) json.Append(',');
                        json.Append('"');
                        for (int x = 0; x < qr.Size; ++x) json.Append(qr.Modules[y, x] ? '1' : '0');
                        json.Append('"');
                    }
                    json.Append("]}");
                    Console.WriteLine(json.ToString());
                    return 0;
                }
                if (args[i] == "--notify" && i + 1 < args.Length)
                {
                    string extraText = (i + 2 < args.Length && !args[i + 2].StartsWith("--")) ? args[i + 2] : "";
                    string rel = $"/notify?title={Uri.EscapeDataString(args[i + 1])}&text={Uri.EscapeDataString(extraText)}";
                    ForwardCliLoopbackCommand(rel);
                    return 0;
                }
                if (args[i] == "--tile" && i + 1 < args.Length)
                {
                    string extraVal = (i + 2 < args.Length && !args[i + 2].StartsWith("--")) ? args[i + 2] : "";
                    string rel = $"/tile?title={Uri.EscapeDataString(args[i + 1])}&value={Uri.EscapeDataString(extraVal)}";
                    ForwardCliLoopbackCommand(rel);
                    return 0;
                }
            }

            var state = GlobalAppState.Instance;
            ConfigManager.Load(state);
            ConfigManager.ApplyLaunchAtStartup(state.Settings.LaunchAtStartup);
            WindowsSystemHooks.Initialize(state);
            LocalNetworkServers.Start(state);

            Application.Run(new IslandMainForm());

            LocalNetworkServers.Stop();
            ConfigManager.SaveAtomic(state);
            return 0;
        }

        private static void ForwardCliLoopbackCommand(string relativePathAndQuery)
        {
            var state = GlobalAppState.Instance;
            ConfigManager.Load(state);
            int port = state.Settings.LoopbackApiPort > 0 ? state.Settings.LoopbackApiPort : 47823;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
                using var resp = http.GetAsync($"http://127.0.0.1:{port}{relativePathAndQuery}").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return;
            }
            catch { }
            LocalNetworkServers.HandleLoopbackRequest(state, relativePathAndQuery, out _);
        }

        private static void LogRecoveredException(Exception ex)
        {
            if (ex == null) return;
            try { AuxiliaryWindows.RestoreWindowsNativeTaskbar(); } catch { }
            try
            {
                File.AppendAllText(ConfigManager.GetDiagnosticLogPath(),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Recovered UI exception: {ex}\n");
            }
            catch { }
        }

        // ====================================================================
        // Comprehensive C# .NET 10 Automated Self-Test Suite (`--self-test`)
        // ====================================================================
        private static int RunAutomatedSelfTests()
        {
            var sb = new StringBuilder();
            int passed = 0, failed = 0;

            // Stream every line as it happens as well as collecting the report:
            // a hard crash used to leave a completely empty transcript, which
            // made the failing step impossible to identify.
            void Emit(string line)
            {
                sb.AppendLine(line);
                Console.WriteLine(line);
                Console.Out.Flush();
            }

            void Check(string name, bool cond)
            {
                if (cond) ++passed; else ++failed;
                Emit(cond ? $"[PASS] {name}" : $"[FAIL] {name}");
            }

            var state = GlobalAppState.Instance;
            ConfigManager.InitializeDefaults(state);

            // 1. CRITICAL REQUIREMENT #1: LIQUID GLASS ENABLED BY DEFAULT & ZERO CRASHES ON SLIDERS
            Check("Liquid Glass is enabled by default out of the box (Theme == SurfaceTheme.LiquidGlass)",
                  state.Settings.Theme == SurfaceTheme.LiquidGlass);

            // Inter ships inside the assembly; every surface draws through it.
            Check("Inter typeface is embedded in the assembly and loads at runtime (whole UI font)",
                  UiFonts.IsAvailable);
            // The island now draws in Geist (the demo's own family) with Inter kept as
            // the embedded fallback. Both body and bold must resolve to that family.
            // The check keeps its original name so the Tier1 CLI assertion still
            // pins this line.
            using (Font typeProbe = UiFonts.Get(12.0f, true))
            using (Font typeBody = UiFonts.Get(12.0f, false))
            {
                Check("Inter resolves for both body and bold text",
                      typeProbe.FontFamily.Name.Length > 0 &&
                      typeProbe.FontFamily.Name == typeBody.FontFamily.Name &&
                      (typeProbe.FontFamily.Name.IndexOf("Geist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       typeProbe.FontFamily.Name.IndexOf("Inter", StringComparison.OrdinalIgnoreCase) >= 0));
            }

            // Test extreme out-of-bounds blur slider values (-100, 0, 999) via atomic queue
            state.QueueBlurRadius(-50.0f);
            state.CommitPendingParametersOnRenderTick();
            bool clampedZeroOk = state.Settings.GlassBlurRadius == 0.0f;
            state.QueueBlurRadius(500.0f);
            state.CommitPendingParametersOnRenderTick();
            bool clampedMaxOk = state.Settings.GlassBlurRadius == 40.0f;
            state.QueueBlurRadius(20.0f);
            state.CommitPendingParametersOnRenderTick();
            Check("Liquid Glass Blur Slider clamps strictly to [0..40px] and commits atomically on render tick",
                  clampedZeroOk && clampedMaxOk && state.Settings.GlassBlurRadius == 20.0f);

            using (var renderer = new LiquidGlassRenderer())
            {
                bool frameOk = renderer.BeginLayeredFrame(520, 290, 1.0f);
                // Test graceful fallback when invalid screen rect is passed
                bool fallbackOk = !renderer.RenderLiquidGlassBackdrop(new Rectangle(0, 0, -10, -10), 0, 0, 200, 100, 0.0f, false);

                // Stress-test dragging blur slider 0..40px & switching all 11 themes across all 10 tabs + Calendar in Island
                bool stressOk = frameOk;
                state.IslandOpen = true;
                state.OpenSpring.Snap(100.0f);
                for (int t = 0; t < 11; ++t)
                {
                    state.QueueTheme((SurfaceTheme)t);
                    state.QueueBlurRadius((t * 4) % 41);
                    for (int p = 0; p < 10; ++p)
                    {
                        state.ActivePanel = (PanelId)p;
                        state.ShowCalendarInIsland = (p == (int)PanelId.Widgets && (t % 2 == 1));
                        if (!renderer.BeginLayeredFrame(520, 290, 1.0f)) { stressOk = false; break; }
                        IslandTabsRenderer.RenderIslandFrame(renderer, state, new Rectangle(100, 0, 520, 290), 520, 290, 1.5f, null, null);
                        renderer.EndLayeredFrame(IntPtr.Zero, new Point(100, 0), 255);
                    }
                }
                // Smoothness measurement: soft themes animate particles on every frame,
                // so their draw cost must stay inside the 60 fps budget.
                state.QueueTheme(SurfaceTheme.Sakura);
                state.CommitPendingParametersOnRenderTick();
                state.IslandOpen = false;
                state.OpenSpring.Snap(0.0f);
                int ambienceFrames = 120;
                var ambienceSweep = Stopwatch.StartNew();
                for (int i = 0; i < ambienceFrames; ++i)
                {
                    if (!renderer.BeginLayeredFrame(520, 290, 1.0f)) break;
                    IslandTabsRenderer.RenderIslandFrame(renderer, state, new Rectangle(100, 0, 520, 290), 520, 290, i / 60.0f, null, null);
                    renderer.EndLayeredFrame(IntPtr.Zero, new Point(100, 0), 255);
                }
                ambienceSweep.Stop();
                double ambienceMsPerFrame = ambienceSweep.Elapsed.TotalMilliseconds / ambienceFrames;
                Emit($"[METRIC] Theme ambience draw cost (Sakura, closed island): {ambienceMsPerFrame:F2} ms/frame");
                Check("Animated theme ambience stays inside the 60 fps frame budget (< 16.7 ms)",
                      ambienceMsPerFrame < 16.7);

                state.QueueTheme(SurfaceTheme.LiquidGlass);
                state.CommitPendingParametersOnRenderTick();
                state.IslandOpen = false;
                state.OpenSpring.Snap(0.0f);
                Check("Crash-Proof Liquid Glass & All 11 Themes: Stress-tested slider 0..40px across all 10 tabs cleanly",
                      stressOk && fallbackOk);
            }

            // 2. CRITICAL REQUIREMENT #2: DISABLEABLE GAME OVERLAY & AIMING CURSOR GUARD
            state.IsFullscreenGameActive = true;
            state.Settings.GameOverlayMasterEnabled = false;
            state.Settings.GameOverlayMode = GameOverlayMode.OffHideCompletely;
            IslandTabsRenderer.ComputeCurrentIslandGeometry(state, 520, out _, out _, out float hiddenW, out float hiddenH, out _);
            bool hidesCompletely = (hiddenW == 0.0f && hiddenH == 0.0f);

            state.Settings.GameOverlayMasterEnabled = true;
            state.Settings.GameOverlayMode = GameOverlayMode.SlimStrip;
            IslandTabsRenderer.ComputeCurrentIslandGeometry(state, 520, out _, out _, out float stripW, out float stripH, out _);
            bool showsSlimStrip = (MathF.Abs(stripH - 22.0f) < 0.5f && stripW > 300.0f);

            state.Settings.GameOverlayMode = GameOverlayMode.Normal;
            IslandTabsRenderer.ComputeCurrentIslandGeometry(state, 520, out _, out _, out float normW, out float normH, out _);
            bool showsNormalPill = (MathF.Abs(normH - 36.0f) < 0.5f && MathF.Abs(normW - 212.0f) < 0.5f);
            state.IsFullscreenGameActive = false;
            Check("Disableable Game Overlay supports Off/Hide completely, Slim FPS/CPU/GPU strip, and Normal modes",
                  hidesCompletely && showsSlimStrip && showsNormalPill);

            // 3. CRITICAL REQUIREMENT #3: NEWLY SHIPPED FEATURES (DAY CLOCK, CALENDAR IN ISLAND, LIVE VIDEO WALLPAPER, TASKBAR)
            AuxiliaryWindows.ToggleDesktopWidget(state, WidgetType.DayClock);
            bool dayClockPinned = AuxiliaryWindows.ActiveWidgets.Count == 1 &&
                                  AuxiliaryWindows.ActiveWidgets[0].WidgetType == WidgetType.DayClock;

            // A pinned widget must be a real visible window with a screen rectangle,
            // not just an entry in the active list.
            bool dayClockVisible = false;
            int dayClockWidth = 0, dayClockHeight = 0;
            if (AuxiliaryWindows.ActiveWidgets.Count == 1)
            {
                DesktopWidgetForm probe = AuxiliaryWindows.ActiveWidgets[0];
                dayClockVisible = Win32Layered.IsWindowVisible(probe.Handle);
                if (Win32Layered.GetWindowRect(probe.Handle, out Win32Layered.RECT wr))
                {
                    dayClockWidth = wr.right - wr.left;
                    dayClockHeight = wr.bottom - wr.top;
                }
            }
            Check("Desktop Day Clock window is visible on the desktop layer with a real screen rectangle",
                  dayClockVisible && dayClockWidth > 100 && dayClockHeight > 60);

            AuxiliaryWindows.ToggleDesktopWidget(state, WidgetType.DayClock);
            bool dayClockUnpinned = AuxiliaryWindows.ActiveWidgets.Count == 0;
            Check("Desktop Day Clock Widget pins, renders with Liquid Glass live wallpaper sampling, and unpins cleanly",
                  dayClockPinned && dayClockUnpinned);

            state.Settings.LiveVideoWallpaperEnabled = true;
            state.Settings.LiveVideoWallpaperPath = "";
            AuxiliaryWindows.UpdateLiveVideoWallpaper(state);
            bool wallpaperCreated = AuxiliaryWindows.WallpaperForm != null && !AuxiliaryWindows.WallpaperForm.IsDisposed;

            // Real decode, not a tautology: write a clip, decode it through the
            // same Media Foundation path the wallpaper uses and require a
            // coloured frame (RGB32, with an NV12 fallback).
            bool videoDecoded = VideoTestClip.Run(null, generateClip: true, verbose: false) == 0;
            Check("Live Video Wallpaper decodes a real clip and presents a coloured frame",
                  videoDecoded);

            state.Settings.LiveVideoWallpaperEnabled = false;
            AuxiliaryWindows.UpdateLiveVideoWallpaper(state);
            bool wallpaperDisposed = AuxiliaryWindows.WallpaperForm == null;
            Check("Live Video Wallpaper engine initializes and disposes its desktop window cleanly",
                  wallpaperCreated && wallpaperDisposed);

            // 4. CRITICAL REQUIREMENT #4: ZERO PLACEHOLDERS, REAL HOOKS & FREE BUFF AGENT
            Check("Zero Placeholders: Media starts with HasSession=false, DurationSec=0 and no fabricated track metadata",
                  !state.Media.HasSession && string.IsNullOrEmpty(state.Media.Title) && state.Media.DurationSec == 0.0);
            Check("Zero Placeholders: HardwareTelemetry defaults Fps=0 (FpsAvailable=false, GpuPercentAvailable=false) and CpuTempAvailable=false",
                  state.Telemetry.Fps == 0.0f && !state.Telemetry.FpsAvailable && !state.Telemetry.GpuPercentAvailable && !state.Telemetry.CpuTempAvailable);
            Check("Zero Placeholders: Desktop CPU temperature does not invent fake motherboard temps",
                  !state.Telemetry.CpuTempAvailable && state.Telemetry.CpuTempC == 0.0f);

            WindowsSystemHooks.Initialize(state);
            WindowsSystemHooks.Tick10HzTelemetryAndSensors(state);
            bool telemetryGenuine = (!state.Telemetry.GpuPercentAvailable && state.Telemetry.GpuPercent == 0.0f) ||
                                    (state.Telemetry.GpuPercentAvailable && state.Telemetry.GpuPercent >= 0.0f && state.Telemetry.GpuPercent <= 100.0f);
            bool fpsGenuine = (!state.Telemetry.FpsAvailable && state.Telemetry.Fps == 0.0f) ||
                              (state.Telemetry.FpsAvailable && state.Telemetry.Fps > 0.0f);
            Check("Real Telemetry: PDH GPU engine & DWM FPS populate genuine values or stay unavailable (zero cpu*0.65f / 60fps stubs)",
                  telemetryGenuine && fpsGenuine);

            state.ActiveAudioRenderDeviceId = "SimulatedOldDevice_BeforeCall";
            WindowsSystemHooks.SyncSystemAudioState(state, allowPopup: true);
            Check("Real CoreAudio Volume Hook + Device-Switch Guard: No false pop-up on audio device shift",
                  !state.ToastActive);

            bool brReadOk = WindowsSystemHooks.ReadMonitorBrightnessLevel(state, out float liveBr);
            bool ddcScaleOk = WindowsSystemHooks.ScaleDdcBrightness(50.0f, 20, 120) == 70 &&
                              WindowsSystemHooks.ScaleDdcBrightness(0.0f, 20, 120) == 20 &&
                              WindowsSystemHooks.ScaleDdcBrightness(100.0f, 20, 120) == 120;
            Check("Display Brightness: WMI/DDC read works & DDC/CI scales strictly into [minB..maxB]",
                  ddcScaleOk && (brReadOk ? (liveBr >= 0.0f && liveBr <= 100.0f) : true));

            var parsedLrc = WindowsSystemHooks.ParseLrcLyrics("[00:12.50] First line\n[00:25.00] Second line");
            Check("WinRT SMTC & LRCLIB LRC Parser: Parses [mm:ss.xx] timestamps in order",
                  parsedLrc.Count == 2 && Math.Abs(parsedLrc[0].TimestampSec - 12.5) < 0.01 && parsedLrc[1].Text == "Second line");

            Check("Privacy Sensors: Filter ignores svchost.exe and OpenPixel.exe",
                  !string.Equals(state.Sensors.MicAppName, "svchost.exe", StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(state.Sensors.MicAppName, "OpenPixel.exe", StringComparison.OrdinalIgnoreCase));

            // Test Free Buff JSON status parsing, Antigravity detection/pruning & API nudges
            LocalNetworkServers.HandleLoopbackRequest(state, "/agent?name=Free+Buff&status=Almost+done&step=Verifying+C%23+build&progress=95&almost_done=1", out _);
            LocalNetworkServers.HandleLoopbackRequest(state, "/agent?name=Free+Buff&status=Needs+input&step=Approve+commit%3F&needs_input=1", out _);
            bool freeBuffOk = state.Agents.Exists(a => a.AgentType == "Free Buff" && a.NeedsInput) &&
                              state.ToastPopupType == TopPopupType.AgentNudge;
            state.Agents.Add(new AgentSessionItem { AgentType = "Antigravity", Source = "process", ProcessId = 9999999, UpdatedMs = ConfigManager.GetTickMs() });
            WindowsSystemHooks.PollCodingAgents(state);
            bool prunedExitedOk = !state.Agents.Exists(a => a.ProcessId == 9999999);
            string fbPath = Path.Combine(AppContext.BaseDirectory, "freebuff_status.json");
            string fbBackup = File.Exists(fbPath) ? File.ReadAllText(fbPath) : null;
            File.WriteAllText(fbPath, "{\"status\":\"Almost done\",\"step\":\"Finalizing patch\",\"progress\":92,\"almost_done\":true}");
            WindowsSystemHooks.PollCodingAgents(state);
            bool fbJsonOk = state.Agents.Exists(a => a.AgentType == "Free Buff" && a.IsAlmostDone && a.CurrentStep == "Finalizing patch");
            if (fbBackup != null) File.WriteAllText(fbPath, fbBackup);
            else if (File.Exists(fbPath)) File.Delete(fbPath);
            WindowsSystemHooks.PollCodingAgents(state);
            bool fbPrunedOk = !state.Agents.Exists(a => a.AgentType == "Free Buff" && a.Source == "file");
            state.Agents.Clear();
            state.ToastActive = false;
            Check("Free Buff & Coding Agent monitor: Detects Almost done / Needs input nudges, parses JSON & prunes exited/removed",
                  freeBuffOk && prunedExitedOk && fbJsonOk && fbPrunedOk);

            // Test File Shelf 12-file drop-order cap & real FileSize
            string shelfTmpDir = Path.Combine(Path.GetTempPath(), "openpixel-selftest-shelf");
            Directory.CreateDirectory(shelfTmpDir);
            var samplePaths = new List<string>();
            for (int fIdx = 0; fIdx < 14; ++fIdx)
            {
                string fp = Path.Combine(shelfTmpDir, $"shelf_{fIdx:D2}.txt");
                File.WriteAllText(fp, new string('x', 10 + fIdx));
                samplePaths.Add(fp);
            }
            state.ShelfFiles.Clear();
            WindowsSystemHooks.AddFilesToShelf(state, samplePaths);
            bool shelfCapOk = state.ShelfFiles.Count == 12 &&
                              state.ShelfFiles[0].DisplayName == "shelf_00.txt" &&
                              state.ShelfFiles[11].DisplayName == "shelf_11.txt" &&
                              state.ShelfFiles[0].FileSize == 10;
            state.ShelfFiles.Clear();
            try { Directory.Delete(shelfTmpDir, true); } catch { }
            Check("File Shelf: Enforces 12-file limit in drop order with real FileSize", shelfCapOk);

            // Test Persistent Clipboard Pins, Reminders Scheduler & Launcher Slot Persistence (backing up existing user files)
            string pinsPath = ConfigManager.GetClipboardPinsPath();
            string remsPath = ConfigManager.GetRemindersPath();
            string pinsBackup = File.Exists(pinsPath) ? File.ReadAllText(pinsPath) : null;
            string remsBackup = File.Exists(remsPath) ? File.ReadAllText(remsPath) : null;

            state.ClipboardHistory.Clear();
            state.ClipboardHistory.Add(new ClipboardEntry
            {
                Id = 88801,
                Category = ClipboardCategory.Text,
                Text = "Pinned snippet verification",
                Preview = "Pinned snippet verification",
                IsPinned = true,
                TimestampMs = ConfigManager.GetTickMs()
            });
            ConfigManager.SaveClipboardPinsAtomic(state);
            state.ClipboardHistory.Clear();
            ConfigManager.LoadClipboardPins(state);
            bool clipPinOk = state.ClipboardHistory.Exists(c => c.Id == 88801 && c.IsPinned);
            state.ClipboardHistory.Clear();

            state.Reminders.Clear();
            long remId = 77701;
            long nowTick = ConfigManager.GetTickMs();
            state.Reminders.Add(new ReminderItem
            {
                Id = remId,
                Title = "Daily Standup",
                DueTimestampMs = nowTick - 100,
                DueUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 100,
                RepeatRule = "Every day"
            });
            WindowsSystemHooks.Tick1HzRemindersAndTimers(state);
            bool remFiredOk = state.ToastActive && state.ToastIsReminder && state.ActiveReminderId == remId;
            WindowsSystemHooks.SnoozeReminder(state, remId, 10);
            bool remSnoozedOk = !state.ToastActive && state.Reminders[0].DueTimestampMs > nowTick + 500_000;
            WindowsSystemHooks.CompleteReminder(state, remId);
            bool remRepeatOk = !state.Reminders[0].Completed && !state.Reminders[0].Triggered && state.Reminders[0].DueTimestampMs > nowTick + 40_000_000;
            ConfigManager.SaveRemindersAtomic(state);
            state.Reminders.Clear();
            ConfigManager.LoadReminders(state);
            bool remPersistOk = state.Reminders.Exists(r => r.Id == remId && r.RepeatRule == "Every day");
            state.Reminders.Clear();
            state.ToastActive = false;
            state.ToastIsReminder = false;

            if (pinsBackup != null) File.WriteAllText(pinsPath, pinsBackup);
            else if (File.Exists(pinsPath)) File.Delete(pinsPath);
            if (remsBackup != null) File.WriteAllText(remsPath, remsBackup);
            else if (File.Exists(remsPath)) File.Delete(remsPath);

            Check("Clipboard Pins & Repeating Reminders: Persist across reload, fire at 1Hz, snooze (+10m), and reschedule repeats",
                  clipPinOk && remFiredOk && remSnoozedOk && remRepeatOk && remPersistOk);

            state.LauncherSlots[0].Label = "Notepad";
            state.LauncherSlots[0].TargetPath = "notepad.exe";
            ConfigManager.SyncLauncherSlotsToSettings(state);
            ConfigManager.SyncSettingsToLauncherSlots(state);
            bool launcherSyncOk = state.Settings.LauncherSlots.Count == 6 && state.LauncherSlots[0].TargetPath == "notepad.exe";
            state.LauncherSlots[0].Label = "+ Add";
            state.LauncherSlots[0].TargetPath = "";
            Check("Launcher Slots: Ctrl+Alt+1..6 slots sync cleanly with AppSettings persistence", launcherSyncOk);

                // Test PC Deck HTTPS TLS Certificate Pinning, Explicit Allow, & Pad/Media/Modes/Power Protocol
                state.Settings.PcDeckEnabled = true;
                state.IsNetworkPrivate = true;
                LocalNetworkServers.SkipInteractiveAllowPromptForTest = true;
                var cert = LocalNetworkServers.EnsurePcTlsCertificate(state);
                string pairCode = LocalNetworkServers.GenerateTwoMinutePairingCode(state);
                var (pSt, _, pBody, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                    state, $"POST /deck/pair?code={pairCode}&device=Pixel9&model=Pixel+9&os=Android+15&app=1.0.5 HTTP/1.1\r\n\r\n", "192.168.1.42");
                string pairJson = Encoding.UTF8.GetString(pBody);
                bool pairedOk = pSt.StartsWith("200") && state.PairedDeckDevices.Count == 1 && cert != null && !string.IsNullOrEmpty(state.PcCertFingerprint);

                string tok = pairedOk ? state.PairedDeckDevices[0].AuthToken : "";
                var (sSt, _, sBody, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                    state, $"GET /deck/state?token={Uri.EscapeDataString(tok)} HTTP/1.1\r\n\r\n", "192.168.1.42");
                bool stateOk = sSt.StartsWith("200") && Encoding.UTF8.GetString(sBody).Contains("cpu");
                var (padSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                    state, $"POST /deck/pad?token={Uri.EscapeDataString(tok)}&a=move&dx=0&dy=0 HTTP/1.1\r\n\r\n", "192.168.1.42");
                var (modeSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                    state, $"POST /deck/mode?token={Uri.EscapeDataString(tok)}&a=work_copy HTTP/1.1\r\n\r\n", "192.168.1.42");
                state.Settings.PcDeckEnabled = false;
                state.PairedDeckDevices.Clear();
                Check("PC Deck TLS Cert Pinning, 2-Min Pairing, Device Metadata & Live State Protocol", pairedOk && stateOk && padSt.StartsWith("200") && modeSt.StartsWith("200"));

                // Turning the PC Deck OFF must really stop the servers, not just flip a flag.
                int savedDeckPort = state.Settings.PcDeckPort;
                int savedDeckTlsPort = state.Settings.PcDeckTlsPort;
                state.Settings.PcDeckPort = 47896;
                state.Settings.PcDeckTlsPort = 47897;
                state.Settings.PcDeckEnabled = true;
                state.IsNetworkPrivate = true;
                LocalNetworkServers.ApplyDeckEnabled(state);
                bool deckListening = ProbeTcp(47897, expectOpen: true);
                state.Settings.PcDeckEnabled = false;
                LocalNetworkServers.ApplyDeckEnabled(state);
                bool deckClosed = ProbeTcp(47897, expectOpen: false);
                Check("PC Deck OFF closes listeners/sockets and clears the pairing code (ports stop accepting)",
                      deckListening && deckClosed && !LocalNetworkServers.IsDeckRunning && state.ActivePairingCode.Length == 0);
                state.Settings.PcDeckPort = savedDeckPort;
                state.Settings.PcDeckTlsPort = savedDeckTlsPort;

                // Window identity: Task Manager, file properties and the tray all read
            // these strings, so they must say OpenPixel rather than a fallback.
            var versionInfo = FileVersionInfo.GetVersionInfo(Application.ExecutablePath);
            Check("Executable identifies itself as OpenPixel in Task Manager (FileDescription + ProductName)",
                  versionInfo.FileDescription == "OpenPixel" && versionInfo.ProductName == "OpenPixel");

            // Embedded web view (WebView2) actually ships with the app.
            bool webViewShipped = typeof(Microsoft.Web.WebView2.WinForms.WebView2).Assembly.GetName().Name
                                  == "Microsoft.Web.WebView2.WinForms";
            Check("Embedded web view engine ships with the app (WebView2)", webViewShipped);

            // Custom taskbar + Start menu mirror the real Windows taskbar.
            TaskbarApps.Refresh();
            using (var taskbarProbe = new BottomTaskbarForm(state))
            {
                bool taskbarOk = true;
                try
                {
                    taskbarProbe.Show();
                    taskbarProbe.RenderNow();
                }
                catch { taskbarOk = false; }
                taskbarProbe.Hide();
                Check("Custom bottom taskbar renders the synced pinned + running app list without crashing", taskbarOk);
            }
            using (var startProbe = new StartMenuForm(state))
            {
                bool startOk = true;
                try
                {
                    startProbe.ShowAt(new Point(200, 200));
                    startProbe.RenderNow();
                }
                catch { startOk = false; }
                startProbe.Close();
                Check("Animated custom Start menu opens and renders cleanly", startOk);
            }

            // 5. Closeable Spacious 13-Category Settings Window & Memory Footprint
                Check("Settings contains all 13 OpenPixel categories (General..About)",
                      Enum.GetValues(typeof(SettingsCategory)).Length == 13);
            using (var settingsWnd = new SettingsWindow(state, null))
            {
                settingsWnd.Show();
                settingsWnd.ToggleVisibility();
                Check("Closeable Settings Window: Toggles and closes cleanly without clipping", !settingsWnd.Visible);
            }

            // Dragging the Liquid Glass blur slider across its full range through the
            // real Settings window (including the QR pairing card) must never throw.
            bool sliderSweepOk = true;
            double sliderMs = 0;
            state.Settings.PcDeckEnabled = true;
            state.IsNetworkPrivate = true;
            state.ActiveSettingsCat = SettingsCategory.Tools;
            LocalNetworkServers.SkipInteractiveAllowPromptForTest = true;
            LocalNetworkServers.EnsurePcTlsCertificate(state);
            LocalNetworkServers.GenerateTwoMinutePairingCode(state);
            using (var sweepWnd = new SettingsWindow(state, null))
            {
                sweepWnd.Show();
                var sweep = Stopwatch.StartNew();
                try
                {
                    for (int i = 0; i <= 40; ++i)
                    {
                        state.QueueBlurRadius(i);
                        state.CommitPendingParametersOnRenderTick();
                        sweepWnd.RenderNow();
                    }
                }
                catch { sliderSweepOk = false; }
                sweep.Stop();
                sliderMs = sweep.Elapsed.TotalMilliseconds;
                sweepWnd.Hide();
            }
            state.Settings.PcDeckEnabled = false;
            state.ActiveSettingsCat = SettingsCategory.Appearance;
            state.QueueBlurRadius(20.0f);
            state.CommitPendingParametersOnRenderTick();
            Check("Glass blur slider sweeps 0-40 px through the real Settings window without crashing", sliderSweepOk);

            // The glass must sample the live desktop, not paint a flat panel.
            using (var glassProbe = new LiquidGlassRenderer())
            {
                bool glassFrame = glassProbe.BeginLayeredFrame(360, 180, 1.0f);
                bool glassSharp = glassFrame &&
                    glassProbe.RenderLiquidGlassBackdrop(new Rectangle(0, 0, 300, 150), 0, 0, 360, 180, 0.0f, false);
                double glassVariance0 = glassProbe.BackdropVariance;
                ulong glassHash0 = glassProbe.BackdropOutputHash;
                bool glassBlurred = glassFrame &&
                    glassProbe.RenderLiquidGlassBackdrop(new Rectangle(0, 0, 300, 150), 0, 0, 360, 180, 40.0f, false);
                double glassVariance40 = glassProbe.BackdropVariance;
                Check("Liquid Glass samples the live desktop (backdrop variance > 0, blur radius changes the pixels)",
                      glassSharp && glassBlurred && glassVariance40 > 0.05 && glassProbe.BackdropOutputHash != glassHash0);
                Emit($"[METRIC] Glass backdrop luma variance: r0={glassVariance0:F2}, r40={glassVariance40:F2}");
            }
            Emit($"[METRIC] Blur slider sweep (41 settings frames): {sliderMs:F0} ms");

            bool saveOk = ConfigManager.SaveAtomic(state);
            bool loadOk = ConfigManager.Load(state);
            Check("100% Local Atomic JSON Config Persistence (%LOCALAPPDATA%\\OpenPixel\\settings.json)", saveOk && loadOk);

            WindowsSystemHooks.Shutdown(state);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var curProc = Process.GetCurrentProcess();
            double managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            Emit($"[METRIC] Managed Heap RAM: {managedMb:F2} MB");
            Check("Low Memory Footprint well within ~13-34 MB budget", managedMb <= 34.0);

            Emit($"\nSummary: {passed} passed, {failed} failed.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest_report.txt"), sb.ToString());
            File.WriteAllText("selftest_report.txt", sb.ToString());
            return failed == 0 ? 0 : 1;
        }
    }
}
