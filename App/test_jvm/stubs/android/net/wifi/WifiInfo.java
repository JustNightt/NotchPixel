package android.net.wifi;

public class WifiInfo {
    private final int ipAddress;

    public WifiInfo(int ipAddress) {
        this.ipAddress = ipAddress;
    }

    public int getIpAddress() {
        return ipAddress;
    }
}
