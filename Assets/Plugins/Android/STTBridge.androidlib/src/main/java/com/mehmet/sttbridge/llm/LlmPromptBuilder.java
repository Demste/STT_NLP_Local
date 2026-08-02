package com.mehmet.sttbridge.llm;

import org.json.JSONObject;
import org.json.JSONArray;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;

/** Builds bounded semantic and object-slot prompts. */
public final class LlmPromptBuilder {
    private static final String OBJECT_SYSTEM =
            "Tek görev: Türkçe RAW nesnesini sınıflandır. Ağır/yük taşıtı=TRUCK; kazı aracı=EXCAVATOR. "
            + "CANONICAL_HINT varsa onu kullan. ALLOWED listesinden seç ve RETURN şemasını doldur. Yalnız minified JSON yaz. "
            + "Açıklama, markdown ve ek alan yasak; evidence değerini değiştirme.";

    private static final String SEMANTIC_SYSTEM =
            "Türkçe drone komutunu yalnız verilen kapalı şema içinde çöz. Yeni action, slot veya değer üretme. "
            + "Action ve her slot için kullanıcı komutunda aynen bulunan kısa evidence zorunludur. "
            + "Söylenmeyen required slotu uydurma; slots içinde hiç verme. Birden fazla eylem, olumsuz emir veya bağlamsız zamirde UNKNOWN seç. "
            + "slots içindeki her değer tam olarak {\"value\":...,\"evidence\":\"...\"} nesnesi olmalı; string olamaz. "
            + "required,type veya şemada olmayan başka key yazma. Enum ve sayıları yalnız TRUSTED_SEMANTIC_FRAME içinden kopyala. "
            + "Yalnız şu biçimde tek JSON nesnesi yaz: "
            + "{\"action\":\"ACTION\",\"action_evidence\":\"metin\",\"slots\":{\"slot\":{\"value\":\"VALUE\",\"evidence\":\"metin\"}}}.";

    private LlmPromptBuilder() {}

    public static String buildObjectClassificationPrompt(String analysisInput, String fixedAction,
                                                         String detectedPhrase, String spacyAnalysisJson,
                                                         JSONObject config) {
        String user = "RAW=" + safe(detectedPhrase) + "\n"
                + "CANONICAL_HINT=" + objectHint(detectedPhrase) + "\n"
                + "ALLOWED=" + LlmCommandSchema.objectCsv() + "\n"
                + "RETURN={\"object\":\"ALLOWED_VALUE\",\"evidence\":"
                + JSONObject.quote(safe(detectedPhrase)) + "}";
        return slotChat(OBJECT_SYSTEM, user);
    }

    public static String buildSemanticCommandPrompt(String analysisInput, String spacyAnalysisJson, JSONObject config) {
        List<String> candidates = semanticCandidates(spacyAnalysisJson);
        if (candidates.isEmpty()) {
            for (String action : LlmCommandSchema.ACTIONS) {
                if (LlmCommandSchema.isClassifiableAction(action) && !"UNKNOWN".equals(action)) {
                    candidates.add(action);
                }
            }
            candidates.add("UNKNOWN");
        }
        StringBuilder schema = new StringBuilder();
        schema.append("CANDIDATE_ACTIONS=").append(String.join(",", candidates)).append('\n');
        schema.append("ACTION_SCHEMA=");
        for (String action : candidates) {
            if (!LlmCommandSchema.isClassifiableAction(action) || "UNKNOWN".equals(action)) continue;
            if (schema.charAt(schema.length() - 1) != '=') schema.append(';');
            Set<String> fields = LlmCommandSchema.fieldsFor(action);
            schema.append(action).append('(').append(String.join(",", fields)).append("|required=")
                    .append(String.join(",", LlmCommandSchema.requiredFieldsFor(action))).append(')');
        }
        schema.append("\nENUMS=direction:").append(String.join(",", LlmCommandSchema.DIRECTIONS))
                .append(";turn_direction:").append(String.join(",", LlmCommandSchema.TURN_DIRECTIONS))
                .append(";object:").append(String.join(",", LlmCommandSchema.OBJECTS))
                .append(";color:").append(String.join(",", LlmCommandSchema.COLORS))
                .append(";shape:").append(String.join(",", LlmCommandSchema.SHAPES))
                .append(";observe_mode:").append(String.join(",", LlmCommandSchema.OBSERVE_MODES))
                .append('\n').append(outputShapes(candidates, spacyAnalysisJson))
                .append('\n').append(semanticFrameEvidence(spacyAnalysisJson))
                .append('\n').append(compactEvidence(spacyAnalysisJson));
        return chat(SEMANTIC_SYSTEM, analysisInput, schema.toString());
    }

    public static String buildObjectGrammar(String rawEvidence) {
        StringBuilder grammar = new StringBuilder();
        grammar.append("root ::= ").append(gbnfLiteral("{\"object\":\""))
                .append(" object ")
                .append(gbnfLiteral("\",\"evidence\":\"" + safe(rawEvidence) + "\"}"))
                .append('\n').append("object ::= ");
        List<String> orderedObjects = new ArrayList<>();
        String hint = objectHint(rawEvidence);
        if (!"NONE".equals(hint)) {
            orderedObjects.add(hint);
        } else {
            for (String value : LlmCommandSchema.OBJECTS) orderedObjects.add(value);
        }
        for (int i = 0; i < orderedObjects.size(); i++) {
            if (i > 0) grammar.append(" | ");
            grammar.append(gbnfLiteral(orderedObjects.get(i)));
        }
        return grammar.toString();
    }

    private static String objectHint(String rawEvidence) {
        String normalized = safe(rawEvidence).toLowerCase(java.util.Locale.forLanguageTag("tr-TR"));
        if (normalized.contains("kaz")) return "EXCAVATOR";
        if (normalized.contains("ağır") || normalized.contains("yük")) return "TRUCK";
        return "NONE";
    }

    private static String gbnfLiteral(String value) {
        String escaped = safe(value).replace("\\", "\\\\").replace("\"", "\\\"")
                .replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t");
        return "\"" + escaped + "\"";
    }

    /** Native JNI currently exposes no tokenizer count; this is marked as an estimate in diagnostics. */
    public static int estimatePromptTokens(String prompt) {
        int bytes = safe(prompt).getBytes(StandardCharsets.UTF_8).length;
        return Math.max(1, (bytes + 3) / 4);
    }

    public static int estimateOutputTokens(String output) {
        int bytes = safe(output).getBytes(StandardCharsets.UTF_8).length;
        return Math.max(0, (bytes + 3) / 4);
    }

    private static List<String> semanticCandidates(String spacyAnalysisJson) {
        List<String> result = new ArrayList<>();
        try {
            JSONObject root = new JSONObject(safe(spacyAnalysisJson).trim().isEmpty() ? "{}" : spacyAnalysisJson);
            JSONObject frame = root.optJSONObject("semantic_frame");
            JSONArray candidates = frame != null ? frame.optJSONArray("candidate_actions") : null;
            if (candidates == null || candidates.length() == 0) {
                JSONObject hints = root.optJSONObject("baseline_hints");
                JSONObject evidence = hints != null ? hints.optJSONObject("action_evidence") : null;
                candidates = evidence != null ? evidence.optJSONArray("supported_actions") : null;
            }
            if (candidates != null) {
                for (int i = 0; i < candidates.length(); i++) {
                    String action = candidates.optString(i, "").trim().toUpperCase(java.util.Locale.ROOT);
                    if (LlmCommandSchema.isClassifiableAction(action) && !result.contains(action)) result.add(action);
                }
            }
        } catch (Throwable ignored) {
        }
        if (!result.contains("UNKNOWN")) result.add("UNKNOWN");
        return result;
    }

    private static String outputShapes(List<String> candidates, String spacyAnalysisJson) {
        StringBuilder result = new StringBuilder("ALLOWED_OUTPUT_SHAPES=");
        boolean first = true;
        JSONObject frame = semanticFrame(spacyAnalysisJson);
        for (String action : candidates) {
            if (!LlmCommandSchema.isClassifiableAction(action) || "UNKNOWN".equals(action)) continue;
            if (!first) result.append(" OR ");
            first = false;
            try {
                JSONObject candidate = new JSONObject();
                candidate.put("action", action);
                candidate.put("action_evidence", actionEvidence(frame, action));
                JSONObject slots = new JSONObject();
                for (String field : LlmCommandSchema.fieldsFor(action)) {
                    JSONObject slot = trustedSlot(frame, field, action);
                    if (slot != null) slots.put(field, slot);
                }
                candidate.put("slots", slots);
                result.append(candidate.toString());
            } catch (Throwable ignored) {
                result.append("{\"action\":\"").append(action)
                        .append("\",\"action_evidence\":\"\",\"slots\":{}}");
            }
        }
        if (!first) result.append(" OR ");
        result.append("{\"action\":\"UNKNOWN\",\"action_evidence\":\"\",\"slots\":{}}");
        return result.toString();
    }

    private static JSONObject semanticFrame(String spacyAnalysisJson) {
        try {
            JSONObject root = new JSONObject(safe(spacyAnalysisJson).trim().isEmpty() ? "{}" : spacyAnalysisJson);
            JSONObject frame = root.optJSONObject("semantic_frame");
            return frame != null ? frame : new JSONObject();
        } catch (Throwable ignored) {
            return new JSONObject();
        }
    }

    private static String actionEvidence(JSONObject frame, String action) {
        JSONObject values = frame.optJSONObject("predicate_evidence");
        String evidence = values != null ? values.optString(action, "") : "";
        if (!evidence.isEmpty()) return evidence;
        String raw = frame.optString("raw_text", "").trim();
        if (raw.isEmpty()) return "";
        String[] words = raw.split("\\s+");
        return words.length > 0 ? words[words.length - 1] : "";
    }

    private static JSONObject trustedSlot(JSONObject frame, String field, String action) {
        if ("observe_mode".equals(field) && "OBSERVE_CURRENT".equals(action)) {
            String evidence = actionEvidence(frame, action);
            return evidence.isEmpty() ? null : slot("WATCH", evidence);
        }
        String type = "";
        String unit = "";
        if ("direction".equals(field) || "turn_direction".equals(field)) type = "DIRECTION";
        else if ("distance_m".equals(field)) { type = "QUANTITY"; unit = "METER"; }
        else if ("duration_s".equals(field)) { type = "QUANTITY"; unit = "SECOND"; }
        else if ("duration_min".equals(field)) { type = "QUANTITY"; unit = "MINUTE"; }
        else if ("angle_deg".equals(field)) { type = "QUANTITY"; unit = "DEGREE"; }
        else if ("speed_kmh".equals(field)) { type = "QUANTITY"; unit = "KMH"; }
        else if ("object".equals(field)) type = "OBJECT";
        else if ("color".equals(field)) type = "COLOR";
        else if ("shape".equals(field)) type = "SHAPE";
        else if ("marker_index".equals(field)) type = "MARKER_INDEX";
        else if ("location_keyword".equals(field)) type = "LOCATION";
        else if ("coordinate".equals(field)) type = "COORDINATE";
        if (type.isEmpty()) return null;

        JSONObject span = firstSpan(frame, type, unit);
        if (span == null || !span.has("value") || span.optString("text", "").isEmpty()) return null;
        return slot(span.opt("value"), span.optString("text", ""));
    }

    private static JSONObject firstSpan(JSONObject frame, String type, String unit) {
        for (String key : new String[] {
                "protected_spans", "direction_spans", "object_spans", "quantity_spans", "location_spans"
        }) {
            JSONArray spans = frame.optJSONArray(key);
            if (spans == null) continue;
            for (int i = 0; i < spans.length(); i++) {
                JSONObject span = spans.optJSONObject(i);
                if (span == null || !type.equals(span.optString("type", ""))) continue;
                if (!unit.isEmpty() && !unit.equals(span.optString("unit", ""))) continue;
                return span;
            }
        }
        return null;
    }

    private static JSONObject slot(Object value, String evidence) {
        try {
            return new JSONObject().put("value", value).put("evidence", evidence);
        } catch (Throwable ignored) {
            return null;
        }
    }

    private static String semanticFrameEvidence(String spacyAnalysisJson) {
        try {
            JSONObject root = new JSONObject(safe(spacyAnalysisJson).trim().isEmpty() ? "{}" : spacyAnalysisJson);
            JSONObject frame = root.optJSONObject("semantic_frame");
            if (frame == null) return "TRUSTED_SEMANTIC_FRAME={}";
            JSONObject bounded = new JSONObject();
            copy(frame, bounded, "raw_text");
            copy(frame, bounded, "normalized_text");
            copy(frame, bounded, "predicate_lemmas");
            copy(frame, bounded, "predicate_evidence");
            copy(frame, bounded, "candidate_actions");
            copy(frame, bounded, "direction_spans");
            copy(frame, bounded, "object_spans");
            copy(frame, bounded, "quantity_spans");
            copy(frame, bounded, "location_spans");
            copy(frame, bounded, "protected_spans");
            return "TRUSTED_SEMANTIC_FRAME=" + bounded.toString();
        } catch (Throwable ignored) {
            return "TRUSTED_SEMANTIC_FRAME={}";
        }
    }

    private static void copy(JSONObject source, JSONObject target, String key) throws Exception {
        if (source.has(key)) target.put(key, source.get(key));
    }

    private static String chat(String system, String command, String evidence) {
        return "<|im_start|>system\n" + system + "\n<|im_end|>\n"
                + "<|im_start|>user\nUSER_COMMAND_BEGIN\n" + safe(command)
                + "\nUSER_COMMAND_END\n" + safe(evidence)
                + "\n<|im_end|>\n<|im_start|>assistant\n";
    }

    private static String slotChat(String system, String user) {
        return "<|im_start|>system\n" + system + "\n<|im_end|>\n"
                + "<|im_start|>user\n" + safe(user)
                + "\n<|im_end|>\n<|im_start|>assistant\n";
    }

    private static String compactEvidence(String spacyAnalysisJson) {
        try {
            JSONObject root = new JSONObject(safe(spacyAnalysisJson).trim().isEmpty() ? "{}" : spacyAnalysisJson);
            JSONObject compact = root.optJSONObject("compact_evidence");
            return compact != null ? "SPACY_COMPACT=" + compact.toString() : "SPACY_COMPACT={}";
        } catch (Throwable ignored) {
            return "SPACY_COMPACT={}";
        }
    }
    private static String safe(String value) { return value != null ? value : ""; }
}
