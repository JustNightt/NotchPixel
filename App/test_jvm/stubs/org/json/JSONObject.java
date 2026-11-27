package org.json;

import java.util.LinkedHashMap;
import java.util.Map;

public class JSONObject {
    private final Map<String, Object> map = new LinkedHashMap<>();

    public JSONObject() {}

    public JSONObject(String json) {
        Object parsed = JsonParser.parseValue(json);
        if (!(parsed instanceof JSONObject)) throw new JSONException("Not a JSONObject");
        this.map.putAll(((JSONObject) parsed).map);
    }

    public JSONObject put(String key, Object value) {
        map.put(key, value);
        return this;
    }

    public JSONObject put(String key, int value) {
        map.put(key, Integer.valueOf(value));
        return this;
    }

    public JSONObject put(String key, long value) {
        map.put(key, Long.valueOf(value));
        return this;
    }

    public JSONObject put(String key, boolean value) {
        map.put(key, Boolean.valueOf(value));
        return this;
    }

    public String optString(String key, String def) {
        Object v = map.get(key);
        if (v == null || v == NULL) return def;
        return String.valueOf(v);
    }

    public int optInt(String key, int def) {
        Object v = map.get(key);
        if (v instanceof Number) return ((Number) v).intValue();
        if (v instanceof String) {
            try { return Integer.parseInt((String) v); } catch (Exception ignored) {}
        }
        return def;
    }

    public long optLong(String key, long def) {
        Object v = map.get(key);
        if (v instanceof Number) return ((Number) v).longValue();
        if (v instanceof String) {
            try { return Long.parseLong((String) v); } catch (Exception ignored) {}
        }
        return def;
    }

    public boolean optBoolean(String key, boolean def) {
        Object v = map.get(key);
        if (v instanceof Boolean) return (Boolean) v;
        if (v instanceof String) return Boolean.parseBoolean((String) v);
        return def;
    }

    public JSONObject optJSONObject(String key) {
        Object v = map.get(key);
        return v instanceof JSONObject ? (JSONObject) v : null;
    }

    public JSONArray optJSONArray(String key) {
        Object v = map.get(key);
        return v instanceof JSONArray ? (JSONArray) v : null;
    }

    public static final Object NULL = new Object();

    @Override
    public String toString() {
        StringBuilder sb = new StringBuilder("{");
        boolean first = true;
        for (Map.Entry<String, Object> e : map.entrySet()) {
            if (!first) sb.append(',');
            first = false;
            sb.append(quote(e.getKey())).append(':').append(serializeValue(e.getValue()));
        }
        sb.append('}');
        return sb.toString();
    }

    static String serializeValue(Object v) {
        if (v == null || v == NULL) return "null";
        if (v instanceof Boolean || v instanceof Number || v instanceof JSONObject || v instanceof JSONArray) {
            return v.toString();
        }
        return quote(String.valueOf(v));
    }

    private static String quote(String s) {
        StringBuilder sb = new StringBuilder("\"");
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\b': sb.append("\\b"); break;
                case '\f': sb.append("\\f"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (c < 0x20) sb.append(String.format("\\u%04x", (int) c));
                    else sb.append(c);
            }
        }
        sb.append('"');
        return sb.toString();
    }

    static final class JsonParser {
        private final String s;
        private int pos = 0;

        private JsonParser(String s) {
            this.s = s;
        }

        static Object parseValue(String src) {
            if (src == null) throw new JSONException("Null JSON");
            JsonParser p = new JsonParser(src);
            Object v = p.readValue();
            p.skipWs();
            if (p.pos != p.s.length()) throw new JSONException("Trailing characters");
            return v;
        }

        private void skipWs() {
            while (pos < s.length() && Character.isWhitespace(s.charAt(pos))) pos++;
        }

        private Object readValue() {
            skipWs();
            if (pos >= s.length()) throw new JSONException("Unexpected end");
            char c = s.charAt(pos);
            if (c == '{') return readObject();
            if (c == '[') return readArray();
            if (c == '"') return readString();
            if (c == 't' && s.startsWith("true", pos)) { pos += 4; return Boolean.TRUE; }
            if (c == 'f' && s.startsWith("false", pos)) { pos += 5; return Boolean.FALSE; }
            if (c == 'n' && s.startsWith("null", pos)) { pos += 4; return NULL; }
            return readNumber();
        }

        private JSONObject readObject() {
            pos++; // '{'
            JSONObject obj = new JSONObject();
            skipWs();
            if (pos < s.length() && s.charAt(pos) == '}') { pos++; return obj; }
            while (true) {
                skipWs();
                String key = readString();
                skipWs();
                if (pos >= s.length() || s.charAt(pos) != ':') throw new JSONException("Expected ':'");
                pos++;
                Object val = readValue();
                obj.put(key, val);
                skipWs();
                if (pos >= s.length()) throw new JSONException("Unterminated object");
                char sep = s.charAt(pos++);
                if (sep == '}') return obj;
                if (sep != ',') throw new JSONException("Expected ',' or '}'");
            }
        }

        private JSONArray readArray() {
            pos++; // '['
            JSONArray arr = new JSONArray();
            skipWs();
            if (pos < s.length() && s.charAt(pos) == ']') { pos++; return arr; }
            while (true) {
                arr.put(readValue());
                skipWs();
                if (pos >= s.length()) throw new JSONException("Unterminated array");
                char sep = s.charAt(pos++);
                if (sep == ']') return arr;
                if (sep != ',') throw new JSONException("Expected ',' or ']'");
            }
        }

        private String readString() {
            if (pos >= s.length() || s.charAt(pos) != '"') throw new JSONException("Expected '\"'");
            pos++;
            StringBuilder sb = new StringBuilder();
            while (pos < s.length()) {
                char c = s.charAt(pos++);
                if (c == '"') return sb.toString();
                if (c == '\\') {
                    if (pos >= s.length()) throw new JSONException("Unterminated escape");
                    char esc = s.charAt(pos++);
                    switch (esc) {
                        case '"': sb.append('"'); break;
                        case '\\': sb.append('\\'); break;
                        case '/': sb.append('/'); break;
                        case 'b': sb.append('\b'); break;
                        case 'f': sb.append('\f'); break;
                        case 'n': sb.append('\n'); break;
                        case 'r': sb.append('\r'); break;
                        case 't': sb.append('\t'); break;
                        case 'u':
                            String hex = s.substring(pos, pos + 4);
                            pos += 4;
                            sb.append((char) Integer.parseInt(hex, 16));
                            break;
                        default: throw new JSONException("Invalid escape \\" + esc);
                    }
                } else {
                    sb.append(c);
                }
            }
            throw new JSONException("Unterminated string");
        }

        private Number readNumber() {
            int start = pos;
            while (pos < s.length()) {
                char c = s.charAt(pos);
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') pos++;
                else break;
            }
            String numStr = s.substring(start, pos);
            if (numStr.isEmpty()) throw new JSONException("Invalid number");
            if (numStr.indexOf('.') >= 0 || numStr.indexOf('e') >= 0 || numStr.indexOf('E') >= 0) {
                return Double.valueOf(numStr);
            }
            long lv = Long.parseLong(numStr);
            if (lv >= Integer.MIN_VALUE && lv <= Integer.MAX_VALUE) return Integer.valueOf((int) lv);
            return Long.valueOf(lv);
        }
    }
}
