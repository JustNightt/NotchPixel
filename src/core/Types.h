#pragma once
#include <windows.h>
#include <d2d1.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <string>
#include <vector>
#include <array>
#include <cmath>
#include <cstdint>
#include <algorithm>
#include <chrono>
#include <mutex>
#include <atomic>
#include <map>
#include <functional>

namespace np {

// ============================================================================
// Core DIP Geometry Constants (Exact match to openpixel.com/assets/island.js)
// ============================================================================
constexpr float PILL_W        = 212.0f;
constexpr float PILL_H        = 36.0f;
constexpr float REC_PILL_W    = 250.0f;
constexpr float CARD_W        = 400.0f;
constexpr float FLARE_R       = 10.0f;
constexpr float TOP_OFFSET_ISLAND = 6.0f;
constexpr float TOP_OFFSET_NOTCH  = 0.0f;

// Per-Panel Expanded Card Heights (in DIPs, matching NPI_STATES)
constexpr float H_MUSIC         = 150.0f;
constexpr float H_CHAT          = 150.0f;
constexpr float H_SHELF         = 156.0f;
constexpr float H_LAUNCHER      = 160.0f;
constexpr float H_CALLS         = 176.0f;
constexpr float H_WIDGETS       = 192.0f;
constexpr float H_CLIPBOARD     = 196.0f;
constexpr float H_CONTROLS      = 204.0f;
constexpr float H_NOTIFICATIONS = 232.0f;
constexpr float H_WEB           = 232.0f;

// ============================================================================
// Enums for Shape, Placement, Themes, Tabs, Progress Bars & Modes
// ============================================================================
enum class IslandShape : int {
    Island = 0, // Floating pill/card (6 DIP top offset, all 4 corners rounded)
    Notch  = 1  // Flush top/edge notch with 10 DIP concave quarter-circle flares
};

enum class EdgePlacement : int {
    TopCenter = 0,
    LeftEdge  = 1,
    RightEdge = 2
};

enum class SurfaceTheme : int {
    LiquidGlass = 0,
    CandyGlass  = 1,
    Acrylic     = 2,
    Black       = 3,
    PureBlack   = 4,
    Graphite    = 5,
    PureWhite   = 6,
    Rose        = 7,
    Lavender    = 8,
    Peach       = 9,
    Sakura      = 10,
    Count       = 11
};

enum class ProgressBarStyle : int {
    Wave      = 0,
    Pixels    = 1,
    Comet     = 2,
    SongShape = 3,
    Simple    = 4,
    Hearts    = 5
};

enum class VisualizerStyle : int {
    Pixels = 0,
    Bars   = 1
};

enum class CardBackgroundMode : int {
    AlbumColours = 0,
    Plain        = 1
};

enum class MotionSpeed : int {
    Calm   = 0,
    Normal = 1,
    Quick  = 2
};

enum class RestingInfoMode : int {
    MediaOnly = 0,
    Time      = 1,
    Date      = 2,
    Weather   = 3,
    Battery   = 4,
    CpuTemp   = 5,
    NextEvent = 6,
    PixelPet  = 7
};

enum class PanelId : int {
    Music         = 0,
    Web           = 1,
    Notifications = 2,
    Chat          = 3,
    Shelf         = 4,
    Clipboard     = 5,
    Widgets       = 6,
    Controls      = 7,
    Launcher      = 8,
    Calls         = 9,
    Count         = 10
};

enum class ClipboardCategory : int {
    All      = 0,
    Text     = 1,
    Links    = 2,
    Images   = 3,
    Files    = 4,
    Snippets = 5
};

enum class SettingsCategory : int {
    General       = 0,
    Accessibility = 1,
    Island        = 2,
    Panels        = 3,
    Widgets       = 4,
    Calls         = 5,
    Streaming     = 6,
    Gaming        = 7,
    Tools         = 8,
    Keys          = 9,
    Count         = 10
};

enum class WidgetType : int {
    Clock           = 0,
    WorldClock      = 1,
    MinimalWeather  = 2,
    TodoList        = 3,
    Photos          = 4,
    Batteries       = 5,
    FocusTimer      = 6,
    PcStats         = 7,
    AnalogClock     = 8,
    PlainWeather    = 9,
    DayClock        = 10,
    Ping            = 11,
    PlayTime        = 12,
    ScreenTime      = 13,
    QuickNote       = 14,
    Countdown       = 15,
    Currency        = 16,
    Calculator      = 17,
    WebsiteCheck    = 18,
    AirQuality      = 19,
    SunMoon         = 20,
    DayYearProgress = 21,
    CustomTile      = 22,
    Count           = 23
};

enum class AltTabStyle : int {
    Off    = 0,
    Ring   = 1,
    Fan    = 2,
    Flip3D = 3
};

enum class TopPopupType : int {
    None        = 0,
    Volume      = 1,
    Brightness  = 2,
    MicMute     = 3,
    LockKey     = 4,
    ColorPicker = 5,
    AgentNudge  = 6,
    Reminder    = 7,
    Notice      = 8
};

// ============================================================================
// Theme Color Specification & Adaptive Text Contrast Calculator
// ============================================================================
struct ThemeSpec {
    const wchar_t* name;
    D2D1_COLOR_F fill;
    D2D1_COLOR_F edge;
    D2D1_COLOR_F accent;
    D2D1_COLOR_F textPrimary;
    D2D1_COLOR_F textMuted;
    D2D1_COLOR_F textFaint;
    ProgressBarStyle defaultBar;
    bool isGlass;
    bool isLight;
};

inline D2D1_COLOR_F Rgba(uint8_t r, uint8_t g, uint8_t b, float a = 1.0f) {
    return D2D1::ColorF(r / 255.0f, g / 255.0f, b / 255.0f, a);
}

// Relative luminance per WCAG 2.1 to pick crisp dark (#0C0C0E) or light (#EFEDE8) ink
inline float RelativeLuminance(const D2D1_COLOR_F& c) {
    auto lin = [](float v) {
        return (v <= 0.03928f) ? (v / 12.92f) : std::pow((v + 0.055f) / 1.055f, 2.4f);
    };
    return 0.2126f * lin(c.r) + 0.7152f * lin(c.g) + 0.0722f * lin(c.b);
}

inline D2D1_COLOR_F AdaptiveInkForBackground(const D2D1_COLOR_F& bg) {
    return (RelativeLuminance(bg) > 0.42f)
        ? Rgba(12, 12, 14, 1.0f)    // --ink: #0c0c0e
        : Rgba(239, 237, 232, 1.0f); // --paper: #efede8
}

inline const ThemeSpec& GetThemeSpec(SurfaceTheme t) {
    static const ThemeSpec specs[] = {
        // 0: Liquid Glass
        { L"Liquid Glass", Rgba(18, 20, 30, 0.48f), Rgba(255, 255, 255, 0.24f), Rgba(255, 255, 255, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(185, 182, 174, 1.0f), Rgba(135, 133, 128, 1.0f),
          ProgressBarStyle::Wave, true, false },
        // 1: Candy Glass
        { L"Candy glass", Rgba(52, 26, 58, 0.46f), Rgba(255, 210, 240, 0.36f), Rgba(255, 158, 207, 1.0f),
          Rgba(248, 242, 246, 1.0f), Rgba(225, 195, 218, 1.0f), Rgba(180, 148, 172, 1.0f),
          ProgressBarStyle::Hearts, true, false },
        // 2: Windows 11 Acrylic
        { L"Acrylic", Rgba(22, 22, 26, 0.72f), Rgba(255, 255, 255, 0.14f), Rgba(255, 255, 255, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(185, 182, 174, 1.0f), Rgba(125, 123, 118, 1.0f),
          ProgressBarStyle::Wave, false, false },
        // 3: Black (Default #0b0b0c)
        { L"Black", Rgba(11, 11, 12, 1.0f), Rgba(255, 255, 255, 0.08f), Rgba(255, 255, 255, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(185, 182, 174, 1.0f), Rgba(125, 123, 118, 1.0f),
          ProgressBarStyle::Wave, false, false },
        // 4: Pure Black (OLED #000000)
        { L"Pure black", Rgba(0, 0, 0, 1.0f), Rgba(255, 255, 255, 0.05f), Rgba(255, 255, 255, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(185, 182, 174, 1.0f), Rgba(125, 123, 118, 1.0f),
          ProgressBarStyle::Wave, false, false },
        // 5: Graphite (#1c1c1e)
        { L"Graphite", Rgba(28, 28, 30, 1.0f), Rgba(255, 255, 255, 0.08f), Rgba(255, 255, 255, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(185, 182, 174, 1.0f), Rgba(125, 123, 118, 1.0f),
          ProgressBarStyle::Wave, false, false },
        // 6: Pure White (Light mode #f7f7f5 with #0c0c0e ink)
        { L"Pure white", Rgba(247, 247, 245, 1.0f), Rgba(12, 12, 14, 0.12f), Rgba(12, 12, 14, 1.0f),
          Rgba(12, 12, 14, 1.0f), Rgba(78, 78, 84, 1.0f), Rgba(128, 128, 134, 1.0f),
          ProgressBarStyle::Simple, false, true },
        // 7: Rosé (#3a172b)
        { L"Ros\x00E9", Rgba(58, 23, 43, 1.0f), Rgba(232, 180, 160, 0.30f), Rgba(255, 143, 177, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(224, 190, 202, 1.0f), Rgba(170, 132, 146, 1.0f),
          ProgressBarStyle::Hearts, false, false },
        // 8: Lavender (#271d40)
        { L"Lavender", Rgba(39, 29, 64, 1.0f), Rgba(195, 166, 255, 0.30f), Rgba(195, 166, 255, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(205, 194, 230, 1.0f), Rgba(150, 138, 178, 1.0f),
          ProgressBarStyle::Wave, false, false },
        // 9: Peach (#3a221c)
        { L"Peach", Rgba(58, 34, 28, 1.0f), Rgba(255, 200, 168, 0.30f), Rgba(255, 179, 138, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(228, 196, 180, 1.0f), Rgba(172, 140, 126, 1.0f),
          ProgressBarStyle::Wave, false, false },
        // 10: Sakura (#2c1522)
        { L"Sakura", Rgba(44, 21, 34, 1.0f), Rgba(255, 196, 210, 0.30f), Rgba(255, 183, 197, 1.0f),
          Rgba(239, 237, 232, 1.0f), Rgba(226, 192, 202, 1.0f), Rgba(168, 134, 144, 1.0f),
          ProgressBarStyle::Wave, false, false }
    };
    int idx = static_cast<int>(t);
    if (idx < 0 || idx >= static_cast<int>(SurfaceTheme::Count)) idx = 3;
    return specs[idx];
}

inline float GetPanelExpandedHeight(PanelId id) {
    switch (id) {
        case PanelId::Music:         return H_MUSIC;
        case PanelId::Web:           return H_WEB;
        case PanelId::Notifications: return H_NOTIFICATIONS;
        case PanelId::Chat:          return H_CHAT;
        case PanelId::Shelf:         return H_SHELF;
        case PanelId::Clipboard:     return H_CLIPBOARD;
        case PanelId::Widgets:       return H_WIDGETS;
        case PanelId::Controls:      return H_CONTROLS;
        case PanelId::Launcher:      return H_LAUNCHER;
        case PanelId::Calls:         return H_CALLS;
        default:                     return H_MUSIC;
    }
}

// ============================================================================
// Domain Data Structures (ZERO PLACEHOLDERS — Clean Idle Defaults)
// ============================================================================
struct LyricLine {
    double timestampSec = 0.0;
    std::wstring text;
};

struct MediaSessionState {
    bool hasSession = false; // Strictly false when nothing is playing!
    bool isPlaying = false;
    std::wstring title;
    std::wstring artist;
    std::wstring album;
    std::wstring sourceAppId;
    std::wstring sourceAppName;
    double positionSec = 0.0;
    double durationSec = 0.0;
    D2D1_COLOR_F dominantColor = Rgba(78, 179, 255, 1.0f);
    D2D1_COLOR_F secondaryColor = Rgba(26, 122, 240, 1.0f);
    std::vector<uint32_t> coverPixels;
    int coverW = 0;
    int coverH = 0;
    std::vector<LyricLine> lyrics;
    int activeLyricIndex = -1;
    uint64_t trackChangeTick = 0;
};

struct PinnedWebSite {
    std::wstring title;
    std::wstring url;
    D2D1_COLOR_F badgeColor;
};

struct NotificationItem {
    uint64_t id = 0;
    std::wstring appName;
    std::wstring title;
    std::wstring body;
    std::wstring timeStr;      // "14:32"
    std::wstring relativeStr;  // "2m ago"
    uint64_t timestampMs = 0;
    D2D1_COLOR_F accentColor = Rgba(58, 130, 247, 1.0f);
    bool expanded = false;
};

// First-class support for Free Buff, Claude Code, Codex, Antigravity, Hermes, and Custom agents
struct AgentSessionItem {
    std::wstring agentType; // "Free Buff", "Claude Code", "Codex", "Antigravity", "Hermes", "Custom"
    std::wstring workspace;
    std::wstring currentStep;
    std::wstring status;    // "Working", "Almost done", "Needs input", "Done", "Idle"
    int progressPercent = -1; // 0..100 (-1 if indeterminate)
    bool isAlmostDone = false;
    bool needsInput = false;
    DWORD processId = 0;
    uint64_t updatedMs = 0;
};

struct ChatMessageItem {
    std::wstring platform; // "Twitch" or "YouTube"
    std::wstring username;
    std::wstring message;
    D2D1_COLOR_F userColor;
};

struct ShelfFileItem {
    std::wstring fullPath;
    std::wstring displayName;
    std::wstring extension;
    uint64_t fileSize = 0;
    bool isImage = false;
    bool selected = false;
};

struct ClipboardEntry {
    uint64_t id = 0;
    ClipboardCategory category = ClipboardCategory::Text;
    std::wstring text;
    std::wstring preview;
    std::wstring sourceInfo;
    std::wstring ocrText;
    std::vector<uint32_t> imageThumb;
    int thumbW = 0;
    int thumbH = 0;
    bool isCurrent = false;
    bool isPinned = false;
    uint64_t timestampMs = 0;
};

struct TodoItem {
    std::wstring text;
    bool done = false;
};

struct ReminderItem {
    uint64_t id = 0;
    std::wstring title;
    uint64_t dueTimestampMs = 0;
    std::wstring repeatRule; // "None", "Daily", "Weekdays", "Weekly"
    bool triggered = false;
    bool completed = false;
};

struct CustomTileData {
    std::wstring id;
    std::wstring title;
    std::wstring value;
    std::wstring subtitle;
    D2D1_COLOR_F accent = Rgba(78, 179, 255, 1.0f);
};

struct DesktopWidgetInstance {
    uint64_t id = 0;
    WidgetType type = WidgetType::Clock;
    int x = 100;
    int y = 100;
    int width = 200;
    int height = 108;
    float scale = 1.0f;
    int styleMode = 0; // 0 = Glass (live wallpaper sample), 1 = Dark, 2 = Light, 3 = Accent
    D2D1_COLOR_F customTint = Rgba(255, 255, 255, 1.0f);
    bool locked = false;
    bool isDragging = false;
    int dragOffsetX = 0;
    int dragOffsetY = 0;
    HWND hwnd = nullptr;
};

struct LauncherSlot {
    std::wstring label;
    std::wstring targetPath;
    D2D1_COLOR_F badgeColor = Rgba(64, 156, 255, 1.0f);
};

struct PairedDeckDevice {
    std::wstring deviceId;
    std::wstring deviceName;
    std::wstring ipAddress;
    std::wstring authToken;
    uint64_t pairedAtMs = 0;
};

struct HardwareTelemetry {
    float fps = 60.0f;
    float cpuPercent = 0.0f;
    float cpuTempC = 0.0f;
    bool cpuTempAvailable = false; // False on desktops without valid die sensor (0.5.80 fix: no fake motherboard temps!)
    float gpuPercent = 0.0f;
    float gpuTempC = 0.0f;
    bool gpuTempAvailable = false;
    float ramPercent = 0.0f;
    float storagePercent = 0.0f;
    float netDownKbps = 0.0f;
    float netUpKbps = 0.0f;
    int batteryPercent = 100;
    bool hasBattery = false;
    bool isCharging = true;
};

struct PrivacySensorState {
    bool micActive = false;
    std::wstring micAppName;
    uint64_t micCallStartMs = 0;

    bool camActive = false;
    std::wstring camAppName;

    bool screenActive = false;
    std::wstring screenAppName;
};

// ============================================================================
// Master User Configuration (All 10 Settings Categories)
// ============================================================================
struct AppSettings {
    // 1. General
    bool launchAtStartup = false;
    std::wstring language = L"en"; // 21 languages incl. "ar" RTL
    bool drawWithProcessor = false; // Software CPU rendering option
    bool reverseScrolling = false;
    bool pauseEffectsOnBattery = true;

    // 2. Accessibility
    float textScale = 1.0f; // 1.0 (100%) up to 1.5 (150% Extra Large)
    bool higherContrast = false;
    bool reduceMotion = false;
    float noticeDurationSec = 4.5f; // 3.0s to 15.0s

    // 3. Island & Appearance
    IslandShape shape = IslandShape::Notch;
    EdgePlacement edge = EdgePlacement::TopCenter;
    int monitorIndex = 0; // 0 = Primary, 1..N = Specific monitor
    float topEdgeDistanceDip = 0.0f;
    float cornerRadiusScale = 1.0f; // 0.15 (Square) to 1.0 (Fully round)
    float pillSizePercent = 100.0f; // 50% to 175%
    float openSizePercent = 100.0f; // 50% to 175%
    MotionSpeed motionSpeed = MotionSpeed::Normal;
    float hoverOpenDelaySec = 0.15f; // 0.0s to 1.5s
    bool reopenOnLastTab = true;     // true = Last tab, false = Always Music
    RestingInfoMode restingMode = RestingInfoMode::MediaOnly;
    bool showSongTitleInClosedPill = false;
    bool hideFromScreenshots = false;
    SurfaceTheme theme = SurfaceTheme::Black;
    float glassBlurRadius = 20.0f;   // 0..40 px
    ProgressBarStyle progressBar = ProgressBarStyle::Wave;
    VisualizerStyle visualizer = VisualizerStyle::Bars;
    CardBackgroundMode cardBackground = CardBackgroundMode::AlbumColours;
    int fontIndex = 0;

    // 4. Panels
    std::vector<PanelId> panelOrder = {
        PanelId::Music, PanelId::Web, PanelId::Notifications, PanelId::Chat,
        PanelId::Shelf, PanelId::Clipboard, PanelId::Widgets, PanelId::Controls,
        PanelId::Launcher, PanelId::Calls
    };
    bool clockTimestampsInNotifications = true; // true = "14:32", false = "2m ago"
    int musicFilterMode = 0; // 0 = All apps, 1 = Music players only, 2 = Exclude list
    std::vector<std::wstring> excludedMusicApps = { L"msedgewebview2.exe" };
    std::vector<std::wstring> ignoredSensorApps = { L"svchost.exe", L"audiodg.exe", L"SearchHost.exe", L"RuntimeBroker.exe" };

    // 5. Widgets
    std::array<WidgetType, 4> islandWidgets = {
        WidgetType::Clock, WidgetType::TodoList, WidgetType::MinimalWeather, WidgetType::PcStats
    };
    bool use24HourClock = false;
    std::wstring weatherCity = L"Local";
    int weatherTempC = 20;
    int weatherHighC = 22;
    int weatherLowC = 14;
    std::wstring worldClockCity = L"Tokyo";
    int worldClockOffsetHours = 9;
    bool lockDesktopWidgets = false;

    // 6. Calls
    bool showGlobalMuteNotice = true;
    std::wstring upcomingMeetingTitle = L"No upcoming events";
    std::wstring upcomingMeetingTime = L"Calendar synced locally";
    std::wstring upcomingMeetingUrl = L"";

    // 7. Streaming
    std::wstring twitchChannel = L"";
    std::wstring youtubeLiveId = L"";
    bool streamChatEnabled = false;
    bool showIslandChatPopups = true;
    bool showObsRecPill = true;

    // 8. Gaming
    int gameStripMode = 0; // 0 = Fullscreen games, 1 = Every fullscreen app, 2 = Off
    bool gameStripShowFps = true;
    bool gameStripShowCpu = true;
    bool gameStripShowGpu = true;
    bool gameStripShowRam = true;
    bool gameStripShowTemps = true;
    bool gameStripShowNet = true;
    bool gameStripNetInMbps = true;
    bool gameStripShowBackground = true;
    std::vector<std::wstring> gameRulesAllowList = { L"VALORANT-Win64-Shipping.exe", L"cs2.exe", L"javaw.exe", L"hl2.exe" };

    // 9. Tools (PC Deck, Bottom Notch Taskbar, Shelf, API, Alt+Tab)
    bool pcDeckEnabled = false; // OFF BY DEFAULT per requirement
    int pcDeckPort = 47824;
    float deckPointerSpeed = 1.0f;
    float deckScrollSpeed = 1.0f;
    bool bottomTaskbarEnabled = false; // Optional bottom notch taskbar
    bool taskbarMultiMonitor = false;
    bool taskbarAutoHideMaximized = true;
    bool taskbarDockMagnification = true;
    bool taskbarLiveHoverPreviews = true;
    bool keepCapturesOffShelf = false;
    bool loopbackApiEnabled = true;
    int loopbackApiPort = 47823;
    AltTabStyle altTabStyle = AltTabStyle::Off;

    // 10. Keys (Virtual key codes with Ctrl+Alt modifiers)
    UINT hkOpenIsland  = 'N';
    UINT hkHideIsland  = 'H';
    UINT hkMuteMic     = 'M';
    UINT hkColorPicker = 'C';
};

} // namespace np
