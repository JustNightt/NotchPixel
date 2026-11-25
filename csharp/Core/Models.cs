using System;
using System.Collections.Generic;
using System.Drawing;

namespace OpenPixel.Core
{
    public static class DipMetrics
    {
        public const float PillW = 212.0f;
        public const float PillH = 36.0f;
        public const float RecPillW = 250.0f;
        public const float CardW = 400.0f;
        public const float FlareR = 10.0f;
        public const float TopOffsetIsland = 6.0f;
        public const float TopOffsetNotch = 0.0f;

        public const float HMusic = 150.0f;
        public const float HChat = 150.0f;
        public const float HShelf = 156.0f;
        public const float HLauncher = 160.0f;
        public const float HCalls = 176.0f;
        public const float HWidgets = 192.0f;
        public const float HClipboard = 196.0f;
        public const float HControls = 204.0f;
        public const float HNotifications = 232.0f;
        public const float HWeb = 232.0f;
    }

    public enum IslandShape { Island = 0, Notch = 1 }
    public enum EdgePlacement { TopCenter = 0, LeftEdge = 1, RightEdge = 2 }

    public enum SurfaceTheme
    {
        LiquidGlass = 0,
        CandyGlass = 1,
        Acrylic = 2,
        Black = 3,
        PureBlack = 4,
        Graphite = 5,
        PureWhite = 6,
        Rose = 7,
        Lavender = 8,
        Peach = 9,
        Sakura = 10
    }

    public enum ProgressBarStyle
    {
        Wave = 0,
        Pixels = 1,
        Comet = 2,
        SongShape = 3,
        Simple = 4,
        Hearts = 5
    }

    public enum VisualizerStyle { Pixels = 0, Bars = 1 }
    public enum CardBackgroundMode { AlbumColours = 0, Plain = 1 }
    public enum MotionSpeed { Calm = 0, Normal = 1, Quick = 2 }

    public enum RestingInfoMode
    {
        MediaOnly = 0,
        Time = 1,
        Date = 2,
        Weather = 3,
        Battery = 4,
        CpuTemp = 5,
        NextEvent = 6,
        PixelPet = 7
    }

    public enum PanelId
    {
        Music = 0,
        Web = 1,
        Notifications = 2,
        Chat = 3,
        Shelf = 4,
        Clipboard = 5,
        Widgets = 6,
        Controls = 7,
        Launcher = 8,
        Calls = 9
    }

    public enum ClipboardCategory
    {
        All = 0,
        Text = 1,
        Links = 2,
        Images = 3,
        Files = 4,
        Snippets = 5
    }

    public enum SettingsCategory
    {
        General = 0,
        Accessibility = 1,
        Appearance = 2,
        Island = 3,
        Panels = 4,
        Widgets = 5,
        Calls = 6,
        Streaming = 7,
        Gaming = 8,
        AiAgents = 9,
        Tools = 10,
        Keys = 11,
        About = 12
    }

    public enum GameOverlayMode
    {
        OffHideCompletely = 0,
        SlimStrip = 1,
        Normal = 2
    }

    public enum WidgetType
    {
        DayClock = 0,
        Clock = 1,
        WorldClock = 2,
        MinimalWeather = 3,
        Calendar = 4,
        TodoList = 5,
        Photos = 6,
        Batteries = 7,
        FocusTimer = 8,
        PcStats = 9,
        AnalogClock = 10,
        PlainWeather = 11,
        CustomTile = 12
    }

    public enum TopPopupType
    {
        None = 0,
        Volume = 1,
        Brightness = 2,
        MicMute = 3,
        LockKey = 4,
        ColorPicker = 5,
        AgentNudge = 6,
        Reminder = 7,
        Notice = 8
    }

    public sealed class ThemeSpec
    {
        public string Name { get; init; } = "";
        public Color Fill { get; init; }
        public Color Edge { get; init; }
        public Color Accent { get; init; }
        public Color TextPrimary { get; init; }
        public Color TextMuted { get; init; }
        public Color TextFaint { get; init; }
        public ProgressBarStyle DefaultBar { get; init; }
        public bool IsGlass { get; init; }
        public bool IsLight { get; init; }

        public static float RelativeLuminance(Color c)
        {
            static float Lin(float v) => (v <= 0.03928f) ? (v / 12.92f) : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
            return 0.2126f * Lin(c.R / 255f) + 0.7152f * Lin(c.G / 255f) + 0.0722f * Lin(c.B / 255f);
        }

        public static Color AdaptiveInkForBackground(Color bg)
        {
            return RelativeLuminance(bg) > 0.42f
                ? Color.FromArgb(255, 12, 12, 14)     // --ink: #0C0C0E
                : Color.FromArgb(255, 239, 237, 232); // --paper: #EFEDE8
        }

        // Soft themes animate ambient particles; used to decide when the 60 fps
        // island loop has to keep running.
        public static bool HasAmbience(SurfaceTheme t) =>
            t == SurfaceTheme.Rose || t == SurfaceTheme.Lavender ||
            t == SurfaceTheme.Peach || t == SurfaceTheme.Sakura;

        private static Color LerpColor(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)MathF.Round(a.A + (b.A - a.A) * t),
                (int)MathF.Round(a.R + (b.R - a.R) * t),
                (int)MathF.Round(a.G + (b.G - a.G) * t),
                (int)MathF.Round(a.B + (b.B - a.B) * t));
        }

        // Blends two surfaces so a theme switch cross-fades instead of hard-cutting.
        public static ThemeSpec Lerp(ThemeSpec a, ThemeSpec b, float t)
        {
            if (t >= 0.999f || ReferenceEquals(a, b)) return b;
            if (t <= 0.001f) return a;
            return new ThemeSpec
            {
                Name = t < 0.5f ? a.Name : b.Name,
                Fill = LerpColor(a.Fill, b.Fill, t),
                Edge = LerpColor(a.Edge, b.Edge, t),
                Accent = LerpColor(a.Accent, b.Accent, t),
                TextPrimary = LerpColor(a.TextPrimary, b.TextPrimary, t),
                TextMuted = LerpColor(a.TextMuted, b.TextMuted, t),
                TextFaint = LerpColor(a.TextFaint, b.TextFaint, t),
                DefaultBar = t < 0.5f ? a.DefaultBar : b.DefaultBar,
                IsGlass = t < 0.5f ? a.IsGlass : b.IsGlass,
                IsLight = t < 0.5f ? a.IsLight : b.IsLight
            };
        }

        private static readonly ThemeSpec[] Specs = new[]
        {
            // 0: Liquid Glass (DEFAULT OUT OF THE BOX)
            new ThemeSpec
            {
                Name = "Liquid Glass",
                Fill = Color.FromArgb(135, 18, 20, 30),
                Edge = Color.FromArgb(68, 255, 255, 255),
                Accent = Color.FromArgb(255, 255, 255, 255),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 185, 182, 174),
                TextFaint = Color.FromArgb(255, 135, 133, 128),
                DefaultBar = ProgressBarStyle.Wave,
                IsGlass = true,
                IsLight = false
            },
            // 1: Candy glass
            new ThemeSpec
            {
                Name = "Candy glass",
                Fill = Color.FromArgb(130, 52, 26, 58),
                Edge = Color.FromArgb(92, 255, 210, 240),
                Accent = Color.FromArgb(255, 255, 158, 207),
                TextPrimary = Color.FromArgb(255, 248, 242, 246),
                TextMuted = Color.FromArgb(255, 225, 195, 218),
                TextFaint = Color.FromArgb(255, 180, 148, 172),
                DefaultBar = ProgressBarStyle.Hearts,
                IsGlass = true,
                IsLight = false
            },
            // 2: Windows 11 Acrylic
            new ThemeSpec
            {
                Name = "Acrylic",
                Fill = Color.FromArgb(196, 22, 22, 26),
                Edge = Color.FromArgb(38, 255, 255, 255),
                Accent = Color.FromArgb(255, 255, 255, 255),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 185, 182, 174),
                TextFaint = Color.FromArgb(255, 125, 123, 118),
                DefaultBar = ProgressBarStyle.Wave,
                IsGlass = false,
                IsLight = false
            },
            // 3: Black
            new ThemeSpec
            {
                Name = "Black",
                Fill = Color.FromArgb(255, 11, 11, 12),
                Edge = Color.FromArgb(22, 255, 255, 255),
                Accent = Color.FromArgb(255, 255, 255, 255),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 185, 182, 174),
                TextFaint = Color.FromArgb(255, 125, 123, 118),
                DefaultBar = ProgressBarStyle.Wave,
                IsGlass = false,
                IsLight = false
            },
            // 4: Pure black (OLED)
            new ThemeSpec
            {
                Name = "Pure black",
                Fill = Color.FromArgb(255, 0, 0, 0),
                Edge = Color.FromArgb(14, 255, 255, 255),
                Accent = Color.FromArgb(255, 255, 255, 255),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 185, 182, 174),
                TextFaint = Color.FromArgb(255, 125, 123, 118),
                DefaultBar = ProgressBarStyle.Wave,
                IsGlass = false,
                IsLight = false
            },
            // 5: Graphite
            new ThemeSpec
            {
                Name = "Graphite",
                Fill = Color.FromArgb(255, 28, 28, 30),
                Edge = Color.FromArgb(22, 255, 255, 255),
                Accent = Color.FromArgb(255, 255, 255, 255),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 185, 182, 174),
                TextFaint = Color.FromArgb(255, 125, 123, 118),
                DefaultBar = ProgressBarStyle.Wave,
                IsGlass = false,
                IsLight = false
            },
            // 6: Pure white
            new ThemeSpec
            {
                Name = "Pure white",
                Fill = Color.FromArgb(255, 247, 247, 245),
                Edge = Color.FromArgb(32, 12, 12, 14),
                Accent = Color.FromArgb(255, 12, 12, 14),
                TextPrimary = Color.FromArgb(255, 12, 12, 14),
                TextMuted = Color.FromArgb(255, 78, 78, 84),
                TextFaint = Color.FromArgb(255, 128, 128, 134),
                DefaultBar = ProgressBarStyle.Simple,
                IsGlass = false,
                IsLight = true
            },
            // 7: Rosé
            new ThemeSpec
            {
                Name = "Ros\u00E9",
                Fill = Color.FromArgb(255, 58, 23, 43),
                Edge = Color.FromArgb(76, 232, 180, 160),
                Accent = Color.FromArgb(255, 255, 143, 177),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 224, 190, 202),
                TextFaint = Color.FromArgb(255, 170, 132, 146),
                DefaultBar = ProgressBarStyle.Hearts,
                IsGlass = false,
                IsLight = false
            },
            // 8: Lavender
            new ThemeSpec
            {
                Name = "Lavender",
                Fill = Color.FromArgb(255, 39, 29, 64),
                Edge = Color.FromArgb(76, 195, 166, 255),
                Accent = Color.FromArgb(255, 195, 166, 255),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 205, 194, 230),
                TextFaint = Color.FromArgb(255, 150, 138, 178),
                DefaultBar = ProgressBarStyle.Wave,
                IsGlass = false,
                IsLight = false
            },
            // 9: Peach
            new ThemeSpec
            {
                Name = "Peach",
                Fill = Color.FromArgb(255, 58, 34, 28),
                Edge = Color.FromArgb(76, 255, 200, 168),
                Accent = Color.FromArgb(255, 255, 179, 138),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 228, 196, 180),
                TextFaint = Color.FromArgb(255, 172, 140, 126),
                DefaultBar = ProgressBarStyle.Wave,
                IsGlass = false,
                IsLight = false
            },
            // 10: Sakura
            new ThemeSpec
            {
                Name = "Sakura",
                Fill = Color.FromArgb(255, 44, 21, 34),
                Edge = Color.FromArgb(76, 255, 196, 210),
                Accent = Color.FromArgb(255, 255, 183, 197),
                TextPrimary = Color.FromArgb(255, 239, 237, 232),
                TextMuted = Color.FromArgb(255, 226, 192, 202),
                TextFaint = Color.FromArgb(255, 168, 134, 144),
                DefaultBar = ProgressBarStyle.Wave,
                IsGlass = false,
                IsLight = false
            }
        };

        public static ThemeSpec Get(SurfaceTheme t)
        {
            int idx = (int)t;
            if (idx < 0 || idx >= Specs.Length) idx = 0;
            return Specs[idx];
        }

        public static float GetExpandedHeight(PanelId id) => id switch
        {
            PanelId.Music => DipMetrics.HMusic,
            PanelId.Web => DipMetrics.HWeb,
            PanelId.Notifications => DipMetrics.HNotifications,
            PanelId.Chat => DipMetrics.HChat,
            PanelId.Shelf => DipMetrics.HShelf,
            PanelId.Clipboard => DipMetrics.HClipboard,
            PanelId.Widgets => DipMetrics.HWidgets,
            PanelId.Controls => DipMetrics.HControls,
            PanelId.Launcher => DipMetrics.HLauncher,
            PanelId.Calls => DipMetrics.HCalls,
            _ => DipMetrics.HMusic
        };
    }

    // ========================================================================
    // Domain Models (ZERO PLACEHOLDERS — Clean Idle Defaults)
    // ========================================================================
    public sealed class LyricLine
    {
        public double TimestampSec { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class MediaSessionState
    {
        public bool HasSession { get; set; } = false; // Strictly false when nothing plays!
        public bool IsPlaying { get; set; } = false;
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string SourceAppId { get; set; } = "";
        public string SourceAppName { get; set; } = "";
        public double PositionSec { get; set; } = 0.0;
        public double DurationSec { get; set; } = 0.0;
        public Color DominantColor { get; set; } = Color.FromArgb(255, 78, 179, 255);
        public Color SecondaryColor { get; set; } = Color.FromArgb(255, 26, 122, 240);
        public List<LyricLine> Lyrics { get; set; } = new();
        public int ActiveLyricIndex { get; set; } = -1;
    }

    public sealed class PinnedWebSite
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public Color BadgeColor { get; set; } = Color.FromArgb(255, 26, 122, 240);
    }

    public sealed class NotificationItem
    {
        public long Id { get; set; }
        public string AppName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string TimeStr { get; set; } = "";
        public string RelativeStr { get; set; } = "";
        public Color AccentColor { get; set; } = Color.FromArgb(255, 58, 130, 247);
    }

    public sealed class AgentSessionItem
    {
        public string AgentType { get; set; } = ""; // "Free Buff", "Claude Code", "Codex", "Antigravity", "Hermes", "Custom"
        public string Workspace { get; set; } = "Local PC";
        public string CurrentStep { get; set; } = "";
        public string Status { get; set; } = "Working"; // "Working", "Almost done", "Needs input", "Done"
        public int ProgressPercent { get; set; } = -1;
        public bool IsAlmostDone { get; set; } = false;
        public bool NeedsInput { get; set; } = false;
        public int ProcessId { get; set; } = 0;
        public long UpdatedMs { get; set; }
    }

    public sealed class ChatMessageItem
    {
        public string Platform { get; set; } = "Twitch";
        public string Username { get; set; } = "";
        public string Message { get; set; } = "";
        public Color UserColor { get; set; } = Color.FromArgb(255, 168, 112, 255);
    }

    public sealed class ShelfFileItem
    {
        public string FullPath { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Extension { get; set; } = "";
        public long FileSize { get; set; }
        public bool IsImage { get; set; }
        public bool Selected { get; set; }
    }

    public sealed class ClipboardEntry
    {
        public long Id { get; set; }
        public ClipboardCategory Category { get; set; } = ClipboardCategory.Text;
        public string Text { get; set; } = "";
        public string Preview { get; set; } = "";
        public string SourceInfo { get; set; } = "Clipboard";
        public string OcrText { get; set; } = "";
        public bool IsCurrent { get; set; }
        public bool IsPinned { get; set; }
        public long TimestampMs { get; set; }
    }

    public sealed class TodoItem
    {
        public string Text { get; set; } = "";
        public bool Done { get; set; }
    }

    public sealed class ReminderItem
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public long DueTimestampMs { get; set; }
        public string RepeatRule { get; set; } = "None";
        public bool Triggered { get; set; }
        public bool Completed { get; set; }
    }

    public sealed class CustomTileData
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Value { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public Color Accent { get; set; } = Color.FromArgb(255, 78, 179, 255);
    }

    public sealed class LauncherSlot
    {
        public string Label { get; set; } = "+ Add";
        public string TargetPath { get; set; } = "";
        public Color BadgeColor { get; set; } = Color.FromArgb(255, 64, 156, 255);
    }

    public sealed class PairedDeckDevice
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string Model { get; set; } = "Android";
        public string OsVersion { get; set; } = "Android";
        public string AppVersion { get; set; } = "1.0.5";
        public string IpAddress { get; set; } = "";
        public string KeyFingerprint { get; set; } = "";
        public string AuthToken { get; set; } = "";
        public long PairedAtMs { get; set; }
    }

    public sealed class HardwareTelemetry
    {
        public float Fps { get; set; } = 60.0f;
        public float CpuPercent { get; set; } = 0.0f;
        public float CpuTempC { get; set; } = 0.0f;
        public bool CpuTempAvailable { get; set; } = false; // Never fake motherboard temps on desktops!
        public float GpuPercent { get; set; } = 0.0f;
        public float GpuTempC { get; set; } = 0.0f;
        public bool GpuTempAvailable { get; set; } = false;
        public float RamPercent { get; set; } = 0.0f;
        public float StoragePercent { get; set; } = 0.0f;
        public float NetDownKbps { get; set; } = 0.0f;
        public float NetUpKbps { get; set; } = 0.0f;
        public int BatteryPercent { get; set; } = 100;
        public bool HasBattery { get; set; } = false;
        public bool IsCharging { get; set; } = true;
    }

    public sealed class PrivacySensorState
    {
        public bool MicActive { get; set; }
        public string MicAppName { get; set; } = "";
        public long MicCallStartMs { get; set; }

        public bool CamActive { get; set; }
        public string CamAppName { get; set; } = "";

        public bool ScreenActive { get; set; }
        public string ScreenAppName { get; set; } = "";
    }
}
