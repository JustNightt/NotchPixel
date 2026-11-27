package android.content;

import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;

public class SharedPreferences {
    private final Map<String, Object> data = new HashMap<>();

    public synchronized String getString(String key, String defValue) {
        Object v = data.get(key);
        return v instanceof String ? (String) v : defValue;
    }

    public Editor edit() {
        return new Editor();
    }

    public final class Editor {
        private final Map<String, Object> puts = new HashMap<>();
        private final Set<String> removes = new HashSet<>();

        public Editor putString(String key, String value) {
            puts.put(key, value);
            removes.remove(key);
            return this;
        }

        public Editor remove(String key) {
            removes.add(key);
            puts.remove(key);
            return this;
        }

        public void apply() {
            synchronized (SharedPreferences.this) {
                for (String r : removes) data.remove(r);
                data.putAll(puts);
            }
        }
    }
}
