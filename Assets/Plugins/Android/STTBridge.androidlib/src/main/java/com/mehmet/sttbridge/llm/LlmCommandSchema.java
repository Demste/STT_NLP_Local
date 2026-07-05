package com.mehmet.sttbridge.llm;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

public final class LlmCommandSchema {
    private LlmCommandSchema() {}

    public static JSONObject defaultConfig() throws JSONException {
        JSONObject config = new JSONObject();
        config.put("version", 1);
        config.put("target_policy", "open_vocabulary");
        config.put("unknown_action", "unknown");

        JSONArray actions = new JSONArray();
        actions.put(action("move_to", "Move to the target."));
        actions.put(action("move_above", "Move above or onto the target."));
        actions.put(action("move_below", "Move below the target."));
        actions.put(action("follow", "Follow the target."));
        actions.put(action("stop", "Stop or hover in place."));
        actions.put(action("pick_up", "Pick up the target."));
        actions.put(action("drop", "Drop the held item."));
        actions.put(action("look_at", "Look at the target."));
        actions.put(action("rotate_to", "Rotate toward the target or direction."));
        actions.put(action("unknown", "No allowed action fits the utterance."));
        config.put("allowed_actions", actions);

        JSONObject llm = new JSONObject();
        llm.put("max_tokens", 128);
        llm.put("temperature", 0.1);
        llm.put("top_p", 0.95);
        llm.put("top_k", 40);
        llm.put("context_size", 1024);
        llm.put("threads", 4);
        config.put("llm", llm);

        JSONArray required = new JSONArray();
        required.put("action");
        required.put("target");
        required.put("confidence");
        config.put("required_fields", required);
        return config;
    }

    public static JSONObject mergeWithDefault(String configJson) throws JSONException {
        JSONObject config = defaultConfig();
        if (configJson == null || configJson.trim().isEmpty()) {
            return config;
        }

        JSONObject override = new JSONObject(configJson);
        JSONArray names = override.names();
        if (names == null) {
            return config;
        }

        for (int i = 0; i < names.length(); i++) {
            String name = names.getString(i);
            config.put(name, override.get(name));
        }
        return config;
    }

    private static JSONObject action(String id, String description) throws JSONException {
        JSONObject action = new JSONObject();
        action.put("id", id);
        action.put("description", description);
        return action;
    }
}
