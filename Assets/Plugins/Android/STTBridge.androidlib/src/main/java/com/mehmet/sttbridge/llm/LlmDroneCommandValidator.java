package com.mehmet.sttbridge.llm;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.Arrays;
import java.util.HashSet;
import java.util.Iterator;
import java.util.Locale;
import java.util.Set;

/** Evidence-gated validator which always emits sparse action-specific JSON. */
public final class LlmDroneCommandValidator {
    private static final Set<String> DIRECTIONS = set(LlmCommandSchema.DIRECTIONS);
    private static final Set<String> TURN_DIRECTIONS = set(LlmCommandSchema.TURN_DIRECTIONS);
    private static final Set<String> COLORS = set(LlmCommandSchema.COLORS);
    private static final Set<String> SHAPES = set(LlmCommandSchema.SHAPES);
    private static final Set<String> OBSERVE_MODES = set(LlmCommandSchema.OBSERVE_MODES);
    private static final Set<String> VOICE_TYPES = set(LlmCommandSchema.VOICE_TYPES);
    private static final Set<String> STT_LEVELS = set(LlmCommandSchema.STT_CONFIDENCE_LEVELS);

    private LlmDroneCommandValidator() {}

    /** Compatibility entrypoint: model payload is treated as classification only. */
    public static Result validate(JSONObject modelOutput, JSONObject context) throws JSONException {
        return validateClassification(modelOutput, null, context);
    }

    /** Trusts values only because they came from the deterministic Python extractor. */
    public static Result validateBaseline(JSONObject evidence, JSONObject context) throws JSONException {
        JSONObject source = evidence != null ? evidence : LlmCommandSchema.defaultCommand();
        String action = upper(source.opt("action"));
        if (!LlmCommandSchema.isAction(action)) action = "UNKNOWN";
        if ("MISSING_INFO".equals(action)) {
            String targetAction = upper(source.opt("target_action"));
            action = LlmCommandSchema.isClassifiableAction(targetAction) ? targetAction : "UNKNOWN";
        }
        double confidence = clamp(number(source.opt("confidence"), "UNKNOWN".equals(action) ? 0.0 : 0.9));
        return build(action, source, context, confidence, new JSONArray(), new JSONArray());
    }

    /**
     * The LLM may select only an action. Slots are copied solely from trusted
     * deterministic evidence when that evidence supports the same action.
     */
    public static Result validateClassification(JSONObject modelOutput, JSONObject evidence, JSONObject context) throws JSONException {
        JSONObject model = modelOutput != null ? modelOutput : new JSONObject();
        JSONArray errors = new JSONArray();
        JSONArray warnings = new JSONArray();

        Iterator<String> names = model.keys();
        while (names.hasNext()) {
            String field = names.next();
            if (!"action".equals(field)) warnings.put("llm_field_removed:not_classification_field:" + field);
        }

        String action = upper(model.opt("action"));
        if (!LlmCommandSchema.isClassifiableAction(action)) {
            errors.put("action_not_allowed");
            action = "UNKNOWN";
        }

        JSONObject trusted = new JSONObject();
        if (evidence != null && action.equals(evidenceAction(evidence))) {
            trusted = evidence;
        }
        double confidence = "UNKNOWN".equals(action) ? 0.0 : (trusted.length() > 0 ? 0.84 : 0.76);
        return build(action, trusted, context, confidence, errors, warnings);
    }

    /** Accepts one semantic object only after whitelist and textual evidence checks. */
    public static Result validateSemanticSlot(JSONObject modelOutput, JSONObject baseline, String action,
                                              String analysisInput, JSONObject context) throws JSONException {
        JSONObject model = modelOutput != null ? modelOutput : new JSONObject();
        JSONArray errors = new JSONArray();
        JSONArray warnings = new JSONArray();
        Iterator<String> names = model.keys();
        while (names.hasNext()) {
            String field = names.next();
            if (!"object".equals(field) && !"evidence".equals(field)) {
                warnings.put("slot_llm_field_removed:" + field);
            }
        }

        String intendedAction = upper(action);
        if (!LlmCommandSchema.supportsObject(intendedAction)) {
            errors.put("object_not_allowed_for_action");
            return build(intendedAction, new JSONObject(), context, .0, errors, warnings);
        }

        if (baseline != null && intendedAction.equals(evidenceAction(baseline)) && baseline.has("object")) {
            warnings.put("deterministic_object_preferred");
            return build(intendedAction, baseline, context, .84, errors, warnings);
        }

        String object = upper(model.opt("object"));
        String evidence = model.optString("evidence", "").trim();
        if ("NONE".equals(object)) {
            warnings.put("slot_llm_returned_none");
            return build(intendedAction, new JSONObject(), context, .0, errors, warnings);
        }
        if (!LlmCommandSchema.isObject(object)) {
            errors.put("object_not_allowed");
            return build(intendedAction, new JSONObject(), context, .0, errors, warnings);
        }
        if (evidence.isEmpty() || !containsEvidence(analysisInput, evidence)) {
            errors.put("slot_evidence_not_in_analysis_input");
            return build(intendedAction, new JSONObject(), context, .0, errors, warnings);
        }
        String detected = baseline != null ? baseline.optString("detected_object_phrase", "") : "";
        if (!detected.isEmpty() && !evidenceOverlaps(detected, evidence)) {
            errors.put("slot_evidence_conflicts_with_detected_phrase");
            return build(intendedAction, new JSONObject(), context, .0, errors, warnings);
        }
        JSONObject trusted = new JSONObject().put("object", object);
        return build(intendedAction, trusted, context, .82, errors, warnings);
    }

    private static Result build(String intendedAction, JSONObject evidence, JSONObject context, double confidence, JSONArray errors, JSONArray warnings) throws JSONException {
        JSONObject output = new JSONObject().put("action", intendedAction);
        Set<String> fields = LlmCommandSchema.fieldsFor(intendedAction);

        copyEnum(evidence, output, warnings, fields, "direction", DIRECTIONS);
        copyEnum(evidence, output, warnings, fields, "turn_direction", TURN_DIRECTIONS);
        copyObject(evidence, output, warnings, fields);
        copyEnum(evidence, output, warnings, fields, "color", COLORS);
        copyEnum(evidence, output, warnings, fields, "shape", SHAPES);
        copyEnum(evidence, output, warnings, fields, "observe_mode", OBSERVE_MODES);
        copyNumber(evidence, output, warnings, fields, "distance_m", false);
        copyNumber(evidence, output, warnings, fields, "duration_s", false);
        copyNumber(evidence, output, warnings, fields, "duration_min", false);
        copyNumber(evidence, output, warnings, fields, "angle_deg", false);
        copyNumber(evidence, output, warnings, fields, "speed_kmh", false);
        copyNumber(evidence, output, warnings, fields, "marker_index", true);
        copyString(evidence, output, fields, "location_keyword");
        copyCoordinate(evidence, output, warnings, fields);

        if (output.has("duration_s") && output.has("duration_min")) {
            output.remove("duration_min");
            warnings.put("duration_min_removed_when_duration_s_present");
        }

        String missing = firstMissing(intendedAction, output, evidence);
        if (!"UNKNOWN".equals(intendedAction) && !missing.isEmpty()) {
            output.put("action", "MISSING_INFO");
            output.put("target_action", intendedAction);
            output.put("missing_field", missing);
            output.put("missing_message", missingMessage(intendedAction, missing));
            warnings.put("required_field_missing:" + missing);
        }

        if ("UNKNOWN".equals(intendedAction)) confidence = 0.0;
        copyUpstreamMetadata(context, output, warnings, "voice_type", VOICE_TYPES);
        copyUpstreamMetadata(context, output, warnings, "stt_confidence_level", STT_LEVELS);
        output.put("confidence", clamp(confidence));
        return new Result(output, errors, warnings);
    }

    private static String evidenceAction(JSONObject evidence) {
        String action = upper(evidence.opt("action"));
        return "MISSING_INFO".equals(action) ? upper(evidence.opt("target_action")) : action;
    }

    private static String firstMissing(String action, JSONObject out, JSONObject evidence) {
        if ("GO_TO_LOCATION".equals(action)) {
            if (!out.has("location_keyword") && !out.has("coordinate")) {
                return evidence != null && evidence.has("coordinate") ? "coordinate" : "location_keyword";
            }
            return "";
        }
        for (String field : LlmCommandSchema.requiredFieldsFor(action)) {
            if (!out.has(field)) return field;
        }
        return "";
    }

    private static String missingMessage(String action, String field) {
        if ("MOVE_DIRECTION".equals(action)) return "distance_m".equals(field) ? "Yatay hareket komutu için metre bilgisi eksik." : "Yatay hareket komutu için yön bilgisi eksik.";
        if ("MOVE_VERTICAL".equals(action)) return "distance_m".equals(field) ? "Dikey hareket komutu için metre bilgisi eksik." : "Dikey hareket komutu için yön bilgisi eksik.";
        if ("SET_SPEED".equals(action)) return "Hız komutu için km/h bilgisi eksik.";
        if ("TURN".equals(action)) return "angle_deg".equals(field) ? "Dönüş komutu için açı bilgisi eksik." : "Dönüş komutu için yön bilgisi eksik.";
        if ("FIND_OBJECT".equals(action)) return "Nesne bulma komutu için hedef nesne bilgisi eksik.";
        if ("TRACK_OBJECT".equals(action)) return "Takip komutu için hedef nesne bilgisi eksik.";
        if ("HOLD_OVER_OBJECT".equals(action)) return "Nesne üzerinde durma komutu için hedef nesne bilgisi eksik.";
        if ("GO_TO_LOCATION".equals(action)) return "coordinate".equals(field) ? "Konuma gitme komutu için geçerli koordinat bilgisi eksik." : "Konuma gitme komutu için hedef konum bilgisi eksik.";
        if ("FIND_MARKER".equals(action)) return "color".equals(field) ? "İşaret bulma komutu için renk bilgisi eksik." : "İşaret bulma komutu için şekil bilgisi eksik.";
        return "Komut için zorunlu bilgi eksik.";
    }

    private static void copyCoordinate(JSONObject in, JSONObject out, JSONArray warnings, Set<String> fields) throws JSONException {
        if (!fields.contains("coordinate") || in == null || !in.has("coordinate")) return;
        Object raw = in.opt("coordinate");
        if (!(raw instanceof JSONObject)) { warnings.put("coordinate_invalid"); return; }
        JSONObject coordinate = (JSONObject) raw;
        double lat = number(coordinate.opt("lat"), Double.NaN);
        double lon = number(coordinate.opt("lon"), Double.NaN);
        if (!Double.isFinite(lat) || !Double.isFinite(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180) {
            warnings.put("coordinate_invalid");
            return;
        }
        out.put("coordinate", new JSONObject().put("lat", lat).put("lon", lon));
    }

    private static void copyNumber(JSONObject in, JSONObject out, JSONArray warnings, Set<String> fields, String field, boolean integer) throws JSONException {
        if (!fields.contains(field) || in == null || !in.has(field) || in.isNull(field)) return;
        double value = number(in.opt(field), Double.NaN);
        if (!Double.isFinite(value) || value < 0 || (integer && value != Math.rint(value))) {
            warnings.put(field + "_invalid");
            return;
        }
        out.put(field, integer ? (long) value : value);
    }

    private static void copyEnum(JSONObject in, JSONObject out, JSONArray warnings, Set<String> fields, String field, Set<String> allowed) throws JSONException {
        if (!fields.contains(field) || in == null || !in.has(field) || in.isNull(field)) return;
        String value = upper(in.opt(field));
        if (allowed.contains(value)) out.put(field, value); else warnings.put(field + "_invalid");
    }

    private static void copyObject(JSONObject in, JSONObject out, JSONArray warnings, Set<String> fields) throws JSONException {
        if (!fields.contains("object") || in == null || !in.has("object") || in.isNull("object")) return;
        String value = upper(in.opt("object"));
        if (LlmCommandSchema.isObject(value)) out.put("object", value); else warnings.put("object_invalid");
    }

    private static boolean containsEvidence(String input, String evidence) {
        String haystack = " " + normalize(input) + " ";
        String needle = normalize(evidence);
        return !needle.isEmpty() && haystack.contains(" " + needle + " ");
    }

    private static boolean evidenceOverlaps(String detected, String evidence) {
        String left = normalize(detected);
        String right = normalize(evidence);
        return !left.isEmpty() && !right.isEmpty() && (left.contains(right) || right.contains(left));
    }

    private static String normalize(String value) {
        String text = value == null ? "" : value.toLowerCase(new Locale("tr", "TR"));
        text = text.replace('ı', 'i').replace('ş', 's').replace('ğ', 'g').replace('ü', 'u').replace('ö', 'o').replace('ç', 'c');
        return text.replaceAll("[^a-z0-9]+", " ").trim().replaceAll("\\s+", " ");
    }

    private static void copyString(JSONObject in, JSONObject out, Set<String> fields, String field) throws JSONException {
        if (!fields.contains(field) || in == null) return;
        Object raw = in.opt(field);
        if (raw instanceof String && !((String) raw).trim().isEmpty()) out.put(field, ((String) raw).trim());
    }

    private static void copyUpstreamMetadata(JSONObject context, JSONObject out, JSONArray warnings, String field, Set<String> allowed) throws JSONException {
        if (context == null) return;
        Object raw = context.opt(field);
        if (raw == null || raw == JSONObject.NULL) return;
        String value = upper(raw);
        if (allowed.contains(value)) out.put(field, value); else warnings.put(field + "_invalid_upstream_metadata");
    }

    private static double number(Object value, double fallback) {
        if (value instanceof Number) return ((Number) value).doubleValue();
        if (value == null || value == JSONObject.NULL || value instanceof Boolean) return fallback;
        try { return Double.parseDouble(String.valueOf(value).trim().replace(',', '.')); }
        catch (Throwable ignored) { return fallback; }
    }

    private static double clamp(double value) { return Math.max(0.0, Math.min(1.0, value)); }
    private static String upper(Object value) { return value == null || value == JSONObject.NULL ? "" : String.valueOf(value).trim().toUpperCase(Locale.ROOT); }
    private static Set<String> set(String... values) { return new HashSet<>(Arrays.asList(values)); }

    public static final class Result {
        public final JSONObject command;
        public final JSONArray errors;
        public final JSONArray warnings;
        Result(JSONObject command, JSONArray errors, JSONArray warnings) {
            this.command = command;
            this.errors = errors;
            this.warnings = warnings;
        }
    }
}
