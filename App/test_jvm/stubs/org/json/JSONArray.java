package org.json;

import java.util.ArrayList;
import java.util.List;

public class JSONArray {
    private final List<Object> items = new ArrayList<>();

    public JSONArray() {}

    public JSONArray(String json) {
        Object parsed = JSONObject.JsonParser.parseValue(json);
        if (!(parsed instanceof JSONArray)) throw new JSONException("Not a JSONArray");
        this.items.addAll(((JSONArray) parsed).items);
    }

    public int length() {
        return items.size();
    }

    public JSONArray put(Object val) {
        items.add(val);
        return this;
    }

    public Object get(int idx) {
        return items.get(idx);
    }

    public JSONObject getJSONObject(int idx) {
        Object v = items.get(idx);
        if (v instanceof JSONObject) return (JSONObject) v;
        throw new JSONException("Index " + idx + " is not a JSONObject");
    }

    @Override
    public String toString() {
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < items.size(); i++) {
            if (i > 0) sb.append(',');
            sb.append(JSONObject.serializeValue(items.get(i)));
        }
        sb.append(']');
        return sb.toString();
    }
}
