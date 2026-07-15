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
        if (start < 0) {
            throw new JSONException("LLM output did not contain a JSON object.");
        }
        int depth = 0;
        boolean inString = false;
        boolean escaped = false;
        for (int i = start; i < trimmed.length(); i++) {
            char c = trimmed.charAt(i);
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return new JSONObject(trimmed.substring(start, i + 1));
        }
        throw new JSONException("LLM JSON object was not closed.");
    }

    /** Parse exactly one balanced root object; no prose, Markdown, or suffix. */
    public static JSONObject parseSingleObject(String value) throws JSONException {
        if (value == null) throw new JSONException("LLM output is null.");
        String trimmed = value.trim();
        if (!trimmed.startsWith("{") || !trimmed.endsWith("}")) {
            throw new JSONException("LLM output must be exactly one JSON object.");
        }
        int end = balancedRootEnd(trimmed);
        if (end != trimmed.length() - 1) {
            throw new JSONException("LLM output contains text after the root JSON object.");
        }
        return new JSONObject(trimmed);
    }

    public static boolean isBalancedRootObject(String value) {
        if (value == null) return false;
        String trimmed = value.trim();
        return trimmed.startsWith("{") && balancedRootEnd(trimmed) == trimmed.length() - 1;
    }

    private static int balancedRootEnd(String value) {
        int depth = 0;
        boolean inString = false;
        boolean escaped = false;
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}') {
                depth--;
                if (depth == 0) return i;
                if (depth < 0) return -1;
            }
        }
        return -1;
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

        if (!arrayContains(result, "MISSING_INFO")) {
            result.put("MISSING_INFO");
        }
        if (!arrayContains(result, "UNKNOWN")) {
            result.put("UNKNOWN");
        }
        return result;
    }
}
