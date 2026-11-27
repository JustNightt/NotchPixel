using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using OpenPixel.Core;
using OpenPixel.Rendering;
using Windows.ApplicationModel;
using Windows.Devices.Radios;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace OpenPixel.Services
{
    // ========================================================================
    // Real Windows System Hooks (Zero Placeholders)
    // - CoreAudio IAudioEndpointVolume + IAudioEndpointVolumeCallback + IMMNotificationClient + IAudioMeterInformation
    // - Display Brightness (WMI WmiMonitorBrightness + scaled DDC/CI per target monitor)
    // - WinRT SMTC Media Sessions + Real Artwork + Dominant Color + Store App Icons + Seek + LRCLIB Lyrics
    // - Real PDH GPU Engine Utilization + DWM Frame-Delta FPS (with *Available flags)
    // - Win32 NonPackaged + UWP Packaged Privacy Sensors (Mic, Camera, Screen Capture)
    // - Global Multi-Mic Mute (Ctrl+Alt+M) + USB Reconnect Resilience + Muted-Mic Speaking Notice
    // - Free Buff (JsonDocument) + Claude Code, Codex, Antigravity, Hermes, Custom Agents + Exited-Process Pruning
    // - Persistent Tabbed Clipboard (Text, Links, Images + WinRT OCR, Files) & File Shelf Drag-In / Multi-Drag-Out
    // - 1 Hz Reminders Scheduler (Snooze, Done, Repeats) & Real Quick Toggles (Wi-Fi, BT, Night Light, Focus, Power)
    // - Monitor-Aware Screenshot, Translucent Region Snip, Color Picker & Media Foundation H.264 MP4 Screen Recorder
    // ========================================================================
    public static class WindowsSystemHooks
    {
        private static ulong _prevIdleTime;
        private static ulong _prevKernelTime;
        private static ulong _prevUserTime;
        private static long _prevNetInBytes;
        private static long _prevNetOutBytes;
        private static long _prevNetTickMs;

        private static short _prevCapsState;
        private static short _prevNumState;
        private static short _prevScrollState;

        private static readonly HttpClient SharedHttp = CreateSharedHttpClient();
        private static readonly object MediaLock = new();
        private static GlobalSystemMediaTransportControlsSessionManager _smtcManager;
        private static GlobalSystemMediaTransportControlsSession _activeSmtcSession;
        private static GlobalSystemMediaTransportControlsSession _subscribedSmtcSession;
        private static Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _playbackChangedHandler;
        private static Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _propsChangedHandler;
        private static Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _timelineChangedHandler;
        private static bool _smtcInitialized = false;
        private static string _cachedMediaArtKey = "";
        private static string _cachedAppIconId = "";
        private static DateTimeOffset _unknownDurTrackStartUtc = DateTimeOffset.UtcNow;
        private static CancellationTokenSource _lyricsCts;
        private static Action _uiWakeCallback;

        // PDH GPU Engine telemetry
        private static readonly object TelemetryLock = new();
        private static IntPtr _pdhQuery = IntPtr.Zero;
        private static IntPtr _pdhGpuCounter = IntPtr.Zero;
        private static bool _pdhInitialized = false;
        private static long _lastGpuPollMs = 0;
        private static long _lastAudioSyncMs = 0;
        private static long _lastDiskSampleMs = 0;
        private static float _lastDiskPct = 0.0f;

        // DWM FPS telemetry
        private static ulong _prevDwmFramesDisplayed = 0;
        private static ulong _prevDwmFramesPresented = 0;
        private static long _prevDwmTickMs = 0;
        private static long _lastDwmProgressMs = 0;

        // CoreAudio device-switch & muted-mic notice guards
        private static long _lastMutedMicNoticeMs = 0;
        private static bool _wasMicActiveLastTick = false;

        // Brightness cache
        private static int _lastBrightnessMonitorIndex = -1;
        private static long _lastBrightnessRefreshMs = 0;

        // Quick-toggle change detection (mirrors UI direct mutations to real OS hooks)
        private static bool _toggleBaselineInit = false;
        private static bool _lastSyncedWifi = true;
        private static bool _lastSyncedBt = true;
        private static bool _lastSyncedFocus = false;
        private static int _lastSyncedPower = 1;
        private static bool _lastSyncedRecording = false;
        private static bool _nightLightAppliedByApp = false;

        // Clipboard self-copy suppression
        private static long _suppressClipboardUntilMs = 0;
        private static string _lastClipboardSignature = "";

        // Screen recording (Media Foundation MP4)
        private static readonly object RecLock = new();
        private static Thread _recThread;
        private static volatile bool _recStopRequested = false;
        private static string _activeRecPath = "";

        private static HttpClient CreateSharedHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            try { client.DefaultRequestHeaders.UserAgent.ParseAdd("NotchPixel/0.5.126"); } catch { }
            return client;
        }

        public static void SetUiWakeCallback(Action callback) => _uiWakeCallback = callback;

        private static void NotifyUiWake()
        {
            try { _uiWakeCallback?.Invoke(); } catch { }
        }

        public static void Initialize(GlobalAppState state)
        {
            _prevCapsState = Win32Layered.GetKeyState(0x14);   // VK_CAPITAL
            _prevNumState = Win32Layered.GetKeyState(0x90);    // VK_NUMLOCK
            _prevScrollState = Win32Layered.GetKeyState(0x91); // VK_SCROLL

            if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            {
                _prevIdleTime = ToUInt64(idle);
                _prevKernelTime = ToUInt64(kernel);
                _prevUserTime = ToUInt64(user);
            }

            InitializePdhGpuCounter();
            CoreAudioInterop.EnsureRegistered(state, NotifyUiWake);
            SyncSystemAudioState(state, allowPopup: false);

            if (ReadMonitorBrightnessLevel(state, out float br))
            {
                lock (state.SyncRoot)
                {
                    state.MonitorBrightness = br;
                    state.BrightnessSupported = true;
                }
            }

            SyncInitialSystemToggles(state);
            InitializeSmtcAsync(state);
            PollRealMediaSession(state);
            PollCodingAgents(state);
        }

        public static void Shutdown(GlobalAppState state)
        {
            try { StopScreenRecording(state); } catch { }
            try { CoreAudioInterop.Shutdown(); } catch { }
            try { _lyricsCts?.Cancel(); } catch { }
            try
            {
                if (_nightLightAppliedByApp)
                {
                    SetNightLightEnabled(false);
                    _nightLightAppliedByApp = false;
                }
            }
            catch { }
            lock (TelemetryLock)
            {
                if (_pdhQuery != IntPtr.Zero)
                {
                    try { PdhCloseQuery(_pdhQuery); } catch { }
                    _pdhQuery = IntPtr.Zero;
                    _pdhGpuCounter = IntPtr.Zero;
                    _pdhInitialized = false;
                }
            }
        }

        private static readonly HashSet<IntPtr> _affinityApplied = new();

        // Every visible window the app owns (island, taskbar, start menu, settings)
        // must carry the same capture-exclusion affinity as the glass surface.
        public static void ApplyCaptureAffinityToOwnedWindows(GlobalAppState state)
        {
            uint ownPid = (uint)Environment.ProcessId;
            Win32Layered.EnumWindows((hwnd, _) =>
            {
                Win32Layered.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == ownPid && _affinityApplied.Add(hwnd))
                {
                    ApplyWindowCaptureAffinity(hwnd, state);
                }
                return true;
            }, IntPtr.Zero);
        }

        public static void ApplyWindowCaptureAffinity(IntPtr hwnd, GlobalAppState state)
        {
            if (hwnd == IntPtr.Zero || !Win32Layered.IsWindow(hwnd)) return;
            bool exclude;
            lock (state.SyncRoot)
            {
                // A crashed glass surface falls back to solid Black; that surface
                // exposes no wallpaper content, so it must not be capture-excluded.
                bool glassActive = !state.Settings.FallbackBlackOnGlassCrash &&
                                   (state.Settings.Theme == SurfaceTheme.LiquidGlass ||
                                    state.Settings.Theme == SurfaceTheme.CandyGlass);
                exclude = state.Settings.HideFromScreenshots || glassActive;
            }
            Win32Layered.SetWindowDisplayAffinity(hwnd, exclude ? Win32Layered.WDA_EXCLUDEFROMCAPTURE : Win32Layered.WDA_NONE);
        }

        // ====================================================================
        // Real CoreAudio Master Volume & Mute + Callbacks + Multi-Mic Mute
        // ====================================================================
        public static void SyncSystemAudioState(GlobalAppState state, bool allowPopup)
        {
            try
            {
                if (CoreAudioInterop.TryGetDefaultEndpointState(isCapture: false, out string devId, out float scalar, out bool muted))
                {
                    float newVolPct = MathF.Round(Math.Clamp(scalar, 0.0f, 1.0f) * 100.0f);
                    lock (state.SyncRoot)
                    {
                        bool deviceChanged = !string.IsNullOrEmpty(state.ActiveAudioRenderDeviceId) &&
                                             devId != state.ActiveAudioRenderDeviceId;
                        bool inSwitchGuard = CoreAudioInterop.IsInDeviceSwitchGuard();
                        bool volChanged = MathF.Abs(newVolPct - state.MasterVolume) >= 1.0f ||
                                          muted != state.MasterOutputMuted;

                        state.ActiveAudioRenderDeviceId = devId;
                        state.MasterVolume = newVolPct;
                        state.MasterOutputMuted = muted;

                        if (allowPopup && state.AudioBaselineInitialized && volChanged && !deviceChanged && !inSwitchGuard && !state.IslandOpen)
                        {
                            ShowVolumeToastLocked(state, newVolPct, muted);
                        }
                        state.AudioBaselineInitialized = true;
                    }
                }

                if (CoreAudioInterop.TryGetDefaultEndpointState(isCapture: true, out _, out _, out bool micMuted))
                {
                    lock (state.SyncRoot)
                    {
                        state.GlobalMicMuted = micMuted;
                    }
                }
            }
            catch { }
        }

        internal static void ShowVolumeToastLocked(GlobalAppState state, float volPct, bool muted)
        {
            state.ToastPopupType = TopPopupType.Volume;
            state.ToastProgress01 = muted ? 0.0f : (volPct / 100.0f);
            state.ToastTitle = muted ? "Muted" : $"{(int)volPct}%";
            state.ToastSubtitle = "Volume";
            state.ToastAccent = Color.FromArgb(255, 239, 237, 232);
            state.ToastIsReminder = false;
            state.ToastActive = true;
            state.ToastExpireMs = ConfigManager.GetTickMs() + 2200;
        }

        public static void SetMasterVolume(GlobalAppState state, float volume0to100, bool showPopup = true)
        {
            float clamped = MathF.Round(Math.Clamp(volume0to100, 0.0f, 100.0f));
            try
            {
                CoreAudioInterop.TrySetDefaultEndpointVolume(isCapture: false, clamped / 100.0f, unmuteIfPositive: clamped > 0.0f);
            }
            catch { }

            lock (state.SyncRoot)
            {
                state.MasterVolume = clamped;
                if (clamped > 0.0f) state.MasterOutputMuted = false;

                if (showPopup && !state.IslandOpen)
                {
                    ShowVolumeToastLocked(state, clamped, state.MasterOutputMuted);
                }
            }
        }

        public static void ToggleMasterOutputMute(GlobalAppState state, bool showPopup = false)
        {
            bool nextMute;
            float vol;
            lock (state.SyncRoot)
            {
                nextMute = !state.MasterOutputMuted;
                state.MasterOutputMuted = nextMute;
                vol = state.MasterVolume;
            }
            try { CoreAudioInterop.TrySetDefaultEndpointMute(isCapture: false, nextMute); } catch { }
            if (showPopup && !state.IslandOpen)
            {
                lock (state.SyncRoot) ShowVolumeToastLocked(state, vol, nextMute);
            }
        }

        public static void SetGlobalMicMute(GlobalAppState state, bool mute, bool showNotice = true)
        {
            lock (state.SyncRoot)
            {
                state.GlobalMicMuted = mute;
                if (mute) state.MicPeakLevel = 0.0f;
            }

            try
            {
                CoreAudioInterop.TrySetAllCaptureEndpointsMute(mute);
            }
            catch { }

            if (showNotice)
            {
                lock (state.SyncRoot)
                {
                    if (state.Settings.ShowGlobalMuteNotice)
                    {
                        state.ToastPopupType = TopPopupType.MicMute;
                        state.ToastTitle = mute ? "muted" : "unmuted";
                        state.ToastSubtitle = "Ctrl+Alt+M";
                        state.ToastAccent = mute ? Color.FromArgb(255, 255, 69, 58) : Color.FromArgb(255, 48, 209, 88);
                        state.ToastIsReminder = false;
                        state.ToastActive = true;
                        state.ToastExpireMs = ConfigManager.GetTickMs() + (long)(state.Settings.NoticeDurationSec * 1000.0f);
                    }
                }
            }
        }

        public static void ToggleGlobalMicMute(GlobalAppState state)
        {
            bool newMute;
            lock (state.SyncRoot) newMute = !state.GlobalMicMuted;
            SetGlobalMicMute(state, newMute, showNotice: true);
        }

        public static void PushNotification(
            GlobalAppState state, string appName, string title, string body, Color accent,
            int processId = 0, string targetUrl = "", bool showNotchToast = true)
        {
            if (state == null || (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))) return;
            long nowMs = ConfigManager.GetTickMs();
            bool inserted = false;
            lock (state.SyncRoot)
            {
                // Deduplicate identical alerts within 20 seconds so false/duplicate bursts never spam the notch
                for (int i = 0; i < Math.Min(6, state.Notifications.Count); ++i)
                {
                    var existing = state.Notifications[i];
                    if ((nowMs - existing.Id) < 20_000 &&
                        string.Equals(existing.Title, title, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Body, body, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                state.Notifications.Insert(0, new NotificationItem
                {
                    Id = nowMs,
                    AppName = appName ?? "",
                    Title = title ?? "",
                    Body = body ?? "",
                    TimeStr = DateTime.Now.ToString(state.Settings.Use24HourClock ? "HH:mm" : "h:mm tt"),
                    RelativeStr = "now",
                    AccentColor = accent,
                    ProcessId = processId,
                    TargetUrl = targetUrl ?? ""
                });
                if (state.Notifications.Count > 24) state.Notifications.RemoveAt(state.Notifications.Count - 1);
                inserted = true;

                if (showNotchToast && !state.FocusModeEnabled)
                {
                    state.ToastPopupType = TopPopupType.Notice;
                    state.ToastTitle = string.IsNullOrWhiteSpace(title) ? appName : title;
                    state.ToastSubtitle = body ?? "";
                    state.ToastAccent = accent;
                    state.ToastIsReminder = false;
                    state.ToastActive = true;
                    state.ToastExpireMs = nowMs + (long)Math.Clamp(state.Settings.NoticeDurationSec * 1000.0f, 2200.0f, 10000.0f);
                }
            }
            if (inserted) NotifyUiWake();
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        public static void OpenNotificationTarget(NotificationItem n)
        {
            if (n == null) return;
            if (!string.IsNullOrWhiteSpace(n.TargetUrl))
            {
                try { Process.Start(new ProcessStartInfo(n.TargetUrl) { UseShellExecute = true }); return; } catch { }
            }

            IntPtr hwnd = IntPtr.Zero;
            if (n.ProcessId > 0)
            {
                try
                {
                    using var p = Process.GetProcessById(n.ProcessId);
                    if (p.MainWindowHandle != IntPtr.Zero) hwnd = p.MainWindowHandle;
                }
                catch { }
            }

            if (hwnd == IntPtr.Zero && !string.IsNullOrWhiteSpace(n.AppName))
            {
                string key = n.AppName.Trim().ToLowerInvariant();
                try
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        try
                        {
                            if (p.MainWindowHandle == IntPtr.Zero) continue;
                            string pname = (p.ProcessName ?? "").ToLowerInvariant();
                            string wtitle = (p.MainWindowTitle ?? "").ToLowerInvariant();
                            if (pname.Contains(key) || key.Contains(pname) || (!string.IsNullOrEmpty(wtitle) && wtitle.Contains(key)))
                            {
                                hwnd = p.MainWindowHandle;
                                break;
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            }

            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9); // SW_RESTORE
                    Win32Layered.SetForegroundWindow(hwnd);
                }
                catch { }
            }
        }

        // ====================================================================
        // Real Display Brightness (WMI + Scaled DDC/CI per Target Monitor)
        // ====================================================================
        public static Screen GetTargetScreen(GlobalAppState state)
        {
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) return Screen.PrimaryScreen;
            int idx = 0;
            if (state != null)
            {
                lock (state.SyncRoot) idx = Math.Clamp(state.Settings.MonitorIndex, 0, screens.Length - 1);
            }
            return screens[idx];
        }

        public static uint ScaleDdcBrightness(float pct0to100, uint minB, uint maxB)
        {
            if (maxB <= minB) return minB;
            float clamped = Math.Clamp(pct0to100, 0.0f, 100.0f);
            uint scaled = minB + (uint)MathF.Round((clamped / 100.0f) * (maxB - minB));
            return Math.Clamp(scaled, minB, maxB);
        }

        public static bool ReadMonitorBrightnessLevel(out float brightness0to100)
            => ReadMonitorBrightnessLevel(GlobalAppState.Instance, out brightness0to100);

        public static bool ReadMonitorBrightnessLevel(GlobalAppState state, out float brightness0to100)
        {
            brightness0to100 = 80.0f;
            Screen target = GetTargetScreen(state);

            // 1. Try DDC/CI on the target monitor first (external monitors & supported panels)
            if (TryReadDdcBrightness(target, out float ddcBr))
            {
                brightness0to100 = ddcBr;
                return true;
            }

            // 2. Try WMI WmiMonitorBrightness (internal laptop displays)
            if (TryReadWmiBrightness(target, out float wmiBr))
            {
                brightness0to100 = wmiBr;
                return true;
            }

            return false;
        }

        public static void SetMonitorBrightnessLevel(GlobalAppState state, float brightness0to100, bool showPopup = true)
        {
            float clamped = MathF.Round(Math.Clamp(brightness0to100, 0.0f, 100.0f));
            Screen target = GetTargetScreen(state);

            bool written = TryWriteDdcBrightness(target, clamped);
            if (!written) written = TryWriteWmiBrightness(target, (byte)clamped);
            else TryWriteWmiBrightnessIfMatched(target, (byte)clamped);

            lock (state.SyncRoot)
            {
                state.MonitorBrightness = clamped;
                if (written) state.BrightnessSupported = true;
                if (showPopup && !state.IslandOpen)
                {
                    state.ToastPopupType = TopPopupType.Brightness;
                    state.ToastProgress01 = clamped / 100.0f;
                    state.ToastTitle = $"{(int)clamped}%";
                    state.ToastSubtitle = "Brightness";
                    state.ToastAccent = Color.FromArgb(255, 239, 237, 232);
                    state.ToastIsReminder = false;
                    state.ToastActive = true;
                    state.ToastExpireMs = ConfigManager.GetTickMs() + 2200;
                }
            }
        }

        private static IntPtr GetHMonitorForScreen(Screen scr)
        {
            if (scr == null) return MonitorFromPoint(new Win32Layered.POINT { x = 0, y = 0 }, 2);
            var pt = new Win32Layered.POINT
            {
                x = scr.Bounds.Left + scr.Bounds.Width / 2,
                y = scr.Bounds.Top + scr.Bounds.Height / 2
            };
            return MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        }

        private static bool TryReadDdcBrightness(Screen target, out float brightness0to100)
        {
            brightness0to100 = 80.0f;
            try
            {
                IntPtr hMon = GetHMonitorForScreen(target);
                if (hMon != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint num) && num > 0)
                {
                    var phys = new PHYSICAL_MONITOR[num];
                    bool ok = false;
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, num, phys))
                    {
                        for (uint i = 0; i < num; ++i)
                        {
                            if (GetMonitorBrightness(phys[i].hPhysicalMonitor, out uint minB, out uint curB, out uint maxB) && maxB > minB)
                            {
                                brightness0to100 = MathF.Round(((float)(curB - minB) * 100.0f) / (maxB - minB));
                                ok = true;
                                break;
                            }
                        }
                        DestroyPhysicalMonitors(num, phys);
                    }
                    if (ok) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TryWriteDdcBrightness(Screen target, float clamped0to100)
        {
            try
            {
                IntPtr hMon = GetHMonitorForScreen(target);
                if (hMon != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint num) && num > 0)
                {
                    var phys = new PHYSICAL_MONITOR[num];
                    bool anyOk = false;
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, num, phys))
                    {
                        for (uint i = 0; i < num; ++i)
                        {
                            if (GetMonitorBrightness(phys[i].hPhysicalMonitor, out uint minB, out uint _, out uint maxB) && maxB > minB)
                            {
                                uint scaled = ScaleDdcBrightness(clamped0to100, minB, maxB);
                                if (SetMonitorBrightness(phys[i].hPhysicalMonitor, scaled)) anyOk = true;
                            }
                        }
                        DestroyPhysicalMonitors(num, phys);
                    }
                    return anyOk;
                }
            }
            catch { }
            return false;
        }

        private static string GetMonitorHardwareTag(Screen scr)
        {
            if (scr == null || string.IsNullOrEmpty(scr.DeviceName)) return "";
            try
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (EnumDisplayDevicesW(scr.DeviceName, 0, ref dd, 0) && !string.IsNullOrEmpty(dd.DeviceID))
                {
                    // DeviceID looks like "MONITOR\BOE0B33\{guid}\0001"
                    string[] parts = dd.DeviceID.Split('\\');
                    if (parts.Length >= 2) return parts[1];
                }
            }
            catch { }
            return "";
        }

        private static bool TryReadWmiBrightness(Screen target, out float brightness0to100)
        {
            brightness0to100 = 80.0f;
            try
            {
                string hwTag = GetMonitorHardwareTag(target);
                var scope = new ManagementScope(@"\\.\root\wmi");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness"));
                using var results = searcher.Get();

                float fallbackVal = -1;
                int count = 0;
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        count++;
                        string inst = obj["InstanceName"]?.ToString() ?? "";
                        float val = Convert.ToSingle(obj["CurrentBrightness"]);
                        if (!string.IsNullOrEmpty(hwTag) && inst.Contains(hwTag, StringComparison.OrdinalIgnoreCase))
                        {
                            brightness0to100 = Math.Clamp(val, 0.0f, 100.0f);
                            return true;
                        }
                        if (fallbackVal < 0) fallbackVal = val;
                    }
                }
                if (fallbackVal >= 0 && (target == null || target.Primary || count == 1))
                {
                    brightness0to100 = Math.Clamp(fallbackVal, 0.0f, 100.0f);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TryWriteWmiBrightnessIfMatched(Screen target, byte level)
        {
            string hwTag = GetMonitorHardwareTag(target);
            if (string.IsNullOrEmpty(hwTag)) return false;
            return TryWriteWmiBrightnessInternal(target, level, requireMatch: true);
        }

        private static bool TryWriteWmiBrightness(Screen target, byte level)
            => TryWriteWmiBrightnessInternal(target, level, requireMatch: false);

        private static bool TryWriteWmiBrightnessInternal(Screen target, byte level, bool requireMatch)
        {
            try
            {
                string hwTag = GetMonitorHardwareTag(target);
                var scope = new ManagementScope(@"\\.\root\wmi");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
                using var results = searcher.Get();

                ManagementObject fallbackObj = null;
                int count = 0;
                foreach (ManagementObject obj in results)
                {
                    count++;
                    string inst = obj["InstanceName"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(hwTag) && inst.Contains(hwTag, StringComparison.OrdinalIgnoreCase))
                    {
                        using (obj)
                        {
                            using var inParams = obj.GetMethodParameters("WmiSetBrightness");
                            inParams["Timeout"] = 0u;
                            inParams["Brightness"] = level;
                            obj.InvokeMethod("WmiSetBrightness", inParams, null);
                            fallbackObj?.Dispose();
                            return true;
                        }
                    }
                    if (fallbackObj == null) fallbackObj = obj;
                    else obj.Dispose();
                }
                if (fallbackObj != null)
                {
                    using (fallbackObj)
                    {
                        if (!requireMatch && (target == null || target.Primary || count == 1))
                        {
                            using var inParams = fallbackObj.GetMethodParameters("WmiSetBrightness");
                            inParams["Timeout"] = 0u;
                            inParams["Brightness"] = level;
                            fallbackObj.InvokeMethod("WmiSetBrightness", inParams, null);
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        // ====================================================================
        // Real Quick Toggles (Night Light, Wi-Fi, Bluetooth, Focus Mode, Power)
        // ====================================================================
        public static unsafe void SetNightLightEnabled(bool enabled)
        {
            _nightLightAppliedByApp = enabled;
            try
            {
                IntPtr hdc = Win32Layered.GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    ushort* ramp = stackalloc ushort[3 * 256];
                    for (int i = 0; i < 256; ++i)
                    {
                        int val = i * 257;
                        ramp[i] = (ushort)val;
                        ramp[256 + i] = (ushort)(enabled ? (val * 88 / 100) : val);
                        ramp[512 + i] = (ushort)(enabled ? (val * 72 / 100) : val);
                    }
                    Win32Layered.SetDeviceGammaRamp(hdc, ramp);
                    Win32Layered.ReleaseDC(IntPtr.Zero, hdc);
                }
            }
            catch { }

            // Also update Windows CloudStore Night Light flag when accessible
            try
            {
                const string regPath = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(regPath, writable: true);
                if (key?.GetValue("Data") is byte[] data && data.Length > 24)
                {
                    bool currentlyOn = data[18] == 0x15;
                    if (currentlyOn != enabled)
                    {
                        byte[] next = (byte[])data.Clone();
                        if (enabled)
                        {
                            if (next.Length > 25)
                            {
                                Array.Resize(ref next, next.Length + 2);
                                System.Buffer.BlockCopy(next, 23, next, 25, next.Length - 25);
                                next[18] = 0x15;
                                next[23] = 0x10;
                                next[24] = 0x00;
                            }
                        }
                        else if (next.Length > 26)
                        {
                            System.Buffer.BlockCopy(next, 25, next, 23, next.Length - 25);
                            Array.Resize(ref next, next.Length - 2);
                            next[18] = 0x13;
                        }
                        key.SetValue("Data", next, RegistryValueKind.Binary);
                    }
                }
            }
            catch { }
        }

        private static bool ReadWindowsNightLightEnabled()
        {
            try
            {
                const string regPath = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(regPath);
                if (key?.GetValue("Data") is byte[] data && data.Length > 18)
                    return data[18] == 0x15;
            }
            catch { }
            return false;
        }

        public static void SetWifiEnabled(GlobalAppState state, bool enabled)
        {
            lock (state.SyncRoot) state.WifiEnabled = enabled;
            _lastSyncedWifi = enabled;
            _ = Task.Run(async () =>
            {
                bool done = await TrySetWinRtRadioAsync(RadioKind.WiFi, enabled).ConfigureAwait(false);
                if (!done)
                {
                    try
                    {
                        var psi = new ProcessStartInfo("netsh", $"interface set interface name=\"Wi-Fi\" admin={(enabled ? "enabled" : "disabled")}")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(3000);
                    }
                    catch { }
                }
            });
        }

        public static void SetBluetoothEnabled(GlobalAppState state, bool enabled)
        {
            lock (state.SyncRoot) state.BluetoothEnabled = enabled;
            _lastSyncedBt = enabled;
            _ = Task.Run(async () =>
            {
                await TrySetWinRtRadioAsync(RadioKind.Bluetooth, enabled).ConfigureAwait(false);
            });
        }

        private static async Task<bool> TrySetWinRtRadioAsync(RadioKind kind, bool enabled)
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access != RadioAccessStatus.Allowed) return false;
                var radios = await Radio.GetRadiosAsync();
                bool any = false;
                foreach (var r in radios)
                {
                    if (r.Kind == kind)
                    {
                        var res = await r.SetStateAsync(enabled ? RadioState.On : RadioState.Off);
                        if (res == RadioAccessStatus.Allowed) any = true;
                    }
                }
                return any;
            }
            catch { return false; }
        }

        public static void SetFocusModeEnabled(GlobalAppState state, bool enabled)
        {
            lock (state.SyncRoot) state.FocusModeEnabled = enabled;
            _lastSyncedFocus = enabled;
            try
            {
                // Publish Windows Quiet Hours / Focus Assist profile state (0=Off, 1=PriorityOnly)
                ulong wnfQuietHours = 0xD83063EA3BC1035UL; // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
                int profile = enabled ? 1 : 0;
                RtlPublishWnfStateData(wnfQuietHours, IntPtr.Zero, ref profile, 4, IntPtr.Zero);
            }
            catch { }
        }

        public static void SetPowerMode(GlobalAppState state, int modeIndex)
        {
            int idx = ((modeIndex % 3) + 3) % 3;
            lock (state.SyncRoot) state.PowerModeIndex = idx;
            _lastSyncedPower = idx;

            try
            {
                // 0 = Eco / Better Battery, 1 = Balanced, 2 = Best Performance
                Guid overlayGuid = idx switch
                {
                    0 => new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"),
                    2 => new Guid("ded574b5-45a0-4f42-8737-46345c09c238"),
                    _ => Guid.Empty
                };
                uint rc = PowerSetActiveOverlayScheme(overlayGuid);
                if (rc != 0)
                {
                    Guid scheme = idx switch
                    {
                        0 => new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"),
                        2 => new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
                        _ => new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")
                    };
                    PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                }
            }
            catch { }
        }

        private static void SyncInitialSystemToggles(GlobalAppState state)
        {
            bool night = ReadWindowsNightLightEnabled();
            int powerIdx = 1;
            try
            {
                if (PowerGetEffectiveOverlayScheme(out Guid eff) == 0)
                {
                    if (eff == new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")) powerIdx = 0;
                    else if (eff == new Guid("ded574b5-45a0-4f42-8737-46345c09c238")) powerIdx = 2;
                    else powerIdx = 1;
                }
            }
            catch { }

            bool wifi = true;
            bool bt = true;
            try
            {
                var t = Task.Run(async () =>
                {
                    try
                    {
                        var radios = await Radio.GetRadiosAsync();
                        bool foundW = false, foundB = false;
                        foreach (var r in radios)
                        {
                            if (r.Kind == RadioKind.WiFi) { wifi = r.State == RadioState.On; foundW = true; }
                            if (r.Kind == RadioKind.Bluetooth) { bt = r.State == RadioState.On; foundB = true; }
                        }
                        lock (state.SyncRoot)
                        {
                            if (foundW) state.WifiEnabled = wifi;
                            if (foundB) state.BluetoothEnabled = bt;
                            _lastSyncedWifi = state.WifiEnabled;
                            _lastSyncedBt = state.BluetoothEnabled;
                        }
                    }
                    catch { }
                });
                t.Wait(350);
            }
            catch { }

            lock (state.SyncRoot)
            {
                state.NightLightEnabled = night;
                state.PowerModeIndex = powerIdx;
                _lastSyncedWifi = state.WifiEnabled;
                _lastSyncedBt = state.BluetoothEnabled;
                _lastSyncedFocus = state.FocusModeEnabled;
                _lastSyncedPower = state.PowerModeIndex;
                _lastSyncedRecording = state.IsRecordingScreen;
                _toggleBaselineInit = true;
            }
        }

        public static void SyncSystemTogglesIfChanged(GlobalAppState state)
        {
            if (state == null || !_toggleBaselineInit) return;
            bool curW, curB, curF, curR;
            int curP;
            lock (state.SyncRoot)
            {
                curW = state.WifiEnabled;
                curB = state.BluetoothEnabled;
                curF = state.FocusModeEnabled;
                curP = state.PowerModeIndex;
                curR = state.IsRecordingScreen;
            }

            if (curW != _lastSyncedWifi) SetWifiEnabled(state, curW);
            if (curB != _lastSyncedBt) SetBluetoothEnabled(state, curB);
            if (curF != _lastSyncedFocus) SetFocusModeEnabled(state, curF);
            if (curP != _lastSyncedPower) SetPowerMode(state, curP);
            if (curR != _lastSyncedRecording)
            {
                if (curR) StartScreenRecording(state);
                else StopScreenRecording(state);
            }
        }

        // ====================================================================
        // Real WinRT SMTC Media Session + Artwork + Store Icons + LRCLIB Lyrics
        // ====================================================================
        private static void InitializeSmtcAsync(GlobalAppState state)
        {
            if (_smtcInitialized) return;
            _smtcInitialized = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    lock (MediaLock) _smtcManager = mgr;
                    if (mgr != null)
                    {
                        mgr.SessionsChanged += (_, _) => _ = RefreshSmtcAsync(state);
                        mgr.CurrentSessionChanged += (_, _) => _ = RefreshSmtcAsync(state);
                        await RefreshSmtcAsync(state).ConfigureAwait(false);
                    }
                }
                catch { }
            });
        }

        public static void PollRealMediaSession(GlobalAppState state)
        {
            if (!_smtcInitialized) InitializeSmtcAsync(state);
            _ = RefreshSmtcAsync(state);
        }

        private static bool IsMusicAppExcluded(GlobalAppState state, string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return false;
            lock (state.SyncRoot)
            {
                foreach (string ex in state.Settings.ExcludedMusicApps)
                {
                    if (string.IsNullOrWhiteSpace(ex)) continue;
                    string stem = Path.GetFileNameWithoutExtension(ex);
                    if (aumid.Contains(ex, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(stem) && aumid.Contains(stem, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static async Task RefreshSmtcAsync(GlobalAppState state)
        {
            GlobalSystemMediaTransportControlsSessionManager mgr;
            lock (MediaLock) mgr = _smtcManager;
            if (mgr == null)
            {
                try
                {
                    mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    lock (MediaLock) _smtcManager = mgr;
                }
                catch { return; }
            }
            if (mgr == null) return;

            try
            {
                GlobalSystemMediaTransportControlsSession chosen = null;
                var current = mgr.GetCurrentSession();
                if (current != null && !IsMusicAppExcluded(state, current.SourceAppUserModelId))
                {
                    chosen = current;
                }
                else
                {
                    var sessions = mgr.GetSessions();
                    GlobalSystemMediaTransportControlsSession firstValid = null;
                    foreach (var sess in sessions)
                    {
                        if (sess == null || IsMusicAppExcluded(state, sess.SourceAppUserModelId)) continue;
                        firstValid ??= sess;
                        try
                        {
                            if (sess.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                chosen = sess;
                                break;
                            }
                        }
                        catch { }
                    }
                    chosen ??= firstValid;
                }

                lock (MediaLock)
                {
                    _activeSmtcSession = chosen;
                    if (!ReferenceEquals(_subscribedSmtcSession, chosen))
                    {
                        if (_subscribedSmtcSession != null)
                        {
                            try
                            {
                                if (_playbackChangedHandler != null) _subscribedSmtcSession.PlaybackInfoChanged -= _playbackChangedHandler;
                                if (_propsChangedHandler != null) _subscribedSmtcSession.MediaPropertiesChanged -= _propsChangedHandler;
                                if (_timelineChangedHandler != null) _subscribedSmtcSession.TimelinePropertiesChanged -= _timelineChangedHandler;
                            }
                            catch { }
                        }
                        _subscribedSmtcSession = chosen;
                        if (chosen != null)
                        {
                            _playbackChangedHandler = (_, _) => _ = RefreshSmtcAsync(state);
                            _propsChangedHandler = (_, _) => _ = RefreshSmtcAsync(state);
                            _timelineChangedHandler = (_, _) => _ = RefreshSmtcAsync(state);
                            try
                            {
                                chosen.PlaybackInfoChanged += _playbackChangedHandler;
                                chosen.MediaPropertiesChanged += _propsChangedHandler;
                                chosen.TimelinePropertiesChanged += _timelineChangedHandler;
                            }
                            catch { }
                        }
                    }
                }

                if (chosen == null)
                {
                    SetIdleMediaState(state);
                    return;
                }

                var props = await chosen.TryGetMediaPropertiesAsync();
                var playback = chosen.GetPlaybackInfo();
                var timeline = chosen.GetTimelineProperties();

                string title = props?.Title?.Trim() ?? "";
                string artist = props?.Artist?.Trim() ?? "";
                string album = props?.AlbumTitle?.Trim() ?? "";
                string aumid = chosen.SourceAppUserModelId ?? "";
                bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist))
                {
                    SetIdleMediaState(state);
                    return;
                }

                double durSec = 0.0;
                double posSec = 0.0;
                if (timeline != null)
                {
                    durSec = Math.Max(0.0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
                    double basePos = Math.Max(0.0, (timeline.Position - timeline.StartTime).TotalSeconds);
                    if (isPlaying && timeline.LastUpdatedTime != default)
                    {
                        basePos += Math.Max(0.0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds);
                    }
                    if (durSec > 0.1)
                    {
                        posSec = Math.Clamp(basePos, 0.0, durSec);
                    }
                    else
                    {
                        durSec = 0.0; // Never fabricate duration!
                        posSec = basePos > 0.0 ? basePos : Math.Max(0.0, (DateTimeOffset.UtcNow - _unknownDurTrackStartUtc).TotalSeconds);
                    }
                }

                string trackKey = $"{aumid}|{title}|{artist}";
                bool trackChanged;
                bool playStateChanged;
                lock (state.SyncRoot)
                {
                    trackChanged = state.Media.Title != title || state.Media.Artist != artist || state.Media.SourceAppId != aumid;
                    playStateChanged = !state.Media.HasSession || state.Media.IsPlaying != isPlaying;
                }
                if (trackChanged)
                {
                    _unknownDurTrackStartUtc = DateTimeOffset.UtcNow;
                    if (durSec <= 0.0) posSec = 0.0;
                }

                Bitmap newArt = null;
                Color domCol = Color.Empty;
                Color secCol = Color.Empty;
                if (_cachedMediaArtKey != trackKey && props?.Thumbnail != null)
                {
                    newArt = await DecodeThumbnailBitmapAsync(props.Thumbnail).ConfigureAwait(false);
                    if (newArt != null)
                    {
                        ExtractAlbumColors(newArt, out domCol, out secCol);
                    }
                    _cachedMediaArtKey = trackKey;
                }

                string friendlyAppName = null;
                Bitmap appIconBmp = null;
                if (_cachedAppIconId != aumid)
                {
                    (friendlyAppName, appIconBmp) = await ResolveSourceAppDisplayAsync(aumid).ConfigureAwait(false);
                    _cachedAppIconId = aumid;
                }

                lock (state.SyncRoot)
                {
                    state.Media.HasSession = true;
                    state.Media.IsPlaying = isPlaying;
                    state.Media.Title = title;
                    state.Media.Artist = artist;
                    state.Media.Album = album;
                    state.Media.SourceAppId = aumid;
                    if (!string.IsNullOrEmpty(friendlyAppName)) state.Media.SourceAppName = friendlyAppName;
                    else if (string.IsNullOrEmpty(state.Media.SourceAppName)) state.Media.SourceAppName = DeriveFriendlyNameFromAumid(aumid);

                    if (appIconBmp != null)
                    {
                        var oldIcon = state.Media.SourceAppIcon;
                        state.Media.SourceAppIcon = appIconBmp;
                        oldIcon?.Dispose();
                    }

                    state.Media.DurationSec = durSec;
                    state.Media.PositionSec = posSec;
                    state.PlaySpring.SetTarget(isPlaying ? 100.0f : 0.0f, state.Settings.ReduceMotion);

                    if (newArt != null)
                    {
                        var oldArt = state.Media.AlbumArt;
                        state.Media.AlbumArt = newArt;
                        oldArt?.Dispose();
                        state.Media.DominantColor = domCol;
                        state.Media.SecondaryColor = secCol;
                    }
                    else if (trackChanged && props?.Thumbnail == null)
                    {
                        var oldArt = state.Media.AlbumArt;
                        state.Media.AlbumArt = null;
                        oldArt?.Dispose();
                        state.Media.DominantColor = Color.FromArgb(255, 78, 179, 255);
                        state.Media.SecondaryColor = Color.FromArgb(255, 26, 122, 240);
                    }

                    if (trackChanged)
                    {
                        state.FlipSpring.Snap(0.0f);
                        state.FlipSpring.SetTarget(100.0f, state.Settings.ReduceMotion);
                        state.Media.Lyrics.Clear();
                        state.Media.ActiveLyricIndex = -1;
                        state.Media.LyricsTrackKey = "";
                    }

                    UpdateActiveLyricIndexLocked(state);
                }

                if (trackChanged || playStateChanged || newArt != null)
                {
                    NotifyUiWake();
                }

                if (!string.IsNullOrEmpty(title))
                {
                    string currentLyricsKey;
                    lock (state.SyncRoot) currentLyricsKey = state.Media.LyricsTrackKey;
                    if (currentLyricsKey != trackKey)
                    {
                        StartFetchLyricsAsync(state, trackKey, title, artist, album, durSec);
                    }
                }
            }
            catch { }
        }

        private static void SetIdleMediaState(GlobalAppState state)
        {
            _cachedMediaArtKey = "";
            bool wasActive;
            lock (state.SyncRoot)
            {
                wasActive = state.Media.HasSession || state.Media.IsPlaying;
                state.Media.HasSession = false;
                state.Media.IsPlaying = false;
                state.Media.Title = "";
                state.Media.Artist = "";
                state.Media.Album = "";
                state.Media.SourceAppId = "";
                state.Media.SourceAppName = "";
                state.Media.PositionSec = 0.0;
                state.Media.DurationSec = 0.0;
                state.Media.Lyrics.Clear();
                state.Media.ActiveLyricIndex = -1;
                state.Media.LyricsTrackKey = "";
                state.Media.LyricsFetching = false;
                var oldArt = state.Media.AlbumArt;
                state.Media.AlbumArt = null;
                oldArt?.Dispose();
                var oldIcon = state.Media.SourceAppIcon;
                state.Media.SourceAppIcon = null;
                oldIcon?.Dispose();
                _cachedAppIconId = "";
                state.PlaySpring.SetTarget(0.0f, state.Settings.ReduceMotion);
            }
            if (wasActive) NotifyUiWake();
        }

        private static async Task<Bitmap> DecodeThumbnailBitmapAsync(IRandomAccessStreamReference thumbRef)
        {
            try
            {
                using IRandomAccessStreamWithContentType stream = await thumbRef.OpenReadAsync();
                using Stream netStream = stream.AsStreamForRead();
                using var ms = new MemoryStream();
                await netStream.CopyToAsync(ms).ConfigureAwait(false);
                ms.Position = 0;
                using var src = new Bitmap(ms);
                return new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb)
                    .Also(dst => { using var g = Graphics.FromImage(dst); g.DrawImage(src, 0, 0, dst.Width, dst.Height); });
            }
            catch { return null; }
        }

        private static T Also<T>(this T obj, Action<T> act) { act(obj); return obj; }

        private static unsafe void ExtractAlbumColors(Bitmap bmp, out Color dominant, out Color secondary)
        {
            dominant = Color.FromArgb(255, 78, 179, 255);
            secondary = Color.FromArgb(255, 26, 122, 240);
            try
            {
                using var small = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.DrawImage(bmp, 0, 0, 24, 24);
                }
                BitmapData bd = small.LockBits(new Rectangle(0, 0, 24, 24), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var buckets = new Dictionary<int, (int weight, long r, long g, long b)>();
                    long sumR = 0, sumG = 0, sumB = 0, count = 0;
                    byte* ptr = (byte*)bd.Scan0;
                    for (int y = 0; y < 24; ++y)
                    {
                        byte* row = ptr + y * bd.Stride;
                        for (int x = 0; x < 24; ++x)
                        {
                            int b = row[x * 4 + 0];
                            int g = row[x * 4 + 1];
                            int r = row[x * 4 + 2];
                            sumR += r; sumG += g; sumB += b; count++;

                            int max = Math.Max(r, Math.Max(g, b));
                            int min = Math.Min(r, Math.Min(g, b));
                            int sat = max - min;
                            int lum = (r * 299 + g * 587 + b * 114) / 1000;
                            if (lum < 22 || lum > 240) continue;

                            int w = 1 + (sat * 4) / 255;
                            int key = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
                            if (buckets.TryGetValue(key, out var acc))
                                buckets[key] = (acc.weight + w, acc.r + r * w, acc.g + g * w, acc.b + b * w);
                            else
                                buckets[key] = (w, r * w, g * w, b * w);
                        }
                    }

                    if (buckets.Count > 0)
                    {
                        var sorted = new List<(int weight, Color col)>(buckets.Count);
                        foreach (var kv in buckets.Values)
                        {
                            sorted.Add((kv.weight, Color.FromArgb(255, (int)(kv.r / kv.weight), (int)(kv.g / kv.weight), (int)(kv.b / kv.weight))));
                        }
                        sorted.Sort((a, b) => b.weight.CompareTo(a.weight));
                        dominant = sorted[0].col;
                        secondary = sorted.Count > 1 ? sorted[1].col : ControlPaint.Dark(dominant, 0.25f);
                    }
                    else if (count > 0)
                    {
                        dominant = Color.FromArgb(255, (int)(sumR / count), (int)(sumG / count), (int)(sumB / count));
                        secondary = ControlPaint.Dark(dominant, 0.2f);
                    }
                }
                finally { small.UnlockBits(bd); }
            }
            catch { }
        }

        private static string DeriveFriendlyNameFromAumid(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return "";
            if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(aumid);
            int bang = aumid.IndexOf('!');
            string pkg = bang > 0 ? aumid.Substring(0, bang) : aumid;
            int under = pkg.IndexOf('_');
            if (under > 0) pkg = pkg.Substring(0, under);
            int dot = pkg.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < pkg.Length) pkg = pkg.Substring(dot + 1);
            return pkg;
        }

        private static async Task<(string name, Bitmap icon)> ResolveSourceAppDisplayAsync(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return ("", null);
            try
            {
                if (aumid.Contains('!'))
                {
                    var info = AppInfo.GetFromAppUserModelId(aumid);
                    if (info != null)
                    {
                        string name = info.DisplayInfo?.DisplayName ?? DeriveFriendlyNameFromAumid(aumid);
                        Bitmap bmp = null;
                        try
                        {
                            var logoRef = info.DisplayInfo?.GetLogo(new Windows.Foundation.Size(48, 48));
                            if (logoRef != null) bmp = await DecodeThumbnailBitmapAsync(logoRef).ConfigureAwait(false);
                        }
                        catch { }
                        return (name, bmp);
                    }
                }
            }
            catch { }

            try
            {
                string stem = Path.GetFileNameWithoutExtension(aumid);
                if (!string.IsNullOrEmpty(stem))
                {
                    var procs = Process.GetProcessesByName(stem);
                    foreach (var p in procs)
                    {
                        try
                        {
                            string path = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            {
                                string desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                                using Icon ic = Icon.ExtractAssociatedIcon(path);
                                return (string.IsNullOrWhiteSpace(desc) ? stem : desc.Trim(), ic?.ToBitmap());
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
            }
            catch { }

            return (DeriveFriendlyNameFromAumid(aumid), null);
        }

        private static void StartFetchLyricsAsync(GlobalAppState state, string trackKey, string title, string artist, string album, double durationSec)
        {
            CancellationTokenSource nextCts;
            lock (MediaLock)
            {
                _lyricsCts?.Cancel();
                _lyricsCts?.Dispose();
                _lyricsCts = new CancellationTokenSource();
                nextCts = _lyricsCts;
            }
            lock (state.SyncRoot)
            {
                state.Media.LyricsTrackKey = trackKey;
                state.Media.LyricsFetching = true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var url = new StringBuilder("https://lrclib.net/api/get?track_name=");
                    url.Append(Uri.EscapeDataString(title));
                    if (!string.IsNullOrWhiteSpace(artist)) url.Append("&artist_name=").Append(Uri.EscapeDataString(artist));
                    if (!string.IsNullOrWhiteSpace(album)) url.Append("&album_name=").Append(Uri.EscapeDataString(album));
                    if (durationSec > 1.0) url.Append("&duration=").Append((int)Math.Round(durationSec));

                    using var resp = await SharedHttp.GetAsync(url.ToString(), nextCts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        lock (state.SyncRoot) state.Media.LyricsFetching = false;
                        return;
                    }
                    string json = await resp.Content.ReadAsStringAsync(nextCts.Token).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    string synced = doc.RootElement.TryGetProperty("syncedLyrics", out var sProp) && sProp.ValueKind == JsonValueKind.String
                        ? sProp.GetString() : null;
                    string plain = doc.RootElement.TryGetProperty("plainLyrics", out var pProp) && pProp.ValueKind == JsonValueKind.String
                        ? pProp.GetString() : null;

                    List<LyricLine> parsed = !string.IsNullOrWhiteSpace(synced)
                        ? ParseLrcLyrics(synced)
                        : ParsePlainLyrics(plain);

                    lock (state.SyncRoot)
                    {
                        if (state.Media.LyricsTrackKey == trackKey)
                        {
                            state.Media.Lyrics.Clear();
                            state.Media.Lyrics.AddRange(parsed);
                            state.Media.LyricsFetching = false;
                            UpdateActiveLyricIndexLocked(state);
                        }
                    }
                }
                catch
                {
                    lock (state.SyncRoot) state.Media.LyricsFetching = false;
                }
            }, nextCts.Token);
        }

        private static readonly Regex LrcTagRegex = new(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]", RegexOptions.Compiled);

        public static List<LyricLine> ParseLrcLyrics(string lrcText)
        {
            var list = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(lrcText)) return list;
            foreach (string rawLine in lrcText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var matches = LrcTagRegex.Matches(line);
                if (matches.Count == 0) continue;
                string lyricText = LrcTagRegex.Replace(line, "").Trim();
                if (string.IsNullOrEmpty(lyricText)) lyricText = "♪";
                foreach (Match m in matches)
                {
                    if (!int.TryParse(m.Groups[1].Value, out int min)) continue;
                    if (!int.TryParse(m.Groups[2].Value, out int sec)) continue;
                    double frac = 0.0;
                    if (m.Groups[3].Success)
                    {
                        string fStr = m.Groups[3].Value;
                        if (double.TryParse(fStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out double fVal))
                            frac = fVal / Math.Pow(10.0, fStr.Length);
                    }
                    list.Add(new LyricLine { TimestampSec = min * 60.0 + sec + frac, Text = lyricText });
                }
            }
            list.Sort((a, b) => a.TimestampSec.CompareTo(b.TimestampSec));
            return list;
        }

        private static List<LyricLine> ParsePlainLyrics(string plainText)
        {
            var list = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(plainText)) return list;
            foreach (string rawLine in plainText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!string.IsNullOrEmpty(line))
                    list.Add(new LyricLine { TimestampSec = -1.0, Text = line });
            }
            return list;
        }

        private static void UpdateActiveLyricIndexLocked(GlobalAppState state)
        {
            var lyrics = state.Media.Lyrics;
            if (lyrics.Count == 0) { state.Media.ActiveLyricIndex = -1; return; }
            double pos = state.Media.PositionSec;
            int active = -1;
            for (int i = 0; i < lyrics.Count; ++i)
            {
                if (lyrics[i].TimestampSec >= 0.0 && pos >= lyrics[i].TimestampSec)
                    active = i;
                else if (lyrics[i].TimestampSec > pos)
                    break;
            }
            state.Media.ActiveLyricIndex = active;
        }

        public static void MediaTogglePlayPause(GlobalAppState state)
        {
            GlobalSystemMediaTransportControlsSession sess;
            lock (MediaLock) sess = _activeSmtcSession;

            lock (state.SyncRoot)
            {
                if (!state.Media.HasSession) return;
                state.Media.IsPlaying = !state.Media.IsPlaying;
                state.PlaySpring.SetTarget(state.Media.IsPlaying ? 100.0f : 0.0f, state.Settings.ReduceMotion);
            }
            NotifyUiWake();
            if (sess == null) return;
            _ = Task.Run(async () =>
            {
                try { await sess.TryTogglePlayPauseAsync(); } catch { }
                await RefreshSmtcAsync(state).ConfigureAwait(false);
            });
        }

        public static void MediaNextTrack(GlobalAppState state)
        {
            GlobalSystemMediaTransportControlsSession sess;
            lock (MediaLock) sess = _activeSmtcSession;
            if (sess == null) return;

            _ = Task.Run(async () =>
            {
                try { await sess.TrySkipNextAsync(); } catch { }
                await Task.Delay(180).ConfigureAwait(false);
                await RefreshSmtcAsync(state).ConfigureAwait(false);
            });
        }

        public static void MediaPreviousTrack(GlobalAppState state)
        {
            GlobalSystemMediaTransportControlsSession sess;
            lock (MediaLock) sess = _activeSmtcSession;
            if (sess == null) return;

            _ = Task.Run(async () =>
            {
                try { await sess.TrySkipPreviousAsync(); } catch { }
                await Task.Delay(180).ConfigureAwait(false);
                await RefreshSmtcAsync(state).ConfigureAwait(false);
            });
        }

        public static void SeekMediaPosition(GlobalAppState state, double positionSec)
        {
            double clamped = Math.Max(0.0, positionSec);
            lock (state.SyncRoot)
            {
                if (state.Media.DurationSec > 0.1) clamped = Math.Clamp(clamped, 0.0, state.Media.DurationSec);
                state.Media.PositionSec = clamped;
                UpdateActiveLyricIndexLocked(state);
            }
            GlobalSystemMediaTransportControlsSession sess;
            lock (MediaLock) sess = _activeSmtcSession;
            if (sess == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var tl = sess.GetTimelineProperties();
                    long targetTicks = (tl?.StartTime.Ticks ?? 0L) + TimeSpan.FromSeconds(clamped).Ticks;
                    await sess.TryChangePlaybackPositionAsync(targetTicks);
                }
                catch { }
            });
        }

        // ====================================================================
        // Free Buff & AI Coding Agents Live Detector (with Antigravity & Pruning)
        // ====================================================================
        private static readonly Dictionary<int, (TimeSpan Cpu, long TickMs)> _agentCpuCache = new();

        public static void PollCodingAgents(GlobalAppState state)
        {
            try
            {
                long nowMs = ConfigManager.GetTickMs();
                string userProf = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] statusPaths =
                {
                    Path.Combine(userProf, ".freebuff", "status.json"),
                    Path.Combine(userProf, ".claude", "status.json"),
                    Path.Combine(userProf, ".codex", "status.json"),
                    Path.Combine(userProf, ".gemini", "antigravity", "status.json"),
                    Path.Combine(AppContext.BaseDirectory, "freebuff_status.json")
                };

                var activeFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool freeBuffFileActive = false;
                foreach (string sp in statusPaths)
                {
                    if (!File.Exists(sp)) continue;
                    activeFilePaths.Add(sp);
                    if (ParseFreeBuffStatusFile(state, sp)) freeBuffFileActive = true;
                }

                // Prune deleted/stale status files before process scan so process fallback can take over cleanly
                lock (state.SyncRoot)
                {
                    for (int i = state.Agents.Count - 1; i >= 0; --i)
                    {
                        var a = state.Agents[i];
                        if (a.Source == "file")
                        {
                            if (!string.IsNullOrEmpty(a.FilePath) && !activeFilePaths.Contains(a.FilePath))
                                state.Agents.RemoveAt(i);
                            else if (a.Status.Equals("Done", StringComparison.OrdinalIgnoreCase) && (nowMs - a.UpdatedMs) > 600_000)
                                state.Agents.RemoveAt(i);
                        }
                    }
                }

                var agentProcs = new List<(string procName, string label)>
                {
                    ("freebuff", "Free Buff"),
                    ("free-buff", "Free Buff"),
                    ("buff", "Free Buff"),
                    ("claude", "Claude Code"),
                    ("codex", "Codex"),
                    ("antigravity", "Antigravity"),
                    ("hermes", "Hermes")
                };

                lock (state.SyncRoot)
                {
                    if (state.Settings.CustomCodingAgents != null)
                    {
                        foreach (string custom in state.Settings.CustomCodingAgents)
                        {
                            if (string.IsNullOrWhiteSpace(custom)) continue;
                            string proc = Path.GetFileNameWithoutExtension(custom.Trim());
                            if (!string.IsNullOrEmpty(proc)) agentProcs.Add((proc, proc));
                        }
                    }
                }

                var activeProcessIds = new HashSet<int>();
                var activeLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (procName, label) in agentProcs)
                {
                    Process[] procs = null;
                    try { procs = Process.GetProcessesByName(procName); } catch { }
                    if (procs == null || procs.Length == 0) continue;

                    int chosenPid = 0;
                    string chosenStep = "Session open (idle)";
                    bool processWorking = false;
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (p.HasExited) continue;
                            activeProcessIds.Add(p.Id);
                            if (chosenPid == 0) chosenPid = p.Id;
                            if (!string.IsNullOrWhiteSpace(p.MainWindowTitle))
                            {
                                chosenPid = p.Id;
                                chosenStep = p.MainWindowTitle.Trim();
                            }

                            TimeSpan cpuNow = p.TotalProcessorTime;
                            if (_agentCpuCache.TryGetValue(p.Id, out var prev) && nowMs > prev.TickMs)
                            {
                                double wallMs = (nowMs - prev.TickMs) * Math.Max(1, Environment.ProcessorCount);
                                double cpuPct = (cpuNow - prev.Cpu).TotalMilliseconds / wallMs * 100.0;
                                if (cpuPct > 1.5) processWorking = true;
                            }
                            _agentCpuCache[p.Id] = (cpuNow, nowMs);
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }

                    if (chosenPid > 0)
                    {
                        activeLabels.Add(label);
                        bool skipOverwrite = label.Equals("Free Buff", StringComparison.OrdinalIgnoreCase) && freeBuffFileActive;
                        if (!skipOverwrite)
                        {
                            lock (state.SyncRoot)
                            {
                                var exApi = state.Agents.Find(a => a.AgentType.Equals(label, StringComparison.OrdinalIgnoreCase));
                                if (exApi != null && exApi.Source == "api" && (nowMs - exApi.UpdatedMs) < 30_000)
                                {
                                    exApi.ProcessId = chosenPid;
                                    skipOverwrite = true;
                                }
                            }
                        }
                        if (skipOverwrite)
                        {
                            lock (state.SyncRoot)
                            {
                                var ex = state.Agents.Find(a => a.AgentType.Equals(label, StringComparison.OrdinalIgnoreCase));
                                if (ex != null && ex.ProcessId <= 0) ex.ProcessId = chosenPid;
                            }
                        }
                        else
                        {
                            bool wasWorking = false;
                            lock (state.SyncRoot)
                            {
                                var prevAg = state.Agents.Find(a => a.AgentType.Equals(label, StringComparison.OrdinalIgnoreCase));
                                wasWorking = prevAg != null && prevAg.Status.Equals("Working", StringComparison.OrdinalIgnoreCase);
                            }
                            bool finishedNow = wasWorking && !processWorking;
                            UpsertAgentSessionInternal(state, label, chosenStep,
                                processWorking ? "Working" : (finishedNow ? "Needs input" : "Idle"),
                                -1, false, finishedNow,
                                source: "process", processId: chosenPid, filePath: "", workspace: "Local PC");
                        }
                    }
                }

                var stalePids = new List<int>();
                foreach (int pid in _agentCpuCache.Keys)
                {
                    if (!activeProcessIds.Contains(pid)) stalePids.Add(pid);
                }
                foreach (int pid in stalePids) _agentCpuCache.Remove(pid);

                // Prune exited processes & stale API sessions
                lock (state.SyncRoot)
                {
                    for (int i = state.Agents.Count - 1; i >= 0; --i)
                    {
                        var a = state.Agents[i];
                        if (a.Source == "process")
                        {
                            bool stillAlive = a.ProcessId > 0
                                ? activeProcessIds.Contains(a.ProcessId)
                                : activeLabels.Contains(a.AgentType);
                            if (!stillAlive) state.Agents.RemoveAt(i);
                        }
                        else if (a.Source == "api")
                        {
                            if (a.Status.Equals("Done", StringComparison.OrdinalIgnoreCase) && (nowMs - a.UpdatedMs) > 120_000)
                            {
                                state.Agents.RemoveAt(i);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static bool ParseFreeBuffStatusFile(GlobalAppState state, string sp)
        {
            try
            {
                string content = File.ReadAllText(sp);
                if (string.IsNullOrWhiteSpace(content)) return false;

                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                string status = GetJsonString(root, "status") ?? "Working";
                string step = GetJsonString(root, "step")
                           ?? GetJsonString(root, "current_step")
                           ?? GetJsonString(root, "action")
                           ?? "Active Free Buff coding session";
                string workspace = GetJsonString(root, "workspace") ?? "Workspace";
                int progress = GetJsonInt(root, "progress", -1);
                int pid = GetJsonInt(root, "pid", 0);
                bool almost = GetJsonBool(root, "almost_done") ||
                              status.Contains("almost", StringComparison.OrdinalIgnoreCase);
                bool input = GetJsonBool(root, "needs_input") ||
                             status.Contains("input", StringComparison.OrdinalIgnoreCase) ||
                             status.Contains("approval", StringComparison.OrdinalIgnoreCase);

                if (pid > 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (p.HasExited) return false;
                    }
                    catch { return false; }
                }

                string displayStatus = input ? "Needs input" : (almost ? "Almost done" : status);
                UpsertAgentSessionInternal(state, "Free Buff", step, displayStatus,
                    almost && progress < 0 ? 90 : progress, almost, input,
                    source: "file", processId: pid, filePath: sp, workspace: workspace);
                return true;
            }
            catch { return false; }
        }

        private static string GetJsonString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        private static int GetJsonInt(JsonElement obj, string name, int fallback)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : fallback;

        private static bool GetJsonBool(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var el)) return false;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v)) return v != 0;
            if (el.ValueKind == JsonValueKind.String)
                return el.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true || el.GetString() == "1";
            return false;
        }

        private static readonly HashSet<string> _notifiedAgentReadyKeys = new(StringComparer.OrdinalIgnoreCase);

        public static void UpsertAgentSession(
            GlobalAppState state, string agentType, string step, string status,
            int progress, bool almostDone, bool needsInput)
        {
            UpsertAgentSessionInternal(state, agentType, step, status, progress, almostDone, needsInput,
                source: "api", processId: 0, filePath: "", workspace: "Workspace");
        }

        private static void UpsertAgentSessionInternal(
            GlobalAppState state, string agentType, string step, string status,
            int progress, bool almostDone, bool needsInput,
            string source, int processId, string filePath, string workspace)
        {
            bool shouldNotifyInput = false;
            bool shouldNotifyAlmost = false;
            lock (state.SyncRoot)
            {
                AgentSessionItem existing = state.Agents.Find(a => a.AgentType.Equals(agentType, StringComparison.OrdinalIgnoreCase));
                bool newlyNeedsInput = needsInput && (existing == null || !existing.NeedsInput);
                bool newlyAlmostDone = almostDone && (existing == null || !existing.IsAlmostDone);

                if (existing != null)
                {
                    existing.Status = status;
                    if (!string.IsNullOrEmpty(step)) existing.CurrentStep = step;
                    if (!string.IsNullOrEmpty(workspace)) existing.Workspace = workspace;
                    existing.ProgressPercent = progress;
                    existing.IsAlmostDone = almostDone;
                    existing.NeedsInput = needsInput;
                    if (processId > 0) existing.ProcessId = processId;
                    if (!string.IsNullOrEmpty(filePath)) existing.FilePath = filePath;
                    // Promote "file" or "api" over "process", never downgrade "file"/"api" to "process"
                    if (source == "api" || source == "file" || existing.Source == "process") existing.Source = source;
                    existing.UpdatedMs = ConfigManager.GetTickMs();
                }
                else
                {
                    state.Agents.Add(new AgentSessionItem
                    {
                        AgentType = agentType,
                        Workspace = string.IsNullOrEmpty(workspace) ? "Workspace" : workspace,
                        CurrentStep = step,
                        Status = status,
                        ProgressPercent = progress,
                        IsAlmostDone = almostDone,
                        NeedsInput = needsInput,
                        ProcessId = processId,
                        Source = source,
                        FilePath = filePath ?? "",
                        UpdatedMs = ConfigManager.GetTickMs()
                    });
                }

                // Remind ONCE per agent session/task, then keep it in notification history without repeating
                string sessionKey = $"{agentType}|{processId}|{(step ?? "").Trim()}";
                if (newlyNeedsInput && _notifiedAgentReadyKeys.Add("input:" + sessionKey))
                {
                    shouldNotifyInput = true;
                }
                else if (newlyAlmostDone && _notifiedAgentReadyKeys.Add("almost:" + sessionKey))
                {
                    shouldNotifyAlmost = true;
                }
            }

            if (shouldNotifyInput)
            {
                PushNotification(state, agentType, $"{agentType} needs your input",
                    string.IsNullOrWhiteSpace(step) ? "Waiting for your reply" : step,
                    Color.FromArgb(255, 255, 159, 10), processId: processId, showNotchToast: true);
            }
            else if (shouldNotifyAlmost)
            {
                PushNotification(state, agentType, $"{agentType} is almost done",
                    string.IsNullOrWhiteSpace(step) ? "Finishing up" : step,
                    Color.FromArgb(255, 48, 209, 88), processId: processId, showNotchToast: true);
            }
        }

        // ====================================================================
        // 10 Hz Hardware Telemetry (PDH GPU, DWM FPS), Sensors & Muted-Mic Notice
        // ====================================================================
        private static void InitializePdhGpuCounter()
        {
            lock (TelemetryLock)
            {
                if (_pdhInitialized) return;
                _pdhInitialized = true;
                try
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out IntPtr q) == 0 && q != IntPtr.Zero)
                    {
                        if (PdhAddEnglishCounterW(q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out IntPtr c) == 0 && c != IntPtr.Zero)
                        {
                            PdhCollectQueryData(q);
                            _pdhQuery = q;
                            _pdhGpuCounter = c;
                        }
                        else
                        {
                            PdhCloseQuery(q);
                        }
                    }
                }
                catch { }
            }
        }

        private static bool TryReadRealGpuUtilization(out float gpuPct)
        {
            gpuPct = 0.0f;
            lock (TelemetryLock)
            {
                if (_pdhQuery == IntPtr.Zero || _pdhGpuCounter == IntPtr.Zero) return false;
                try
                {
                    if (PdhCollectQueryData(_pdhQuery) != 0) return false;
                    uint bufSize = 0;
                    uint itemCount = 0;
                    uint status = PdhGetFormattedCounterArrayW(_pdhGpuCounter, PDH_FMT_DOUBLE, ref bufSize, ref itemCount, IntPtr.Zero);
                    const uint PDH_MORE_DATA = 0x800007D2;
                    if (status != PDH_MORE_DATA && status != 0) return false;
                    if (bufSize == 0 || itemCount == 0) return false;

                    IntPtr buf = Marshal.AllocHGlobal((int)bufSize);
                    try
                    {
                        status = PdhGetFormattedCounterArrayW(_pdhGpuCounter, PDH_FMT_DOUBLE, ref bufSize, ref itemCount, buf);
                        if (status != 0 || itemCount == 0) return false;

                        var engineSums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
                        int validSamples = 0;

                        for (uint i = 0; i < itemCount; ++i)
                        {
                            IntPtr itemPtr = IntPtr.Add(buf, (int)(i * itemSize));
                            var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(itemPtr);
                            if (item.FmtValue.CStatus != 0 && item.FmtValue.CStatus != 1) continue;
                            string name = item.szName != IntPtr.Zero ? (Marshal.PtrToStringUni(item.szName) ?? "") : "";
                            string bucket = ExtractGpuEngineBucket(name);
                            double val = Math.Max(0.0, item.FmtValue.doubleValue);
                            if (engineSums.TryGetValue(bucket, out double acc)) engineSums[bucket] = acc + val;
                            else engineSums[bucket] = val;
                            validSamples++;
                        }

                        if (validSamples == 0) return false;
                        double maxEngine = 0.0;
                        foreach (double sum in engineSums.Values)
                        {
                            if (sum > maxEngine) maxEngine = sum;
                        }
                        gpuPct = Math.Clamp((float)maxEngine, 0.0f, 100.0f);
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                catch { return false; }
            }
        }

        private static string ExtractGpuEngineBucket(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return "default";
            int luidIdx = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int engIdx = instanceName.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            string luidPart = luidIdx >= 0 ? instanceName.Substring(luidIdx, Math.Min(28, instanceName.Length - luidIdx)) : "luid0";
            string engPart = engIdx >= 0 ? instanceName.Substring(engIdx) : "eng";
            return luidPart + "|" + engPart;
        }

        private static void MeasureDwmFps(long nowMs, ref float fps, ref bool fpsAvailable)
        {
            try
            {
                var timing = new DWM_TIMING_INFO { cbSize = Marshal.SizeOf<DWM_TIMING_INFO>() };
                if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing) != 0)
                {
                    fpsAvailable = false;
                    fps = 0.0f;
                    return;
                }

                if (_prevDwmTickMs == 0)
                {
                    _prevDwmFramesDisplayed = timing.cFramesDisplayed;
                    _prevDwmFramesPresented = timing.cFramesComplete;
                    _prevDwmTickMs = nowMs;
                    fpsAvailable = false;
                    fps = 0.0f;
                    return;
                }

                double dtSec = (nowMs - _prevDwmTickMs) / 1000.0;
                if (dtSec < 0.25) return;

                ulong dDisp = timing.cFramesDisplayed >= _prevDwmFramesDisplayed ? timing.cFramesDisplayed - _prevDwmFramesDisplayed : 0;
                ulong dPres = timing.cFramesComplete >= _prevDwmFramesPresented ? timing.cFramesComplete - _prevDwmFramesPresented : 0;
                ulong delta = dDisp > 0 ? dDisp : dPres;

                _prevDwmFramesDisplayed = timing.cFramesDisplayed;
                _prevDwmFramesPresented = timing.cFramesComplete;
                _prevDwmTickMs = nowMs;

                if (delta > 0)
                {
                    float measured = (float)(delta / dtSec);
                    if (measured >= 0.5f && measured <= 1000.0f)
                    {
                        fps = MathF.Round(measured * 10.0f) / 10.0f;
                        fpsAvailable = true;
                        _lastDwmProgressMs = nowMs;
                        return;
                    }
                }

                if (_lastDwmProgressMs == 0 || (nowMs - _lastDwmProgressMs) > 1500)
                {
                    fpsAvailable = false;
                    fps = 0.0f;
                }
            }
            catch
            {
                fpsAvailable = false;
                fps = 0.0f;
            }
        }

        public static void Tick10HzTelemetryAndSensors(GlobalAppState state)
        {
            long nowMs = ConfigManager.GetTickMs();

            // Keep audio baseline & quick toggles in sync even if callbacks miss
            // anything. These are COM/device reads, so they run at 1 Hz while the
            // cheap local sensors still run at the caller's tick rate.
            if (nowMs - _lastAudioSyncMs >= 1000)
            {
                _lastAudioSyncMs = nowMs;
                if (!CoreAudioInterop.HasActiveVolumeCallback) SyncSystemAudioState(state, allowPopup: true);
                SyncSystemTogglesIfChanged(state);
            }

            // Refresh brightness if target MonitorIndex changed or periodically every 5s
            int curMonIdx;
            lock (state.SyncRoot) curMonIdx = state.Settings.MonitorIndex;
            if (curMonIdx != _lastBrightnessMonitorIndex || (nowMs - _lastBrightnessRefreshMs) >= 5000)
            {
                _lastBrightnessMonitorIndex = curMonIdx;
                _lastBrightnessRefreshMs = nowMs;
                if (ReadMonitorBrightnessLevel(state, out float br))
                {
                    lock (state.SyncRoot)
                    {
                        state.MonitorBrightness = br;
                        state.BrightnessSupported = true;
                    }
                }
            }

            // 1. Check Caps / Num / Scroll Lock changes (suppressed over fullscreen games)
            CheckLockKey(state, 0x14, ref _prevCapsState, "Caps Lock");
            CheckLockKey(state, 0x90, ref _prevNumState, "Num Lock");
            CheckLockKey(state, 0x91, ref _prevScrollState, "Scroll Lock");

            // 2. Real CPU Utilization
            float cpuPct = state.Telemetry.CpuPercent;
            if (GetSystemTimes(out FILETIME idleFt, out FILETIME kernelFt, out FILETIME userFt))
            {
                ulong idle = ToUInt64(idleFt);
                ulong kernel = ToUInt64(kernelFt);
                ulong user = ToUInt64(userFt);
                ulong total = (kernel - _prevKernelTime) + (user - _prevUserTime);
                ulong idleDiff = idle - _prevIdleTime;
                if (total > 0)
                {
                    float busy = (float)(total - idleDiff) * 100.0f / total;
                    cpuPct = cpuPct * 0.6f + Math.Clamp(busy, 0.0f, 100.0f) * 0.4f;
                }
                _prevIdleTime = idle;
                _prevKernelTime = kernel;
                _prevUserTime = user;
            }

            // 2b. Real GPU Engine Utilization (PDH) — throttled to 2 Hz, NEVER fabricated from CPU%
            float gpuPct = state.Telemetry.GpuPercent;
            bool gpuAvail = state.Telemetry.GpuPercentAvailable;
            if (nowMs - _lastGpuPollMs >= 1000)
            {
                _lastGpuPollMs = nowMs;
                if (TryReadRealGpuUtilization(out float realGpu))
                {
                    gpuPct = realGpu;
                    gpuAvail = true;
                }
                else
                {
                    gpuPct = 0.0f;
                    gpuAvail = false;
                }
            }

            // 2c. Real DWM FPS
            float fpsVal = state.Telemetry.Fps;
            bool fpsAvail = state.Telemetry.FpsAvailable;
            MeasureDwmFps(nowMs, ref fpsVal, ref fpsAvail);

            // 3. Real RAM, Disk & Battery
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref mem);

            // DriveInfo is a real disk query; sampling it 10x/sec showed up in the
            // idle CPU measurement, so it refreshes every 2 s instead.
            float diskPct = _lastDiskPct;
            if (nowMs - _lastDiskSampleMs >= 2000)
            {
                _lastDiskSampleMs = nowMs;
                try
                {
                    var drive = new DriveInfo("C");
                    if (drive.IsReady && drive.TotalSize > 0)
                    {
                        diskPct = (float)(drive.TotalSize - drive.AvailableFreeSpace) * 100.0f / drive.TotalSize;
                        _lastDiskPct = diskPct;
                    }
                }
                catch { }
            }

            var pwr = SystemInformation.PowerStatus;

            // 4. Real Network Throughput
            float downKbps = state.Telemetry.NetDownKbps;
            float upKbps = state.Telemetry.NetUpKbps;
            if (nowMs - _prevNetTickMs >= 1000)
            {
                try
                {
                    long inTotal = 0, outTotal = 0;
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus == OperationalStatus.Up &&
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        {
                            var stats = ni.GetIPv4Statistics();
                            inTotal += stats.BytesReceived;
                            outTotal += stats.BytesSent;
                        }
                    }
                    if (_prevNetTickMs > 0 && inTotal >= _prevNetInBytes && outTotal >= _prevNetOutBytes)
                    {
                        float dtSec = Math.Max(0.1f, (nowMs - _prevNetTickMs) / 1000.0f);
                        downKbps = ((inTotal - _prevNetInBytes) * 8.0f / 1000.0f) / dtSec;
                        upKbps = ((outTotal - _prevNetOutBytes) * 8.0f / 1000.0f) / dtSec;
                    }
                    _prevNetInBytes = inTotal;
                    _prevNetOutBytes = outTotal;
                    _prevNetTickMs = nowMs;
                }
                catch { }
            }

            // 5. Fullscreen Game & Aiming Cursor Lock Detection (/support Section 7)
            var ci = new Win32Layered.CURSORINFO { cbSize = Marshal.SizeOf<Win32Layered.CURSORINFO>() };
            bool cursorShowing = true;
            if (Win32Layered.GetCursorInfo(ref ci))
            {
                cursorShowing = (ci.flags & Win32Layered.CURSOR_SHOWING) != 0;
            }

            bool isFullscreen = false;
            string fgExe = "";
            IntPtr fgWnd = Win32Layered.GetForegroundWindow();
            if (fgWnd != IntPtr.Zero && Win32Layered.GetWindowRect(fgWnd, out var wrc))
            {
                Rectangle scr = Screen.FromHandle(fgWnd)?.Bounds ?? Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                if (wrc.left <= scr.Left && wrc.top <= scr.Top &&
                    wrc.right >= scr.Right && wrc.bottom >= scr.Bottom)
                {
                    Win32Layered.GetWindowThreadProcessId(fgWnd, out uint pid);
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        fgExe = proc.ProcessName + ".exe";
                    }
                    catch { }

                    if (!fgExe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                        !fgExe.Equals("OpenPixel.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (state.SyncRoot)
                        {
                            foreach (string allowed in state.Settings.GameRulesAllowList)
                            {
                                if (allowed.Equals(fgExe, StringComparison.OrdinalIgnoreCase))
                                {
                                    isFullscreen = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 6. Privacy Sensor Dots (Win32 NonPackaged + UWP Packaged across Mic, Camera, Screen Capture)
            string micApp = QueryActiveCapabilityApp(state, "microphone");
            string camApp = QueryActiveCapabilityApp(state, "webcam");
            string scrApp = QueryActiveCapabilityApp(state, "graphicsCaptureWithoutBorder");
            if (string.IsNullOrEmpty(scrApp)) scrApp = QueryActiveCapabilityApp(state, "graphicsCaptureProgrammatic");

            // 7. Real Mic Peak Level via CoreAudio IAudioMeterInformation
            float micPeak = 0.0f;
            if (!state.GlobalMicMuted)
            {
                micPeak = CoreAudioInterop.TryGetCapturePeakLevel();
            }

            lock (state.SyncRoot)
            {
                state.Telemetry.CpuPercent = Math.Clamp(cpuPct, 0.0f, 100.0f);
                state.Telemetry.CpuTempAvailable = false; // Zero fake motherboard temps on desktops!
                state.Telemetry.GpuTempAvailable = false;
                state.Telemetry.GpuPercent = Math.Clamp(gpuPct, 0.0f, 100.0f);
                state.Telemetry.GpuPercentAvailable = gpuAvail;
                state.Telemetry.Fps = fpsVal;
                state.Telemetry.FpsAvailable = fpsAvail;
                state.Telemetry.RamPercent = Math.Clamp((float)mem.dwMemoryLoad, 0.0f, 100.0f);
                state.Telemetry.StoragePercent = diskPct;
                state.Telemetry.NetDownKbps = downKbps;
                state.Telemetry.NetUpKbps = upKbps;
                state.Telemetry.BatteryPercent = Math.Clamp((int)MathF.Round(pwr.BatteryLifePercent * 100.0f), 0, 100);
                state.Telemetry.HasBattery = pwr.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
                state.Telemetry.IsCharging = pwr.PowerLineStatus == PowerLineStatus.Online;

                state.MicPeakLevel = micPeak;

                state.IsFullscreenGameActive = isFullscreen;
                state.IsGameCursorShowing = cursorShowing;
                state.ActiveFullscreenApp = fgExe;

                bool wasMic = state.Sensors.MicActive;
                state.Sensors.MicActive = !string.IsNullOrEmpty(micApp);
                state.Sensors.MicAppName = micApp;
                if (state.Sensors.MicActive && !wasMic) state.Sensors.MicCallStartMs = nowMs;
                else if (!state.Sensors.MicActive) state.Sensors.MicCallStartMs = 0;

                state.Sensors.CamActive = !string.IsNullOrEmpty(camApp);
                state.Sensors.CamAppName = camApp;

                state.Sensors.ScreenActive = !string.IsNullOrEmpty(scrApp);
                state.Sensors.ScreenAppName = scrApp;

                // Muted-mic speaking alert (never triggered by svchost/system services)
                bool micJustStarted = state.Sensors.MicActive && !_wasMicActiveLastTick;
                _wasMicActiveLastTick = state.Sensors.MicActive;
                if (state.GlobalMicMuted && state.Sensors.MicActive && state.Settings.ShowGlobalMuteNotice &&
                    !IsSystemServiceApp(micApp) &&
                    (micJustStarted || (micPeak > 0.04f && (nowMs - _lastMutedMicNoticeMs) > 12000)))
                {
                    _lastMutedMicNoticeMs = nowMs;
                    state.ToastPopupType = TopPopupType.MicMute;
                    state.ToastTitle = "mic muted";
                    state.ToastSubtitle = $"{micApp} · Ctrl+Alt+M";
                    state.ToastAccent = Color.FromArgb(255, 255, 69, 58);
                    state.ToastIsReminder = false;
                    state.ToastActive = true;
                    state.ToastExpireMs = nowMs + (long)(state.Settings.NoticeDurationSec * 1000.0f);
                }
            }
        }

        private static void CheckLockKey(GlobalAppState state, int vk, ref short prevState, string keyName)
        {
            short cur = Win32Layered.GetKeyState(vk);
            if ((cur & 1) != (prevState & 1))
            {
                prevState = cur;
                lock (state.SyncRoot)
                {
                    if (state.IsFullscreenGameActive) return;
                    bool isOn = (cur & 1) != 0;
                    state.ToastPopupType = TopPopupType.LockKey;
                    state.ToastTitle = $"{keyName} {(isOn ? "ON" : "OFF")}";
                    state.ToastSubtitle = "";
                    state.ToastAccent = isOn ? Color.FromArgb(255, 48, 209, 88) : Color.FromArgb(255, 185, 182, 174);
                    state.ToastIsReminder = false;
                    state.ToastActive = true;
                    state.ToastExpireMs = ConfigManager.GetTickMs() + (long)(state.Settings.NoticeDurationSec * 1000.0f);
                }
            }
        }

        private static bool IsSystemServiceApp(string appOrPath)
        {
            if (string.IsNullOrWhiteSpace(appOrPath)) return true;
            string name = Path.GetFileName(appOrPath);
            if (name.Equals("OpenPixel.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("audiodg.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RuntimeBroker.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("backgroundTaskHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (appOrPath.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) &&
                name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static bool IsSensorAppIgnored(GlobalAppState state, string appOrPath)
        {
            if (IsSystemServiceApp(appOrPath)) return true;
            string exeName = Path.GetFileName(appOrPath);
            lock (state.SyncRoot)
            {
                foreach (string ig in state.Settings.IgnoredSensorApps)
                {
                    if (string.IsNullOrWhiteSpace(ig)) continue;
                    if (ig.Equals(exeName, StringComparison.OrdinalIgnoreCase) ||
                        ig.Equals(appOrPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string QueryActiveCapabilityApp(GlobalAppState state, string capability)
        {
            try
            {
                string capRoot = $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}";

                // 1. Win32 NonPackaged apps
                using (RegistryKey nonPkg = Registry.CurrentUser.OpenSubKey(capRoot + @"\NonPackaged"))
                {
                    if (nonPkg != null)
                    {
                        foreach (string subName in nonPkg.GetSubKeyNames())
                        {
                            using RegistryKey sub = nonPkg.OpenSubKey(subName);
                            if (sub == null) continue;
                            if (sub.GetValue("LastUsedTimeStop") is long stopTime && stopTime == 0)
                            {
                                string decodedPath = subName.Replace('#', '\\');
                                string exe = Path.GetFileName(decodedPath);
                                if (!IsSensorAppIgnored(state, decodedPath) && !IsSensorAppIgnored(state, exe))
                                {
                                    return exe;
                                }
                            }
                        }
                    }
                }

                // 2. Packaged UWP Store apps directly under ConsentStore\{capability}
                using (RegistryKey pkgRoot = Registry.CurrentUser.OpenSubKey(capRoot))
                {
                    if (pkgRoot != null)
                    {
                        foreach (string subName in pkgRoot.GetSubKeyNames())
                        {
                            if (subName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                            using RegistryKey sub = pkgRoot.OpenSubKey(subName);
                            if (sub == null) continue;
                            if (sub.GetValue("LastUsedTimeStop") is long stopTime && stopTime == 0)
                            {
                                string friendly = ResolveUwpPackageFriendlyName(subName);
                                if (!IsSensorAppIgnored(state, subName) && !IsSensorAppIgnored(state, friendly))
                                {
                                    return friendly;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private static string ResolveUwpPackageFriendlyName(string packageFamilyName)
        {
            if (string.IsNullOrWhiteSpace(packageFamilyName)) return "";
            try
            {
                var info = AppInfo.GetFromAppUserModelId(packageFamilyName + "!App");
                if (!string.IsNullOrWhiteSpace(info?.DisplayInfo?.DisplayName))
                    return info.DisplayInfo.DisplayName;
            }
            catch { }
            int under = packageFamilyName.IndexOf('_');
            string pkg = under > 0 ? packageFamilyName.Substring(0, under) : packageFamilyName;
            int dot = pkg.LastIndexOf('.');
            return (dot >= 0 && dot + 1 < pkg.Length) ? pkg.Substring(dot + 1) : pkg;
        }

        // ====================================================================
        // 1 Hz Reminders Scheduler, Repeat Rules, Focus Timer & Lyrics Index
        // ====================================================================
        public static void Tick1HzRemindersAndTimers(GlobalAppState state)
        {
            if (state == null) return;
            long nowMs = ConfigManager.GetTickMs();
            long unixNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (state.SyncRoot)
            {
                // Advance unknown-duration SMTC elapsed position smoothly between SMTC events
                if (state.Media.HasSession && state.Media.IsPlaying)
                {
                    state.Media.PositionSec += 1.0;
                    if (state.Media.DurationSec > 0.1)
                    {
                        state.Media.PositionSec = Math.Min(state.Media.PositionSec, state.Media.DurationSec);
                    }
                    UpdateActiveLyricIndexLocked(state);
                }

                // Focus timer countdown
                if (state.FocusTimerRunning && state.FocusTimerRemainingSec > 0)
                {
                    state.FocusTimerRemainingSec--;
                    if (state.FocusTimerRemainingSec == 0)
                    {
                        state.FocusTimerRunning = false;
                        state.ToastPopupType = TopPopupType.Reminder;
                        state.ToastTitle = "Focus session complete";
                        state.ToastSubtitle = "Take a short break";
                        state.ToastAccent = Color.FromArgb(255, 48, 209, 88);
                        state.ToastIsReminder = false;
                        state.ToastActive = true;
                        state.ToastExpireMs = nowMs + 8000;
                    }
                }

                // Reminders due check & repeat rescheduling
                for (int i = 0; i < state.Reminders.Count; ++i)
                {
                    var rem = state.Reminders[i];
                    if (rem == null) continue;

                    // Handle UI completion ("Done" hit region sets Completed=true directly)
                    if (rem.Completed)
                    {
                        if (TryRescheduleRepeatingReminder(rem, nowMs, unixNow))
                            continue;
                        continue;
                    }

                    // Keep DueUnixMs synchronized if UI snooze mutated DueTimestampMs directly
                    if (rem.DueTimestampMs > 0)
                    {
                        long expectedUnix = unixNow + (rem.DueTimestampMs - nowMs);
                        if (rem.DueUnixMs <= 0 || Math.Abs(expectedUnix - rem.DueUnixMs) > 2500)
                        {
                            rem.DueUnixMs = expectedUnix;
                        }
                    }
                    else if (rem.DueUnixMs > 0)
                    {
                        rem.DueTimestampMs = nowMs + (rem.DueUnixMs - unixNow);
                    }

                    if (!rem.Triggered && rem.DueTimestampMs > 0 && nowMs >= rem.DueTimestampMs)
                    {
                        rem.Triggered = true;
                        state.ActiveReminderId = rem.Id;
                        state.ToastPopupType = TopPopupType.Reminder;
                        state.ToastTitle = string.IsNullOrWhiteSpace(rem.Title) ? "Reminder" : rem.Title;
                        state.ToastSubtitle = rem.RepeatRule;
                        state.ToastAccent = Color.FromArgb(255, 255, 159, 10);
                        state.ToastIsReminder = true;
                        state.ToastActive = true;
                        state.ToastExpireMs = nowMs + 60000;
                        NotifyUiWake();
                    }
                }
            }
        }

        private static bool TryRescheduleRepeatingReminder(ReminderItem rem, long nowMs, long unixNow)
        {
            if (rem == null || string.IsNullOrWhiteSpace(rem.RepeatRule)) return false;
            string rule = rem.RepeatRule.Trim();
            if (rule.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                rule.Equals("Once", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            DateTimeOffset baseUtc = rem.DueUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(rem.DueUnixMs)
                : DateTimeOffset.UtcNow;
            if (baseUtc < DateTimeOffset.UtcNow) baseUtc = DateTimeOffset.UtcNow;

            DateTimeOffset nextUtc;
            if (rule.Equals("Every day", StringComparison.OrdinalIgnoreCase) ||
                rule.Equals("Daily", StringComparison.OrdinalIgnoreCase))
            {
                nextUtc = baseUtc.AddDays(1);
            }
            else if (rule.Equals("Weekdays", StringComparison.OrdinalIgnoreCase))
            {
                nextUtc = baseUtc.AddDays(1);
                while (nextUtc.DayOfWeek == DayOfWeek.Saturday || nextUtc.DayOfWeek == DayOfWeek.Sunday)
                {
                    nextUtc = nextUtc.AddDays(1);
                }
            }
            else if (rule.Equals("Every week", StringComparison.OrdinalIgnoreCase) ||
                     rule.Equals("Weekly", StringComparison.OrdinalIgnoreCase))
            {
                nextUtc = baseUtc.AddDays(7);
            }
            else
            {
                return false;
            }

            long nextUnix = nextUtc.ToUnixTimeMilliseconds();
            rem.DueUnixMs = nextUnix;
            rem.DueTimestampMs = nowMs + (nextUnix - unixNow);
            rem.Triggered = false;
            rem.Completed = false;
            return true;
        }

        public static void SnoozeReminder(GlobalAppState state, long reminderId, int minutes = 10)
        {
            long nowMs = ConfigManager.GetTickMs();
            long unixNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long deltaMs = Math.Max(1, minutes) * 60_000L;
            lock (state.SyncRoot)
            {
                foreach (var rem in state.Reminders)
                {
                    if (rem.Id == reminderId)
                    {
                        rem.DueTimestampMs = nowMs + deltaMs;
                        rem.DueUnixMs = unixNow + deltaMs;
                        rem.Triggered = false;
                        rem.Completed = false;
                    }
                }
                if (state.ActiveReminderId == reminderId)
                {
                    state.ToastActive = false;
                    state.ToastIsReminder = false;
                }
            }
            ConfigManager.SaveRemindersAtomic(state);
        }

        public static void CompleteReminder(GlobalAppState state, long reminderId)
        {
            long nowMs = ConfigManager.GetTickMs();
            long unixNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (state.SyncRoot)
            {
                foreach (var rem in state.Reminders)
                {
                    if (rem.Id == reminderId)
                    {
                        rem.Completed = true;
                        TryRescheduleRepeatingReminder(rem, nowMs, unixNow);
                    }
                }
                if (state.ActiveReminderId == reminderId)
                {
                    state.ToastActive = false;
                    state.ToastIsReminder = false;
                }
            }
            ConfigManager.SaveRemindersAtomic(state);
        }

        // ====================================================================
        // Launcher Slots (Ctrl+Alt+1..6 + UI Clicks)
        // ====================================================================
        public static void ExecuteLauncherSlot(GlobalAppState state, int slotIndex)
        {
            if (state == null || slotIndex < 0 || slotIndex >= 6) return;
            string target;
            lock (state.SyncRoot) target = state.LauncherSlots[slotIndex]?.TargetPath ?? "";

            if (!string.IsNullOrWhiteSpace(target))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return;
                }
                catch { }
            }

            // Unconfigured slot: let user pick an app/file cleanly
            ConfigureLauncherSlot(state, slotIndex);
        }

        public static void ConfigureLauncherSlot(GlobalAppState state, int slotIndex)
        {
            if (state == null || slotIndex < 0 || slotIndex >= 6) return;
            try
            {
                using var dlg = new OpenFileDialog
                {
                    Title = $"Configure Launcher Slot {slotIndex + 1} (Ctrl+Alt+{slotIndex + 1})",
                    Filter = "Apps & Files (*.exe;*.lnk;*.bat;*.cmd;*.*)|*.exe;*.lnk;*.bat;*.cmd;*.*"
                };
                if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.FileName))
                {
                    Color[] palette =
                    {
                        Color.FromArgb(255, 64, 156, 255),
                        Color.FromArgb(255, 48, 209, 88),
                        Color.FromArgb(255, 255, 159, 10),
                        Color.FromArgb(255, 191, 90, 242),
                        Color.FromArgb(255, 255, 55, 95),
                        Color.FromArgb(255, 100, 210, 255)
                    };
                    lock (state.SyncRoot)
                    {
                        state.LauncherSlots[slotIndex] ??= new LauncherSlot();
                        state.LauncherSlots[slotIndex].TargetPath = dlg.FileName;
                        state.LauncherSlots[slotIndex].Label = Path.GetFileNameWithoutExtension(dlg.FileName);
                        state.LauncherSlots[slotIndex].BadgeColor = palette[slotIndex % palette.Length];
                    }
                    ConfigManager.SaveAtomic(state);
                }
            }
            catch { }
        }

        // ====================================================================
        // Persistent Tabbed Clipboard (Text, Links, Images + OCR, Files)
        // ====================================================================
        public static void SuppressNextClipboardUpdate(int ms = 1200)
        {
            _suppressClipboardUntilMs = ConfigManager.GetTickMs() + ms;
        }

        public static void OnClipboardUpdated(GlobalAppState state)
        {
            if (state == null) return;
            long nowMs = ConfigManager.GetTickMs();
            if (nowMs < _suppressClipboardUntilMs) return;

            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    if (files != null && files.Count > 0)
                    {
                        var list = new List<string>(files.Count);
                        foreach (string f in files) if (!string.IsNullOrEmpty(f)) list.Add(f);
                        if (list.Count == 0) return;
                        string sig = "FILES:" + string.Join("|", list);
                        if (sig == _lastClipboardSignature) return;
                        _lastClipboardSignature = sig;

                        string first = Path.GetFileName(list[0]);
                        string preview = list.Count == 1 ? first : $"{first} (+{list.Count - 1} more)";
                        InsertClipboardEntry(state, new ClipboardEntry
                        {
                            Id = nowMs,
                            Category = ClipboardCategory.Files,
                            Text = string.Join("\n", list),
                            Preview = preview.Length > 72 ? preview.Substring(0, 72) : preview,
                            SourceInfo = $"{list.Count} file{(list.Count == 1 ? "" : "s")}",
                            FilePaths = list,
                            IsCurrent = true,
                            TimestampMs = nowMs
                        });
                        return;
                    }
                }

                if (Clipboard.ContainsImage())
                {
                    using Image img = Clipboard.GetImage();
                    if (img != null)
                    {
                        string dir = ConfigManager.GetClipboardImagesDirectory();
                        string imgPath = Path.Combine(dir, $"clip_{nowMs}.png");
                        using var bmp = new Bitmap(img);
                        bmp.Save(imgPath, ImageFormat.Png);

                        string sig = $"IMG:{bmp.Width}x{bmp.Height}:{new FileInfo(imgPath).Length}";
                        if (sig == _lastClipboardSignature)
                        {
                            try { File.Delete(imgPath); } catch { }
                            return;
                        }
                        _lastClipboardSignature = sig;

                        var entry = new ClipboardEntry
                        {
                            Id = nowMs,
                            Category = ClipboardCategory.Images,
                            Text = imgPath,
                            ImagePath = imgPath,
                            Preview = $"Image {bmp.Width}\u00D7{bmp.Height}",
                            SourceInfo = "PNG",
                            IsCurrent = true,
                            TimestampMs = nowMs
                        };
                        InsertClipboardEntry(state, entry);
                        RunClipboardOcrAsync(state, entry, imgPath);
                        return;
                    }
                }

                if (Clipboard.ContainsText())
                {
                    string txt = Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(txt)) return;
                    string sig = "TXT:" + txt;
                    if (sig == _lastClipboardSignature) return;
                    _lastClipboardSignature = sig;

                    bool isLink = txt.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                  txt.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    InsertClipboardEntry(state, new ClipboardEntry
                    {
                        Id = nowMs,
                        Category = isLink ? ClipboardCategory.Links : ClipboardCategory.Text,
                        Text = txt,
                        Preview = txt.Length > 72 ? txt.Substring(0, 72) : txt,
                        SourceInfo = isLink ? "Link" : "Clipboard",
                        IsCurrent = true,
                        TimestampMs = nowMs
                    });
                }
            }
            catch { }
        }

        private static void InsertClipboardEntry(GlobalAppState state, ClipboardEntry entry)
        {
            lock (state.SyncRoot)
            {
                foreach (var e in state.ClipboardHistory) e.IsCurrent = false;

                // Deduplicate identical unpinned entry by moving it to top
                int existingIdx = state.ClipboardHistory.FindIndex(e =>
                    e.Category == entry.Category &&
                    string.Equals(e.Text ?? "", entry.Text ?? "", StringComparison.Ordinal));
                if (existingIdx >= 0)
                {
                    var ex = state.ClipboardHistory[existingIdx];
                    ex.IsCurrent = true;
                    ex.TimestampMs = entry.TimestampMs;
                    state.ClipboardHistory.RemoveAt(existingIdx);
                    state.ClipboardHistory.Insert(0, ex);
                    return;
                }

                state.ClipboardHistory.Insert(0, entry);

                // Keep up to 50 non-pinned items (always preserve pinned & snippets)
                int nonPinned = 0;
                for (int i = 0; i < state.ClipboardHistory.Count; ++i)
                {
                    var item = state.ClipboardHistory[i];
                    if (item.IsPinned || item.Category == ClipboardCategory.Snippets) continue;
                    nonPinned++;
                    if (nonPinned > 50)
                    {
                        state.ClipboardHistory.RemoveAt(i);
                        i--;
                    }
                }
            }
        }

        private static void RunClipboardOcrAsync(GlobalAppState state, ClipboardEntry entry, string imgPath)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!File.Exists(imgPath)) return;
                    var engine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (engine == null) return;

                    using var fs = File.OpenRead(imgPath);
                    using var ras = fs.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(ras);
                    using var swBmp = await decoder.GetSoftwareBitmapAsync();
                    var result = await engine.RecognizeAsync(swBmp);
                    if (!string.IsNullOrWhiteSpace(result?.Text))
                    {
                        lock (state.SyncRoot)
                        {
                            entry.OcrText = result.Text.Trim();
                        }
                    }
                }
                catch { }
            });
        }

        // ====================================================================
        // File Shelf Drag-In / Multi-Drag-Out / Copy
        // ====================================================================
        public static int AddFilesToShelf(GlobalAppState state, IEnumerable<string> paths)
        {
            if (state == null || paths == null) return 0;
            int added = 0;
            lock (state.SyncRoot)
            {
                foreach (string raw in paths)
                {
                    if (state.ShelfFiles.Count >= 12) break;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string full = raw.Trim();
                    bool isFile = File.Exists(full);
                    bool isDir = !isFile && Directory.Exists(full);
                    if (!isFile && !isDir) continue;
                    if (state.ShelfFiles.Exists(x => x.FullPath.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;

                    long size = 0;
                    if (isFile)
                    {
                        try { size = new FileInfo(full).Length; } catch { }
                    }
                    string ext = Path.GetExtension(full).ToLowerInvariant();
                    bool isImg = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

                    state.ShelfFiles.Add(new ShelfFileItem
                    {
                        FullPath = full,
                        DisplayName = Path.GetFileName(full),
                        Extension = ext,
                        FileSize = size,
                        IsImage = isImg,
                        Selected = false
                    });
                    added++;
                }
            }
            return added;
        }

        public static void StartShelfDragOut(Control source, GlobalAppState state)
        {
            if (source == null || state == null) return;
            string[] paths;
            lock (state.SyncRoot)
            {
                var selected = state.ShelfFiles.FindAll(f => f.Selected && (File.Exists(f.FullPath) || Directory.Exists(f.FullPath)));
                if (selected.Count == 0)
                    selected = state.ShelfFiles.FindAll(f => File.Exists(f.FullPath) || Directory.Exists(f.FullPath));
                if (selected.Count == 0) return;
                paths = new string[selected.Count];
                for (int i = 0; i < selected.Count; ++i) paths[i] = selected[i].FullPath;
            }
            try
            {
                var data = new DataObject(DataFormats.FileDrop, paths);
                source.DoDragDrop(data, DragDropEffects.Copy);
            }
            catch { }
        }

        public static bool CopyShelfSelectionToClipboard(GlobalAppState state)
        {
            if (state == null) return false;
            var sc = new StringCollection();
            lock (state.SyncRoot)
            {
                var selected = state.ShelfFiles.FindAll(f => f.Selected && (File.Exists(f.FullPath) || Directory.Exists(f.FullPath)));
                if (selected.Count == 0)
                    selected = state.ShelfFiles.FindAll(f => File.Exists(f.FullPath) || Directory.Exists(f.FullPath));
                foreach (var f in selected) sc.Add(f.FullPath);
            }
            if (sc.Count == 0) return false;
            try
            {
                SuppressNextClipboardUpdate();
                Clipboard.SetFileDropList(sc);
                return true;
            }
            catch { return false; }
        }

        // ====================================================================
        // Colour Picker, Full Screenshot, Region Snip & Media Foundation MP4 Recorder
        // ====================================================================
        public static string PickScreenColorUnderCursor(GlobalAppState state)
        {
            Point pt = Cursor.Position;
            IntPtr screenDc = Win32Layered.GetDC(IntPtr.Zero);
            uint col = 0x00F07A1A;
            if (screenDc != IntPtr.Zero)
            {
                col = Win32Layered.GetPixel(screenDc, pt.X, pt.Y);
                Win32Layered.ReleaseDC(IntPtr.Zero, screenDc);
            }
            int r = (int)(col & 0xFF);
            int g = (int)((col >> 8) & 0xFF);
            int b = (int)((col >> 16) & 0xFF);
            string hex = $"#{r:X2}{g:X2}{b:X2}";

            try
            {
                SuppressNextClipboardUpdate(500);
                Clipboard.SetText(hex);
            }
            catch { }

            lock (state.SyncRoot)
            {
                state.LastPickedColorHex = hex;
                state.ToastPopupType = TopPopupType.ColorPicker;
                state.ToastTitle = $"Copied {hex}";
                state.ToastSubtitle = "Colour Picker";
                state.ToastAccent = Color.FromArgb(255, r, g, b);
                state.ToastIsReminder = false;
                state.ToastActive = true;
                state.ToastExpireMs = ConfigManager.GetTickMs() + 3200;
            }
            return hex;
        }

        public static void CaptureFullScreenshot(GlobalAppState state)
        {
            try
            {
                Screen scr = GetTargetScreen(state);
                Rectangle bounds = scr?.Bounds ?? Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                SaveRegionScreenshot(state, bounds, "Screenshot");
            }
            catch { }
        }

        public static void LaunchRegionSnip(GlobalAppState state)
        {
            try
            {
                Screen scr = GetTargetScreen(state);
                Rectangle bounds = scr?.Bounds ?? Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                using var overlay = new RegionSnipOverlayForm(bounds);
                if (overlay.ShowDialog() == DialogResult.OK && overlay.SelectedScreenRect.Width >= 4 && overlay.SelectedScreenRect.Height >= 4)
                {
                    SaveRegionScreenshot(state, overlay.SelectedScreenRect, "Snip");
                }
            }
            catch { }
        }

        private static void SaveRegionScreenshot(GlobalAppState state, Rectangle bounds, string prefix)
        {
            using var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            try
            {
                SuppressNextClipboardUpdate(500);
                Clipboard.SetImage(bmp);
            }
            catch { }

            string dir = ConfigManager.GetCapturesDirectory();
            try { Directory.CreateDirectory(dir); } catch { }
            string path = Path.Combine(dir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{ConfigManager.GetTickMs() % 1000}.png");
            bmp.Save(path, ImageFormat.Png);
            long fileSize = 0;
            try { fileSize = new FileInfo(path).Length; } catch { }

            lock (state.SyncRoot)
            {
                if (!state.Settings.KeepCapturesOffShelf && state.ShelfFiles.Count < 12)
                {
                    state.ShelfFiles.Add(new ShelfFileItem
                    {
                        FullPath = path,
                        DisplayName = Path.GetFileName(path),
                        Extension = ".png",
                        FileSize = fileSize,
                        IsImage = true
                    });
                }
                state.ToastPopupType = TopPopupType.Notice;
                state.ToastTitle = $"{prefix} captured";
                state.ToastSubtitle = "Copied to clipboard";
                state.ToastAccent = Color.FromArgb(255, 48, 209, 88);
                state.ToastIsReminder = false;
                state.ToastActive = true;
                state.ToastExpireMs = ConfigManager.GetTickMs() + 3800;
            }
        }

        public static void StartScreenRecording(GlobalAppState state)
        {
            if (state == null) return;
            lock (RecLock)
            {
                if (_recThread != null && _recThread.IsAlive) return;
                Screen scr = GetTargetScreen(state);
                Rectangle bounds = scr?.Bounds ?? Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                int width = Math.Max(64, bounds.Width & ~1);
                int height = Math.Max(64, bounds.Height & ~1);
                string recDir = ConfigManager.GetCapturesDirectory();
                try { Directory.CreateDirectory(recDir); } catch { }
                string outPath = Path.Combine(recDir, $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                _activeRecPath = outPath;
                _recStopRequested = false;

                lock (state.SyncRoot)
                {
                    state.IsRecordingScreen = true;
                    state.RecordingStartMs = ConfigManager.GetTickMs();
                    state.RecSpring.SetTarget(100.0f, state.Settings.ReduceMotion);
                    _lastSyncedRecording = true;
                }

                _recThread = new Thread(() => RecordScreenLoop(state, bounds.Left, bounds.Top, width, height, outPath))
                {
                    IsBackground = true,
                    Name = "OpenPixelScreenRecorder"
                };
                _recThread.SetApartmentState(ApartmentState.MTA);
                _recThread.Start();
            }
        }

        public static void StopScreenRecording(GlobalAppState state)
        {
            Thread toJoin;
            lock (RecLock)
            {
                _recStopRequested = true;
                toJoin = _recThread;
            }
            if (toJoin != null && toJoin.IsAlive && Thread.CurrentThread != toJoin)
            {
                toJoin.Join(4000);
            }
            lock (state.SyncRoot)
            {
                state.IsRecordingScreen = false;
                state.RecSpring.SetTarget(0.0f, state.Settings.ReduceMotion);
                _lastSyncedRecording = false;
            }
        }

        private static unsafe void RecordScreenLoop(GlobalAppState state, int srcX, int srcY, int width, int height, string outPath)
        {
            bool mfStarted = false;
            MediaFoundationInterop.IMFSinkWriter writer = null;
            IntPtr hdcScreen = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hDib = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;
            IntPtr dibBits = IntPtr.Zero;

            try
            {
                int hr = MediaFoundationInterop.MFStartup(MediaFoundationInterop.MF_VERSION, 0);
                if (hr < 0) return;
                mfStarted = true;

                MediaFoundationInterop.IMFAttributes writerAttrs = null;
                if (MediaFoundationInterop.MFCreateAttributes(out writerAttrs, 1) >= 0 && writerAttrs != null)
                {
                    Guid hwAttr = MediaFoundationInterop.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
                    writerAttrs.SetUINT32(ref hwAttr, 1);
                }

                hr = MediaFoundationInterop.MFCreateSinkWriterFromURL(outPath, IntPtr.Zero, writerAttrs, out writer);
                if (writerAttrs != null) Marshal.ReleaseComObject(writerAttrs);
                if (hr < 0 || writer == null) return;

                // Configure output stream (H.264)
                MediaFoundationInterop.MFCreateMediaType(out var outType);
                Guid gMajor = MediaFoundationInterop.MF_MT_MAJOR_TYPE;
                Guid gSub = MediaFoundationInterop.MF_MT_SUBTYPE;
                Guid gBitrate = MediaFoundationInterop.MF_MT_AVG_BITRATE;
                Guid gInterlace = MediaFoundationInterop.MF_MT_INTERLACE_MODE;
                Guid gFrameSize = MediaFoundationInterop.MF_MT_FRAME_SIZE;
                Guid gFrameRate = MediaFoundationInterop.MF_MT_FRAME_RATE;
                Guid gPar = MediaFoundationInterop.MF_MT_PIXEL_ASPECT_RATIO;
                Guid gStride = MediaFoundationInterop.MF_MT_DEFAULT_STRIDE;

                Guid majorVideo = MediaFoundationInterop.MFMediaType_Video;
                Guid h264Sub = MediaFoundationInterop.MFVideoFormat_H264;
                Guid rgb32Sub = MediaFoundationInterop.MFVideoFormat_RGB32;

                const uint fps = 30;
                ulong frameSizeVal = ((ulong)(uint)width << 32) | (uint)height;
                ulong frameRateVal = ((ulong)fps << 32) | 1u;
                ulong parVal = (1UL << 32) | 1u;

                outType.SetGUID(ref gMajor, ref majorVideo);
                outType.SetGUID(ref gSub, ref h264Sub);
                outType.SetUINT32(ref gBitrate, 8_000_000);
                outType.SetUINT32(ref gInterlace, 2); // Progressive
                outType.SetUINT64(ref gFrameSize, frameSizeVal);
                outType.SetUINT64(ref gFrameRate, frameRateVal);
                outType.SetUINT64(ref gPar, parVal);

                hr = writer.AddStream(outType, out uint streamIndex);
                Marshal.ReleaseComObject(outType);
                if (hr < 0) return;

                // Configure input stream (top-down RGB32)
                MediaFoundationInterop.MFCreateMediaType(out var inType);
                inType.SetGUID(ref gMajor, ref majorVideo);
                inType.SetGUID(ref gSub, ref rgb32Sub);
                inType.SetUINT32(ref gInterlace, 2);
                inType.SetUINT64(ref gFrameSize, frameSizeVal);
                inType.SetUINT64(ref gFrameRate, frameRateVal);
                inType.SetUINT64(ref gPar, parVal);
                inType.SetUINT32(ref gStride, (uint)(width * 4));

                hr = writer.SetInputMediaType(streamIndex, inType, IntPtr.Zero);
                Marshal.ReleaseComObject(inType);
                if (hr < 0) return;

                hr = writer.BeginWriting();
                if (hr < 0) return;

                // Top-down 32bpp DIB section (negative height => stride = width * 4, top-down!)
                hdcScreen = Win32Layered.GetDC(IntPtr.Zero);
                hdcMem = Win32Layered.CreateCompatibleDC(hdcScreen);
                var bmi = new Win32Layered.BITMAPINFO
                {
                    bmiHeader = new Win32Layered.BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<Win32Layered.BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0
                    }
                };
                hDib = Win32Layered.CreateDIBSection(hdcMem, ref bmi, 0, out dibBits, IntPtr.Zero, 0);
                if (hDib == IntPtr.Zero || dibBits == IntPtr.Zero) return;
                oldObj = Win32Layered.SelectObject(hdcMem, hDib);

                uint bufLen = (uint)(width * height * 4);
                long frameDurationHns = 10_000_000L / fps;
                long rtStart = 0;
                var sw = Stopwatch.StartNew();
                long frameIndex = 0;

                while (!_recStopRequested)
                {
                    Win32Layered.StretchBlt(hdcMem, 0, 0, width, height, hdcScreen, srcX, srcY, width, height, 0x00CC0020 /* SRCCOPY */);

                    if (MediaFoundationInterop.MFCreateMemoryBuffer(bufLen, out var mfBuf) >= 0 && mfBuf != null)
                    {
                        try
                        {
                            if (mfBuf.Lock(out IntPtr dstPtr, out _, out _) >= 0)
                            {
                                try
                                {
                                    System.Buffer.MemoryCopy((void*)dibBits, (void*)dstPtr, bufLen, bufLen);
                                }
                                finally { mfBuf.Unlock(); }
                                mfBuf.SetCurrentLength(bufLen);

                                if (MediaFoundationInterop.MFCreateSample(out var sample) >= 0 && sample != null)
                                {
                                    try
                                    {
                                        sample.AddBuffer(mfBuf);
                                        sample.SetSampleTime(rtStart);
                                        sample.SetSampleDuration(frameDurationHns);
                                        writer.WriteSample(streamIndex, sample);
                                    }
                                    finally { Marshal.ReleaseComObject(sample); }
                                }
                            }
                        }
                        finally { Marshal.ReleaseComObject(mfBuf); }
                    }

                    rtStart += frameDurationHns;
                    frameIndex++;
                    long targetMs = (frameIndex * 1000L) / fps;
                    int sleepMs = (int)(targetMs - sw.ElapsedMilliseconds);
                    if (sleepMs > 1) Thread.Sleep(sleepMs);
                }

                writer.DoFinalize();
            }
            catch { }
            finally
            {
                if (oldObj != IntPtr.Zero && hdcMem != IntPtr.Zero) Win32Layered.SelectObject(hdcMem, oldObj);
                if (hDib != IntPtr.Zero) Win32Layered.DeleteObject(hDib);
                if (hdcMem != IntPtr.Zero) Win32Layered.DeleteDC(hdcMem);
                if (hdcScreen != IntPtr.Zero) Win32Layered.ReleaseDC(IntPtr.Zero, hdcScreen);
                if (writer != null) { try { Marshal.ReleaseComObject(writer); } catch { } }
                if (mfStarted) { try { MediaFoundationInterop.MFShutdown(); } catch { } }

                if (File.Exists(outPath))
                {
                    long fsize = 0;
                    try { fsize = new FileInfo(outPath).Length; } catch { }
                    if (fsize > 0)
                    {
                        lock (state.SyncRoot)
                        {
                            if (!state.Settings.KeepCapturesOffShelf && state.ShelfFiles.Count < 12)
                            {
                                state.ShelfFiles.Add(new ShelfFileItem
                                {
                                    FullPath = outPath,
                                    DisplayName = Path.GetFileName(outPath),
                                    Extension = ".mp4",
                                    FileSize = fsize,
                                    IsImage = false
                                });
                            }
                            state.ToastPopupType = TopPopupType.Notice;
                            state.ToastTitle = "Recording saved";
                            state.ToastSubtitle = Path.GetFileName(outPath);
                            state.ToastAccent = Color.FromArgb(255, 48, 209, 88);
                            state.ToastIsReminder = false;
                            state.ToastActive = true;
                            state.ToastExpireMs = ConfigManager.GetTickMs() + 3800;
                        }
                    }
                }
            }
        }

        public static void SaveDiagnosticReportToDesktop(GlobalAppState state)
        {
            try
            {
                string logPath = ConfigManager.GetDiagnosticLogPath();
                string report = $"OpenPixel 0.5.126 Diagnostic Report\nGenerated: {DateTime.Now:O}\n" +
                                $"OS: {Environment.OSVersion}\nTheme: {state.Settings.Theme}\n" +
                                $"Blur: {state.Settings.GlassBlurRadius}px\nDrawWithProcessor: {state.Settings.DrawWithProcessor}\n" +
                                $"GPU Available: {state.Telemetry.GpuPercentAvailable} ({state.Telemetry.GpuPercent:F1}%)\n" +
                                $"FPS Available: {state.Telemetry.FpsAvailable} ({state.Telemetry.Fps:F1})\n" +
                                $"Brightness Supported: {state.BrightnessSupported} ({state.MonitorBrightness:F0}%)\n";
                File.WriteAllText(logPath, report);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop))
                {
                    File.WriteAllText(Path.Combine(desktop, "OpenPixel-Report.txt"), report);
                }
            }
            catch { }
        }

        // Translucent interactive region snipping overlay
        private sealed class RegionSnipOverlayForm : Form
        {
            private Point _dragStart;
            private Point _dragCurrent;
            private bool _dragging;
            public Rectangle SelectedScreenRect { get; private set; }

            public RegionSnipOverlayForm(Rectangle monitorBounds)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = monitorBounds;
                TopMost = true;
                ShowInTaskbar = false;
                DoubleBuffered = true;
                BackColor = Color.Black;
                Opacity = 0.28;
                Cursor = Cursors.Cross;
                KeyPreview = true;
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
                base.OnKeyDown(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                    return;
                }
                if (e.Button == MouseButtons.Left)
                {
                    _dragging = true;
                    _dragStart = e.Location;
                    _dragCurrent = e.Location;
                    Invalidate();
                }
                base.OnMouseDown(e);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (_dragging)
                {
                    _dragCurrent = e.Location;
                    Invalidate();
                }
                base.OnMouseMove(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (_dragging && e.Button == MouseButtons.Left)
                {
                    _dragging = false;
                    var rc = GetLocalRect();
                    SelectedScreenRect = new Rectangle(Bounds.Left + rc.Left, Bounds.Top + rc.Top, rc.Width, rc.Height);
                    DialogResult = rc.Width >= 4 && rc.Height >= 4 ? DialogResult.OK : DialogResult.Cancel;
                    Close();
                }
                base.OnMouseUp(e);
            }

            private Rectangle GetLocalRect()
            {
                int x = Math.Min(_dragStart.X, _dragCurrent.X);
                int y = Math.Min(_dragStart.Y, _dragCurrent.Y);
                int w = Math.Abs(_dragCurrent.X - _dragStart.X);
                int h = Math.Abs(_dragCurrent.Y - _dragStart.Y);
                return new Rectangle(x, y, w, h);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (_dragging)
                {
                    var rc = GetLocalRect();
                    using var fill = new SolidBrush(Color.FromArgb(65, 255, 255, 255));
                    using var pen = new Pen(Color.White, 2.0f);
                    e.Graphics.FillRectangle(fill, rc);
                    e.Graphics.DrawRectangle(pen, rc);
                }
            }
        }

        // Native P/Invokes
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }
        private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Win32Layered.POINT pt, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
        [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);
        [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
        [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);
        [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);
        [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        // PDH P/Invokes
        private const uint PDH_FMT_DOUBLE = 0x00000200;

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            private uint _pad;
            public double doubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string szDataSource, IntPtr dwUserData, out IntPtr phQuery);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr hQuery);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr hQuery);

        // DWM Timing Info
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct UNSIGNED_RATIO { public uint uiNumerator; public uint uiDenominator; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct DWM_TIMING_INFO
        {
            public int cbSize;
            public UNSIGNED_RATIO rateRefresh;
            public ulong qpcRefreshPeriod;
            public UNSIGNED_RATIO rateCompose;
            public ulong qpcVBlank;
            public ulong cRefresh;
            public uint cDXRefresh;
            public ulong qpcCompose;
            public ulong cFrame;
            public uint cDXPresent;
            public ulong cRefreshFrame;
            public ulong cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;
            public ulong cFrameDisplayed;
            public ulong qpcFrameDisplayed;
            public ulong cRefreshFrameDisplayed;
            public ulong cFrameComplete;
            public ulong qpcFrameComplete;
            public ulong cFramePending;
            public ulong qpcFramePending;
            public ulong cFramesDisplayed;
            public ulong cFramesComplete;
            public ulong cFramesPending;
            public ulong cFramesAvailable;
            public ulong cFramesDropped;
            public ulong cFramesMissed;
            public ulong cRefreshNextDisplayed;
            public ulong cRefreshNextPresented;
            public ulong cRefreshesDisplayed;
            public ulong cRefreshesPresented;
            public ulong cRefreshStarted;
            public ulong cPixelsReceived;
            public ulong cPixelsDrawn;
            public ulong cBuffersEmpty;
        }
        [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

        // Power & Quiet Hours WNF
        [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayPolicyGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid OverlayPolicyGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);
        [DllImport("ntdll.dll")] private static extern int RtlPublishWnfStateData(ulong StateName, IntPtr TypeId, ref int Buffer, int Length, IntPtr ExplicitScope);
    }

    // ========================================================================
    // Direct CoreAudio COM Interop (Callbacks, NotificationClient, Meter, Multi-Mic)
    // ========================================================================
    internal static class CoreAudioInterop
    {
        private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
        private static readonly Guid SelfEventContextGuid = new("D3487E2A-91B5-4C94-9022-8F03A864415A");

        private static PROPERTYKEY PKEY_AudioEndpoint_FormFactor = new() { fmtid = new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), pid = 0 };
        private static PROPERTYKEY PKEY_Device_FriendlyName = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

        private static readonly object AudioLock = new();
        private static IMMDeviceEnumerator _enumerator;
        private static NotificationClient _notifClient;
        private static IAudioEndpointVolume _boundRenderVol;
        private static VolumeCallback _volumeCallback;
        private static IAudioMeterInformation _captureMeter;
        private static IAudioMeterInformation _renderMeter;
        private static GlobalAppState _boundState;
        private static Action _wakeUi;
        private static long _lastRenderSwitchMs = 0;

        public static bool HasActiveVolumeCallback => _volumeCallback != null && _boundRenderVol != null;
        public static bool IsInDeviceSwitchGuard() => (ConfigManager.GetTickMs() - _lastRenderSwitchMs) < 900;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr p1;
            public IntPtr p2;
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pvar);

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIO_VOLUME_NOTIFICATION_DATA
        {
            public Guid guidEventContext;
            [MarshalAs(UnmanagedType.Bool)] public bool bMuted;
            public float fMasterVolume;
            public uint nChannels;
            public float afChannelVolumes0;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint pcDevices);
            [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            [PreserveSig] int GetState(out uint pdwState);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
            [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
            [PreserveSig] int GetChannelCount(out uint pnChannelCount);
            [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
            [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolumeCallback
        {
            [PreserveSig] int OnNotify(IntPtr pNotifyData);
        }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMNotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PROPERTYKEY key);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float pfPeak);
            [PreserveSig] int GetMeteringChannelCount(out uint pnChannels);
            [PreserveSig] int GetChannelsPeakValues(uint u32ChannelCount, IntPtr afPeakValues);
            [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        }

        [ComVisible(true)]
        private sealed class VolumeCallback : IAudioEndpointVolumeCallback
        {
            public int OnNotify(IntPtr pNotifyData)
            {
                if (pNotifyData == IntPtr.Zero) return 0;
                try
                {
                    var data = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(pNotifyData);
                    var st = _boundState;
                    if (st == null) return 0;

                    float newVolPct = MathF.Round(Math.Clamp(data.fMasterVolume, 0.0f, 1.0f) * 100.0f);
                    bool muted = data.bMuted;
                    bool selfInitiated = data.guidEventContext == SelfEventContextGuid;
                    bool inSwitchGuard = IsInDeviceSwitchGuard();

                    bool wake = false;
                    lock (st.SyncRoot)
                    {
                        bool changed = MathF.Abs(newVolPct - st.MasterVolume) >= 1.0f || muted != st.MasterOutputMuted;
                        st.MasterVolume = newVolPct;
                        st.MasterOutputMuted = muted;

                        if (!selfInitiated && !inSwitchGuard && st.AudioBaselineInitialized && changed && !st.IslandOpen)
                        {
                            WindowsSystemHooks.ShowVolumeToastLocked(st, newVolPct, muted);
                            wake = true;
                        }
                    }
                    if (wake) _wakeUi?.Invoke();
                }
                catch { }
                return 0;
            }
        }

        [ComVisible(true)]
        private sealed class NotificationClient : IMMNotificationClient
        {
            public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState)
            {
                if (dwNewState == 1 /* DEVICE_STATE_ACTIVE */) ReapplyMicMuteIfNeeded();
                return 0;
            }

            public int OnDeviceAdded(string pwstrDeviceId)
            {
                ReapplyMicMuteIfNeeded();
                return 0;
            }

            public int OnDeviceRemoved(string pwstrDeviceId) => 0;

            public int OnDefaultDeviceChanged(int flow, int role, string pwstrDefaultDeviceId)
            {
                var st = _boundState;
                if (st == null) return 0;
                if (flow == 0 && role == 1) // eRender, eMultimedia
                {
                    _lastRenderSwitchMs = ConfigManager.GetTickMs();
                    lock (st.SyncRoot) st.ActiveAudioRenderDeviceId = pwstrDefaultDeviceId ?? "";
                    RebindRenderCallback();
                    WindowsSystemHooks.SyncSystemAudioState(st, allowPopup: false);
                }
                else if (flow == 1) // eCapture
                {
                    RebindCaptureMeter();
                    ReapplyMicMuteIfNeeded();
                }
                return 0;
            }

            public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) => 0;

            private static void ReapplyMicMuteIfNeeded()
            {
                var st = _boundState;
                if (st == null) return;
                bool muted;
                lock (st.SyncRoot) muted = st.GlobalMicMuted;
                if (muted)
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { TrySetAllCaptureEndpointsMute(true); } catch { }
                    });
                }
            }
        }

        public static void EnsureRegistered(GlobalAppState state, Action wakeUi)
        {
            lock (AudioLock)
            {
                _boundState = state;
                _wakeUi = wakeUi;
                try
                {
                    if (_enumerator == null)
                    {
                        Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                        if (t != null) _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(t);
                    }
                    if (_enumerator != null && _notifClient == null)
                    {
                        _notifClient = new NotificationClient();
                        _enumerator.RegisterEndpointNotificationCallback(_notifClient);
                    }
                    RebindRenderCallbackLocked();
                    RebindCaptureMeterLocked();
                }
                catch { }
            }
        }

        public static void Shutdown()
        {
            lock (AudioLock)
            {
                try
                {
                    if (_boundRenderVol != null && _volumeCallback != null)
                        _boundRenderVol.UnregisterControlChangeNotify(_volumeCallback);
                }
                catch { }
                try
                {
                    if (_enumerator != null && _notifClient != null)
                        _enumerator.UnregisterEndpointNotificationCallback(_notifClient);
                }
                catch { }
                if (_boundRenderVol != null) { try { Marshal.ReleaseComObject(_boundRenderVol); } catch { } _boundRenderVol = null; }
                if (_captureMeter != null) { try { Marshal.ReleaseComObject(_captureMeter); } catch { } _captureMeter = null; }
                if (_enumerator != null) { try { Marshal.ReleaseComObject(_enumerator); } catch { } _enumerator = null; }
                _volumeCallback = null;
                _notifClient = null;
            }
        }

        private static void RebindRenderCallback()
        {
            lock (AudioLock) RebindRenderCallbackLocked();
        }

        private static void RebindRenderCallbackLocked()
        {
            try
            {
                if (_boundRenderVol != null && _volumeCallback != null)
                {
                    try { _boundRenderVol.UnregisterControlChangeNotify(_volumeCallback); } catch { }
                    try { Marshal.ReleaseComObject(_boundRenderVol); } catch { }
                    _boundRenderVol = null;
                }
                if (_enumerator == null) return;
                if (_enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice dev) == 0 && dev != null)
                {
                    try
                    {
                        Guid iid = IID_IAudioEndpointVolume;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out object obj) == 0 && obj is IAudioEndpointVolume epVol)
                        {
                            _volumeCallback ??= new VolumeCallback();
                            if (epVol.RegisterControlChangeNotify(_volumeCallback) == 0)
                                _boundRenderVol = epVol;
                            else
                                Marshal.ReleaseComObject(epVol);
                        }
                        if (_renderMeter != null)
                        {
                            try { Marshal.ReleaseComObject(_renderMeter); } catch { }
                            _renderMeter = null;
                        }
                        Guid meterIid = IID_IAudioMeterInformation;
                        if (dev.Activate(ref meterIid, 23, IntPtr.Zero, out object mObj) == 0 && mObj is IAudioMeterInformation rMeter)
                        {
                            _renderMeter = rMeter;
                        }
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
            }
            catch { }
        }

        private static void RebindCaptureMeter()
        {
            lock (AudioLock) RebindCaptureMeterLocked();
        }

        private static void RebindCaptureMeterLocked()
        {
            try
            {
                if (_captureMeter != null)
                {
                    try { Marshal.ReleaseComObject(_captureMeter); } catch { }
                    _captureMeter = null;
                }
                if (_enumerator == null) return;
                if (_enumerator.GetDefaultAudioEndpoint(1, 2, out IMMDevice dev) == 0 && dev != null)
                {
                    try
                    {
                        Guid iid = IID_IAudioMeterInformation;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out object obj) == 0 && obj is IAudioMeterInformation meter)
                            _captureMeter = meter;
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
            }
            catch { }
        }

        public static float TryGetCapturePeakLevel()
        {
            lock (AudioLock)
            {
                try
                {
                    if (_captureMeter == null) RebindCaptureMeterLocked();
                    if (_captureMeter != null && _captureMeter.GetPeakValue(out float peak) == 0)
                        return Math.Clamp(peak, 0.0f, 1.0f);
                }
                catch { _captureMeter = null; }
            }
            return 0.0f;
        }

        public static float TryGetRenderPeakLevel()
        {
            lock (AudioLock)
            {
                try
                {
                    if (_renderMeter == null)
                    {
                        _enumerator ??= GetOrCreateEnumerator();
                        if (_enumerator != null && _enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice dev) == 0 && dev != null)
                        {
                            try
                            {
                                Guid meterIid = IID_IAudioMeterInformation;
                                if (dev.Activate(ref meterIid, 23, IntPtr.Zero, out object mObj) == 0 && mObj is IAudioMeterInformation rMeter)
                                    _renderMeter = rMeter;
                            }
                            finally { Marshal.ReleaseComObject(dev); }
                        }
                    }
                    if (_renderMeter != null && _renderMeter.GetPeakValue(out float peak) == 0)
                        return Math.Clamp(peak, 0.0f, 1.0f);
                }
                catch { _renderMeter = null; }
            }
            return 0.0f;
        }

        private static IMMDeviceEnumerator GetOrCreateEnumerator()
        {
            if (_enumerator != null) return _enumerator;
            Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            return t != null ? (IMMDeviceEnumerator)Activator.CreateInstance(t) : null;
        }

        public static bool TryGetDefaultEndpointState(bool isCapture, out string deviceId, out float scalar, out bool muted)
        {
            deviceId = "";
            scalar = 0.5f;
            muted = false;
            try
            {
                var enumerator = GetOrCreateEnumerator();
                if (enumerator == null) return false;

                int flow = isCapture ? 1 : 0; // eCapture = 1, eRender = 0
                int role = isCapture ? 2 : 1; // eCommunications = 2, eMultimedia = 1
                if (enumerator.GetDefaultAudioEndpoint(flow, role, out IMMDevice dev) == 0 && dev != null)
                {
                    try
                    {
                        dev.GetId(out deviceId);
                        Guid iid = IID_IAudioEndpointVolume;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out object obj) == 0 && obj is IAudioEndpointVolume epVol)
                        {
                            try
                            {
                                epVol.GetMasterVolumeLevelScalar(out scalar);
                                epVol.GetMute(out muted);
                                return true;
                            }
                            finally { Marshal.ReleaseComObject(epVol); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
            }
            catch { }
            return false;
        }

        public static bool TrySetDefaultEndpointVolume(bool isCapture, float scalar01, bool unmuteIfPositive)
        {
            try
            {
                var enumerator = GetOrCreateEnumerator();
                if (enumerator == null) return false;

                int flow = isCapture ? 1 : 0;
                if (enumerator.GetDefaultAudioEndpoint(flow, 1, out IMMDevice dev) == 0 && dev != null)
                {
                    try
                    {
                        Guid iid = IID_IAudioEndpointVolume;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out object obj) == 0 && obj is IAudioEndpointVolume epVol)
                        {
                            try
                            {
                                Guid ctx = SelfEventContextGuid;
                                epVol.SetMasterVolumeLevelScalar(Math.Clamp(scalar01, 0.0f, 1.0f), ref ctx);
                                if (unmuteIfPositive) epVol.SetMute(false, ref ctx);
                                return true;
                            }
                            finally { Marshal.ReleaseComObject(epVol); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
            }
            catch { }
            return false;
        }

        private static bool IsMixerOrLoopbackEndpoint(IMMDevice dev)
        {
            try
            {
                if (dev.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore props) == 0 && props != null)
                {
                    try
                    {
                        var pvName = new PROPVARIANT();
                        try
                        {
                            if (props.GetValue(ref PKEY_Device_FriendlyName, out pvName) == 0 && pvName.vt == 31 && pvName.p1 != IntPtr.Zero)
                            {
                                string name = Marshal.PtrToStringUni(pvName.p1) ?? "";
                                if (name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("What U Hear", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Wave Out", StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                        }
                        finally { PropVariantClear(ref pvName); }

                        var pvForm = new PROPVARIANT();
                        try
                        {
                            if (props.GetValue(ref PKEY_AudioEndpoint_FormFactor, out pvForm) == 0 && pvForm.vt == 19)
                            {
                                uint ff = (uint)pvForm.p1.ToInt64();
                                // DigitalAudioDisplayDevice=9, Spdif=7 — skip non-mic render/loopback capture jacks
                                if (ff == 7 || ff == 9) return true;
                            }
                        }
                        finally { PropVariantClear(ref pvForm); }
                    }
                    finally { Marshal.ReleaseComObject(props); }
                }
            }
            catch { }
            return false;
        }

        public static bool TrySetAllCaptureEndpointsMute(bool mute)
        {
            bool anyOk = false;
            try
            {
                var enumerator = GetOrCreateEnumerator();
                if (enumerator == null) return false;
                Guid ctx = SelfEventContextGuid;

                if (enumerator.EnumAudioEndpoints(1 /* eCapture */, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection col) == 0 && col != null)
                {
                    try
                    {
                        if (col.GetCount(out uint count) == 0)
                        {
                            for (uint i = 0; i < count; ++i)
                            {
                                try
                                {
                                    if (col.Item(i, out IMMDevice dev) == 0 && dev != null)
                                    {
                                        try
                                        {
                                            if (IsMixerOrLoopbackEndpoint(dev)) continue;
                                            Guid iid = IID_IAudioEndpointVolume;
                                            if (dev.Activate(ref iid, 23, IntPtr.Zero, out object obj) == 0 && obj is IAudioEndpointVolume epVol)
                                            {
                                                try
                                                {
                                                    if (epVol.SetMute(mute, ref ctx) == 0) anyOk = true;
                                                }
                                                finally { Marshal.ReleaseComObject(epVol); }
                                            }
                                        }
                                        finally { Marshal.ReleaseComObject(dev); }
                                    }
                                }
                                catch (COMException) { }
                                catch { }
                            }
                        }
                    }
                    finally { Marshal.ReleaseComObject(col); }
                }

                // Fallback on default capture roles if collection was empty
                if (!anyOk)
                {
                    for (int role = 0; role <= 2; ++role)
                    {
                        try
                        {
                            if (enumerator.GetDefaultAudioEndpoint(1, role, out IMMDevice dev) == 0 && dev != null)
                            {
                                try
                                {
                                    Guid iid = IID_IAudioEndpointVolume;
                                    if (dev.Activate(ref iid, 23, IntPtr.Zero, out object obj) == 0 && obj is IAudioEndpointVolume epVol)
                                    {
                                        try { if (epVol.SetMute(mute, ref ctx) == 0) anyOk = true; }
                                        finally { Marshal.ReleaseComObject(epVol); }
                                    }
                                }
                                finally { Marshal.ReleaseComObject(dev); }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return anyOk;
        }

        public static bool TrySetDefaultEndpointMute(bool isCapture, bool mute)
        {
            if (isCapture) return TrySetAllCaptureEndpointsMute(mute);
            try
            {
                var enumerator = GetOrCreateEnumerator();
                if (enumerator == null) return false;
                Guid ctx = SelfEventContextGuid;
                bool ok = false;
                for (int role = 0; role <= 2; ++role)
                {
                    if (enumerator.GetDefaultAudioEndpoint(0, role, out IMMDevice dev) == 0 && dev != null)
                    {
                        try
                        {
                            Guid iid = IID_IAudioEndpointVolume;
                            if (dev.Activate(ref iid, 23, IntPtr.Zero, out object obj) == 0 && obj is IAudioEndpointVolume epVol)
                            {
                                try { if (epVol.SetMute(mute, ref ctx) == 0) ok = true; }
                                finally { Marshal.ReleaseComObject(epVol); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(dev); }
                    }
                }
                return ok;
            }
            catch { return false; }
        }
    }

    // ========================================================================
    // Windows Media Foundation H.264 MP4 SinkWriter Interop
    // ========================================================================
    internal static class MediaFoundationInterop
    {
        public const uint MF_VERSION = 0x00020070;

        public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-feef521167cf");
        public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-4fb8-9494-4de4b551f39f");

        public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");

        [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint dwFlags);
        [DllImport("mfplat.dll")] public static extern int MFShutdown();
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType ppMFType);
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample ppIMFSample);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        public static extern int MFCreateSinkWriterFromURL(string pwszOutputURL, IntPtr pByteStream, IMFAttributes pAttributes, out IMFSinkWriter ppSinkWriter);

        [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFAttributes
        {
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
            [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString(ref Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        }

        [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaType
        {
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
            [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString(ref Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);

            // IMFMediaType methods
            [PreserveSig] int GetMajorType(out Guid pguidMajorType);
            [PreserveSig] int IsCompressedFormat(out bool pfCompressed);
            [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
            [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
            [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
        }

        [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
            [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
            [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
        }

        [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSample
        {
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
            [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, out bool pbResult);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString(ref Guid guidKey, StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble(ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);

            // IMFSample methods
            [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
            [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
            [PreserveSig] int GetSampleTime(out long phnsSampleTime);
            [PreserveSig] int SetSampleTime(long hnsSampleTime);
            [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
            [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
            [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
            [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        }

        [ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSinkWriter
        {
            [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
            [PreserveSig] int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IntPtr pEncodingParameters);
            [PreserveSig] int BeginWriting();
            [PreserveSig] int WriteSample(uint dwStreamIndex, IMFSample pSample);
            [PreserveSig] int SendStreamTick(uint dwStreamIndex, long llTimestamp);
            [PreserveSig] int PlaceMarker(int eMarkerType, IntPtr pvContext);
            [PreserveSig] int NotifyEndOfSegment(uint dwStreamIndex);
            [PreserveSig] int Flush(uint dwStreamIndex);
            [PreserveSig] int DoFinalize();
            [PreserveSig] int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
            [PreserveSig] int GetStatistics(uint dwStreamIndex, IntPtr pStats);
        }
    }
}
