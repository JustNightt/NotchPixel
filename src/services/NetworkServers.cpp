#include "NetworkServers.h"
#include "SystemServices.h"
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <shellapi.h>
#include <powrprof.h>
#include <thread>
#include <atomic>
#include <sstream>
#include <fstream>
#include <random>
#include <cstring>

namespace np {

static std::atomic<bool> s_running{ false };
static SOCKET s_loopbackSock = INVALID_SOCKET;
static SOCKET s_deckSock = INVALID_SOCKET;
static std::thread s_loopbackThread;
static std::thread s_deckThread;

// ============================================================================
// Network Profile Security Check (Private vs Public Wi-Fi/LAN)
// ============================================================================
bool NetworkServers::DetectPrivateNetworkAndLocalIp(GlobalAppState& state) {
    WSADATA wsa = {};
    WSAStartup(MAKEWORD(2, 2), &wsa);

    std::wstring bestIp = L"127.0.0.1";
    bool isPrivateRfc1918 = false;

    char hostName[256] = {};
    if (gethostname(hostName, sizeof(hostName)) == 0) {
        addrinfo hints = {}, *res = nullptr;
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        if (getaddrinfo(hostName, nullptr, &hints, &res) == 0 && res) {
            for (addrinfo* p = res; p != nullptr; p = p->ai_next) {
                auto* sin = reinterpret_cast<sockaddr_in*>(p->ai_addr);
                uint32_t ip = ntohl(sin->sin_addr.s_addr);
                if ((ip >> 24) == 127) continue;
                // Check RFC 1918 Private Network ranges (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16)
                if ((ip >> 24) == 10 ||
                    ((ip >> 24) == 172 && ((ip >> 16) & 0xF0) == 16) ||
                    ((ip >> 24) == 192 && ((ip >> 16) & 0xFF) == 168))
                {
                    wchar_t ipBuf[64] = {};
                    InetNtopW(AF_INET, &sin->sin_addr, ipBuf, 64);
                    bestIp = ipBuf;
                    isPrivateRfc1918 = true;
                    break;
                }
            }
            freeaddrinfo(res);
        }
    }

    // Also inspect Windows NetworkList profiles in registry (Category: 0=Public, 1=Private, 2=Domain)
    bool registrySaysPublic = false;
    HKEY hProfiles = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
        L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\NetworkList\\Profiles",
        0, KEY_READ, &hProfiles) == ERROR_SUCCESS)
    {
        wchar_t subKey[256];
        DWORD idx = 0, len = 256;
        while (RegEnumKeyExW(hProfiles, idx++, subKey, &len, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS) {
            HKEY hSub = nullptr;
            if (RegOpenKeyExW(hProfiles, subKey, 0, KEY_READ, &hSub) == ERROR_SUCCESS) {
                DWORD cat = 1, cb = sizeof(cat);
                if (RegQueryValueExW(hSub, L"Category", nullptr, nullptr, reinterpret_cast<LPBYTE>(&cat), &cb) == ERROR_SUCCESS) {
                    if (cat == 0) registrySaysPublic = true;
                    else if (cat == 1 || cat == 2) { registrySaysPublic = false; RegCloseKey(hSub); break; }
                }
                RegCloseKey(hSub);
            }
            len = 256;
        }
        RegCloseKey(hProfiles);
    }

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.localPrivateIp = bestIp;
    state.isNetworkPrivate = isPrivateRfc1918 && !registrySaysPublic;
    if (!state.isNetworkPrivate) {
        state.settings.pcDeckEnabled = false; // Automatically disabled on Public networks
    }
    return state.isNetworkPrivate;
}

std::wstring NetworkServers::GenerateTwoMinutePairingCode(GlobalAppState& state) {
    std::random_device rd;
    std::mt19937 gen(rd());
    std::uniform_int_distribution<int> dist(100000, 999999);
    int code = dist(gen);
    std::wstring codeStr = std::to_wstring(code);

    std::lock_guard<std::recursive_mutex> lock(state.mutex);
    state.activePairingCode = codeStr;
    state.pairingCodeExpireMs = GetTickMs() + 2 * 60 * 1000; // Strictly expires in 2 minutes
    return codeStr;
}

bool NetworkServers::SendWakeOnLanMagicPacket(const std::string& macHex) {
    uint8_t mac[6] = {};
    int parsed = sscanf_s(macHex.c_str(), "%2hhx:%2hhx:%2hhx:%2hhx:%2hhx:%2hhx",
                          &mac[0], &mac[1], &mac[2], &mac[3], &mac[4], &mac[5]);
    if (parsed != 6) return false;

    uint8_t packet[102];
    std::memset(packet, 0xFF, 6);
    for (int i = 0; i < 16; ++i) {
        std::memcpy(packet + 6 + i * 6, mac, 6);
    }

    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) return false;
    BOOL bcast = TRUE;
    setsockopt(s, SOL_SOCKET, SO_BROADCAST, reinterpret_cast<const char*>(&bcast), sizeof(bcast));

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(9);
    addr.sin_addr.s_addr = INADDR_BROADCAST;
    int sent = sendto(s, reinterpret_cast<const char*>(packet), sizeof(packet), 0,
                      reinterpret_cast<sockaddr*>(&addr), sizeof(addr));
    closesocket(s);
    return sent == sizeof(packet);
}

static std::string ExtractQueryParam(const std::string& target, const std::string& key) {
    std::string pattern = key + "=";
    auto pos = target.find(pattern);
    if (pos == std::string::npos) return "";
    pos += pattern.size();
    std::string out;
    while (pos < target.size() && target[pos] != '&' && target[pos] != ' ' && target[pos] != '\r' && target[pos] != '\n') {
        if (target[pos] == '+') {
            out.push_back(' ');
        } else if (target[pos] == '%' && pos + 2 < target.size()) {
            int hexVal = 0;
            std::istringstream iss(target.substr(pos + 1, 2));
            if (iss >> std::hex >> hexVal) {
                out.push_back(static_cast<char>(hexVal));
                pos += 2;
            }
        } else {
            out.push_back(target[pos]);
        }
        ++pos;
    }
    return out;
}

static void SendHttpResponse(SOCKET client, const char* status, const char* contentType, const std::string& body, const std::string& extraHeaders = "") {
    std::ostringstream resp;
    resp << "HTTP/1.1 " << status << "\r\n"
         << "Content-Type: " << contentType << "\r\n"
         << "Content-Length: " << body.size() << "\r\n"
         << "Access-Control-Allow-Origin: *\r\n"
         << "Connection: close\r\n"
         << extraHeaders
         << "\r\n"
         << body;
    std::string raw = resp.str();
    send(client, raw.data(), static_cast<int>(raw.size()), 0);
}

// ============================================================================
// 1. Local Loopback Scripting Server (http://127.0.0.1:47823)
//    Endpoints: /notify, /tile, /agent, /status
// ============================================================================
bool NetworkServers::HandleLoopbackRequest(GlobalAppState& state, const std::string& combined, std::string& outJson) {
    if (combined.find("/notify") != std::string::npos) {
        std::wstring title = Utf8ToWide(ExtractQueryParam(combined, "title"));
        std::wstring text  = Utf8ToWide(ExtractQueryParam(combined, "text"));
        std::wstring app   = Utf8ToWide(ExtractQueryParam(combined, "app"));
        if (title.empty()) title = L"Custom Script Notice";
        if (app.empty()) app = L"Script";

        SYSTEMTIME st;
        GetLocalTime(&st);
        wchar_t ts[16];
        swprintf_s(ts, L"%02d:%02d", st.wHour, st.wMinute);

        {
            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            NotificationItem item;
            item.id = GetTickMs();
            item.appName = app;
            item.title = title;
            item.body = text;
            item.timeStr = ts;
            item.relativeStr = L"Just now";
            item.timestampMs = GetTickMs();
            state.notifications.insert(state.notifications.begin(), item);

            state.toastTitle = app + L": " + title;
            state.toastSubtitle = text;
            state.toastAccent = Rgba(78, 179, 255, 1.0f);
            state.toastIsReminder = false;
            state.toastActive = true;
            state.toastExpireMs = GetTickMs() + static_cast<uint64_t>(state.settings.noticeDurationSec * 1000.0f);
        }
        if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
        outJson = "{\"ok\":true,\"endpoint\":\"notify\"}";
        return true;
    }
    if (combined.find("/tile") != std::string::npos) {
        std::wstring id    = Utf8ToWide(ExtractQueryParam(combined, "id"));
        std::wstring title = Utf8ToWide(ExtractQueryParam(combined, "title"));
        std::wstring val   = Utf8ToWide(ExtractQueryParam(combined, "value"));
        std::wstring sub   = Utf8ToWide(ExtractQueryParam(combined, "sub"));
        if (id.empty()) id = L"default";
        if (title.empty()) title = L"Live Tile";

        {
            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            bool updated = false;
            for (auto& t : state.customTiles) {
                if (t.id == id) {
                    t.title = title;
                    t.value = val;
                    t.subtitle = sub;
                    updated = true;
                    break;
                }
            }
            if (!updated) {
                state.customTiles.push_back({ id, title, val, sub, Rgba(78, 179, 255, 1.0f) });
            }
        }
        if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
        outJson = "{\"ok\":true,\"endpoint\":\"tile\"}";
        return true;
    }
    if (combined.find("/agent") != std::string::npos) {
        std::wstring name     = Utf8ToWide(ExtractQueryParam(combined, "name"));
        std::wstring status   = Utf8ToWide(ExtractQueryParam(combined, "status"));
        std::wstring step     = Utf8ToWide(ExtractQueryParam(combined, "step"));
        std::string progStr   = ExtractQueryParam(combined, "progress");
        int progress          = progStr.empty() ? -1 : std::clamp(std::atoi(progStr.c_str()), 0, 100);
        bool almostDone       = (ExtractQueryParam(combined, "almost_done") == "1") ||
                                (progress >= 80 && progress < 100) ||
                                (status == L"Almost done" || status == L"Completing");
        bool needsInput       = (ExtractQueryParam(combined, "needs_input") == "1") ||
                                (status == L"Needs input" || status == L"Awaiting approval");
        if (name.empty()) name = L"Free Buff";
        if (status.empty()) {
            status = needsInput ? L"Needs input" : (almostDone ? L"Almost done" : L"Working");
        }

        {
            std::lock_guard<std::recursive_mutex> lock(state.mutex);
            bool found = false;
            for (auto& ag : state.agents) {
                if (ag.agentType == name) {
                    ag.status = status;
                    if (!step.empty()) ag.currentStep = step;
                    ag.progressPercent = progress;
                    ag.isAlmostDone = almostDone;
                    ag.needsInput = needsInput;
                    ag.updatedMs = GetTickMs();
                    found = true;
                    break;
                }
            }
            if (!found) {
                state.agents.push_back({ name, L"Workspace", step, status, progress, almostDone, needsInput, 0, GetTickMs() });
            }
            if (needsInput) {
                state.toastPopupType = TopPopupType::AgentNudge;
                state.toastTitle = name + L" needs your input";
                state.toastSubtitle = step;
                state.toastAccent = Rgba(255, 159, 10, 1.0f);
                state.toastIsReminder = false;
                state.toastActive = true;
                state.toastExpireMs = GetTickMs() + 8000;
            } else if (almostDone) {
                state.toastPopupType = TopPopupType::AgentNudge;
                state.toastTitle = name + L" is almost done";
                state.toastSubtitle = step;
                state.toastAccent = Rgba(48, 209, 88, 1.0f);
                state.toastIsReminder = false;
                state.toastActive = true;
                state.toastExpireMs = GetTickMs() + 5000;
            }
        }
        if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
        outJson = "{\"ok\":true,\"endpoint\":\"agent\"}";
        return true;
    }
    outJson = "{\"app\":\"OpenPixel\",\"version\":\"0.5.126\",\"loopback\":true}";
    return true;
}

static void LoopbackServerWorker(GlobalAppState* statePtr) {
    GlobalAppState& state = *statePtr;

    s_loopbackSock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s_loopbackSock == INVALID_SOCKET) return;

    BOOL reuse = TRUE;
    setsockopt(s_loopbackSock, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&reuse), sizeof(reuse));

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(static_cast<u_short>(state.settings.loopbackApiPort));
    inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

    if (bind(s_loopbackSock, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR ||
        listen(s_loopbackSock, 8) == SOCKET_ERROR)
    {
        closesocket(s_loopbackSock);
        s_loopbackSock = INVALID_SOCKET;
        return;
    }

    while (s_running.load()) {
        SOCKET client = accept(s_loopbackSock, nullptr, nullptr);
        if (client == INVALID_SOCKET) break;

        char buf[4096] = {};
        int n = recv(client, buf, sizeof(buf) - 1, 0);
        if (n > 0) {
            std::string req(buf, n);
            auto lineEnd = req.find("\r\n");
            std::string firstLine = (lineEnd != std::string::npos) ? req.substr(0, lineEnd) : req;
            auto bodyPos = req.find("\r\n\r\n");
            std::string body = (bodyPos != std::string::npos) ? req.substr(bodyPos + 4) : "";
            std::string combined = firstLine + "&" + body;

            std::string outJson;
            NetworkServers::HandleLoopbackRequest(state, combined, outJson);
            SendHttpResponse(client, "200 OK", "application/json", outJson);
        }
        closesocket(client);
    }
}

// ============================================================================
// 2. PC Deck Mobile Remote HTML App (5 Tabs: Now, Pad, Apps, Modes, PC)
//    & Send-to-Phone File Shelf Wi-Fi Server
// ============================================================================
static std::string BuildPcDeckMobileHtml(GlobalAppState& state) {
    std::ostringstream h;
    h << R"HTML(<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no"/>
<title>OpenPixel Deck</title>
<style>
:root{--bg:#0b0b0c;--card:#18181b;--paper:#efede8;--muted:#a1a09a;--accent:#1a7af0;}
*{box-sizing:border-box;user-select:none;-webkit-user-select:none;}
body{margin:0;background:var(--bg);color:var(--paper);font-family:system-ui,-apple-system,sans-serif;display:flex;flex-direction:column;height:100vh;overflow:hidden;}
header{padding:12px 16px;display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid rgba(255,255,255,.08);}
.pill{background:rgba(48,209,88,.18);color:#30d158;padding:4px 10px;border-radius:99px;font-size:12px;font-weight:600;}
main{flex:1;overflow-y:auto;padding:16px;}
.tab-view{display:none;}
.tab-view.active{display:block;}
.card{background:var(--card);border:1px solid rgba(255,255,255,.08);border-radius:18px;padding:16px;margin-bottom:12px;}
.btn{background:#27272a;color:var(--paper);border:none;border-radius:14px;padding:14px;font-size:14px;font-weight:600;width:100%;cursor:pointer;}
.btn:active{background:var(--accent);}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:10px;}
.grid4{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;text-align:center;}
#trackpad{height:240px;background:#141417;border:1px solid rgba(255,255,255,.12);border-radius:20px;display:flex;align-items:center;justify-content:center;color:var(--muted);touch-action:none;margin-bottom:12px;}
nav{display:grid;grid-template-columns:repeat(5,1fr);background:#121214;border-top:1px solid rgba(255,255,255,.08);padding:8px 4px;}
nav button{background:none;border:none;color:var(--muted);font-size:12px;font-weight:600;padding:10px 4px;border-radius:10px;}
nav button.active{color:#fff;background:rgba(255,255,255,.08);}
input[type=range],input[type=text]{width:100%;margin:8px 0;}
</style>
</head>
<body>
<header>
  <strong>OpenPixel Deck</strong>
  <span class="pill">&#x25CF; ENCRYPTED LAN</span>
</header>
<main>
  <!-- TAB 1: NOW PLAYING -->
  <section id="tab-now" class="tab-view active">
    <div class="card" style="text-align:center">
      <div style="width:120px;height:120px;border-radius:22px;background:linear-gradient(135deg,#eb5ea8,#4a30a2);margin:0 auto 12px;display:flex;align-items:center;justify-content:center;font-size:36px;">&#x266B;</div>
      <h3 style="margin:4px 0">)HTML" << WideToUtf8(state.media.title) << R"HTML(</h3>
      <div style="color:var(--muted);font-size:14px;margin-bottom:12px">)HTML" << WideToUtf8(state.media.artist) << R"HTML(</div>
      <input type="range" min="0" max="100" value="35" onchange="cmd('seek',this.value)"/>
      <div class="grid2" style="grid-template-columns:1fr 1fr 1fr;margin-top:10px">
        <button class="btn" onclick="cmd('prev')">&#x23EE; Prev</button>
        <button class="btn" style="background:#fff;color:#000" onclick="cmd('play')">&#x23EF; Play</button>
        <button class="btn" onclick="cmd('next')">&#x23ED; Next</button>
      </div>
      <div style="margin-top:14px;text-align:left;font-size:12px;color:var(--muted)">Volume</div>
      <input type="range" min="0" max="100" value=")HTML" << static_cast<int>(state.masterVolume) << R"HTML(" oninput="cmd('vol',this.value)"/>
    </div>
    <div class="grid4">
      <div class="card"><div>CPU</div><strong>)HTML" << static_cast<int>(state.telemetry.cpuPercent) << R"HTML(%</strong></div>
      <div class="card"><div>GPU</div><strong>)HTML" << static_cast<int>(state.telemetry.gpuPercent) << R"HTML(%</strong></div>
      <div class="card"><div>RAM</div><strong>)HTML" << static_cast<int>(state.telemetry.ramPercent) << R"HTML(%</strong></div>
      <div class="card"><div>Temp</div><strong>)HTML" << static_cast<int>(state.telemetry.cpuTempC) << R"HTML(&deg;</strong></div>
    </div>
  </section>

  <!-- TAB 2: PAD (TRACKPAD & KEYBOARD) -->
  <section id="tab-pad" class="tab-view">
    <div id="trackpad">Touch &amp; slide trackpad</div>
    <div class="grid2" style="margin-bottom:12px">
      <button class="btn" onclick="cmd('lclick')">Left Click</button>
      <button class="btn" onclick="cmd('rclick')">Right Click</button>
    </div>
    <div class="card">
      <div style="font-size:12px;color:var(--muted)">Keyboard Input</div>
      <div style="display:flex;gap:8px;margin-top:6px">
        <input id="kbtext" type="text" placeholder="Type onto PC..." style="flex:1;padding:10px;border-radius:10px;border:none;background:#27272a;color:#fff"/>
        <button class="btn" style="width:auto" onclick="cmd('type',document.getElementById('kbtext').value);document.getElementById('kbtext').value=''">Send</button>
      </div>
    </div>
  </section>

  <!-- TAB 3: APPS -->
  <section id="tab-apps" class="tab-view">
    <div class="grid2">
      <button class="btn" onclick="cmd('app','0')">1: Terminal</button>
      <button class="btn" onclick="cmd('app','1')">2: Explorer</button>
      <button class="btn" onclick="cmd('app','2')">3: Notepad</button>
      <button class="btn" onclick="cmd('app','3')">4: Task Manager</button>
      <button class="btn" onclick="cmd('app','4')">5: Calculator</button>
      <button class="btn" onclick="cmd('app','5')">6: Browser</button>
    </div>
  </section>

  <!-- TAB 4: MODES (GAMES, SLIDES, MOVIES) -->
  <section id="tab-modes" class="tab-view">
    <div class="card">
      <strong>Games Mode</strong>
      <div class="grid2" style="margin-top:8px">
        <button class="btn" onclick="cmd('mute')">Mic Mute (Ctrl+Alt+M)</button>
        <button class="btn" onclick="cmd('shot')">Screenshot</button>
      </div>
    </div>
    <div class="card">
      <strong>Slides Mode</strong>
      <div class="grid2" style="margin-top:8px">
        <button class="btn" onclick="cmd('key','left')">&larr; Prev Slide</button>
        <button class="btn" onclick="cmd('key','right')">Next Slide &rarr;</button>
      </div>
    </div>
    <div class="card">
      <strong>Movies Mode</strong>
      <div class="grid2" style="margin-top:8px">
        <button class="btn" onclick="cmd('key','space')">Play / Pause</button>
        <button class="btn" onclick="cmd('key','f')">Fullscreen (F)</button>
      </div>
    </div>
  </section>

  <!-- TAB 5: PC (STATS & PRESS-AND-HOLD POWER) -->
  <section id="tab-pc" class="tab-view">
    <div class="card">
      <button class="btn" onclick="cmd('lock')">&#x1F512; Lock PC Immediately</button>
    </div>
    <div class="card">
      <div style="font-size:12px;color:var(--muted);margin-bottom:8px">Press and hold for 1.5s to execute:</div>
      <div class="grid2">
        <button class="btn" onpointerdown="holdStart('sleep')" onpointerup="holdEnd()">Hold: Sleep</button>
        <button class="btn" onpointerdown="holdStart('restart')" onpointerup="holdEnd()">Hold: Restart</button>
      </div>
      <button class="btn" style="margin-top:10px;background:#7f1d1d" onpointerdown="holdStart('shutdown')" onpointerup="holdEnd()">Hold: Shutdown PC</button>
    </div>
  </section>
</main>
<nav>
  <button class="active" onclick="showTab('now',this)">Now</button>
  <button onclick="showTab('pad',this)">Pad</button>
  <button onclick="showTab('apps',this)">Apps</button>
  <button onclick="showTab('modes',this)">Modes</button>
  <button onclick="showTab('pc',this)">PC</button>
</nav>
<script>
function showTab(id,btn){
  document.querySelectorAll('.tab-view').forEach(e=>e.classList.remove('active'));
  document.getElementById('tab-'+id).classList.add('active');
  document.querySelectorAll('nav button').forEach(b=>b.classList.remove('active'));
  btn.classList.add('active');
}
function cmd(action,val=''){
  fetch('/deck/cmd?a='+encodeURIComponent(action)+'&v='+encodeURIComponent(val));
}
let holdTimer=null;
function holdStart(act){ holdTimer=setTimeout(()=>cmd(act),1500); }
function holdEnd(){ if(holdTimer){clearTimeout(holdTimer);holdTimer=null;} }
const pad=document.getElementById('trackpad');
let lx=0,ly=0;
pad.addEventListener('pointerdown',e=>{lx=e.clientX;ly=e.clientY;});
pad.addEventListener('pointermove',e=>{
  if(e.buttons){
    let dx=Math.round(e.clientX-lx),dy=Math.round(e.clientY-ly);
    lx=e.clientX;ly=e.clientY;
    if(Math.abs(dx)+Math.abs(dy)>1) fetch('/deck/move?dx='+dx+'&dy='+dy);
  }
});
</script>
</body>
</html>)HTML";
    return h.str();
}

static void DeckAndShelfServerWorker(GlobalAppState* statePtr) {
    GlobalAppState& state = *statePtr;

    s_deckSock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s_deckSock == INVALID_SOCKET) return;

    BOOL reuse = TRUE;
    setsockopt(s_deckSock, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&reuse), sizeof(reuse));

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(static_cast<u_short>(state.settings.pcDeckPort));
    addr.sin_addr.s_addr = INADDR_ANY;

    if (bind(s_deckSock, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR ||
        listen(s_deckSock, 8) == SOCKET_ERROR)
    {
        closesocket(s_deckSock);
        s_deckSock = INVALID_SOCKET;
        return;
    }

    while (s_running.load()) {
        sockaddr_in clientAddr = {};
        int addrLen = sizeof(clientAddr);
        SOCKET client = accept(s_deckSock, reinterpret_cast<sockaddr*>(&clientAddr), &addrLen);
        if (client == INVALID_SOCKET) break;

        char buf[4096] = {};
        int n = recv(client, buf, sizeof(buf) - 1, 0);
        if (n > 0) {
            std::string req(buf, n);
            auto lineEnd = req.find("\r\n");
            std::string firstLine = (lineEnd != std::string::npos) ? req.substr(0, lineEnd) : req;

            // 1. Send-to-Phone File Shelf Page (/shelf and /download/<idx>)
            if (firstLine.find("GET /shelf") != std::string::npos) {
                std::ostringstream html;
                html << "<!DOCTYPE html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>"
                     << "<title>OpenPixel File Shelf</title>"
                     << "<style>body{background:#0b0b0c;color:#efede8;font-family:system-ui,sans-serif;padding:20px}"
                     << "a.file{display:block;background:#18181b;color:#efede8;text-decoration:none;padding:16px;border-radius:14px;margin-bottom:10px;border:1px solid rgba(255,255,255,.1)}</style></head><body>"
                     << "<h2>OpenPixel Shelf &middot; Files to your phone</h2><p>Tap any file to download over local Wi-Fi:</p>";
                {
                    std::lock_guard<std::recursive_mutex> lock(state.mutex);
                    for (size_t i = 0; i < state.shelfFiles.size(); ++i) {
                        html << "<a class='file' href='/download/" << i << "' download='"
                             << WideToUtf8(state.shelfFiles[i].displayName) << "'><strong>"
                             << WideToUtf8(state.shelfFiles[i].displayName) << "</strong></a>";
                    }
                }
                html << "</body></html>";
                SendHttpResponse(client, "200 OK", "text/html; charset=utf-8", html.str());
            } else if (firstLine.find("GET /download/") != std::string::npos) {
                auto pos = firstLine.find("/download/") + 10;
                int idx = std::atoi(firstLine.c_str() + pos);
                std::wstring filePath;
                std::wstring fileName;
                {
                    std::lock_guard<std::recursive_mutex> lock(state.mutex);
                    if (idx >= 0 && idx < static_cast<int>(state.shelfFiles.size())) {
                        filePath = state.shelfFiles[idx].fullPath;
                        fileName = state.shelfFiles[idx].displayName;
                    }
                }
                std::ifstream ifs(filePath.c_str(), std::ios::binary);
                if (ifs.is_open()) {
                    std::ostringstream ss;
                    ss << ifs.rdbuf();
                    // Preserve exact name and extension across all phone browsers (0.5.123 release fix)
                    std::string disp = "Content-Disposition: attachment; filename=\"" + WideToUtf8(fileName) + "\"\r\n";
                    SendHttpResponse(client, "200 OK", "application/octet-stream", ss.str(), disp);
                } else {
                    SendHttpResponse(client, "404 Not Found", "text/plain", "File not found");
                }
            }
            // 2. PC Deck Remote Endpoints (Only active when pcDeckEnabled == true and on Private network)
            else if (!state.settings.pcDeckEnabled || !state.isNetworkPrivate) {
                SendHttpResponse(client, "403 Forbidden", "text/plain",
                                 "PC Deck is disabled (Off by default, or network is marked Public). Enable PC Deck in OpenPixel Settings > Tools.");
            } else if (firstLine.find("GET /deck/pair") != std::string::npos) {
                std::string code = ExtractQueryParam(firstLine, "code");
                std::string dev  = ExtractQueryParam(firstLine, "device");
                if (dev.empty()) dev = "Phone";
                bool validCode = false;
                {
                    std::lock_guard<std::recursive_mutex> lock(state.mutex);
                    if (!state.activePairingCode.empty() &&
                        GetTickMs() <= state.pairingCodeExpireMs &&
                        WideToUtf8(state.activePairingCode) == code)
                    {
                        validCode = true;
                    }
                }
                if (!validCode) {
                    SendHttpResponse(client, "401 Unauthorized", "application/json", "{\"paired\":false,\"error\":\"Invalid or expired 2-minute pairing code\"}");
                } else {
                    // Explicit "Allow" prompt on PC per requirement
                    std::wstring msg = L"Allow \"" + Utf8ToWide(dev) + L"\" to control this PC via OpenPixel Deck over your Private Wi-Fi?";
                    int ans = MessageBoxW(state.hwndIsland, msg.c_str(), L"OpenPixel PC Deck Pairing Request", MB_YESNO | MB_ICONQUESTION | MB_TOPMOST);
                    if (ans == IDYES) {
                        std::lock_guard<std::recursive_mutex> lock(state.mutex);
                        PairedDeckDevice pdd;
                        pdd.deviceId = std::to_wstring(GetTickMs());
                        pdd.deviceName = Utf8ToWide(dev);
                        pdd.ipAddress = L"Private LAN";
                        pdd.authToken = L"np_enc_" + std::to_wstring(GetTickMs());
                        pdd.pairedAtMs = GetTickMs();
                        state.pairedDeckDevices.push_back(pdd);
                        SendHttpResponse(client, "200 OK", "application/json",
                                         "{\"paired\":true,\"token\":\"" + WideToUtf8(pdd.authToken) + "\"}");
                    } else {
                        SendHttpResponse(client, "403 Forbidden", "application/json", "{\"paired\":false,\"error\":\"Denied on PC\"}");
                    }
                }
            } else if (firstLine.find("GET /deck/move") != std::string::npos) {
                int dx = std::atoi(ExtractQueryParam(firstLine, "dx").c_str());
                int dy = std::atoi(ExtractQueryParam(firstLine, "dy").c_str());
                float spd = state.settings.deckPointerSpeed;
                POINT pt;
                if (GetCursorPos(&pt)) {
                    SetCursorPos(pt.x + static_cast<int>(dx * spd), pt.y + static_cast<int>(dy * spd));
                }
                SendHttpResponse(client, "200 OK", "application/json", "{\"ok\":true}");
            } else if (firstLine.find("GET /deck/cmd") != std::string::npos) {
                std::string act = ExtractQueryParam(firstLine, "a");
                std::string val = ExtractQueryParam(firstLine, "v");
                if (act == "play")   SystemServices::MediaTogglePlayPause(state);
                else if (act == "next") SystemServices::MediaNextTrack(state);
                else if (act == "prev") SystemServices::MediaPreviousTrack(state);
                else if (act == "vol") {
                    float v = static_cast<float>(std::clamp(std::atoi(val.c_str()), 0, 100));
                    SystemServices::SetMasterVolume(state, v, true);
                } else if (act == "lclick" || act == "rclick") {
                    INPUT inp[2] = {};
                    inp[0].type = INPUT_MOUSE;
                    inp[0].mi.dwFlags = (act == "lclick") ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_RIGHTDOWN;
                    inp[1].type = INPUT_MOUSE;
                    inp[1].mi.dwFlags = (act == "lclick") ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_RIGHTUP;
                    SendInput(2, inp, sizeof(INPUT));
                } else if (act == "app") {
                    SystemServices::ExecuteLauncherSlot(state, std::atoi(val.c_str()));
                } else if (act == "mute") {
                    SystemServices::ToggleGlobalMicMute(state);
                } else if (act == "shot") {
                    SystemServices::CaptureFullScreenshot(state);
                } else if (act == "lock") {
                    LockWorkStation();
                } else if (act == "sleep") {
                    SetSuspendState(FALSE, FALSE, FALSE);
                }
                if (state.hwndIsland) InvalidateRect(state.hwndIsland, nullptr, FALSE);
                SendHttpResponse(client, "200 OK", "application/json", "{\"ok\":true}");
            } else {
                SendHttpResponse(client, "200 OK", "text/html; charset=utf-8", BuildPcDeckMobileHtml(state));
            }
        }
        closesocket(client);
    }
}

void NetworkServers::Start(GlobalAppState& state) {
    if (s_running.exchange(true)) return;
    DetectPrivateNetworkAndLocalIp(state);
    GenerateTwoMinutePairingCode(state);

    if (state.settings.loopbackApiEnabled) {
        s_loopbackThread = std::thread(LoopbackServerWorker, &state);
        s_loopbackThread.detach();
    }
    s_deckThread = std::thread(DeckAndShelfServerWorker, &state);
    s_deckThread.detach();
}

void NetworkServers::Stop() {
    s_running.store(false);
    if (s_loopbackSock != INVALID_SOCKET) {
        closesocket(s_loopbackSock);
        s_loopbackSock = INVALID_SOCKET;
    }
    if (s_deckSock != INVALID_SOCKET) {
        closesocket(s_deckSock);
        s_deckSock = INVALID_SOCKET;
    }
}

} // namespace np
