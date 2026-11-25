#include "ConfigManager.h"
#include <shlobj.h>
#include <shlwapi.h>
#include <fstream>
#include <sstream>
#include <iomanip>

namespace np {

GlobalAppState& GetAppState() {
    static GlobalAppState instance;
    return instance;
}

uint64_t GetTickMs() {
    return static_cast<uint64_t>(GetTickCount64());
}

std::wstring Utf8ToWide(const std::string& str) {
    if (str.empty()) return L"";
    int size = MultiByteToWideChar(CP_UTF8, 0, str.data(), static_cast<int>(str.size()), nullptr, 0);
    if (size <= 0) return L"";
    std::wstring result(size, 0);
    MultiByteToWideChar(CP_UTF8, 0, str.data(), static_cast<int>(str.size()), result.data(), size);
    return result;
}

std::string WideToUtf8(const std::wstring& wstr) {
    if (wstr.empty()) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, wstr.data(), static_cast<int>(wstr.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) return "";
    std::string result(size, 0);
    WideCharToMultiByte(CP_UTF8, 0, wstr.data(), static_cast<int>(wstr.size()), result.data(), size, nullptr, nullptr);
    return result;
}

std::wstring ConfigManager::GetAppDataDirectory() {
    wchar_t path[MAX_PATH] = { 0 };
    if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, path))) {
        std::wstring dir = std::wstring(path) + L"\\OpenPixel";
        if (CreateDirectoryW(dir.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS) {
            std::wstring probe = dir + L"\\.write_probe";
            HANDLE h = CreateFileW(probe.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                   FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
            if (h != INVALID_HANDLE_VALUE) {
                CloseHandle(h);
                return dir;
            }
        }
    }
    wchar_t exePath[MAX_PATH] = { 0 };
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    PathRemoveFileSpecW(exePath);
    std::wstring localDir = std::wstring(exePath) + L"\\OpenPixelData";
    CreateDirectoryW(localDir.c_str(), nullptr);
    return localDir;
}

std::wstring ConfigManager::GetConfigFilePath() {
    return GetAppDataDirectory() + L"\\settings.json";
}

std::wstring ConfigManager::GetCapturesDirectory() {
    wchar_t path[MAX_PATH] = { 0 };
    if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_MYPICTURES, nullptr, 0, path))) {
        std::wstring dir = std::wstring(path) + L"\\OpenPixel Captures";
        if (CreateDirectoryW(dir.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS) {
            return dir;
        }
    }
    return GetAppDataDirectory();
}

static std::string EscapeJson(const std::wstring& ws) {
    std::string s = WideToUtf8(ws);
    std::ostringstream o;
    for (unsigned char c : s) {
        switch (c) {
            case '"':  o << "\\\""; break;
            case '\\': o << "\\\\"; break;
            case '\b': o << "\\b";  break;
            case '\f': o << "\\f";  break;
            case '\n': o << "\\n";  break;
            case '\r': o << "\\r";  break;
            case '\t': o << "\\t";  break;
            default:
                if (c < 0x20) {
                    o << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(c);
                } else {
                    o << c;
                }
        }
    }
    return o.str();
}

// ============================================================================
// InitializeDefaults: ZERO PLACEHOLDERS ANYWHERE
// - Media starts with hasSession = false, empty title/artist
// - Notifications, Clipboard History, File Shelf, Reminders, Chat start empty
// - Launcher slots start unconfigured ("+ Add") until user picks apps
// - Web tab starts with the 3 removable default web shortcuts (0.5.113)
// ============================================================================
void ConfigManager::InitializeDefaults(GlobalAppState& state) {
    std::lock_guard<std::recursive_mutex> lock(state.mutex);

    state.notchSpring.Snap(state.settings.shape == IslandShape::Notch ? 100.0f : 0.0f);

    // Clear any dummy data
    state.media = MediaSessionState{};
    state.media.hasSession = false;
    state.media.isPlaying = false;

    // 0.5.113: Web tab default sites (YT Music, YouTube, Spotify), removable & restorable
    if (state.pinnedSites.empty()) {
        state.pinnedSites = {
            { L"YT Music", L"https://music.youtube.com", Rgba(255, 46, 76, 1.0f) },
            { L"YouTube",  L"https://www.youtube.com",   Rgba(255, 0, 51, 1.0f) },
            { L"Spotify",  L"https://open.spotify.com",  Rgba(30, 215, 96, 1.0f) }
        };
    }

    // Launcher slots start empty ("+ Add") unless configured by user
    for (int i = 0; i < 6; ++i) {
        if (state.launcherSlots[i].label.empty()) {
            state.launcherSlots[i] = { L"+ Add", L"", Rgba(64, 156, 255, 1.0f) };
        }
    }
}

static void ParseSimpleJsonBool(const std::string& content, const char* key, bool& outVal) {
    std::string pattern = std::string("\"") + key + "\":";
    auto pos = content.find(pattern);
    if (pos == std::string::npos) return;
    pos += pattern.size();
    while (pos < content.size() && (content[pos] == ' ' || content[pos] == '\t')) ++pos;
    if (content.compare(pos, 4, "true") == 0) outVal = true;
    else if (content.compare(pos, 5, "false") == 0) outVal = false;
}

static void ParseSimpleJsonFloat(const std::string& content, const char* key, float& outVal) {
    std::string pattern = std::string("\"") + key + "\":";
    auto pos = content.find(pattern);
    if (pos == std::string::npos) return;
    pos += pattern.size();
    try {
        outVal = std::stof(content.substr(pos, 32));
    } catch (...) {}
}

static void ParseSimpleJsonInt(const std::string& content, const char* key, int& outVal) {
    std::string pattern = std::string("\"") + key + "\":";
    auto pos = content.find(pattern);
    if (pos == std::string::npos) return;
    pos += pattern.size();
    try {
        outVal = std::stoi(content.substr(pos, 32));
    } catch (...) {}
}

static void ParseSimpleJsonString(const std::string& content, const char* key, std::wstring& outVal) {
    std::string pattern = std::string("\"") + key + "\":\"";
    auto pos = content.find(pattern);
    if (pos == std::string::npos) return;
    pos += pattern.size();
    std::string raw;
    while (pos < content.size() && content[pos] != '"') {
        if (content[pos] == '\\' && pos + 1 < content.size()) {
            ++pos;
        }
        raw.push_back(content[pos]);
        ++pos;
    }
    outVal = Utf8ToWide(raw);
}

bool ConfigManager::Load(GlobalAppState& state) {
    InitializeDefaults(state);

    std::wstring path = GetConfigFilePath();
    HANDLE hFile = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) return false;

    DWORD fileSize = GetFileSize(hFile, nullptr);
    if (fileSize == 0 || fileSize == INVALID_FILE_SIZE) {
        CloseHandle(hFile);
        return false;
    }

    std::string json(fileSize, '\0');
    DWORD bytesRead = 0;
    BOOL readOk = ReadFile(hFile, json.data(), fileSize, &bytesRead, nullptr);
    CloseHandle(hFile);
    if (!readOk || bytesRead == 0) return false;

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    auto& s = state.settings;

    int shapeInt = static_cast<int>(s.shape);
    int edgeInt = static_cast<int>(s.edge);
    int themeInt = static_cast<int>(s.theme);
    int barInt = static_cast<int>(s.progressBar);
    int visInt = static_cast<int>(s.visualizer);
    int bgInt = static_cast<int>(s.cardBackground);
    int motionInt = static_cast<int>(s.motionSpeed);
    int restInt = static_cast<int>(s.restingMode);

    ParseSimpleJsonInt(json, "shape", shapeInt);
    ParseSimpleJsonInt(json, "edge", edgeInt);
    ParseSimpleJsonInt(json, "monitorIndex", s.monitorIndex);
    ParseSimpleJsonInt(json, "theme", themeInt);
    ParseSimpleJsonInt(json, "progressBar", barInt);
    ParseSimpleJsonInt(json, "visualizer", visInt);
    ParseSimpleJsonInt(json, "cardBackground", bgInt);
    ParseSimpleJsonInt(json, "motionSpeed", motionInt);
    ParseSimpleJsonInt(json, "restingMode", restInt);
    ParseSimpleJsonInt(json, "fontIndex", s.fontIndex);
    ParseSimpleJsonInt(json, "gameStripMode", s.gameStripMode);

    s.shape = static_cast<IslandShape>(std::clamp(shapeInt, 0, 1));
    s.edge = static_cast<EdgePlacement>(std::clamp(edgeInt, 0, 2));
    s.theme = static_cast<SurfaceTheme>(std::clamp(themeInt, 0, static_cast<int>(SurfaceTheme::Count) - 1));
    s.progressBar = static_cast<ProgressBarStyle>(std::clamp(barInt, 0, 5));
    s.visualizer = static_cast<VisualizerStyle>(std::clamp(visInt, 0, 1));
    s.cardBackground = static_cast<CardBackgroundMode>(std::clamp(bgInt, 0, 1));
    s.motionSpeed = static_cast<MotionSpeed>(std::clamp(motionInt, 0, 2));
    s.restingMode = static_cast<RestingInfoMode>(std::clamp(restInt, 0, 7));

    ParseSimpleJsonFloat(json, "topEdgeDistanceDip", s.topEdgeDistanceDip);
    ParseSimpleJsonFloat(json, "cornerRadiusScale", s.cornerRadiusScale);
    ParseSimpleJsonFloat(json, "pillSizePercent", s.pillSizePercent);
    ParseSimpleJsonFloat(json, "openSizePercent", s.openSizePercent);
    ParseSimpleJsonFloat(json, "hoverOpenDelaySec", s.hoverOpenDelaySec);
    ParseSimpleJsonFloat(json, "glassBlurRadius", s.glassBlurRadius);
    ParseSimpleJsonFloat(json, "textScale", s.textScale);
    ParseSimpleJsonFloat(json, "noticeDurationSec", s.noticeDurationSec);

    ParseSimpleJsonBool(json, "launchAtStartup", s.launchAtStartup);
    ParseSimpleJsonBool(json, "drawWithProcessor", s.drawWithProcessor);
    ParseSimpleJsonBool(json, "reverseScrolling", s.reverseScrolling);
    ParseSimpleJsonBool(json, "higherContrast", s.higherContrast);
    ParseSimpleJsonBool(json, "reduceMotion", s.reduceMotion);
    ParseSimpleJsonBool(json, "reopenOnLastTab", s.reopenOnLastTab);
    ParseSimpleJsonBool(json, "showSongTitleInClosedPill", s.showSongTitleInClosedPill);
    ParseSimpleJsonBool(json, "hideFromScreenshots", s.hideFromScreenshots);
    ParseSimpleJsonBool(json, "clockTimestampsInNotifications", s.clockTimestampsInNotifications);
    ParseSimpleJsonBool(json, "use24HourClock", s.use24HourClock);
    ParseSimpleJsonBool(json, "lockDesktopWidgets", s.lockDesktopWidgets);
    ParseSimpleJsonBool(json, "showGlobalMuteNotice", s.showGlobalMuteNotice);
    ParseSimpleJsonBool(json, "streamChatEnabled", s.streamChatEnabled);
    ParseSimpleJsonBool(json, "showIslandChatPopups", s.showIslandChatPopups);
    ParseSimpleJsonBool(json, "showObsRecPill", s.showObsRecPill);
    ParseSimpleJsonBool(json, "gameStripShowBackground", s.gameStripShowBackground);
    ParseSimpleJsonBool(json, "pcDeckEnabled", s.pcDeckEnabled);
    ParseSimpleJsonBool(json, "bottomTaskbarEnabled", s.bottomTaskbarEnabled);
    ParseSimpleJsonBool(json, "taskbarDockMagnification", s.taskbarDockMagnification);
    ParseSimpleJsonBool(json, "taskbarLiveHoverPreviews", s.taskbarLiveHoverPreviews);
    ParseSimpleJsonBool(json, "keepCapturesOffShelf", s.keepCapturesOffShelf);
    ParseSimpleJsonBool(json, "loopbackApiEnabled", s.loopbackApiEnabled);

    ParseSimpleJsonString(json, "language", s.language);
    ParseSimpleJsonString(json, "weatherCity", s.weatherCity);
    ParseSimpleJsonString(json, "worldClockCity", s.worldClockCity);
    ParseSimpleJsonString(json, "twitchChannel", s.twitchChannel);

    for (int i = 0; i < 6; ++i) {
        std::string lKey = "slotLabel" + std::to_string(i);
        std::string pKey = "slotPath" + std::to_string(i);
        ParseSimpleJsonString(json, lKey.c_str(), state.launcherSlots[i].label);
        ParseSimpleJsonString(json, pKey.c_str(), state.launcherSlots[i].targetPath);
    }

    state.notchSpring.Snap(s.shape == IslandShape::Notch ? 100.0f : 0.0f);
    return true;
}

bool ConfigManager::SaveAtomic(GlobalAppState& state) {
    std::wstring finalPath = GetConfigFilePath();
    std::wstring tmpPath = finalPath + L".tmp";

    std::ostringstream o;
    {
        std::lock_guard<std::recursive_mutex> lock(state.mutex);
        const auto& s = state.settings;
        o << "{\n";
        o << "  \"version\": \"0.5.126\",\n";
        o << "  \"shape\": " << static_cast<int>(s.shape) << ",\n";
        o << "  \"edge\": " << static_cast<int>(s.edge) << ",\n";
        o << "  \"monitorIndex\": " << s.monitorIndex << ",\n";
        o << "  \"theme\": " << static_cast<int>(s.theme) << ",\n";
        o << "  \"progressBar\": " << static_cast<int>(s.progressBar) << ",\n";
        o << "  \"visualizer\": " << static_cast<int>(s.visualizer) << ",\n";
        o << "  \"cardBackground\": " << static_cast<int>(s.cardBackground) << ",\n";
        o << "  \"motionSpeed\": " << static_cast<int>(s.motionSpeed) << ",\n";
        o << "  \"restingMode\": " << static_cast<int>(s.restingMode) << ",\n";
        o << "  \"fontIndex\": " << s.fontIndex << ",\n";
        o << "  \"gameStripMode\": " << s.gameStripMode << ",\n";
        o << "  \"topEdgeDistanceDip\": " << s.topEdgeDistanceDip << ",\n";
        o << "  \"cornerRadiusScale\": " << s.cornerRadiusScale << ",\n";
        o << "  \"pillSizePercent\": " << s.pillSizePercent << ",\n";
        o << "  \"openSizePercent\": " << s.openSizePercent << ",\n";
        o << "  \"hoverOpenDelaySec\": " << s.hoverOpenDelaySec << ",\n";
        o << "  \"glassBlurRadius\": " << s.glassBlurRadius << ",\n";
        o << "  \"textScale\": " << s.textScale << ",\n";
        o << "  \"noticeDurationSec\": " << s.noticeDurationSec << ",\n";
        o << "  \"launchAtStartup\": " << (s.launchAtStartup ? "true" : "false") << ",\n";
        o << "  \"drawWithProcessor\": " << (s.drawWithProcessor ? "true" : "false") << ",\n";
        o << "  \"reverseScrolling\": " << (s.reverseScrolling ? "true" : "false") << ",\n";
        o << "  \"higherContrast\": " << (s.higherContrast ? "true" : "false") << ",\n";
        o << "  \"reduceMotion\": " << (s.reduceMotion ? "true" : "false") << ",\n";
        o << "  \"reopenOnLastTab\": " << (s.reopenOnLastTab ? "true" : "false") << ",\n";
        o << "  \"showSongTitleInClosedPill\": " << (s.showSongTitleInClosedPill ? "true" : "false") << ",\n";
        o << "  \"hideFromScreenshots\": " << (s.hideFromScreenshots ? "true" : "false") << ",\n";
        o << "  \"clockTimestampsInNotifications\": " << (s.clockTimestampsInNotifications ? "true" : "false") << ",\n";
        o << "  \"use24HourClock\": " << (s.use24HourClock ? "true" : "false") << ",\n";
        o << "  \"lockDesktopWidgets\": " << (s.lockDesktopWidgets ? "true" : "false") << ",\n";
        o << "  \"showGlobalMuteNotice\": " << (s.showGlobalMuteNotice ? "true" : "false") << ",\n";
        o << "  \"streamChatEnabled\": " << (s.streamChatEnabled ? "true" : "false") << ",\n";
        o << "  \"showIslandChatPopups\": " << (s.showIslandChatPopups ? "true" : "false") << ",\n";
        o << "  \"showObsRecPill\": " << (s.showObsRecPill ? "true" : "false") << ",\n";
        o << "  \"gameStripShowBackground\": " << (s.gameStripShowBackground ? "true" : "false") << ",\n";
        o << "  \"pcDeckEnabled\": " << (s.pcDeckEnabled ? "true" : "false") << ",\n";
        o << "  \"bottomTaskbarEnabled\": " << (s.bottomTaskbarEnabled ? "true" : "false") << ",\n";
        o << "  \"taskbarDockMagnification\": " << (s.taskbarDockMagnification ? "true" : "false") << ",\n";
        o << "  \"taskbarLiveHoverPreviews\": " << (s.taskbarLiveHoverPreviews ? "true" : "false") << ",\n";
        o << "  \"keepCapturesOffShelf\": " << (s.keepCapturesOffShelf ? "true" : "false") << ",\n";
        o << "  \"loopbackApiEnabled\": " << (s.loopbackApiEnabled ? "true" : "false") << ",\n";
        for (int i = 0; i < 6; ++i) {
            o << "  \"slotLabel" << i << "\": \"" << EscapeJson(state.launcherSlots[i].label) << "\",\n";
            o << "  \"slotPath" << i << "\": \"" << EscapeJson(state.launcherSlots[i].targetPath) << "\",\n";
        }
        o << "  \"language\": \"" << EscapeJson(s.language) << "\",\n";
        o << "  \"weatherCity\": \"" << EscapeJson(s.weatherCity) << "\",\n";
        o << "  \"worldClockCity\": \"" << EscapeJson(s.worldClockCity) << "\",\n";
        o << "  \"twitchChannel\": \"" << EscapeJson(s.twitchChannel) << "\"\n";
        o << "}\n";
    }

    std::string payload = o.str();
    HANDLE hFile = CreateFileW(tmpPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) return false;

    DWORD written = 0;
    BOOL ok = WriteFile(hFile, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr);
    FlushFileBuffers(hFile);
    CloseHandle(hFile);

    if (!ok || written != payload.size()) return false;
    if (MoveFileExW(tmpPath.c_str(), finalPath.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        return true;
    }
    if (CopyFileW(tmpPath.c_str(), finalPath.c_str(), FALSE)) {
        DeleteFileW(tmpPath.c_str());
        return true;
    }
    HANDLE hDirect = CreateFileW(finalPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hDirect == INVALID_HANDLE_VALUE) return false;
    DWORD directWritten = 0;
    BOOL directOk = WriteFile(hDirect, payload.data(), static_cast<DWORD>(payload.size()), &directWritten, nullptr);
    FlushFileBuffers(hDirect);
    CloseHandle(hDirect);
    DeleteFileW(tmpPath.c_str());
    return directOk && directWritten == payload.size();
}

} // namespace np
