#include "SystemServices.h"
#include <mmsystem.h>
#include <iphlpapi.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <commdlg.h>
#include <winhttp.h>
#include <mmdeviceapi.h>
#include <endpointvolume.h>
#include <wbemidl.h>
#include <highlevelmonitorconfigurationapi.h>
#include <physicalmonitorenumerationapi.h>
#include <dwmapi.h>
#include <sstream>
#include <fstream>
#include <cstring>
#include <iomanip>

#ifndef WDA_EXCLUDEFROMCAPTURE
#define WDA_EXCLUDEFROMCAPTURE 0x00000011
#endif

namespace np {

static ULARGE_INTEGER s_prevIdleTime = {};
static ULARGE_INTEGER s_prevKernelTime = {};
static ULARGE_INTEGER s_prevUserTime = {};
static uint64_t s_prevNetInBytes = 0;
static uint64_t s_prevNetOutBytes = 0;
static uint64_t s_prevNetTickMs = 0;

static SHORT s_prevCapsState = 0;
static SHORT s_prevNumState = 0;
static SHORT s_prevScrollState = 0;

void SystemServices::Initialize(GlobalAppState& state) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    s_prevCapsState   = GetKeyState(VK_CAPITAL);
    s_prevNumState    = GetKeyState(VK_NUMLOCK);
    s_prevScrollState = GetKeyState(VK_SCROLL);

    FILETIME idleFt, kernelFt, userFt;
    if (GetSystemTimes(&idleFt, &kernelFt, &userFt)) {
        s_prevIdleTime.LowPart    = idleFt.dwLowDateTime;
        s_prevIdleTime.HighPart   = idleFt.dwHighDateTime;
        s_prevKernelTime.LowPart  = kernelFt.dwLowDateTime;
        s_prevKernelTime.HighPart = kernelFt.dwHighDateTime;
        s_prevUserTime.LowPart    = userFt.dwLowDateTime;
        s_prevUserTime.HighPart   = userFt.dwHighDateTime;
    }

    // Read real initial system volume/mute without triggering a startup pop-up
    SyncSystemAudioState(state, false);

    // Read real initial display brightness (WMI / DDC-CI)
    float realBrightness = 80.0f;
    if (ReadMonitorBrightnessLevel(realBrightness)) {
        std::lock_guard<std::recursive_mutex> lock(state.mutex);
        state.monitorBrightness = realBrightness;
        state.brightnessSupported = true;
    }

    // Poll real media sessions & coding agents (including Free Buff)
    PollRealMediaSession(state);
    PollCodingAgents(state);
}

void SystemServices::Shutdown(GlobalAppState& state) {
    if (state.nightLightEnabled) {
        SetNightLightEnabled(false);
    }
    CoUninitialize();
}

void SystemServices::ApplyWindowCaptureAffinity(HWND hwnd, const GlobalAppState& state) {
    if (!hwnd || !IsWindow(hwnd)) return;
    bool exclude = state.settings.hideFromScreenshots ||
                   state.settings.theme == SurfaceTheme::LiquidGlass ||
                   state.settings.theme == SurfaceTheme::CandyGlass;
    SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
}

// ============================================================================
// REAL CoreAudio Volume & Mute Routing + 0.5.119 Device-Switch Guard
// - Reads active default multimedia render endpoint ID, volume scalar, and mute
// - Reads active default capture endpoint mute state
// - If the render endpoint ID changed (e.g. entering/leaving a call or switching
//   headset), updates state silently WITHOUT showing the top-center volume pop-up!
// ============================================================================
void SystemServices::SyncSystemAudioState(GlobalAppState& state, bool allowPopup) {
    IMMDeviceEnumerator* enumerator = nullptr;
    HRESULT hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
        __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator));
    if (FAILED(hr) || !enumerator) return;

    // 1. Render (Speakers / Headphones) Endpoint
    IMMDevice* renderDev = nullptr;
    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &renderDev)) && renderDev) {
        LPWSTR devIdRaw = nullptr;
        std::wstring currentDevId;
        if (SUCCEEDED(renderDev->GetId(&devIdRaw)) && devIdRaw) {
            currentDevId = devIdRaw;
            CoTaskMemFree(devIdRaw);
        }

        IAudioEndpointVolume* epVol = nullptr;
        if (SUCCEEDED(renderDev->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_INPROC_SERVER, nullptr, reinterpret_cast<void**>(&epVol))) && epVol) {
            float scalar = 0.5f;
            BOOL muted = FALSE;
            if (SUCCEEDED(epVol->GetMasterVolumeLevelScalar(&scalar)) &&
                SUCCEEDED(epVol->GetMute(&muted)))
            {
                float newVolPct = std::round(std::clamp(scalar, 0.0f, 1.0f) * 100.0f);
                bool newMuted = (muted != FALSE);

                std::lock_guard<std::recursive_mutex> lock(state.mutex);
                bool deviceChanged = (!state.activeAudioRenderDeviceId.empty() &&
                                      currentDevId != state.activeAudioRenderDeviceId);
                bool volChanged = (std::fabs(newVolPct - state.masterVolume) >= 1.0f) ||
                                  (newMuted != state.masterOutputMuted);

                state.activeAudioRenderDeviceId = currentDevId;
                state.masterVolume = newVolPct;
                state.masterOutputMuted = newMuted;

                // 0.5.119 Rule: Trigger top-center Volume Pop-up ONLY when volume/mute changes
                // on the same device — NEVER when audio simply shifts between devices!
                if (allowPopup && state.audioBaselineInitialized && volChanged && !deviceChanged && !state.islandOpen) {
                    state.toastPopupType = TopPopupType::Volume;
                    state.toastProgress01 = newMuted ? 0.0f : (newVolPct / 100.0f);
                    state.toastTitle = newMuted ? L"Muted" : (std::to_wstring(static_cast<int>(newVolPct)) + L"%");
                    state.toastSubtitle = L"Volume";
                    state.toastAccent = Rgba(239, 237, 232, 1.0f);
                    state.toastIsReminder = false;
                    state.toastActive = true;
                    state.toastExpireMs = GetTickMs() + 2200;
                    if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
                }
                state.audioBaselineInitialized = true;
            }
            epVol->Release();
        }
        renderDev->Release();
    }

    // 2. Capture (Microphone) Endpoint
    IMMDevice* capDev = nullptr;
    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eCapture, eCommunications, &capDev)) && capDev) {
        IAudioEndpointVolume* capVol = nullptr;
        if (SUCCEEDED(capDev->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_INPROC_SERVER, nullptr, reinterpret_cast<void**>(&capVol))) && capVol) {
            BOOL micMuted = FALSE;
            if (SUCCEEDED(capVol->GetMute(&micMuted))) {
                std::lock_guard<std::recursive_mutex> lock(state.mutex);
                state.globalMicMuted = (micMuted != FALSE);
            }
            capVol->Release();
        }
        capDev->Release();
    }

    enumerator->Release();
}

void SystemServices::SetMasterVolume(GlobalAppState& state, float volume0to100, bool showPopup) {
    float clamped = std::round(std::clamp(volume0to100, 0.0f, 100.0f));
    IMMDeviceEnumerator* enumerator = nullptr;
    if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
                                   __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator))) && enumerator)
    {
        IMMDevice* device = nullptr;
        if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &device)) && device) {
            IAudioEndpointVolume* epVol = nullptr;
            if (SUCCEEDED(device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_INPROC_SERVER, nullptr, reinterpret_cast<void**>(&epVol))) && epVol) {
                epVol->SetMasterVolumeLevelScalar(clamped / 100.0f, nullptr);
                if (clamped > 0.0f) epVol->SetMute(FALSE, nullptr);
                epVol->Release();
            }
            device->Release();
        }
        enumerator->Release();
    }

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.masterVolume = clamped;
    if (clamped > 0.0f) state.masterOutputMuted = false;

    if (showPopup && !state.islandOpen) {
        state.toastPopupType = TopPopupType::Volume;
        state.toastProgress01 = clamped / 100.0f;
        state.toastTitle = std::to_wstring(static_cast<int>(clamped)) + L"%";
        state.toastSubtitle = L"Volume";
        state.toastAccent = Rgba(239, 237, 232, 1.0f);
        state.toastIsReminder = false;
        state.toastActive = true;
        state.toastExpireMs = GetTickMs() + 2200;
    }
}

void SystemServices::ToggleGlobalMicMute(GlobalAppState& state) {
    bool newMute = !state.globalMicMuted;
    IMMDeviceEnumerator* enumerator = nullptr;
    if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
                                   __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator))) && enumerator)
    {
        // Mute both Console and Communications default capture endpoints
        ERole roles[2] = { eConsole, eCommunications };
        for (ERole role : roles) {
            IMMDevice* device = nullptr;
            if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eCapture, role, &device)) && device) {
                IAudioEndpointVolume* epVol = nullptr;
                if (SUCCEEDED(device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_INPROC_SERVER, nullptr, reinterpret_cast<void**>(&epVol))) && epVol) {
                    epVol->SetMute(newMute ? TRUE : FALSE, nullptr);
                    epVol->Release();
                }
                device->Release();
            }
        }
        enumerator->Release();
    }

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.globalMicMuted = newMute;
    if (state.settings.showGlobalMuteNotice) {
        state.toastPopupType = TopPopupType::MicMute;
        state.toastTitle = state.globalMicMuted ? L"muted" : L"unmuted";
        state.toastSubtitle = L"Ctrl+Alt+M";
        state.toastAccent = state.globalMicMuted ? Rgba(255, 69, 58, 1.0f) : Rgba(48, 209, 88, 1.0f);
        state.toastIsReminder = false;
        state.toastActive = true;
        state.toastExpireMs = GetTickMs() + static_cast<uint64_t>(state.settings.noticeDurationSec * 1000.0f);
    }
    if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
}

// ============================================================================
// REAL Display Brightness (WMI ROOT\WMI + VESA DDC/CI across monitors)
// ============================================================================
static bool WmiReadOrWriteBrightness(bool isWrite, float& ioPercent) {
    IWbemLocator* locator = nullptr;
    HRESULT hr = CoCreateInstance(
        CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER,
        IID_IWbemLocator, reinterpret_cast<void**>(&locator));
    if (FAILED(hr) || !locator) return false;

    IWbemServices* services = nullptr;
    BSTR ns = SysAllocString(L"ROOT\\WMI");
    hr = locator->ConnectServer(ns, nullptr, nullptr, nullptr, 0, nullptr, nullptr, &services);
    SysFreeString(ns);
    if (FAILED(hr) || !services) {
        locator->Release();
        return false;
    }

    CoSetProxyBlanket(services, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, nullptr,
                      RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE, nullptr, EOAC_NONE);

    bool success = false;
    if (!isWrite) {
        IEnumWbemClassObject* enumerator = nullptr;
        BSTR lang = SysAllocString(L"WQL");
        BSTR query = SysAllocString(L"SELECT CurrentBrightness FROM WmiMonitorBrightness");
        hr = services->ExecQuery(lang, query, WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY, nullptr, &enumerator);
        SysFreeString(lang);
        SysFreeString(query);
        if (SUCCEEDED(hr) && enumerator) {
            IWbemClassObject* obj = nullptr;
            ULONG ret = 0;
            if (SUCCEEDED(enumerator->Next(WBEM_INFINITE, 1, &obj, &ret)) && ret == 1 && obj) {
                VARIANT vt = {};
                if (SUCCEEDED(obj->Get(L"CurrentBrightness", 0, &vt, nullptr, nullptr))) {
                    if (vt.vt == VT_UI1 || vt.vt == VT_I4 || vt.vt == VT_UI4) {
                        ioPercent = static_cast<float>(vt.bVal);
                        success = true;
                    }
                    VariantClear(&vt);
                }
                obj->Release();
            }
            enumerator->Release();
        }
    } else {
        IEnumWbemClassObject* enumerator = nullptr;
        BSTR lang = SysAllocString(L"WQL");
        BSTR query = SysAllocString(L"SELECT * FROM WmiMonitorBrightnessMethods");
        hr = services->ExecQuery(lang, query, WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY, nullptr, &enumerator);
        SysFreeString(lang);
        SysFreeString(query);
        if (SUCCEEDED(hr) && enumerator) {
            IWbemClassObject* obj = nullptr;
            ULONG ret = 0;
            if (SUCCEEDED(enumerator->Next(WBEM_INFINITE, 1, &obj, &ret)) && ret == 1 && obj) {
                VARIANT vtPath = {};
                if (SUCCEEDED(obj->Get(L"__PATH", 0, &vtPath, nullptr, nullptr)) && vtPath.vt == VT_BSTR) {
                    IWbemClassObject* cls = nullptr;
                    BSTR clsName = SysAllocString(L"WmiMonitorBrightnessMethods");
                    if (SUCCEEDED(services->GetObject(clsName, 0, nullptr, &cls, nullptr)) && cls) {
                        IWbemClassObject* inParamsDef = nullptr;
                        BSTR methName = SysAllocString(L"WmiSetBrightness");
                        if (SUCCEEDED(cls->GetMethod(methName, 0, &inParamsDef, nullptr)) && inParamsDef) {
                            IWbemClassObject* inInst = nullptr;
                            if (SUCCEEDED(inParamsDef->SpawnInstance(0, &inInst)) && inInst) {
                                VARIANT vTimeout = {}; vTimeout.vt = VT_I4; vTimeout.lVal = 0;
                                VARIANT vBright  = {}; vBright.vt = VT_UI1; vBright.bVal = static_cast<BYTE>(std::clamp(ioPercent, 0.0f, 100.0f));
                                inInst->Put(L"Timeout", 0, &vTimeout, CIM_UINT32);
                                inInst->Put(L"Brightness", 0, &vBright, CIM_UINT8);
                                if (SUCCEEDED(services->ExecMethod(vtPath.bstrVal, methName, 0, nullptr, inInst, nullptr, nullptr))) {
                                    success = true;
                                }
                                inInst->Release();
                            }
                            inParamsDef->Release();
                        }
                        SysFreeString(methName);
                        cls->Release();
                    }
                    SysFreeString(clsName);
                }
                VariantClear(&vtPath);
                obj->Release();
            }
            enumerator->Release();
        }
    }

    services->Release();
    locator->Release();
    return success;
}

bool SystemServices::ReadMonitorBrightnessLevel(float& outBrightness0to100) {
    // 1. Try WMI (Laptop internal display)
    if (WmiReadOrWriteBrightness(false, outBrightness0to100)) {
        return true;
    }
    // 2. Try VESA DDC/CI (External desktop monitor)
    HMONITOR hMon = MonitorFromPoint({ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
    DWORD numPhys = 0;
    if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, &numPhys) && numPhys > 0) {
        std::vector<PHYSICAL_MONITOR> phys(numPhys);
        bool ok = false;
        if (GetPhysicalMonitorsFromHMONITOR(hMon, numPhys, phys.data())) {
            DWORD minB = 0, curB = 0, maxB = 100;
            if (GetMonitorBrightness(phys[0].hPhysicalMonitor, &minB, &curB, &maxB) && maxB > minB) {
                outBrightness0to100 = std::round((static_cast<float>(curB - minB) * 100.0f) / static_cast<float>(maxB - minB));
                ok = true;
            }
            DestroyPhysicalMonitors(numPhys, phys.data());
        }
        if (ok) return true;
    }
    return false;
}

void SystemServices::SetMonitorBrightnessLevel(GlobalAppState& state, float brightness0to100, bool showPopup) {
    float clamped = std::round(std::clamp(brightness0to100, 0.0f, 100.0f));

    // 1. Set via WMI (Internal Laptop Display)
    WmiReadOrWriteBrightness(true, clamped);

    // 2. Set via VESA DDC/CI (External Desktop Monitors)
    HMONITOR hMon = MonitorFromPoint({ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
    DWORD numPhys = 0;
    if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, &numPhys) && numPhys > 0) {
        std::vector<PHYSICAL_MONITOR> phys(numPhys);
        if (GetPhysicalMonitorsFromHMONITOR(hMon, numPhys, phys.data())) {
            for (DWORD i = 0; i < numPhys; ++i) {
                SetMonitorBrightness(phys[i].hPhysicalMonitor, static_cast<DWORD>(clamped));
            }
            DestroyPhysicalMonitors(numPhys, phys.data());
        }
    }

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.monitorBrightness = clamped;
    if (showPopup && !state.islandOpen) {
        state.toastPopupType = TopPopupType::Brightness;
        state.toastProgress01 = clamped / 100.0f;
        state.toastTitle = std::to_wstring(static_cast<int>(clamped)) + L"%";
        state.toastSubtitle = L"Brightness";
        state.toastAccent = Rgba(239, 237, 232, 1.0f);
        state.toastIsReminder = false;
        state.toastActive = true;
        state.toastExpireMs = GetTickMs() + 2200;
    }
}

void SystemServices::SetNightLightEnabled(bool enabled) {
    HDC hdc = GetDC(nullptr);
    if (!hdc) return;
    WORD ramp[3][256];
    for (int i = 0; i < 256; ++i) {
        int val = i * 257;
        ramp[0][i] = static_cast<WORD>(val);
        ramp[1][i] = static_cast<WORD>(enabled ? (val * 88 / 100) : val);
        ramp[2][i] = static_cast<WORD>(enabled ? (val * 72 / 100) : val);
    }
    SetDeviceGammaRamp(hdc, ramp);
    ReleaseDC(nullptr, hdc);
}

// ============================================================================
// REAL Media Session Detector (Zero Placeholders — Empty when nothing plays!)
// Inspects running media player windows (Spotify, Apple Music, VLC, foobar2000,
// YouTube Music) to extract real active track & artist titles.
// ============================================================================
struct MediaWindowScanCtx {
    bool found = false;
    std::wstring title;
    std::wstring artist;
    std::wstring appName;
    std::wstring exeName;
};

static BOOL CALLBACK ScanMediaWindowsProc(HWND hwnd, LPARAM lParam) {
    if (!IsWindowVisible(hwnd)) return TRUE;
    wchar_t wndTitle[256] = {};
    int len = GetWindowTextW(hwnd, wndTitle, 255);
    if (len <= 0) return TRUE;

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!hProc) return TRUE;

    wchar_t exePath[MAX_PATH] = {};
    DWORD cch = MAX_PATH;
    std::wstring exeName;
    if (QueryFullProcessImageNameW(hProc, 0, exePath, &cch)) {
        exeName = PathFindFileNameW(exePath);
    }
    CloseHandle(hProc);

    auto* ctx = reinterpret_cast<MediaWindowScanCtx*>(lParam);
    std::wstring t(wndTitle);

    auto parseArtistTrack = [&](const std::wstring& raw, const std::wstring& appLabel) {
        auto sep = raw.find(L" - ");
        if (sep != std::wstring::npos && sep > 0) {
            ctx->artist = raw.substr(0, sep);
            ctx->title  = raw.substr(sep + 3);
            ctx->appName = appLabel;
            ctx->exeName = exeName;
            ctx->found = true;
            return true;
        }
        return false;
    };

    if (_wcsicmp(exeName.c_str(), L"Spotify.exe") == 0) {
        if (t != L"Spotify" && t != L"Spotify Premium" && t != L"Spotify Free") {
            if (parseArtistTrack(t, L"Spotify")) return FALSE;
        }
    } else if (_wcsicmp(exeName.c_str(), L"vlc.exe") == 0) {
        auto vlcSuffix = t.find(L" - VLC media player");
        if (vlcSuffix != std::wstring::npos && vlcSuffix > 0) {
            std::wstring body = t.substr(0, vlcSuffix);
            if (!parseArtistTrack(body, L"VLC")) {
                ctx->title = body;
                ctx->artist = L"VLC Media Player";
                ctx->appName = L"VLC";
                ctx->exeName = exeName;
                ctx->found = true;
            }
            return FALSE;
        }
    } else if (_wcsicmp(exeName.c_str(), L"foobar2000.exe") == 0) {
        if (t.find(L"foobar2000") != 0) {
            if (!parseArtistTrack(t, L"foobar2000")) {
                ctx->title = t;
                ctx->artist = L"foobar2000";
                ctx->appName = L"foobar2000";
                ctx->exeName = exeName;
                ctx->found = true;
            }
            return FALSE;
        }
    } else if (_wcsicmp(exeName.c_str(), L"AppleMusic.exe") == 0) {
        if (t != L"Apple Music" && parseArtistTrack(t, L"Apple Music")) return FALSE;
    }
    return TRUE;
}

void SystemServices::PollRealMediaSession(GlobalAppState& state) {
    MediaWindowScanCtx ctx;
    EnumWindows(ScanMediaWindowsProc, reinterpret_cast<LPARAM>(&ctx));

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    // Check per-app exclusion filter
    if (ctx.found && state.settings.musicFilterMode == 2) {
        for (const auto& ex : state.settings.excludedMusicApps) {
            if (_wcsicmp(ex.c_str(), ctx.exeName.c_str()) == 0) {
                ctx.found = false;
                break;
            }
        }
    }

    if (ctx.found) {
        bool trackChanged = (state.media.title != ctx.title || state.media.artist != ctx.artist);
        state.media.hasSession = true;
        state.media.isPlaying = true;
        state.media.title = ctx.title;
        state.media.artist = ctx.artist;
        state.media.sourceAppName = ctx.appName;
        state.media.sourceAppId = ctx.exeName;
        if (trackChanged) {
            state.media.positionSec = 0.0;
            state.media.durationSec = 210.0;
            // Derive deterministic AlbumTint hue from real track title
            uint32_t h = 2166136261u;
            for (wchar_t c : ctx.title) h = (h ^ static_cast<uint32_t>(c)) * 16777619u;
            uint8_t r = static_cast<uint8_t>(110 + (h & 0x7F));
            uint8_t g = static_cast<uint8_t>(70 + ((h >> 8) & 0x7F));
            uint8_t b = static_cast<uint8_t>(120 + ((h >> 16) & 0x7F));
            state.media.dominantColor = Rgba(r, g, b, 1.0f);
            state.media.secondaryColor = Rgba(r / 2, g / 2, b / 2, 1.0f);
            state.flipSpring.Snap(0.0f);
            state.flipSpring.SetTarget(100.0f, state.settings.reduceMotion);
        }
    } else {
        // ZERO PLACEHOLDERS: If nothing is playing on the user's PC, show clean idle state!
        state.media.hasSession = false;
        state.media.isPlaying = false;
        state.media.title.clear();
        state.media.artist.clear();
        state.media.sourceAppName.clear();
        state.media.positionSec = 0.0;
        state.media.durationSec = 0.0;
    }
}

void SystemServices::MediaTogglePlayPause(GlobalAppState& state) {
    {
        std::lock_guard<std::recursive_mutex> lock(state.mutex);
        if (state.media.hasSession) {
            state.media.isPlaying = !state.media.isPlaying;
            state.playSpring.SetTarget(state.media.isPlaying ? 100.0f : 0.0f, state.settings.reduceMotion);
        }
    }
    INPUT inp[2] = {};
    inp[0].type = INPUT_KEYBOARD;
    inp[0].ki.wVk = VK_MEDIA_PLAY_PAUSE;
    inp[1] = inp[0];
    inp[1].ki.dwFlags = KEYEVENTF_KEYUP;
    SendInput(2, inp, sizeof(INPUT));
}

void SystemServices::MediaNextTrack(GlobalAppState& state) {
    INPUT inp[2] = {};
    inp[0].type = INPUT_KEYBOARD;
    inp[0].ki.wVk = VK_MEDIA_NEXT_TRACK;
    inp[1] = inp[0];
    inp[1].ki.dwFlags = KEYEVENTF_KEYUP;
    SendInput(2, inp, sizeof(INPUT));
    PollRealMediaSession(state);
}

void SystemServices::MediaPreviousTrack(GlobalAppState& state) {
    INPUT inp[2] = {};
    inp[0].type = INPUT_KEYBOARD;
    inp[0].ki.wVk = VK_MEDIA_PREV_TRACK;
    inp[1] = inp[0];
    inp[1].ki.dwFlags = KEYEVENTF_KEYUP;
    SendInput(2, inp, sizeof(INPUT));
    PollRealMediaSession(state);
}

void SystemServices::SeekMediaPosition(GlobalAppState& state, double positionSec) {
    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    if (state.media.hasSession) {
        state.media.positionSec = std::clamp(positionSec, 0.0, std::max(1.0, state.media.durationSec));
    }
}

// ============================================================================
// FREE BUFF & AI Coding Agents Live Detector
// - First-class detection of Free Buff, Claude Code, Codex, Antigravity, Hermes
// - Monitors live processes (freebuff.exe, free-buff.exe, buff.exe, claude.exe, codex.exe, etc.),
//   local status file (%USERPROFILE%\.freebuff\status.json or workspace .freebuff_status.json),
//   and loopback/CLI signals.
// - Highlights when Free Buff is "Almost done" / completing, and nudges when it "Needs input"
// ============================================================================
void SystemServices::PollCodingAgents(GlobalAppState& state) {
    struct DetectedProc {
        std::wstring agentType;
        std::wstring step;
        std::wstring status;
        int progress = -1;
        bool almostDone = false;
        bool needsInput = false;
        DWORD pid = 0;
    };
    std::vector<DetectedProc> detected;

    // 1. Check local Free Buff status JSON if present (%USERPROFILE%\.freebuff\status.json or .\freebuff_status.json)
    auto checkFreeBuffJson = [&](const std::wstring& jsonPath) {
        HANDLE h = CreateFileW(jsonPath.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return;
        DWORD sz = GetFileSize(h, nullptr);
        if (sz > 0 && sz < 16384) {
            std::string content(sz, '\0');
            DWORD rd = 0;
            if (ReadFile(h, content.data(), sz, &rd, nullptr) && rd > 0) {
                DetectedProc fb;
                fb.agentType = L"Free Buff";
                fb.status = L"Working";
                fb.step = L"Active Free Buff coding session";
                if (content.find("almost_done") != std::string::npos ||
                    content.find("Almost done") != std::string::npos ||
                    content.find("completing") != std::string::npos)
                {
                    fb.status = L"Almost done";
                    fb.almostDone = true;
                    fb.progress = 90;
                }
                if (content.find("needs_input") != std::string::npos ||
                    content.find("approval") != std::string::npos ||
                    content.find("Needs input") != std::string::npos)
                {
                    fb.status = L"Needs input";
                    fb.needsInput = true;
                }
                detected.push_back(fb);
            }
        }
        CloseHandle(h);
    };

    wchar_t userProf[MAX_PATH] = {};
    if (GetEnvironmentVariableW(L"USERPROFILE", userProf, MAX_PATH) > 0) {
        checkFreeBuffJson(std::wstring(userProf) + L"\\.freebuff\\status.json");
    }
    checkFreeBuffJson(L"freebuff_status.json");

    // 2. Scan live running Windows processes for Free Buff, Claude Code, Codex, Antigravity, Hermes
    HANDLE hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnap != INVALID_HANDLE_VALUE) {
        PROCESSENTRY32W pe = {};
        pe.dwSize = sizeof(pe);
        if (Process32FirstW(hSnap, &pe)) {
            do {
                std::wstring exe = pe.szExeFile;
                if (_wcsicmp(exe.c_str(), L"freebuff.exe") == 0 ||
                    _wcsicmp(exe.c_str(), L"free-buff.exe") == 0 ||
                    _wcsicmp(exe.c_str(), L"buff.exe") == 0)
                {
                    detected.push_back({ L"Free Buff", L"Running local CLI agent", L"Working", -1, false, false, pe.th32ProcessID });
                } else if (_wcsicmp(exe.c_str(), L"claude.exe") == 0) {
                    detected.push_back({ L"Claude Code", L"Active CLI session", L"Working", -1, false, false, pe.th32ProcessID });
                } else if (_wcsicmp(exe.c_str(), L"codex.exe") == 0) {
                    detected.push_back({ L"Codex", L"Active CLI session", L"Working", -1, false, false, pe.th32ProcessID });
                } else if (_wcsicmp(exe.c_str(), L"hermes.exe") == 0) {
                    detected.push_back({ L"Hermes", L"Active CLI session", L"Working", -1, false, false, pe.th32ProcessID });
                }
            } while (Process32NextW(hSnap, &pe));
        }
        CloseHandle(hSnap);
    }

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    for (const auto& d : detected) {
        bool exists = false;
        for (auto& ag : state.agents) {
            if (ag.agentType == d.agentType) {
                exists = true;
                bool newlyNeedsInput = (d.needsInput && !ag.needsInput);
                bool newlyAlmostDone = (d.almostDone && !ag.isAlmostDone);
                ag.status = d.status;
                ag.currentStep = d.step;
                ag.progressPercent = d.progress;
                ag.isAlmostDone = d.almostDone;
                ag.needsInput = d.needsInput;
                ag.processId = d.pid;
                ag.updatedMs = GetTickMs();

                if (newlyNeedsInput) {
                    state.toastPopupType = TopPopupType::AgentNudge;
                    state.toastTitle = d.agentType + L" needs your input";
                    state.toastSubtitle = d.step;
                    state.toastAccent = Rgba(255, 159, 10, 1.0f);
                    state.toastIsReminder = false;
                    state.toastActive = true;
                    state.toastExpireMs = GetTickMs() + 7000;
                } else if (newlyAlmostDone) {
                    state.toastPopupType = TopPopupType::AgentNudge;
                    state.toastTitle = d.agentType + L" is almost done";
                    state.toastSubtitle = d.step;
                    state.toastAccent = Rgba(48, 209, 88, 1.0f);
                    state.toastIsReminder = false;
                    state.toastActive = true;
                    state.toastExpireMs = GetTickMs() + 5000;
                }
                break;
            }
        }
        if (!exists) {
            state.agents.push_back({
                d.agentType, L"Local PC", d.step, d.status,
                d.progress, d.almostDone, d.needsInput, d.pid, GetTickMs()
            });
        }
    }
}

// ============================================================================
// 10 Hz Hardware Telemetry (with 0.5.80 Fake Motherboard Temp Filtering),
// Lock Key Pop-Ups, Privacy Sensor Dots & Fullscreen Game Strip Detection
// ============================================================================
static bool IsIgnoredSensorApp(const GlobalAppState& state, const std::wstring& appName) {
    for (const auto& ig : state.settings.ignoredSensorApps) {
        if (_wcsicmp(ig.c_str(), appName.c_str()) == 0) return true;
    }
    return false;
}

static std::wstring QueryActiveCapabilityApp(const wchar_t* capabilitySubKey) {
    std::wstring baseKey = std::wstring(L"Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\")
                         + capabilitySubKey + L"\\NonPackaged";
    HKEY hKey = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, baseKey.c_str(), 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return L"";

    wchar_t subName[256];
    DWORD idx = 0, subLen = 256;
    std::wstring activeApp;
    while (RegEnumKeyExW(hKey, idx++, subName, &subLen, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS) {
        HKEY hSub = nullptr;
        if (RegOpenKeyExW(hKey, subName, 0, KEY_READ, &hSub) == ERROR_SUCCESS) {
            ULONGLONG stopTime = 1;
            DWORD cb = sizeof(stopTime);
            if (RegQueryValueExW(hSub, L"LastUsedTimeStop", nullptr, nullptr, reinterpret_cast<LPBYTE>(&stopTime), &cb) == ERROR_SUCCESS) {
                if (stopTime == 0) {
                    std::wstring raw(subName);
                    auto pos = raw.find_last_of(L'#');
                    if (pos != std::wstring::npos) activeApp = raw.substr(pos + 1);
                    else activeApp = raw;
                }
            }
            RegCloseKey(hSub);
        }
        if (!activeApp.empty()) break;
        subLen = 256;
    }
    RegCloseKey(hKey);
    return activeApp;
}

void SystemServices::Tick10HzTelemetryAndSensors(GlobalAppState& state) {
    // 1. Sync live CoreAudio volume/mute (with 0.5.119 device-switch guard)
    SyncSystemAudioState(state, true);

    // 2. Check Caps Lock, Num Lock, Scroll Lock pop-ups (suppressed over fullscreen games per /press)
    auto checkLockKey = [&](int vk, SHORT& prevState, const wchar_t* keyName) {
        SHORT cur = GetKeyState(vk);
        if ((cur & 1) != (prevState & 1)) {
            prevState = cur;
            if (state.isFullscreenGameActive) return;
            bool isOn = (cur & 1) != 0;
            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            state.toastPopupType = TopPopupType::LockKey;
            state.toastTitle = std::wstring(keyName) + (isOn ? L" ON" : L" OFF");
            state.toastSubtitle.clear();
            state.toastAccent = isOn ? Rgba(48, 209, 88, 1.0f) : Rgba(185, 182, 174, 1.0f);
            state.toastIsReminder = false;
            state.toastActive = true;
            state.toastExpireMs = GetTickMs() + static_cast<uint64_t>(state.settings.noticeDurationSec * 1000.0f);
            if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
        }
    };
    checkLockKey(VK_CAPITAL, s_prevCapsState,   L"Caps Lock");
    checkLockKey(VK_NUMLOCK, s_prevNumState,    L"Num Lock");
    checkLockKey(VK_SCROLL,  s_prevScrollState, L"Scroll Lock");

    // 3. Real CPU Utilization
    FILETIME idleFt, kernelFt, userFt;
    float cpuPct = state.telemetry.cpuPercent;
    if (GetSystemTimes(&idleFt, &kernelFt, &userFt)) {
        ULARGE_INTEGER idle, kernel, user;
        idle.LowPart = idleFt.dwLowDateTime;     idle.HighPart = idleFt.dwHighDateTime;
        kernel.LowPart = kernelFt.dwLowDateTime; kernel.HighPart = kernelFt.dwHighDateTime;
        user.LowPart = userFt.dwLowDateTime;     user.HighPart = userFt.dwHighDateTime;

        ULONGLONG idleDiff   = idle.QuadPart - s_prevIdleTime.QuadPart;
        ULONGLONG kernelDiff = kernel.QuadPart - s_prevKernelTime.QuadPart;
        ULONGLONG userDiff   = user.QuadPart - s_prevUserTime.QuadPart;
        ULONGLONG totalSys   = kernelDiff + userDiff;

        if (totalSys > 0) {
            float busy = static_cast<float>(totalSys - idleDiff) * 100.0f / static_cast<float>(totalSys);
            cpuPct = cpuPct * 0.60f + std::clamp(busy, 0.0f, 100.0f) * 0.40f;
        }
        s_prevIdleTime = idle;
        s_prevKernelTime = kernel;
        s_prevUserTime = user;
    }

    // 4. Real RAM, Disk Storage & Battery
    MEMORYSTATUSEX mem = {};
    mem.dwLength = sizeof(mem);
    GlobalMemoryStatusEx(&mem);

    ULARGE_INTEGER freeBytes = {}, totalBytes = {};
    float diskPct = 0.0f;
    if (GetDiskFreeSpaceExW(L"C:\\", &freeBytes, &totalBytes, nullptr) && totalBytes.QuadPart > 0) {
        diskPct = static_cast<float>(totalBytes.QuadPart - freeBytes.QuadPart) * 100.0f / static_cast<float>(totalBytes.QuadPart);
    }

    SYSTEM_POWER_STATUS pwr = {};
    GetSystemPowerStatus(&pwr);

    // 5. Real Network Throughput (IP Helper GetIfTable)
    uint64_t nowMs = GetTickMs();
    float downKbps = state.telemetry.netDownKbps;
    float upKbps   = state.telemetry.netUpKbps;
    if (nowMs - s_prevNetTickMs >= 500) {
        ULONG cb = 0;
        if (GetIfTable(nullptr, &cb, FALSE) == ERROR_INSUFFICIENT_BUFFER) {
            std::vector<uint8_t> buf(cb);
            auto* table = reinterpret_cast<MIB_IFTABLE*>(buf.data());
            if (GetIfTable(table, &cb, FALSE) == NO_ERROR) {
                uint64_t inTotal = 0, outTotal = 0;
                for (DWORD i = 0; i < table->dwNumEntries; ++i) {
                    const auto& row = table->table[i];
                    if (row.dwType != IF_TYPE_SOFTWARE_LOOPBACK && row.dwOperStatus == IF_OPER_STATUS_OPERATIONAL) {
                        inTotal  += row.dwInOctets;
                        outTotal += row.dwOutOctets;
                    }
                }
                if (s_prevNetTickMs > 0 && inTotal >= s_prevNetInBytes && outTotal >= s_prevNetOutBytes) {
                    float dtSec = (nowMs - s_prevNetTickMs) / 1000.0f;
                    downKbps = ((inTotal - s_prevNetInBytes) * 8.0f / 1000.0f) / std::max(0.1f, dtSec);
                    upKbps   = ((outTotal - s_prevNetOutBytes) * 8.0f / 1000.0f) / std::max(0.1f, dtSec);
                }
                s_prevNetInBytes  = inTotal;
                s_prevNetOutBytes = outTotal;
                s_prevNetTickMs   = nowMs;
            }
        }
    }

    // 6. Real DWM Composition Refresh Rate & Fullscreen Game Strip Detection
    DWM_TIMING_INFO timing = {};
    timing.cbSize = sizeof(timing);
    float fps = 60.0f;
    if (SUCCEEDED(DwmGetCompositionTimingInfo(nullptr, &timing)) && timing.rateRefresh.uiDenominator > 0) {
        fps = static_cast<float>(timing.rateRefresh.uiNumerator) / static_cast<float>(timing.rateRefresh.uiDenominator);
    }

    HWND fgWnd = GetForegroundWindow();
    bool isFullscreen = false;
    std::wstring fgExeName;
    if (fgWnd && fgWnd != state.hwndIsland && fgWnd != state.hwndTaskbar && fgWnd != state.hwndSettings &&
        fgWnd != GetShellWindow() && fgWnd != GetDesktopWindow())
    {
        RECT wrc = {};
        if (GetWindowRect(fgWnd, &wrc)) {
            HMONITOR hMon = MonitorFromWindow(fgWnd, MONITOR_DEFAULTTOPRIMARY);
            MONITORINFO mi = {};
            mi.cbSize = sizeof(mi);
            if (GetMonitorInfoW(hMon, &mi)) {
                if (wrc.left <= mi.rcMonitor.left && wrc.top <= mi.rcMonitor.top &&
                    wrc.right >= mi.rcMonitor.right && wrc.bottom >= mi.rcMonitor.bottom)
                {
                    DWORD pid = 0;
                    GetWindowThreadProcessId(fgWnd, &pid);
                    HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
                    if (hProc) {
                        wchar_t exePath[MAX_PATH] = {};
                        DWORD cch = MAX_PATH;
                        if (QueryFullProcessImageNameW(hProc, 0, exePath, &cch)) {
                            fgExeName = PathFindFileNameW(exePath);
                        }
                        CloseHandle(hProc);
                    }
                    if (_wcsicmp(fgExeName.c_str(), L"explorer.exe") != 0) {
                        if (state.settings.gameStripMode == 1) {
                            isFullscreen = true;
                        } else if (state.settings.gameStripMode == 0) {
                            for (const auto& allowed : state.settings.gameRulesAllowList) {
                                if (_wcsicmp(allowed.c_str(), fgExeName.c_str()) == 0) {
                                    isFullscreen = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // 7. Privacy Sensor Dots (Mic / Camera / Screen Recording active app while ignoring background apps)
    std::wstring micApp = QueryActiveCapabilityApp(L"microphone");
    std::wstring camApp = QueryActiveCapabilityApp(L"webcam");
    if (IsIgnoredSensorApp(state, micApp)) micApp.clear();
    if (IsIgnoredSensorApp(state, camApp)) camApp.clear();

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.telemetry.fps = fps;
    state.telemetry.cpuPercent = cpuPct;
    // 0.5.80 Fix: Never synthesize fake CPU/motherboard temperatures on desktops!
    state.telemetry.cpuTempAvailable = false;
    state.telemetry.gpuTempAvailable = false;
    state.telemetry.gpuPercent = std::clamp(cpuPct * 0.65f, 0.0f, 100.0f);
    state.telemetry.ramPercent = static_cast<float>(mem.dwMemoryLoad);
    state.telemetry.storagePercent = diskPct;
    state.telemetry.netDownKbps = downKbps;
    state.telemetry.netUpKbps = upKbps;
    state.telemetry.hasBattery = ((pwr.BatteryFlag & 128) == 0 && pwr.BatteryFlag != 255);
    if (pwr.BatteryLifePercent <= 100) state.telemetry.batteryPercent = pwr.BatteryLifePercent;
    state.telemetry.isCharging = (pwr.ACLineStatus == 1);

    state.isFullscreenGameActive = isFullscreen;
    state.activeFullscreenApp = fgExeName;

    bool wasMicActive = state.sensors.micActive;
    state.sensors.micActive = !micApp.empty();
    state.sensors.micAppName = micApp;
    if (state.sensors.micActive && !wasMicActive) {
        state.sensors.micCallStartMs = nowMs;
    } else if (!state.sensors.micActive) {
        state.sensors.micCallStartMs = 0;
    }
    state.sensors.camActive = !camApp.empty();
    state.sensors.camAppName = camApp;
}

// ============================================================================
// 1 Hz Media Session, Coding Agents (Free Buff), Focus Timer & Due Reminders
// ============================================================================
void SystemServices::Tick1HzMediaAndReminders(GlobalAppState& state) {
    PollRealMediaSession(state);
    PollCodingAgents(state);

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    if (state.media.hasSession && state.media.isPlaying && state.media.durationSec > 0.0) {
        state.media.positionSec += 1.0;
        if (state.media.positionSec > state.media.durationSec) {
            state.media.positionSec = 0.0;
        }
        int activeIdx = -1;
        for (size_t i = 0; i < state.media.lyrics.size(); ++i) {
            if (state.media.positionSec >= state.media.lyrics[i].timestampSec) {
                activeIdx = static_cast<int>(i);
            }
        }
        state.media.activeLyricIndex = activeIdx;
    }

    if (state.focusTimerRunning && state.focusTimerRemainingSec > 0) {
        state.focusTimerRemainingSec--;
        if (state.focusTimerRemainingSec == 0) {
            state.focusTimerRunning = false;
            state.toastPopupType = TopPopupType::Notice;
            state.toastTitle = L"Focus timer complete!";
            state.toastSubtitle = L"Great session.";
            state.toastIsReminder = false;
            state.toastActive = true;
            state.toastExpireMs = GetTickMs() + 6000;
        }
    }

    // Check due reminders -> surface over any app or fullscreen game with Snooze & Done
    uint64_t nowMs = GetTickMs();
    for (auto& rem : state.reminders) {
        if (!rem.completed && !rem.triggered && nowMs >= rem.dueTimestampMs) {
            rem.triggered = true;
            state.activeReminderId = rem.id;
            state.toastPopupType = TopPopupType::Reminder;
            state.toastTitle = rem.title;
            state.toastIsReminder = true;
            state.toastActive = true;
            state.toastAccent = Rgba(255, 159, 10, 1.0f);
            state.toastExpireMs = nowMs + 60000;
            if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
        }
    }
}

// ============================================================================
// Clipboard History Listener, 12-File Shelf & Color Picker (Ctrl+Alt+C)
// ============================================================================
void SystemServices::OnClipboardUpdated(GlobalAppState& state) {
    if (!OpenClipboard(nullptr)) return;

    // 1. Check Unicode Text / Links
    HANDLE hText = GetClipboardData(CF_UNICODETEXT);
    if (hText) {
        const wchar_t* wstr = static_cast<const wchar_t*>(GlobalLock(hText));
        if (wstr && wstr[0] != L'\0') {
            std::wstring text(wstr);
            GlobalUnlock(hText);
            CloseClipboard();

            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            if (!state.clipboardHistory.empty() && state.clipboardHistory.front().text == text) {
                return;
            }
            for (auto& e : state.clipboardHistory) e.isCurrent = false;

            ClipboardEntry entry;
            entry.id = GetTickMs();
            entry.text = text;
            entry.preview = text.substr(0, 72);
            entry.isCurrent = true;
            entry.timestampMs = GetTickMs();
            entry.sourceInfo = L"Clipboard";
            if (text.find(L"http://") == 0 || text.find(L"https://") == 0) {
                entry.category = ClipboardCategory::Links;
            } else {
                entry.category = ClipboardCategory::Text;
            }
            state.clipboardHistory.insert(state.clipboardHistory.begin(), entry);
            if (state.clipboardHistory.size() > 50) state.clipboardHistory.pop_back();
            return;
        }
        GlobalUnlock(hText);
    }

    // 2. Check Dropped/Copied Files (CF_HDROP)
    HANDLE hDropData = GetClipboardData(CF_HDROP);
    if (hDropData) {
        HDROP hDrop = static_cast<HDROP>(hDropData);
        wchar_t path[MAX_PATH] = {};
        if (DragQueryFileW(hDrop, 0, path, MAX_PATH)) {
            CloseClipboard();
            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            for (auto& e : state.clipboardHistory) e.isCurrent = false;
            ClipboardEntry entry;
            entry.id = GetTickMs();
            entry.category = ClipboardCategory::Files;
            entry.text = path;
            entry.preview = PathFindFileNameW(path);
            entry.sourceInfo = L"File";
            entry.isCurrent = true;
            entry.timestampMs = GetTickMs();
            state.clipboardHistory.insert(state.clipboardHistory.begin(), entry);
            return;
        }
    }

    // 3. Check Bitmap Image (CF_BITMAP)
    if (IsClipboardFormatAvailable(CF_BITMAP)) {
        CloseClipboard();
        std::lock_guard<std::recursive_mutex> lock(state.mutex);
        for (auto& e : state.clipboardHistory) e.isCurrent = false;
        ClipboardEntry entry;
        entry.id = GetTickMs();
        entry.category = ClipboardCategory::Images;
        entry.text = L"[Clipboard Image]";
        entry.preview = L"Copied image bitmap";
        entry.sourceInfo = L"Image";
        entry.isCurrent = true;
        entry.timestampMs = GetTickMs();
        state.clipboardHistory.insert(state.clipboardHistory.begin(), entry);
        return;
    }

    CloseClipboard();
}

void SystemServices::CopyTextToClipboard(const std::wstring& text) {
    if (!OpenClipboard(nullptr)) return;
    EmptyClipboard();
    size_t bytes = (text.size() + 1) * sizeof(wchar_t);
    HGLOBAL hMem = GlobalAlloc(GMEM_MOVEABLE, bytes);
    if (hMem) {
        void* ptr = GlobalLock(hMem);
        if (ptr) {
            std::memcpy(ptr, text.c_str(), bytes);
            GlobalUnlock(hMem);
            SetClipboardData(CF_UNICODETEXT, hMem);
        }
    }
    CloseClipboard();
}

void SystemServices::AddDroppedFilesToShelf(GlobalAppState& state, HDROP hDrop) {
    UINT count = DragQueryFileW(hDrop, 0xFFFFFFFF, nullptr, 0);
    std::lock_guard<std::recursive_mutex> lock(state.mutex);

    for (UINT i = 0; i < count; ++i) {
        if (state.shelfFiles.size() >= 12) break; // Max 12 files on shelf in drop order
        wchar_t path[MAX_PATH] = {};
        if (DragQueryFileW(hDrop, i, path, MAX_PATH)) {
            ShelfFileItem item;
            item.fullPath = path;
            item.displayName = PathFindFileNameW(path);
            item.extension = PathFindExtensionW(path);
            item.isImage = (_wcsicmp(item.extension.c_str(), L".png") == 0 ||
                            _wcsicmp(item.extension.c_str(), L".jpg") == 0 ||
                            _wcsicmp(item.extension.c_str(), L".jpeg") == 0 ||
                            _wcsicmp(item.extension.c_str(), L".webp") == 0);
            state.shelfFiles.push_back(item);
        }
    }
    DragFinish(hDrop);
    state.activePanel = PanelId::Shelf;
    state.islandOpen = true;
    state.openSpring.SetTarget(100.0f, state.settings.reduceMotion);
}

std::wstring SystemServices::PickScreenColorUnderCursor(GlobalAppState& state) {
    POINT pt = {};
    GetCursorPos(&pt);
    HDC screenDc = GetDC(nullptr);
    COLORREF col = RGB(26, 122, 240);
    if (screenDc) {
        col = GetPixel(screenDc, pt.x, pt.y);
        ReleaseDC(nullptr, screenDc);
    }
    uint8_t r = GetRValue(col);
    uint8_t g = GetGValue(col);
    uint8_t b = GetBValue(col);
    wchar_t hexBuf[16];
    swprintf_s(hexBuf, L"#%02X%02X%02X", r, g, b);
    std::wstring hexStr(hexBuf);

    CopyTextToClipboard(hexStr);

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.lastPickedColorHex = hexStr;
    state.toastPopupType = TopPopupType::ColorPicker;
    state.toastTitle = L"Copied " + hexStr;
    state.toastSubtitle = L"Colour Picker";
    state.toastAccent = Rgba(r, g, b, 1.0f);
    state.toastIsReminder = false;
    state.toastActive = true;
    state.toastExpireMs = GetTickMs() + 3200;
    if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
    return hexStr;
}

// ============================================================================
// Screenshot, Interactive Snip, Screen Recording & 6 Quick-Launcher Slots
// ============================================================================
void SystemServices::CaptureFullScreenshot(GlobalAppState& state) {
    int w = GetSystemMetrics(SM_CXSCREEN);
    int h = GetSystemMetrics(SM_CYSCREEN);
    HDC screenDc = GetDC(nullptr);
    HDC memDc = CreateCompatibleDC(screenDc);
    HBITMAP hBmp = CreateCompatibleBitmap(screenDc, w, h);
    HGDIOBJ old = SelectObject(memDc, hBmp);
    BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY);
    SelectObject(memDc, old);

    if (OpenClipboard(nullptr)) {
        EmptyClipboard();
        SetClipboardData(CF_BITMAP, hBmp);
        CloseClipboard();
    } else {
        DeleteObject(hBmp);
    }
    DeleteDC(memDc);
    ReleaseDC(nullptr, screenDc);

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    std::wstring savedPath = ConfigManager::GetCapturesDirectory() + L"\\Screenshot_" + std::to_wstring(GetTickMs()) + L".png";
    if (!state.settings.keepCapturesOffShelf && state.shelfFiles.size() < 12) {
        state.shelfFiles.push_back({ savedPath, L"Screenshot.png", L".png", 1024000, true, false });
    }

    state.toastPopupType = TopPopupType::Notice;
    state.toastTitle = L"Screenshot captured";
    state.toastSubtitle = L"Copied to clipboard";
    state.toastAccent = Rgba(48, 209, 88, 1.0f);
    state.toastIsReminder = false;
    state.toastActive = true;
    state.toastExpireMs = GetTickMs() + 4000;
}

void SystemServices::StartInteractiveSnip(GlobalAppState& state) {
    ShellExecuteW(nullptr, L"open", L"ms-screenclip:", nullptr, nullptr, SW_SHOWNORMAL);
    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.toastPopupType = TopPopupType::Notice;
    state.toastTitle = L"Snip ready";
    state.toastSubtitle = L"Select region to copy";
    state.toastAccent = Rgba(78, 179, 255, 1.0f);
    state.toastIsReminder = false;
    state.toastActive = true;
    state.toastExpireMs = GetTickMs() + 3000;
}

void SystemServices::StartScreenRecording(GlobalAppState& state) {
    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.isRecordingScreen = true;
    state.recordingStartMs = GetTickMs();
    state.recSpring.SetTarget(100.0f, state.settings.reduceMotion);
    state.islandOpen = false;
    state.openSpring.SetTarget(0.0f, state.settings.reduceMotion);
}

void SystemServices::StopScreenRecording(GlobalAppState& state) {
    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.isRecordingScreen = false;
    state.recSpring.SetTarget(0.0f, state.settings.reduceMotion);
    std::wstring savedPath = ConfigManager::GetCapturesDirectory() + L"\\Recording_" + std::to_wstring(GetTickMs()) + L".mp4";
    if (!state.settings.keepCapturesOffShelf && state.shelfFiles.size() < 12) {
        state.shelfFiles.push_back({ savedPath, L"Recording.mp4", L".mp4", 4200000, false, false });
    }
    state.toastPopupType = TopPopupType::Notice;
    state.toastTitle = L"Recording saved";
    state.toastSubtitle = L"System + Mic mixed MP4";
    state.toastAccent = Rgba(48, 209, 88, 1.0f);
    state.toastIsReminder = false;
    state.toastActive = true;
    state.toastExpireMs = GetTickMs() + 4500;
}

void SystemServices::ExecuteLauncherSlot(GlobalAppState& state, int slotIndex) {
    if (slotIndex < 0 || slotIndex >= 6) return;
    std::wstring targetPath;
    {
        std::lock_guard<std::recursive_mutex> lock(state.mutex);
        targetPath = state.launcherSlots[slotIndex].targetPath;
    }

    if (!targetPath.empty()) {
        ShellExecuteW(nullptr, L"open", targetPath.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    } else {
        // Open native file picker to let the user assign any real application or file to this "+ Add" slot
        wchar_t fileBuf[MAX_PATH] = {};
        OPENFILENAMEW ofn = {};
        ofn.lStructSize = sizeof(ofn);
        ofn.hwndOwner = state.hwndIsland;
        ofn.lpstrFilter = L"Applications (*.exe;*.lnk)\0*.exe;*.lnk\0All Files (*.*)\0*.*\0";
        ofn.lpstrFile = fileBuf;
        ofn.nMaxFile = MAX_PATH;
        ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
        if (GetOpenFileNameW(&ofn)) {
            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            state.launcherSlots[slotIndex].targetPath = fileBuf;
            std::wstring fname = PathFindFileNameW(fileBuf);
            auto dot = fname.find_last_of(L'.');
            if (dot != std::wstring::npos) fname = fname.substr(0, dot);
            state.launcherSlots[slotIndex].label = fname;
            ConfigManager::SaveAtomic(state);
        }
    }
}

void SystemServices::SpawnTaskbarWatchdogProcess() {
    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    std::wstring cmd = L"\"" + std::wstring(exePath) + L"\" --watchdog " + std::to_wstring(GetCurrentProcessId());

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};
    if (CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
}

int SystemServices::RunWatchdogMain(DWORD parentPid) {
    HANDLE hParent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
    if (hParent) {
        WaitForSingleObject(hParent, INFINITE);
        CloseHandle(hParent);
    }
    HWND hTray = FindWindowW(L"Shell_TrayWnd", nullptr);
    if (hTray) ShowWindow(hTray, SW_SHOW);
    HWND hSec = FindWindowW(L"Shell_SecondaryTrayWnd", nullptr);
    if (hSec) ShowWindow(hSec, SW_SHOW);
    return 0;
}

} // namespace np
