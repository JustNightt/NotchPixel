#pragma once
#include "../core/ConfigManager.h"

namespace np {

// ============================================================================
// Local Network Servers
// 1. Local Loopback Scripting API (http://127.0.0.1:47823/notify, /tile, /agent)
// 2. PC Deck Companion Remote Server (http://<private-lan-ip>:47824)
//    - Off by default until turned on in Deck / Tools settings
//    - Restricted strictly to Windows Private networks (disabled on Public)
//    - 2-minute expiring 6-digit pairing code + explicit PC "Allow" prompt
//    - Paired device removal, Wake-on-LAN packet helper, local encrypted token
//    - Serves 5-tab mobile remote: Pad, Now Playing, Apps, Modes (Games/Slides/Movies), PC
// 3. Send-to-Phone File Shelf Wi-Fi QR Transfer Server (/shelf & /download/<idx>)
// ============================================================================
class NetworkServers {
public:
    static void Start(GlobalAppState& state);
    static void Stop();

    static bool DetectPrivateNetworkAndLocalIp(GlobalAppState& state);
    static std::wstring GenerateTwoMinutePairingCode(GlobalAppState& state);
    static bool SendWakeOnLanMagicPacket(const std::string& macHex);
    static bool HandleLoopbackRequest(GlobalAppState& state, const std::string& reqLineAndBody, std::string& outJson);
};

} // namespace np
