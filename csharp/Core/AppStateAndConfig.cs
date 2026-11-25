using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPixel.Core
{
    // ========================================================================
    // User Settings Schema (10 Categories, Liquid Glass Default Out of the Box)
    // ========================================================================
    public sealed class AppSettings
    {
        // Bumped when defaults must be re-applied to an existing settings.json.
        public int ConfigRevision { get; set; } = 2;

        // 1. General
        public bool LaunchAtStartup { get; set; } = false;
        public string Language { get; set; } = "en";
        public bool DrawWithProcessor { get; set; } = false;
        public bool ReverseScrolling { get; set; } = false;
        public bool PauseEffectsOnBattery { get; set; } = true;

        // 2. Accessibility
        public float TextScale { get; set; } = 1.0f;
        public bool HigherContrast { get; set; } = false;
        public bool ReduceMotion { get; set; } = false;
        public float NoticeDurationSec { get; set; } = 4.5f;

        // 3. Island & Appearance (LIQUID GLASS ENABLED BY DEFAULT!)
        public IslandShape Shape { get; set; } = IslandShape.Notch;
        public EdgePlacement Edge { get; set; } = EdgePlacement.TopCenter;
        public int MonitorIndex { get; set; } = 0;
        public float TopEdgeDistanceDip { get; set; } = 0.0f;
        public float CornerRadiusScale { get; set; } = 1.0f;
        public float PillSizePercent { get; set; } = 100.0f;
        public float OpenSizePercent { get; set; } = 100.0f;
        public MotionSpeed MotionSpeed { get; set; } = MotionSpeed.Normal;
        public float HoverOpenDelaySec { get; set; } = 0.15f;
        public bool ReopenOnLastTab { get; set; } = true;
        public RestingInfoMode RestingMode { get; set; } = RestingInfoMode.MediaOnly;
        public bool ShowSongTitleInClosedPill { get; set; } = false;
        public bool HideFromScreenshots { get; set; } = false;
        public SurfaceTheme Theme { get; set; } = SurfaceTheme.LiquidGlass; // Default out of the box!
        public float GlassBlurRadius { get; set; } = 20.0f; // Clamped [0..40]
        public ProgressBarStyle ProgressBar { get; set; } = ProgressBarStyle.Wave;
        public VisualizerStyle Visualizer { get; set; } = VisualizerStyle.Bars;
        public CardBackgroundMode CardBackground { get; set; } = CardBackgroundMode.AlbumColours;
        public int FontIndex { get; set; } = 0;

        // 4. Panels
        public List<PanelId> PanelOrder { get; set; } = new()
        {
            PanelId.Music, PanelId.Web, PanelId.Notifications, PanelId.Chat,
            PanelId.Shelf, PanelId.Clipboard, PanelId.Widgets, PanelId.Controls,
            PanelId.Launcher, PanelId.Calls
        };
        public bool ClockTimestampsInNotifications { get; set; } = true;
        public int MusicFilterMode { get; set; } = 0;
        public List<string> ExcludedMusicApps { get; set; } = new() { "msedgewebview2.exe" };
        public List<string> IgnoredSensorApps { get; set; } = new() { "svchost.exe", "audiodg.exe", "SearchHost.exe", "RuntimeBroker.exe" };

        // 5. Widgets & Calendar in the Island
        public bool Use24HourClock { get; set; } = false;
        public string WeatherCity { get; set; } = "Local";
        public int WeatherTempC { get; set; } = 20;
        public int WeatherHighC { get; set; } = 22;
        public int WeatherLowC { get; set; } = 14;
        public string WorldClockCity { get; set; } = "Tokyo";
        public int WorldClockOffsetHours { get; set; } = 9;
        public bool LockDesktopWidgets { get; set; } = false;
        public int DayClockAccentColorArgb { get; set; } = Color.FromArgb(255, 255, 255, 255).ToArgb();

        // 6. Calls
        public bool ShowGlobalMuteNotice { get; set; } = true;
        public string UpcomingMeetingTitle { get; set; } = "No upcoming events";
        public string UpcomingMeetingTime { get; set; } = "Calendar synced locally";
        public string UpcomingMeetingUrl { get; set; } = "";

        // 7. Streaming
        public string TwitchChannel { get; set; } = "";
        public string YoutubeLiveId { get; set; } = "";
        public bool StreamChatEnabled { get; set; } = false;
        public bool ShowIslandChatPopups { get; set; } = true;
        public bool ShowObsRecPill { get; set; } = true;

        // 8. Gaming (DISABLEABLE GAME OVERLAY: Master Toggle + Off/SlimStrip/Normal)
        public bool GameOverlayMasterEnabled { get; set; } = true;
        public GameOverlayMode GameOverlayMode { get; set; } = GameOverlayMode.SlimStrip;
        public bool GameStripShowFps { get; set; } = true;
        public bool GameStripShowCpu { get; set; } = true;
        public bool GameStripShowGpu { get; set; } = true;
        public bool GameStripShowRam { get; set; } = true;
        public bool GameStripShowTemps { get; set; } = true;
        public bool GameStripShowNet { get; set; } = true;
        public bool GameStripNetInMbps { get; set; } = true;
        public bool GameStripShowBackground { get; set; } = true;
        public List<string> GameRulesAllowList { get; set; } = new() { "VALORANT-Win64-Shipping.exe", "cs2.exe", "javaw.exe", "hl2.exe" };

        // 9. Tools (PC Deck, Bottom Notch Taskbar, Live Video Wallpaper, Shelf, API)
        public bool PcDeckEnabled { get; set; } = false; // OFF BY DEFAULT
        public int PcDeckPort { get; set; } = 47824;
        public int PcDeckTlsPort { get; set; } = 47825;
        public float DeckPointerSpeed { get; set; } = 1.0f;
        public float DeckScrollSpeed { get; set; } = 1.0f;
        public bool BottomTaskbarEnabled { get; set; } = false;
        public bool TaskbarMultiMonitor { get; set; } = false;
        public bool TaskbarAutoHideMaximized { get; set; } = true;
        public bool TaskbarDockMagnification { get; set; } = true;
        public bool TaskbarLiveHoverPreviews { get; set; } = true;
        public bool LiveVideoWallpaperEnabled { get; set; } = false;
        public string LiveVideoWallpaperPath { get; set; } = "";
        public float WallpaperHue { get; set; } = 0.0f;
        public float WallpaperSaturation { get; set; } = 100.0f;
        public float WallpaperBrightness { get; set; } = 100.0f;
        public float WallpaperContrast { get; set; } = 100.0f;
        public bool KeepCapturesOffShelf { get; set; } = false;
        public bool LoopbackApiEnabled { get; set; } = true;
        public int LoopbackApiPort { get; set; } = 47823;
    }

    // ========================================================================
    // GlobalAppState with Atomic Render-Tick Parameter Queuing
    // Dragging the Liquid Glass blur slider or switching themes NEVER disposes
    // or recreates render surfaces mid-frame; it queues atomic updates that
    // are committed safely at the start of the next render tick!
    // ========================================================================
    public sealed class GlobalAppState
    {
        public static GlobalAppState Instance { get; } = new();

        public readonly object SyncRoot = new();
        public AppSettings Settings { get; set; } = new();

        // ------------------------------------------------------------------
        // Theme cross-fade: switching surfaces eases from the theme on screen to
        // the new one over ~320 ms using wall-clock time, so the transition is
        // identical no matter which window happens to tick first.
        // ------------------------------------------------------------------
        private const float ThemeBlendDurationMs = 320.0f;
        private SurfaceTheme _themeBlendFrom = SurfaceTheme.LiquidGlass;
        private SurfaceTheme _themeBlendTo = SurfaceTheme.LiquidGlass;
        private long _themeBlendStartMs = -1;

        public SurfaceTheme ThemeBlendFrom => _themeBlendFrom;
        public SurfaceTheme ThemeBlendTo => _themeBlendTo;

        public float ThemeBlendProgress01
        {
            get
            {
                if (_themeBlendStartMs < 0) return 1.0f;
                float raw = Math.Clamp((ConfigManager.GetTickMs() - _themeBlendStartMs) / ThemeBlendDurationMs, 0.0f, 1.0f);
                return raw >= 1.0f ? 1.0f : SpringPresets.CubicEaseInOut(raw);
            }
        }

        public bool IsThemeBlending => ThemeBlendProgress01 < 0.999f;

        public ThemeSpec CurrentTheme
        {
            get
            {
                float t = ThemeBlendProgress01;
                if (t >= 0.999f) return ThemeSpec.Get(_themeBlendTo);
                return ThemeSpec.Lerp(ThemeSpec.Get(_themeBlendFrom), ThemeSpec.Get(_themeBlendTo), t);
            }
        }

        public void SnapThemeBlend(SurfaceTheme theme)
        {
            _themeBlendFrom = theme;
            _themeBlendTo = theme;
            _themeBlendStartMs = -1;
        }

        private void BeginThemeBlend(SurfaceTheme theme, bool instant)
        {
            if (theme == _themeBlendTo) return;
            _themeBlendFrom = _themeBlendTo;
            _themeBlendTo = theme;
            _themeBlendStartMs = instant ? -1 : ConfigManager.GetTickMs();
            if (instant) _themeBlendFrom = theme;
        }

        // Atomic Pending Render Parameters (prevents mid-frame slider crashes!)
        private bool _hasPendingRenderParams = false;
        private float _pendingBlurRadius = 20.0f;
        private SurfaceTheme _pendingTheme = SurfaceTheme.LiquidGlass;
        private float _pendingCornerScale = 1.0f;

        public void QueueBlurRadius(float rawRadius)
        {
            float clamped = Math.Clamp(MathF.Round(rawRadius), 0.0f, 40.0f);
            lock (SyncRoot)
            {
                _pendingBlurRadius = clamped;
                _pendingTheme = Settings.Theme;
                _pendingCornerScale = Settings.CornerRadiusScale;
                _hasPendingRenderParams = true;
            }
        }

        public void QueueTheme(SurfaceTheme theme)
        {
            lock (SyncRoot)
            {
                _pendingTheme = theme;
                _pendingBlurRadius = Math.Clamp(Settings.GlassBlurRadius, 0.0f, 40.0f);
                _pendingCornerScale = Settings.CornerRadiusScale;
                _hasPendingRenderParams = true;
            }
        }

        public void QueueCornerRadiusScale(float scale)
        {
            float clamped = Math.Clamp(scale, 0.15f, 1.0f);
            lock (SyncRoot)
            {
                _pendingCornerScale = clamped;
                _pendingBlurRadius = Math.Clamp(Settings.GlassBlurRadius, 0.0f, 40.0f);
                _pendingTheme = Settings.Theme;
                _hasPendingRenderParams = true;
            }
        }

        public bool CommitPendingParametersOnRenderTick()
        {
            lock (SyncRoot)
            {
                if (!_hasPendingRenderParams) return false;
                Settings.GlassBlurRadius = Math.Clamp(_pendingBlurRadius, 0.0f, 40.0f);
                if (Settings.Theme != _pendingTheme)
                {
                    BeginThemeBlend(_pendingTheme, Settings.ReduceMotion);
                }
                Settings.Theme = _pendingTheme;
                Settings.CornerRadiusScale = Math.Clamp(_pendingCornerScale, 0.15f, 1.0f);
                _hasPendingRenderParams = false;
                return true;
            }
        }

        // Live UI & Spring Animation State
        public bool IslandOpen = false;
        public bool IslandHidden = false;
        public bool IsRecordingScreen = false;
        public long RecordingStartMs = 0;
        public bool IsFullscreenGameActive = false;
        public bool IsGameCursorShowing = true; // False when mouse is locked/hidden for aiming in a game!
        public string ActiveFullscreenApp = "";

        public PanelId ActivePanel = PanelId.Music;
        public PanelId PreviousPanel = PanelId.Music;
        public bool ShowCalendarInIsland = false; // Newly shipped Calendar in the island view!
        public bool ShowDeckInIsland = false;     // Dedicated PC Deck view in the island!
        public int CalendarMonthOffset = 0;
        public ClipboardCategory ActiveClipCat = ClipboardCategory.All;
        public SettingsCategory ActiveSettingsCat = SettingsCategory.Appearance;
        public string SettingsSearchQuery = "";

        // 240 Hz Springs
        public readonly Spring OpenSpring = new(0.0f);
        public readonly Spring FadeSpring = new(0.0f);
        public readonly Spring PanelSpring = new(100.0f);
        public readonly Spring NotchSpring = new(100.0f);
        public readonly Spring RecSpring = new(0.0f);
        public readonly Spring FlipSpring = new(100.0f);
        public readonly Spring PlaySpring = new(0.0f);

        // Hover & Pointer State
        public bool PointerInsideIsland = false;
        public float PointerDipX = -1000.0f;
        public float PointerDipY = -1000.0f;

        // Transient Top-Center Pop-Up State
        public bool ToastActive = false;
        public TopPopupType ToastPopupType = TopPopupType.None;
        public float ToastProgress01 = 0.0f;
        public string ToastTitle = "";
        public string ToastSubtitle = "";
        public Color ToastAccent = Color.FromArgb(255, 78, 179, 255);
        public long ToastExpireMs = 0;
        public bool ToastIsReminder = false;
        public long ActiveReminderId = 0;

        // Real System State (Zero Placeholders)
        public readonly MediaSessionState Media = new();
        public readonly HardwareTelemetry Telemetry = new();
        public readonly PrivacySensorState Sensors = new();

        public string ActiveAudioRenderDeviceId = "";
        public bool AudioBaselineInitialized = false;
        public float MasterVolume = 50.0f;
        public bool MasterOutputMuted = false;
        public bool GlobalMicMuted = false;
        public float MicPeakLevel = 0.0f;
        public float MonitorBrightness = 80.0f;
        public bool BrightnessSupported = false;
        public bool WifiEnabled = true;
        public bool BluetoothEnabled = true;
        public bool NightLightEnabled = false;
        public bool FocusModeEnabled = false;
        public int PowerModeIndex = 1;
        public string LastPickedColorHex = "";

        // Collections (Zero Fake Items)
        public readonly List<PinnedWebSite> PinnedSites = new();
        public readonly List<NotificationItem> Notifications = new();
        public readonly List<AgentSessionItem> Agents = new();
        public readonly List<ChatMessageItem> ChatMessages = new();
        public readonly List<ShelfFileItem> ShelfFiles = new();
        public readonly List<ClipboardEntry> ClipboardHistory = new();
        public readonly List<TodoItem> TodoList = new();
        public readonly List<ReminderItem> Reminders = new();
        public readonly List<CustomTileData> CustomTiles = new();
        public readonly LauncherSlot[] LauncherSlots = new LauncherSlot[6];
        public readonly List<PairedDeckDevice> PairedDeckDevices = new();

        // Focus Timer
        public bool FocusTimerRunning = false;
        public int FocusTimerRemainingSec = 25 * 60;

        // OBS & Drawers
        public bool ObsRecordingActive = false;
        public long ObsRecordingStartMs = 0;
        public bool ChatDrawerVisible = false;
        public bool LyricsDrawerVisible = false;
        public bool WebDrawerVisible = false;
        public string CurrentWebDrawerUrl = "";
        public string CurrentWebDrawerTitle = "";
        public bool SendToPhoneQrActive = false;
        public string SendToPhoneUrl = "";

        // PC Deck Security & Certificate Pinning
        public bool IsNetworkPrivate = true;
        public string LocalPrivateIp = "127.0.0.1";
        public string LocalMacAddress = "00:00:00:00:00:00";
        public string PcCertFingerprint = "";
        public string ActivePairingCode = "";
        public long PairingCodeExpireMs = 0;
    }

    public static class ConfigManager
    {
        public static long GetTickMs() => Environment.TickCount64;

        // The app was renamed from NotchPixel: carry the old profile (settings,
        // pinned certificate, diagnostic log) over the first time it starts.
        private static bool _legacyAppDataMigrated = false;

        private static void MigrateLegacyAppDataOnce()
        {
            if (_legacyAppDataMigrated) return;
            _legacyAppDataMigrated = true;
            try
            {
                MigrateLegacyFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                MigrateLegacyFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            }
            catch { }
        }

        private static void MigrateLegacyFolder(string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            string legacy = Path.Combine(root, "NotchPixel");
            string current = Path.Combine(root, "OpenPixel");
            if (!Directory.Exists(legacy) || Directory.Exists(current)) return;
            try
            {
                Directory.Move(legacy, current);
            }
            catch
            {
                try
                {
                    Directory.CreateDirectory(current);
                    foreach (string file in Directory.GetFiles(legacy))
                        File.Copy(file, Path.Combine(current, Path.GetFileName(file)), true);
                }
                catch { }
            }
        }

        public static string GetAppDataDirectory()
        {
            MigrateLegacyAppDataOnce();
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    string dir = Path.Combine(localAppData, "OpenPixel");
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".write_probe");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return dir;
                }
            }
            catch { }

            string fallback = Path.Combine(AppContext.BaseDirectory, "OpenPixelData");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        public static string GetConfigFilePath() => Path.Combine(GetAppDataDirectory(), "settings.json");

        public static string GetDiagnosticLogPath()
        {
            MigrateLegacyAppDataOnce();
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                {
                    string dir = Path.Combine(appData, "OpenPixel");
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "openpixel.log");
                }
            }
            catch { }
            return Path.Combine(GetAppDataDirectory(), "openpixel.log");
        }

        public static void InitializeDefaults(GlobalAppState state)
        {
            lock (state.SyncRoot)
            {
                // Ensure Liquid Glass is the default theme out of the box!
                state.Settings.Theme = SurfaceTheme.LiquidGlass;
                state.Settings.GlassBlurRadius = Math.Clamp(state.Settings.GlassBlurRadius, 0.0f, 40.0f);
                state.Settings.ConfigRevision = Math.Max(state.Settings.ConfigRevision, 2);
                state.NotchSpring.Snap(state.Settings.Shape == IslandShape.Notch ? 100.0f : 0.0f);

                // Zero placeholders anywhere
                state.Media.HasSession = false;
                state.Media.IsPlaying = false;
                state.Media.Title = "";
                state.Media.Artist = "";

                if (state.PinnedSites.Count == 0)
                {
                    state.PinnedSites.Add(new PinnedWebSite { Title = "YT Music", Url = "https://music.youtube.com", BadgeColor = Color.FromArgb(255, 255, 46, 76) });
                    state.PinnedSites.Add(new PinnedWebSite { Title = "YouTube",  Url = "https://www.youtube.com",   BadgeColor = Color.FromArgb(255, 255, 0, 51) });
                    state.PinnedSites.Add(new PinnedWebSite { Title = "Spotify",  Url = "https://open.spotify.com",  BadgeColor = Color.FromArgb(255, 30, 215, 96) });
                }

                for (int i = 0; i < 6; ++i)
                {
                    state.LauncherSlots[i] ??= new LauncherSlot { Label = "+ Add", TargetPath = "" };
                }
            }
        }

        public static bool Load(GlobalAppState state)
        {
            InitializeDefaults(state);
            try
            {
                string path = GetConfigFilePath();
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return false;

                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    lock (state.SyncRoot)
                    {
                        loaded.GlassBlurRadius = Math.Clamp(loaded.GlassBlurRadius, 0.0f, 40.0f);
                        loaded.CornerRadiusScale = Math.Clamp(loaded.CornerRadiusScale, 0.15f, 1.0f);
                        loaded.HoverOpenDelaySec = Math.Clamp(loaded.HoverOpenDelaySec, 0.0f, 1.5f);
                        loaded.TextScale = Math.Clamp(loaded.TextScale, 0.85f, 1.5f);

                        // One-time migration: earlier configs may have saved a non-glass
                        // theme or an extreme blur value. Re-apply the Liquid Glass default.
                        if (loaded.ConfigRevision < 2)
                        {
                            loaded.Theme = SurfaceTheme.LiquidGlass;
                            loaded.GlassBlurRadius = 20.0f;
                            loaded.ConfigRevision = 2;
                        }

                        state.Settings = loaded;
                        state.SnapThemeBlend(loaded.Theme);
                        state.NotchSpring.Snap(loaded.Shape == IslandShape.Notch ? 100.0f : 0.0f);
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static bool SaveAtomic(GlobalAppState state)
        {
            try
            {
                string finalPath = GetConfigFilePath();
                string tmpPath = finalPath + ".tmp";
                string json;
                lock (state.SyncRoot)
                {
                    state.Settings.GlassBlurRadius = Math.Clamp(state.Settings.GlassBlurRadius, 0.0f, 40.0f);
                    state.Settings.CornerRadiusScale = Math.Clamp(state.Settings.CornerRadiusScale, 0.15f, 1.0f);
                    json = JsonSerializer.Serialize(state.Settings, new JsonSerializerOptions { WriteIndented = true });
                }
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, finalPath, overwrite: true);
                return true;
            }
            catch
            {
                try
                {
                    string finalPath = GetConfigFilePath();
                    string json = JsonSerializer.Serialize(state.Settings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(finalPath, json);
                    return true;
                }
                catch { return false; }
            }
        }
    }
}
