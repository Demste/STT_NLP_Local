package com.mehmet.sttbridge.llm;

import org.json.JSONException;
import org.json.JSONObject;

public final class LlmJsonUtils {
    private LlmJsonUtils() {}

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

}
