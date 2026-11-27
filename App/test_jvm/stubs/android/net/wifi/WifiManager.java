package android.net.wifi;

public class WifiManager {
    private WifiInfo info;

    public WifiManager(int ipv4LittleEndian) {
        this.info = new WifiInfo(ipv4LittleEndian);
    }

    public WifiInfo getConnectionInfo() {
        return info;
    }
}
