package com.mehmet.sttbridge.llm;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

public final class LlmJsonUtils {
    private LlmJsonUtils() {}

    public static JSONObject extractObject(String value) throws JSONException {
        if (value == null) {
            throw new JSONException("LLM output is null.");
        }

        String trimmed = value.trim();
        int start = trimmed.indexOf('{');
        int end = trimmed.lastIndexOf('}');
        if (start < 0 || end < start) {
            throw new JSONException("LLM output did not contain a JSON object.");
        }

        return new JSONObject(trimmed.substring(start, end + 1));
    }

    public static boolean arrayContains(JSONArray array, String value) {
        if (array == null || value == null) {
            return false;
        }

        for (int i = 0; i < array.length(); i++) {
            Object item = array.opt(i);
            if (item instanceof JSONObject) {
                if (value.equals(((JSONObject) item).optString("id", null))) {
                    return true;
                }
            } else if (value.equals(String.valueOf(item))) {
                return true;
            }
        }
        return false;
    }

    public static JSONArray allowedActionIds(JSONObject config) {
        JSONArray result = new JSONArray();
        JSONArray actions = config != null ? config.optJSONArray("allowed_actions") : null;
        if (actions != null) {
            for (int i = 0; i < actions.length(); i++) {
                Object item = actions.opt(i);
                if (item instanceof JSONObject) {
                    String id = ((JSONObject) item).optString("id", "");
                    if (!id.isEmpty()) {
                        result.put(id);
                    }
                } else if (item != null) {
                    result.put(String.valueOf(item));
                }
            }
        }

        if (!arrayContains(result, "unknown")) {
            result.put("unknown");
        }
        return result;
    }
}

