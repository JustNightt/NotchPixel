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
    // One pinned desktop widget: type, last position and scale, restored on launch.
    public sealed class WidgetPinRecord
    {
        public int Type { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int ScalePercent { get; set; } = 100;
    }

    public sealed class AppSettings
    {
        // Bumped when defaults must be re-applied to an existing settings.json.
        public int ConfigRevision { get; set; } = 2;

        // Glass that fails is drawn as solid Black instead of crashing. A black
        // surface carries no wallpaper detail, so it is not capture-excluded.
        public bool FallbackBlackOnGlassCrash { get; set; } = false;

        // Desktop widget layout survives restarts.
        public List<WidgetPinRecord> PinnedWidgets { get; set; } = new();

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
        // Zero follows the active theme; otherwise this color overrides its accent.
        public int AccentColorArgb { get; set; } = 0;
        public MotionSpeed MotionSpeed { get; set; } = MotionSpeed.Normal;
        public float HoverOpenDelaySec { get; set; } = 0.15f;
        public bool ReopenOnLastTab { get; set; } = true;
        public bool NotchAutoHide { get; set; } = false; // OFF by default: notch always shows
        public RestingInfoMode RestingMode { get; set; } = RestingInfoMode.MediaOnly;
        public bool ShowSongTitleInClosedPill { get; set; } = false;
        public bool HideFromScreenshots { get; set; } = false;
        public SurfaceTheme Theme { get; set; } = SurfaceTheme.LiquidGlass; // Default out of the box!
        public float GlassBlurRadius { get; set; } = 20.0f; // Clamped [0..40]
        public ProgressBarStyle ProgressBar { get; set; } = ProgressBarStyle.Wave;
        public VisualizerStyle Visualizer { get; set; } = VisualizerStyle.Pixels;
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
        public int DayClockAccentColorArgb { get; set; } = Color.FromArgb(255, 232, 138, 42).ToArgb();
        public bool DayClockShowWeekday { get; set; } = true;
        public bool DayClockShowDate { get; set; } = true;
        public bool DayClockShowTime { get; set; } = true;
        public bool DayClockShowDashes { get; set; } = true;
        public bool DayClockShowNowPlaying { get; set; } = true;
        public bool DayClockShowWeather { get; set; } = true;
        public bool DayClockShowRecycleBin { get; set; } = true;
        public int DayClockLetterSpacing { get; set; } = 16;

        // 6. Calls & Voice
        public bool ShowGlobalMuteNotice { get; set; } = true;
        public bool ShowVoiceWaveBar { get; set; } = true;
        public string UpcomingMeetingTitle { get; set; } = "No upcoming events";
        public string UpcomingMeetingTime { get; set; } = "Calendar synced locally";
        public string UpcomingMeetingUrl { get; set; } = "";

        // Editable Global Shortcuts (format: "Ctrl+Alt+N")
        public string HotkeyOpenIsland { get; set; } = "Ctrl+Alt+N";
        public string HotkeyPlayPause { get; set; } = "Ctrl+Alt+P";
        public string HotkeyMuteMic { get; set; } = "Ctrl+Alt+M";
        public string HotkeyHideIsland { get; set; } = "Ctrl+Alt+H";
        public string HotkeyScreenshot { get; set; } = "Ctrl+Alt+S";
        public string HotkeyColorPicker { get; set; } = "Ctrl+Alt+C";
        public bool SyncThemeToAudioColor { get; set; } = false;

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
        public bool TaskbarAutoHideMaximized { get; set; } = false; // OFF by default: always show
        public bool TaskbarDockMagnification { get; set; } = true;
        public bool TaskbarLiveHoverPreviews { get; set; } = true;
        public bool LiveVideoWallpaperEnabled { get; set; } = false;
        public string LiveVideoWallpaperPath { get; set; } = "";
        public float WallpaperHue { get; set; } = 0.0f;
        public float WallpaperSaturation { get; set; } = 100.0f;
        public float WallpaperBrightness { get; set; } = 100.0f;
        public float WallpaperContrast { get; set; } = 100.0f;
        public bool PauseVideoWallpaperWhenCovered { get; set; } = true;
        public string PhotosFolder { get; set; } = "";
        public List<string> StockSymbols { get; set; } = new() { "AAPL", "MSFT", "NVDA" };
        public bool KeepCapturesOffShelf { get; set; } = false;
        public bool LoopbackApiEnabled { get; set; } = true;
        public int LoopbackApiPort { get; set; } = 47823;
        public List<LauncherSlotConfig> LauncherSlots { get; set; } = new();
        public List<string> CustomCodingAgents { get; set; } = new();
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
                ThemeSpec current = t >= 0.999f
                    ? ThemeSpec.Get(_themeBlendTo)
                    : ThemeSpec.Lerp(ThemeSpec.Get(_themeBlendFrom), ThemeSpec.Get(_themeBlendTo), t);

                bool audioSync = Settings.SyncThemeToAudioColor && Media.HasSession;
                if (!audioSync && Settings.AccentColorArgb == 0) return current;

                Color accent = audioSync
                    ? Media.DominantColor
                    : Color.FromArgb(Settings.AccentColorArgb);
                // Ensure audio-synced accent is bright & legible on dark surfaces
                if (audioSync && !current.IsLight)
                {
                    int maxC = Math.Max(accent.R, Math.Max(accent.G, accent.B));
                    if (maxC < 150)
                    {
                        float boost = 165.0f / Math.Max(1, maxC);
                        accent = Color.FromArgb(255,
                            Math.Min(255, (int)(accent.R * boost)),
                            Math.Min(255, (int)(accent.G * boost)),
                            Math.Min(255, (int)(accent.B * boost)));
                    }
                }

                Color edge = audioSync
                    ? Color.FromArgb(Math.Max(current.Edge.A, (byte)76), accent.R, accent.G, accent.B)
                    : current.Edge;
                Color muted = audioSync
                    ? Color.FromArgb(current.TextMuted.A,
                        (current.TextMuted.R * 3 + accent.R) / 4,
                        (current.TextMuted.G * 3 + accent.G) / 4,
                        (current.TextMuted.B * 3 + accent.B) / 4)
                    : current.TextMuted;

                return new ThemeSpec
                {
                    Name = current.Name,
                    Fill = current.Fill,
                    Edge = edge,
                    Accent = accent,
                    TextPrimary = current.TextPrimary,
                    TextMuted = muted,
                    TextFaint = current.TextFaint,
                    DefaultBar = current.DefaultBar,
                    IsGlass = current.IsGlass,
                    IsLight = current.IsLight
                };
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
        public PanelId ShownPanel = PanelId.Music;
        public bool ShowCalendarInIsland = false; // Newly shipped Calendar in the island view!
        public bool ShowDeckInIsland = false;     // Dedicated PC Deck view in the island!
        public bool ShowLyricsInIsland = false;   // Expand inline synced lyrics inside Music card
        public int CalendarMonthOffset = 0;
        public ClipboardCategory ActiveClipCat = ClipboardCategory.All;
        public SettingsCategory ActiveSettingsCat = SettingsCategory.Appearance;
        public string SettingsSearchQuery = "";

        // Music card interactive animation state (matching notchpixel.com island.js)
        public float SwapAtSec = -1000.0f;
        public int SwapDir = 1;
        public float PrevKickSec = -1000.0f;
        public float NextKickSec = -1000.0f;
        public float PlayKickSec = -1000.0f;
        public float MarqueeSec = 0.0f;
        public string LastMediaTrackKey = "";
        public bool PlayButtonPressed = false;
        public float AudioPeakSmoothed = 0.0f;
        public float MicPeakSmoothed = 0.0f;

        public void SelectPanel(PanelId pid)
        {
            if (pid == ActivePanel && pid == ShownPanel) return;
            PreviousPanel = ActivePanel;
            ActivePanel = pid;
            if (Settings.ReduceMotion)
            {
                ShownPanel = pid;
                PanelSpring.Snap(100.0f);
                float h = ThemeSpec.GetExpandedHeight(pid) + (pid == PanelId.Music && ShowLyricsInIsland ? 76.0f : 0.0f);
                CardHeightSpring.Snap(h);
            }
            else
            {
                PanelSpring.SetTarget(0.0f, false);
            }
        }

        // 240 Hz Springs
        public readonly Spring OpenSpring = new(0.0f);
        public readonly Spring FadeSpring = new(0.0f);
        public readonly Spring PanelSpring = new(100.0f);
        public readonly Spring NotchSpring = new(100.0f);
        public readonly Spring RecSpring = new(0.0f);
        public readonly Spring FlipSpring = new(100.0f);
        public readonly Spring PlaySpring = new(0.0f);
        // The demo eases the play/pause glyph at rate 14 (eased.morph), not with a spring.
        public float MorphEase = 1.0f;
        // The demo fades the cover-colour backdrop at rate 5 (eased.bgK).
        public float AlbumBgEase = 1.0f;
        public readonly Spring CardWidthSpring = new(DipMetrics.CardW);
        public readonly Spring CardHeightSpring = new(DipMetrics.HMusic);

        // Smooth Hover Alphas (key -> 0..1)
        public readonly Dictionary<string, float> HoverAlphas = new();
        public readonly Dictionary<string, float> HoverTargets = new();

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
        public static string GetClipboardPinsPath() => Path.Combine(GetAppDataDirectory(), "clipboard_pins.json");
        public static string GetRemindersPath() => Path.Combine(GetAppDataDirectory(), "reminders.json");

        public static string GetClipboardImagesDirectory()
        {
            string dir = Path.Combine(GetAppDataDirectory(), "clipboard_images");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public static string GetCapturesDirectory()
        {
            try
            {
                string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (!string.IsNullOrEmpty(pics))
                {
                    string cap = Path.Combine(pics, "OpenPixel Captures");
                    Directory.CreateDirectory(cap);
                    return cap;
                }
            }
            catch { }
            string fallback = Path.Combine(GetAppDataDirectory(), "Captures");
            try { Directory.CreateDirectory(fallback); } catch { }
            return fallback;
        }

        // Launch-at-startup is a real per-user Run entry, not just a stored flag.
        public static void ApplyLaunchAtStartup(bool enabled)
        {
            try
            {
                using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                if (key == null) return;
                if (enabled)
                {
                    string exe = Path.Combine(AppContext.BaseDirectory, "OpenPixel.exe");
                    key.SetValue("OpenPixel", "\"" + exe + "\"");
                }
                else
                {
                    key.DeleteValue("OpenPixel", false);
                }
            }
            catch { }
        }

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
                state.Telemetry.Fps = 0.0f;
                state.Telemetry.FpsAvailable = false;
                state.Telemetry.GpuPercent = 0.0f;
                state.Telemetry.GpuPercentAvailable = false;

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
            bool loadedSettings = false;
            try
            {
                string path = GetConfigFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
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
                                SyncSettingsToLauncherSlots(state);
                            }
                            loadedSettings = true;
                        }
                    }
                }
            }
            catch { }
            LoadClipboardPins(state);
            LoadReminders(state);
            return loadedSettings;
        }

        private static string _lastPinnedDigest = "";
        private static bool _clipboardPinsLoadFailed;
        private static string _lastReminderDigest = "";

        public static void SyncSettingsToLauncherSlots(GlobalAppState state)
        {
            if (state?.Settings == null) return;
            for (int i = 0; i < 6; ++i)
            {
                state.LauncherSlots[i] ??= new LauncherSlot { Label = "+ Add", TargetPath = "" };
                if (state.Settings.LauncherSlots != null && i < state.Settings.LauncherSlots.Count && state.Settings.LauncherSlots[i] != null)
                {
                    var cfg = state.Settings.LauncherSlots[i];
                    state.LauncherSlots[i].Label = string.IsNullOrEmpty(cfg.Label) ? "+ Add" : cfg.Label;
                    state.LauncherSlots[i].TargetPath = cfg.TargetPath ?? "";
                    state.LauncherSlots[i].IconPath = cfg.IconPath ?? "";
                    state.LauncherSlots[i].BadgeColor = Color.FromArgb(cfg.BadgeColorArgb == 0
                        ? Color.FromArgb(255, 64, 156, 255).ToArgb()
                        : cfg.BadgeColorArgb);
                }
            }
        }

        public static void SyncLauncherSlotsToSettings(GlobalAppState state)
        {
            if (state?.Settings == null) return;
            state.Settings.LauncherSlots ??= new List<LauncherSlotConfig>();
            state.Settings.LauncherSlots.Clear();
            for (int i = 0; i < 6; ++i)
            {
                var slot = state.LauncherSlots[i] ?? new LauncherSlot();
                state.Settings.LauncherSlots.Add(new LauncherSlotConfig
                {
                    Label = slot.Label ?? "+ Add",
                    TargetPath = slot.TargetPath ?? "",
                    IconPath = slot.IconPath ?? "",
                    BadgeColorArgb = slot.BadgeColor.ToArgb()
                });
            }
        }

        public static bool SaveClipboardPinsAtomic(GlobalAppState state)
        {
            try
            {
                string finalPath = GetClipboardPinsPath();
                string tmpPath = finalPath + ".tmp";
                List<ClipboardEntry> pins;
                lock (state.SyncRoot)
                {
                    pins = state.ClipboardHistory.FindAll(e => e != null && (e.IsPinned || e.Category == ClipboardCategory.Snippets));
                }
                // A failed load leaves the in-memory list empty. Writing that over a
                // real pins file would destroy the user's saved pins, so never let an
                // unreadable file be replaced by an empty one.
                if (pins.Count == 0 && _clipboardPinsLoadFailed)
                {
                    LogDiagnostic("clipboard pins save skipped: the previous load failed");
                    return false;
                }

                string json = JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, finalPath, overwrite: true);
                _lastPinnedDigest = ComputePinnedDigest(pins);
                return true;
            }
            catch { return false; }
        }

        public static bool LoadClipboardPins(GlobalAppState state)
        {
            try
            {
                string path = GetClipboardPinsPath();
                if (!File.Exists(path)) { _clipboardPinsLoadFailed = false; return false; }
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) { _clipboardPinsLoadFailed = false; return false; }
                var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(json);
                if (loaded == null)
                {
                    _clipboardPinsLoadFailed = true;
                    LogDiagnostic("clipboard pins load failed: the file did not contain a list");
                    return false;
                }
                lock (state.SyncRoot)
                {
                    foreach (var entry in loaded)
                    {
                        if (entry == null) continue;
                        entry.IsCurrent = false;
                        bool exists = state.ClipboardHistory.Exists(x =>
                            x.Id == entry.Id ||
                            (x.Category == entry.Category &&
                             string.Equals(x.Text ?? "", entry.Text ?? "", StringComparison.Ordinal) &&
                             string.Equals(x.ImagePath ?? "", entry.ImagePath ?? "", StringComparison.Ordinal)));
                        if (!exists)
                        {
                            state.ClipboardHistory.Add(entry);
                        }
                        else
                        {
                            var match = state.ClipboardHistory.Find(x =>
                                x.Id == entry.Id ||
                                (x.Category == entry.Category && string.Equals(x.Text ?? "", entry.Text ?? "", StringComparison.Ordinal)));
                            if (match != null && entry.IsPinned) match.IsPinned = true;
                        }
                    }
                    _lastPinnedDigest = ComputePinnedDigest(state.ClipboardHistory.FindAll(e => e != null && (e.IsPinned || e.Category == ClipboardCategory.Snippets)));
                    _clipboardPinsLoadFailed = false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _clipboardPinsLoadFailed = true;
                LogDiagnostic("clipboard pins load failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        internal static void LogDiagnostic(string message)
        {
            try
            {
                File.AppendAllText(GetDiagnosticLogPath(),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch { }
        }

        public static bool SaveRemindersAtomic(GlobalAppState state)
        {
            try
            {
                string finalPath = GetRemindersPath();
                string tmpPath = finalPath + ".tmp";
                List<ReminderItem> snapshot;
                long tickNow = GetTickMs();
                long unixNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                lock (state.SyncRoot)
                {
                    snapshot = new List<ReminderItem>(state.Reminders.Count);
                    foreach (var rem in state.Reminders)
                    {
                        if (rem == null) continue;
                        // Keep DueUnixMs synchronized with DueTimestampMs (including UI snoozes)
                        if (rem.DueTimestampMs > 0)
                        {
                            rem.DueUnixMs = unixNow + (rem.DueTimestampMs - tickNow);
                        }
                        else if (rem.DueUnixMs > 0)
                        {
                            rem.DueTimestampMs = tickNow + (rem.DueUnixMs - unixNow);
                        }
                        snapshot.Add(rem);
                    }
                }
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, finalPath, overwrite: true);
                _lastReminderDigest = ComputeReminderDigest(snapshot);
                return true;
            }
            catch { return false; }
        }

        public static bool LoadReminders(GlobalAppState state)
        {
            try
            {
                string path = GetRemindersPath();
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return false;
                var loaded = JsonSerializer.Deserialize<List<ReminderItem>>(json);
                if (loaded == null) return false;
                long tickNow = GetTickMs();
                long unixNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                lock (state.SyncRoot)
                {
                    foreach (var rem in loaded)
                    {
                        if (rem == null) continue;
                        if (rem.DueUnixMs > 0)
                        {
                            rem.DueTimestampMs = tickNow + (rem.DueUnixMs - unixNow);
                        }
                        int idx = state.Reminders.FindIndex(r => r.Id == rem.Id);
                        if (idx >= 0) state.Reminders[idx] = rem;
                        else state.Reminders.Add(rem);
                    }
                    _lastReminderDigest = ComputeReminderDigest(state.Reminders);
                }
                return true;
            }
            catch { return false; }
        }

        public static void FlushPersistentCollectionsIfChanged(GlobalAppState state)
        {
            if (state == null) return;
            string pinDigest;
            string remDigest;
            lock (state.SyncRoot)
            {
                var pins = state.ClipboardHistory.FindAll(e => e != null && (e.IsPinned || e.Category == ClipboardCategory.Snippets));
                pinDigest = ComputePinnedDigest(pins);
                remDigest = ComputeReminderDigest(state.Reminders);
            }
            if (pinDigest != _lastPinnedDigest) SaveClipboardPinsAtomic(state);
            if (remDigest != _lastReminderDigest) SaveRemindersAtomic(state);
        }

        private static string ComputePinnedDigest(List<ClipboardEntry> pins)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in pins)
            {
                sb.Append(p.Id).Append('|').Append((int)p.Category).Append('|')
                  .Append(p.IsPinned ? '1' : '0').Append('|').Append(p.Text?.Length ?? 0).Append(';');
            }
            return sb.ToString();
        }

        private static string ComputeReminderDigest(List<ReminderItem> rems)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in rems)
            {
                if (r == null) continue;
                sb.Append(r.Id).Append('|').Append(r.DueTimestampMs).Append('|')
                  .Append(r.RepeatRule).Append('|').Append(r.Triggered ? '1' : '0')
                  .Append(r.Completed ? '1' : '0').Append(';');
            }
            return sb.ToString();
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
                    SyncLauncherSlotsToSettings(state);
                    json = JsonSerializer.Serialize(state.Settings, new JsonSerializerOptions { WriteIndented = true });
                }
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, finalPath, overwrite: true);
                SaveClipboardPinsAtomic(state);
                SaveRemindersAtomic(state);
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
