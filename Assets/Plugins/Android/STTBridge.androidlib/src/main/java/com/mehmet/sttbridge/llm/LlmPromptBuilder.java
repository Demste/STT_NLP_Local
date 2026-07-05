package com.mehmet.sttbridge.llm;

import org.json.JSONArray;
import org.json.JSONObject;

public final class LlmPromptBuilder {
    private LlmPromptBuilder() {}

    public static String build(String rawText, String spacyAnalysisJson, JSONObject config) {
        JSONObject hints = compactHints(spacyAnalysisJson);
        String allowedActions = allowedActions(config);

        String system =
                "You are an offline Turkish game command parser. "
                        + "Return exactly one valid compact JSON object. No markdown. "
                        + "Map Turkish commands to allowed actions. "
                        + "Keep target open vocabulary. Do not reject objects because they are not in the scene. "
                        + "Allowed actions: " + allowedActions + ". "
                        + "Use unknown only when no allowed action fits. "
                        + "Use these exact keys only: action, target, confidence, reason. "
                        + "No nested objects. No arrays. No parentheses. Keep reason short. "
                        + "action must be one of the allowed actions. "
                        + "target is open vocabulary and must preserve the color plus object phrase. "
                        + "For Turkish phrases like üzerine çık or üstüne çık, action must be move_above. "
                        + "Schema: {\"action\":\"move_above\",\"target\":\"mavi araba\",\"confidence\":0.9,\"reason\":\"...\"}";

        String user =
                "Text: " + safe(rawText) + "\n"
                        + "Compact NLP hints:\n"
                        + "root: " + hints.optString("root", "") + "\n"
                        + "candidate_action: " + hints.optString("candidate_action", "") + "\n"
                        + "candidate_target: " + hints.optString("candidate_target", "") + "\n"
                        + "spatial_relation: " + hints.optString("spatial_relation", "") + "\n"
                        + "lemmas: " + hints.optString("lemmas", "");

        return "<|im_start|>system\n"
                + system
                + "\n<|im_end|>\n"
                + "<|im_start|>user\n"
                + user
                + "\n<|im_end|>\n"
                + "<|im_start|>assistant\n";
    }

    public static String buildSmokePrompt() {
        return "<|im_start|>system\n"
                + "Return only JSON.\n"
                + "<|im_end|>\n"
                + "<|im_start|>user\n"
                + "Return {\"ok\":true}\n"
                + "<|im_end|>\n"
                + "<|im_start|>assistant\n";
    }

    private static JSONObject compactHints(String spacyAnalysisJson) {
        JSONObject hints = new JSONObject();

        try {
            JSONObject payload = new JSONObject(safe(spacyAnalysisJson));
            JSONObject analysis = payload.optJSONObject("analysis");
            JSONObject root = analysis != null ? analysis.optJSONObject("root") : payload.optJSONObject("root");
            if (root != null) {
                hints.put("root", firstNonEmpty(root.optString("lemma", ""), root.optString("text", "")));
            }

            JSONObject baseline = payload.optJSONObject("baseline_hints");
            if (baseline != null) {
                hints.put("candidate_action", baseline.optString("action_hint", ""));
                hints.put("candidate_target", baseline.optString("target_hint", ""));
                hints.put("spatial_relation", baseline.optString("spatial_relation_hint", ""));
            }

            if (!hints.has("candidate_target") || hints.optString("candidate_target", "").isEmpty()) {
                JSONArray targets = analysis != null ? analysis.optJSONArray("target_candidates") : null;
                JSONObject target = firstObject(targets);
                if (target != null) {
                    hints.put("candidate_target", firstNonEmpty(target.optString("lemma_text", ""), target.optString("text", "")));
                }
            }

            if (!hints.has("candidate_action") || hints.optString("candidate_action", "").isEmpty()) {
                JSONArray actions = analysis != null ? analysis.optJSONArray("action_candidates") : null;
                JSONObject action = firstObject(actions);
                if (action != null) {
                    hints.put("candidate_action", firstNonEmpty(action.optString("lemma", ""), action.optString("text", "")));
                }
            }

            hints.put("lemmas", compactLemmas(analysis != null ? analysis.optJSONArray("tokens") : payload.optJSONArray("tokens")));
        } catch (Throwable ignored) {
            // Prompt construction must not fail just because debug NLP JSON had
            // an unexpected shape. The raw text remains the source of truth.
        }

        return hints;
    }

    private static String compactLemmas(JSONArray tokens) {
        if (tokens == null) {
            return "";
        }

        StringBuilder builder = new StringBuilder();
        int count = Math.min(tokens.length(), 16);
        for (int i = 0; i < count; i++) {
            JSONObject token = tokens.optJSONObject(i);
            if (token == null) {
                continue;
            }

            String lemma = firstNonEmpty(token.optString("lemma", ""), token.optString("text", ""));
            if (lemma.isEmpty()) {
                continue;
            }

            if (builder.length() > 0) {
                builder.append(' ');
            }
            builder.append(lemma);
        }

        return builder.toString();
    }

    private static String allowedActions(JSONObject config) {
        JSONArray actions = config != null ? config.optJSONArray("allowed_actions") : null;
        if (actions == null || actions.length() == 0) {
            return "move_to, move_above, move_below, follow, stop, pick_up, drop, look_at, rotate_to, unknown";
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < actions.length(); i++) {
            Object action = actions.opt(i);
            String id = "";
            if (action instanceof JSONObject) {
                id = ((JSONObject) action).optString("id", "");
            } else if (action != null) {
                id = String.valueOf(action);
            }

            if (id.isEmpty()) {
                continue;
            }

            if (builder.length() > 0) {
                builder.append(", ");
            }
            builder.append(id);
        }

        return builder.length() > 0 ? builder.toString() : "unknown";
    }

    private static JSONObject firstObject(JSONArray array) {
        if (array == null || array.length() == 0) {
            return null;
        }
        return array.optJSONObject(0);
    }

    private static String firstNonEmpty(String first, String second) {
        return !safe(first).isEmpty() ? safe(first) : safe(second);
    }

    private static String safe(String value) {
        return value != null ? value : "";
    }
}
