#pragma once
#include "Types.h"
#include "Spring.h"

namespace np {

// ============================================================================
// Central Application State & Atomic Local Configuration Persistence
// ============================================================================
struct GlobalAppState {
    std::recursive_mutex mutex;
    AppSettings settings;

    // Live UI & Spring Animation State
    bool islandOpen = false;
    bool islandHidden = false;
    bool isRecordingScreen = false;
    uint64_t recordingStartMs = 0;
    bool isFullscreenGameActive = false;
    std::wstring activeFullscreenApp;

    PanelId activePanel = PanelId::Music;
    PanelId previousPanel = PanelId::Music;
    ClipboardCategory activeClipCat = ClipboardCategory::All;
    SettingsCategory activeSettingsCat = SettingsCategory::Island;
    std::wstring settingsSearchQuery;

    // 240 Hz Springs (0..100 normalized range, matching island.js)
    Spring openSpring{ 0.0f };     // 0 = resting pill, 100 = expanded card
    Spring fadeSpring{ 0.0f };     // 0..100 content opacity
    Spring panelSpring{ 100.0f };  // 0..100 tab transition slide/fade
    Spring notchSpring{ 100.0f };  // 100 = Notch with concave flares, 0 = Floating Island
    Spring recSpring{ 0.0f };      // 0..100 recording pill expansion
    Spring flipSpring{ 100.0f };   // 0..100 cover 3D flip on track skip
    Spring playSpring{ 0.0f };     // 100 = playing, 0 = paused cover scale/morph

    // Hover & Toast / Top-Center Pop-Up Overlay State
    bool pointerInsideIsland = false;
    float pointerDipX = -1000.0f;
    float pointerDipY = -1000.0f;
    uint64_t hoverEnterTickMs = 0;
    uint64_t hoverLeaveTickMs = 0;

    // Transient Closed-Island Top-Center Pop-Up (Volume, Brightness, Mic Mute, Lock Key, Color Picker, Free Buff Alert, Reminder)
    bool toastActive = false;
    TopPopupType toastPopupType = TopPopupType::None;
    float toastProgress01 = 0.0f; // Used for Volume/Brightness horizontal progress pill (0..1)
    std::wstring toastTitle;
    std::wstring toastSubtitle;
    D2D1_COLOR_F toastAccent = Rgba(78, 179, 255, 1.0f);
    uint64_t toastExpireMs = 0;
    bool toastIsReminder = false;
    uint64_t activeReminderId = 0;

    // Domain Subsystems State (Zero Placeholders)
    MediaSessionState media;
    HardwareTelemetry telemetry;
    PrivacySensorState sensors;

    // Real System Volume & Brightness State (synced live with CoreAudio & WMI/DDC-CI)
    std::wstring activeAudioRenderDeviceId;
    bool audioBaselineInitialized = false;
    float masterVolume = 50.0f;
    bool masterOutputMuted = false;
    bool globalMicMuted = false;
    float micPeakLevel = 0.0f;
    float speakerPeakLevel = 0.0f;
    bool mutedSpeechWarning = false;
    float monitorBrightness = 80.0f;
    bool brightnessSupported = false;
    bool wifiEnabled = true;
    bool bluetoothEnabled = true;
    bool nightLightEnabled = false;
    bool focusModeEnabled = false;
    int powerModeIndex = 1; // 0 = Battery Saver, 1 = Balanced, 2 = Best Performance
    std::wstring lastPickedColorHex;

    // Collections (Zero Fake Items)
    std::vector<PinnedWebSite> pinnedSites;
    std::vector<NotificationItem> notifications;
    std::vector<AgentSessionItem> agents;
    std::vector<ChatMessageItem> chatMessages;
    std::vector<ShelfFileItem> shelfFiles;
    std::vector<ClipboardEntry> clipboardHistory;
    std::vector<TodoItem> todoList;
    std::vector<ReminderItem> reminders;
    std::vector<CustomTileData> customTiles;
    std::vector<DesktopWidgetInstance> desktopWidgets;
    std::array<LauncherSlot, 6> launcherSlots;
    std::vector<PairedDeckDevice> pairedDeckDevices;

    // Focus Timer State
    bool focusTimerRunning = false;
    int focusTimerRemainingSec = 25 * 60;
    int focusTimerTotalSec = 25 * 60;

    // OBS Studio & Stream Chat State
    bool obsRecordingActive = false;
    uint64_t obsRecordingStartMs = 0;
    bool chatDrawerVisible = false;
    bool lyricsDrawerVisible = false;
    bool webDrawerVisible = false;
    std::wstring currentWebDrawerUrl;
    std::wstring currentWebDrawerTitle;
    bool sendToPhoneQrActive = false;
    std::wstring sendToPhoneUrl;

    // PC Deck Pairing & Security State
    bool isNetworkPrivate = true;
    std::wstring localPrivateIp = L"127.0.0.1";
    std::wstring activePairingCode;
    uint64_t pairingCodeExpireMs = 0;
    bool pendingPairingApproval = false;
    std::wstring pendingPairingDeviceName;
    std::wstring pendingPairingIp;

    // Window Handles
    HWND hwndIsland = nullptr;
    HWND hwndSettings = nullptr;
    HWND hwndTaskbar = nullptr;
    HWND hwndLyricsDrawer = nullptr;
    HWND hwndChatDrawer = nullptr;
    HWND hwndWebDrawer = nullptr;
    HWND hwndSnipOverlay = nullptr;
};

class ConfigManager {
public:
    static std::wstring GetAppDataDirectory();
    static std::wstring GetConfigFilePath();
    static std::wstring GetCapturesDirectory();

    static void InitializeDefaults(GlobalAppState& state);
    static bool Load(GlobalAppState& state);
    static bool SaveAtomic(GlobalAppState& state);
};

GlobalAppState& GetAppState();

uint64_t GetTickMs();
std::wstring Utf8ToWide(const std::string& str);
std::string WideToUtf8(const std::wstring& wstr);

} // namespace np
