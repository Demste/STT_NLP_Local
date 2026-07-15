package com.mehmet.sttbridge.llm;

import org.json.JSONException;
import org.json.JSONObject;

/** Materializes the stable Unity/downstream command shape from sparse validated data. */
public final class LlmPublicCommandSerializer {
    public static final String[] PUBLIC_FIELDS = {
            "action", "direction", "turn_direction", "distance_m", "duration_s", "duration_min",
            "angle_deg", "speed_kmh", "object", "target_action", "color", "shape", "marker_index",
            "location_keyword", "coordinate", "observe_mode", "voice_type", "stt_confidence_level",
            "missing_field", "missing_message", "confidence"
    };

    private LlmPublicCommandSerializer() {}

    public static JSONObject materialize(JSONObject sparse) throws JSONException {
        JSONObject source = sparse != null ? sparse : new JSONObject();
        JSONObject output = new JSONObject();
        for (String field : PUBLIC_FIELDS) {
            Object value = source.has(field) && !source.isNull(field) ? source.get(field) : JSONObject.NULL;
            if ("action".equals(field) && value == JSONObject.NULL) value = "UNKNOWN";
            if ("confidence".equals(field) && value == JSONObject.NULL) value = 0.0;
            output.put(field, value);
        }
        return output;
    }
}
