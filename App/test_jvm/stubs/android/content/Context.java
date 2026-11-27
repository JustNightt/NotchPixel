package android.content;

import java.util.HashMap;
import java.util.Map;

public class Context {
    public static final String WIFI_SERVICE = "wifi";
    private final Map<String, SharedPreferences> prefsMap = new HashMap<>();
    private final Map<String, Object> services = new HashMap<>();

    public Context getApplicationContext() {
        return this;
    }

    public SharedPreferences getSharedPreferences(String name, int mode) {
        return prefsMap.computeIfAbsent(name, k -> new SharedPreferences());
    }

    public void registerSystemService(String name, Object svc) {
        services.put(name, svc);
    }

    public Object getSystemService(String name) {
        return services.get(name);
    }

    public static final int MODE_PRIVATE = 0;
}
