package com.mehmet.sttbridge.llm;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.Arrays;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;

/** Action-specific tagged-union contract used at the Java/LLM boundary. */
public final class LlmCommandSchema {
    public static final String[] ACTIONS = {
            "TAKE_OFF", "LAND_HOME", "LAND_CURRENT", "LAND_AT_WAIT_POINT",
            "MOVE_DIRECTION", "MOVE_VERTICAL", "SET_SPEED", "TURN", "FIND_OBJECT",
            "TRACK_OBJECT", "HOLD_OVER_OBJECT", "OBSERVE_CURRENT", "GO_TO_LOCATION",
            "HOVER", "FIND_MARKER", "STOP", "MISSING_INFO", "UNKNOWN"
    };
    public static final String[] OBJECTS = {
            "TRUCK", "PICKUP", "CAR", "EXCAVATOR", "LOADER", "FORKLIFT",
            "CEMENT_TRUCK", "SEMI_TRUCK", "CRANE"
    };
    public static final String[] DIRECTIONS = {"LEFT", "RIGHT", "FORWARD", "BACKWARD", "UP", "DOWN"};
    public static final String[] TURN_DIRECTIONS = {"CLOCKWISE", "COUNTER_CLOCKWISE", "LEFT", "RIGHT"};
    public static final String[] COLORS = {"RED", "GREEN", "BLUE", "WHITE", "BLACK"};
    public static final String[] SHAPES = {"LINE", "SQUARE", "TRIANGLE", "PLUS"};
    public static final String[] OBSERVE_MODES = {"WATCH", "MONITOR", "HOLD_AND_WATCH"};
    public static final String[] VOICE_TYPES = {"NORMAL", "WHISTLED", "ENCRYPTED"};
    public static final String[] STT_CONFIDENCE_LEVELS = {"GOOD", "MEDIUM", "BAD"};

    private static final Map<String, Set<String>> ACTION_FIELDS = buildActionFields();
    private static final Map<String, String[]> REQUIRED_FIELDS = buildRequiredFields();

    private LlmCommandSchema() {}

    public static JSONObject defaultCommand() throws JSONException {
        return new JSONObject().put("action", "UNKNOWN").put("confidence", 0.0);
    }

    public static boolean isAction(String action) {
        return action != null && ACTION_FIELDS.containsKey(action);
    }

    public static boolean isClassifiableAction(String action) {
        return isAction(action) && !"MISSING_INFO".equals(action);
    }

    public static Set<String> fieldsFor(String action) {
        Set<String> fields = ACTION_FIELDS.get(action);
        return fields != null ? fields : Collections.<String>emptySet();
    }

    public static String[] requiredFieldsFor(String action) {
        String[] fields = REQUIRED_FIELDS.get(action);
        return fields != null ? fields.clone() : new String[0];
    }

    public static boolean supportsObject(String action) {
        return fieldsFor(action).contains("object");
    }

    public static boolean isObject(String value) {
        if (value == null) return false;
        for (String object : OBJECTS) if (object.equals(value)) return true;
        return false;
    }

    public static String objectCsv() {
        StringBuilder builder = new StringBuilder();
        for (String value : OBJECTS) {
            if (builder.length() > 0) builder.append(',');
            builder.append(value);
        }
        return builder.toString();
    }

    public static JSONObject defaultConfig() throws JSONException {
        JSONObject config = new JSONObject();
        config.put("version", 3);
        config.put("unknown_action", "UNKNOWN");
        config.put("missing_action", "MISSING_INFO");
        JSONArray actions = new JSONArray();
        for (String id : ACTIONS) actions.put(new JSONObject().put("id", id));
        config.put("allowed_actions", actions);

        JSONObject llm = new JSONObject();
        llm.put("max_tokens", 128); // legacy/general ceiling
        llm.put("classification_max_tokens", 32);
        llm.put("slot_max_tokens", 64);
        llm.put("temperature", 0.1);
        llm.put("classification_temperature", 0.0);
        llm.put("slot_temperature", 0.0);
        llm.put("top_p", 0.95);
        llm.put("top_k", 40);
        llm.put("context_size", 1024);
        llm.put("threads", 4);
        config.put("llm", llm);
        return config;
    }

    public static JSONObject mergeWithDefault(String configJson) throws JSONException {
        JSONObject config = defaultConfig();
        if (configJson == null || configJson.trim().isEmpty()) return config;
        JSONObject override = new JSONObject(configJson);
        if (override.has("llm") && override.optJSONObject("llm") != null) {
            mergeObject(config.getJSONObject("llm"), override.getJSONObject("llm"));
        }
        for (String field : new String[] {"voice_type", "stt_confidence_level"}) {
            if (override.has(field)) config.put(field, override.get(field));
        }
        return config;
    }

    private static void mergeObject(JSONObject target, JSONObject source) throws JSONException {
        JSONArray names = source.names();
        if (names == null) return;
        for (int i = 0; i < names.length(); i++) {
            String name = names.getString(i);
            target.put(name, source.get(name));
        }
    }

    private static Map<String, Set<String>> buildActionFields() {
        Map<String, Set<String>> value = new LinkedHashMap<>();
        value.put("TAKE_OFF", fields());
        value.put("LAND_HOME", fields());
        value.put("LAND_CURRENT", fields());
        value.put("LAND_AT_WAIT_POINT", fields());
        value.put("MOVE_DIRECTION", fields("direction", "distance_m"));
        value.put("MOVE_VERTICAL", fields("direction", "distance_m"));
        value.put("SET_SPEED", fields("speed_kmh"));
        value.put("TURN", fields("turn_direction", "angle_deg"));
        value.put("FIND_OBJECT", fields("object"));
        value.put("TRACK_OBJECT", fields("object"));
        value.put("HOLD_OVER_OBJECT", fields("object"));
        value.put("OBSERVE_CURRENT", fields("observe_mode", "duration_s", "duration_min"));
        value.put("GO_TO_LOCATION", fields("location_keyword", "coordinate"));
        value.put("HOVER", fields("duration_s", "duration_min"));
        value.put("FIND_MARKER", fields("color", "shape", "marker_index"));
        value.put("STOP", fields());
        value.put("MISSING_INFO", fields("target_action", "missing_field", "missing_message"));
        value.put("UNKNOWN", fields());
        return Collections.unmodifiableMap(value);
    }

    private static Map<String, String[]> buildRequiredFields() {
        Map<String, String[]> value = new LinkedHashMap<>();
        value.put("MOVE_DIRECTION", new String[] {"direction", "distance_m"});
        value.put("MOVE_VERTICAL", new String[] {"direction", "distance_m"});
        value.put("SET_SPEED", new String[] {"speed_kmh"});
        value.put("TURN", new String[] {"turn_direction", "angle_deg"});
        value.put("FIND_OBJECT", new String[] {"object"});
        value.put("TRACK_OBJECT", new String[] {"object"});
        value.put("HOLD_OVER_OBJECT", new String[] {"object"});
        value.put("GO_TO_LOCATION", new String[] {"location_keyword|coordinate"});
        value.put("FIND_MARKER", new String[] {"color", "shape"});
        return Collections.unmodifiableMap(value);
    }

    private static Set<String> fields(String... values) {
        return Collections.unmodifiableSet(new LinkedHashSet<>(Arrays.asList(values)));
    }
}
