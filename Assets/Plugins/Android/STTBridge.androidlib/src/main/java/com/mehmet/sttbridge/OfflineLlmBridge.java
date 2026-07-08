package com.mehmet.sttbridge;

import android.content.Context;
import android.util.Log;

import com.mehmet.sttbridge.llm.LlamaCppEngine;
import com.mehmet.sttbridge.llm.LlmBackendStatus;
import com.mehmet.sttbridge.llm.LlmCommandSchema;
import com.mehmet.sttbridge.llm.LlmGenerationOptions;
import com.mehmet.sttbridge.llm.LlmJsonUtils;
import com.mehmet.sttbridge.llm.LlmPromptBuilder;
import com.mehmet.sttbridge.llm.OfflineLlmEngine;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.PrintWriter;
import java.io.StringWriter;

public final class OfflineLlmBridge {
    private static final String TAG = "OfflineLlmBridge";
    private static final String ENGINE_NAME = "offline_llm";
    private static final Object ENGINE_LOCK = new Object();
    private static OfflineLlmEngine cachedEngine;
    private static String cachedEngineConfigKey = "";

    private OfflineLlmBridge() {}

    public static String runSmokePrompt(Context context, String configJson) {
        long started = System.currentTimeMillis();
        try {
            if (context == null) {
                throw new IllegalArgumentException("Context is null.");
            }

            JSONObject config = LlmCommandSchema.mergeWithDefault(configJson);
            OfflineLlmEngine engine = acquireEngine(context.getApplicationContext(), config);
            LlmBackendStatus backendStatus = engine.getStatus();
            if (!backendStatus.initialized) {
                return buildBackendFailureJson("", backendStatus, System.currentTimeMillis() - started);
            }

            LlmGenerationOptions options = LlmGenerationOptions.fromConfig(config);
            String prompt = LlmPromptBuilder.buildSmokePrompt();
            String llmRaw = engine.generate(prompt, options);

            JSONObject payload = new JSONObject();
            payload.put("ok", true);
            payload.put("engine", ENGINE_NAME);
            payload.put("backend", engine.getEngineName());
            payload.put("stage", "smoke_generated");
            payload.put("initialized", true);
            payload.put("inference_ran", true);
            payload.put("backend_status", engine.getStatus().toJson());
            payload.put("llm_raw", llmRaw);
            payload.put("durationMs", System.currentTimeMillis() - started);
            return payload.toString();
        } catch (Throwable t) {
            Log.e(TAG, "Offline LLM smoke prompt failed.", t);
            return buildFailureJson(
                    "",
                    ENGINE_NAME,
                    "smoke_generation_failed",
                    true,
                    null,
                    "",
                    null,
                    "Smoke generation failed: " + throwableMessage(t),
                    t,
                    System.currentTimeMillis() - started);
        }
    }

    public static String parseCommand(Context context, String rawText, String spacyAnalysisJson, String configJson) {
        long started = System.currentTimeMillis();
        try {
            if (context == null) {
                throw new IllegalArgumentException("Context is null.");
            }

            JSONObject config = LlmCommandSchema.mergeWithDefault(configJson);
            String safeRawText = rawText != null ? rawText.trim() : "";
            if (safeRawText.isEmpty()) {
                return buildUnknownCommandJson(
                        rawText,
                        config,
                        "empty_or_unclear_command",
                        System.currentTimeMillis() - started);
            }

            OfflineLlmEngine engine = acquireEngine(context.getApplicationContext(), config);
            LlmBackendStatus backendStatus = engine.getStatus();

            if (!backendStatus.initialized) {
                return buildBackendFailureJson(
                        rawText,
                        backendStatus,
                        System.currentTimeMillis() - started);
            }

            JSONObject llmCommand;
            String prompt = LlmPromptBuilder.build(safeRawText, spacyAnalysisJson, config);
            String llmRaw = "";
            LlmGenerationOptions options = LlmGenerationOptions.fromConfig(config);

            try {
                llmRaw = engine.generate(prompt, options);
                logLong("raw LLM output: ", llmRaw);
            } catch (Throwable t) {
                Log.e(TAG, "Offline LLM generation failed.", t);
                return buildFailureJson(
                        rawText,
                        ENGINE_NAME,
                        "generation_failed",
                        true,
                        engine.getStatus(),
                        llmRaw,
                        null,
                        "LLM generation failed: " + throwableMessage(t),
                        t,
                        System.currentTimeMillis() - started);
            }

            JsonRepairResult repairResult = JsonRepairResult.none(llmRaw);
            try {
                llmCommand = LlmJsonUtils.extractObject(llmRaw);
                llmCommand.put("source", engine.getEngineName());
            } catch (Throwable t) {
                repairResult = repairJson(llmRaw);
                if (!repairResult.applied) {
                    Log.e(TAG, "Offline LLM JSON parse failed.", t);
                    logLong("raw LLM output before parse_error: ", llmRaw);
                    return buildFailureJson(
                            rawText,
                            ENGINE_NAME,
                            "parse_error",
                            true,
                            engine.getStatus(),
                            llmRaw,
                            null,
                            "LLM inference completed but did not return a valid JSON object: " + throwableMessage(t),
                            t,
                            System.currentTimeMillis() - started,
                            false,
                            repairResult.notes);
                }

                try {
                    llmCommand = LlmJsonUtils.extractObject(repairResult.json);
                    llmCommand.put("source", engine.getEngineName());
                    logLong("json repair applied: ", repairResult.notes.toString());
                    logLong("repaired LLM JSON: ", repairResult.json);
                } catch (Throwable repairedError) {
                    Log.e(TAG, "Offline LLM JSON parse failed after repair.", repairedError);
                    logLong("raw LLM output before parse_error: ", llmRaw);
                    logLong("repaired LLM JSON before parse_error: ", repairResult.json);
                    return buildFailureJson(
                            rawText,
                            ENGINE_NAME,
                            "parse_error",
                            true,
                            engine.getStatus(),
                            llmRaw,
                            null,
                            "LLM inference completed but repair still did not return a valid JSON object: " + throwableMessage(repairedError),
                            repairedError,
                            System.currentTimeMillis() - started,
                            true,
                            repairResult.notes);
                }
            }

            JSONObject normalizedCommand = normalizeCommandForValidator(llmCommand);
            JSONObject validated = validate(normalizedCommand, config);
            validated.put("stage", "llm_validated");
            validated.put("inference_ran", true);
            logLong("normalized_command: ", normalizedCommand.toString());
            logLong("validated_command: ", validated.toString());

            JSONObject payload = new JSONObject();
            payload.put("ok", validated.optBoolean("ok", false));
            payload.put("engine", ENGINE_NAME);
            payload.put("backend", engine.getEngineName());
            payload.put("stage", "llm_validated");
            payload.put("initialized", true);
            payload.put("inference_ran", true);
            payload.put("backend_status", engine.getStatus().toJson());
            payload.put("input", safeRawText);
            payload.put("llm_raw", llmRaw);
            payload.put("llm_command", llmCommand);
            payload.put("json_repair_applied", repairResult.applied);
            payload.put("json_repair_notes", repairResult.notes);
            payload.put("normalized_command", normalizedCommand);
            payload.put("validated_command", validated);
            payload.put("durationMs", System.currentTimeMillis() - started);
            return payload.toString();
        } catch (Throwable t) {
            Log.e(TAG, "Offline LLM bridge failed.", t);
            return buildErrorJson(rawText, "offline_llm_bridge_failed", t, System.currentTimeMillis() - started);
        }
    }

    private static String buildUnknownCommandJson(String rawText, JSONObject config, String reason, long durationMs) {
        try {
            String unknownAction = config != null ? config.optString("unknown_action", "unknown") : "unknown";
            JSONObject command = new JSONObject();
            command.put("action", unknownAction);
            command.put("target", "");
            command.put("confidence", 0.0);
            command.put("requires_clarification", true);
            command.put("reason", reason != null ? reason : "command is empty or unclear");
            command.put("source", ENGINE_NAME);

            JSONObject validated = validate(command, config != null ? config : LlmCommandSchema.defaultConfig());
            validated.put("stage", "input_rejected");
            validated.put("inference_ran", false);

            JSONObject payload = new JSONObject();
            payload.put("ok", false);
            payload.put("engine", ENGINE_NAME);
            payload.put("backend", JSONObject.NULL);
            payload.put("stage", "input_rejected");
            payload.put("initialized", false);
            payload.put("inference_ran", false);
            payload.put("backend_status", JSONObject.NULL);
            payload.put("input", rawText != null ? rawText : "");
            payload.put("llm_raw", "");
            payload.put("llm_command", command);
            payload.put("json_repair_applied", false);
            payload.put("json_repair_notes", new JSONArray());
            payload.put("normalized_command", command);
            payload.put("validated_command", validated);
            payload.put("durationMs", durationMs);
            return payload.toString();
        } catch (Throwable t) {
            return buildErrorJson(rawText, "input_rejected", t, durationMs);
        }
    }

    private static OfflineLlmEngine acquireEngine(Context appContext, JSONObject config) {
        String configKey = llmConfigKey(config);

        synchronized (ENGINE_LOCK) {
            if (cachedEngine != null &&
                    cachedEngine.getStatus().initialized &&
                    configKey.equals(cachedEngineConfigKey)) {
                return cachedEngine;
            }

            OfflineLlmEngine engine = new LlamaCppEngine(appContext);
            LlmBackendStatus status = engine.initialize(config);
            if (status.initialized) {
                closeCachedEngine();
                cachedEngine = engine;
                cachedEngineConfigKey = configKey;
            }
            return engine;
        }
    }

    private static String llmConfigKey(JSONObject config) {
        JSONObject llm = config != null ? config.optJSONObject("llm") : null;
        return llm != null ? llm.toString() : "{}";
    }

    private static void closeCachedEngine() {
        if (cachedEngine instanceof LlamaCppEngine) {
            ((LlamaCppEngine) cachedEngine).close();
        }
        cachedEngine = null;
        cachedEngineConfigKey = "";
    }

    private static JSONObject normalizeCommandForValidator(JSONObject command) throws JSONException {
        JSONObject normalized = new JSONObject();

        Object actionValue = command.opt("action");
        JSONObject actionObject = actionValue instanceof JSONObject ? (JSONObject) actionValue : null;
        String action = "";
        if (actionObject != null) {
            action = firstNonEmpty(
                    stringField(actionObject, "mapped"),
                    stringField(actionObject, "id"),
                    stringField(actionObject, "raw"));
        } else if (actionValue != null && actionValue != JSONObject.NULL) {
            action = String.valueOf(actionValue);
        }

        Object targetValue = command.opt("target");
        if ((targetValue == null || targetValue == JSONObject.NULL) && actionObject != null) {
            targetValue = actionObject.opt("target");
        }
        String target = null;
        JSONObject parameters = command.optJSONObject("parameters");
        if (parameters == null) {
            parameters = new JSONObject();
        }

        if (targetValue instanceof JSONObject) {
            JSONObject targetObject = (JSONObject) targetValue;
            target = firstNonEmpty(
                    stringField(targetObject, "raw"),
                    stringField(targetObject, "name"),
                    stringField(targetObject, "text"),
                    stringField(targetObject, "label"));

            String color = stringField(targetObject, "color");
            String name = stringField(targetObject, "name");
            if ((target == null || target.isEmpty()) && !color.isEmpty() && !name.isEmpty()) {
                target = color + " " + name;
            }

            if (!color.isEmpty()) {
                parameters.put("color", color);
            }
        } else if (targetValue != null && targetValue != JSONObject.NULL) {
            target = String.valueOf(targetValue);
        }

        normalized.put("action", action);
        normalized.put("target", target != null && !target.isEmpty() ? target : JSONObject.NULL);
        normalized.put("confidence", confidenceValue(command, actionObject));
        normalized.put("parameters", parameters);
        normalized.put("requires_clarification", command.optBoolean("requires_clarification", false));
        normalized.put("reason", firstNonEmpty(command.optString("reason", ""), actionObject != null ? actionObject.optString("reason", "") : ""));
        normalized.put("source", command.optString("source", "offline_llm"));
        return normalized;
    }

    private static JsonRepairResult repairJson(String raw) {
        JSONArray notes = new JSONArray();
        if (raw == null) {
            return new JsonRepairResult("", false, notes);
        }

        String repaired = raw.trim();
        boolean applied = false;

        int start = repaired.indexOf('{');
        if (start > 0) {
            repaired = repaired.substring(start);
            notes.put("trimmed_text_before_first_object");
            applied = true;
        }

        int lastBrace = repaired.lastIndexOf('}');
        int lastParen = repaired.lastIndexOf(')');
        int likelyEnd = Math.max(lastBrace, lastParen);
        if (likelyEnd >= 0 && likelyEnd < repaired.length() - 1) {
            repaired = repaired.substring(0, likelyEnd + 1).trim();
            notes.put("trimmed_text_after_likely_json_end");
            applied = true;
        }

        String trailingParenFixed = replaceTrailingParenWithBrace(repaired);
        if (!trailingParenFixed.equals(repaired)) {
            repaired = trailingParenFixed;
            notes.put("replaced_trailing_parenthesis_with_brace");
            applied = true;
        }

        String withoutTrailingCommas = repaired.replaceAll(",\\s*}", "}");
        if (!withoutTrailingCommas.equals(repaired)) {
            repaired = withoutTrailingCommas;
            notes.put("removed_trailing_comma_before_object_close");
            applied = true;
        }

        int missingBraces = openBraceCount(repaired) - closeBraceCount(repaired);
        if (missingBraces > 0 && missingBraces <= 3) {
            for (int i = 0; i < missingBraces; i++) {
                repaired += "}";
            }
            notes.put("appended_missing_closing_braces:" + missingBraces);
            applied = true;
        }

        return new JsonRepairResult(repaired, applied, notes);
    }

    private static String replaceTrailingParenWithBrace(String value) {
        if (value == null || value.isEmpty()) {
            return value != null ? value : "";
        }

        StringBuilder builder = new StringBuilder(value);
        int index = builder.length() - 1;
        while (index >= 0 && Character.isWhitespace(builder.charAt(index))) {
            index--;
        }

        int suffixStart = index;
        while (suffixStart >= 0 && builder.charAt(suffixStart) == '}') {
            suffixStart--;
        }

        if (suffixStart >= 0 && builder.charAt(suffixStart) == ')') {
            builder.setCharAt(suffixStart, '}');
        }

        return builder.toString();
    }

    private static int openBraceCount(String value) {
        return braceCount(value, '{');
    }

    private static int closeBraceCount(String value) {
        return braceCount(value, '}');
    }

    private static int braceCount(String value, char brace) {
        if (value == null) {
            return 0;
        }
        int count = 0;
        boolean inString = false;
        boolean escaped = false;
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (escaped) {
                escaped = false;
                continue;
            }
            if (c == '\\' && inString) {
                escaped = true;
                continue;
            }
            if (c == '"') {
                inString = !inString;
                continue;
            }
            if (!inString && c == brace) {
                count++;
            }
        }
        return count;
    }

    private static String firstNonEmpty(String... values) {
        if (values == null) {
            return "";
        }
        for (String value : values) {
            if (value != null && !value.trim().isEmpty()) {
                return value.trim();
            }
        }
        return "";
    }

    private static String stringField(JSONObject object, String name) {
        if (object == null || name == null) {
            return "";
        }

        Object value = object.opt(name);
        if (value == null || value == JSONObject.NULL) {
            return "";
        }

        if (value instanceof JSONObject) {
            JSONObject nested = (JSONObject) value;
            return firstNonEmpty(
                    stringField(nested, "raw"),
                    stringField(nested, "text"),
                    stringField(nested, "name"),
                    stringField(nested, "value"));
        }

        return String.valueOf(value).trim();
    }

    private static double confidenceValue(JSONObject command, JSONObject actionObject) {
        if (command == null) {
            return 0.0;
        }

        Object value = command.opt("confidence");
        if (value == null || value == JSONObject.NULL) {
            value = command.opt("score");
        }
        if ((value == null || value == JSONObject.NULL) && actionObject != null) {
            value = actionObject.opt("confidence");
        }
        if ((value == null || value == JSONObject.NULL) && actionObject != null) {
            value = actionObject.opt("score");
        }

        if (value instanceof Number) {
            return ((Number) value).doubleValue();
        }

        if (value instanceof JSONObject) {
            JSONObject object = (JSONObject) value;
            return firstDouble(
                    object.opt("value"),
                    object.opt("score"),
                    object.opt("confidence"));
        }

        return doubleValue(value, 0.0);
    }

    private static double firstDouble(Object first, Object second, Object third) {
        double parsed = doubleValue(first, Double.NaN);
        if (!Double.isNaN(parsed)) return parsed;

        parsed = doubleValue(second, Double.NaN);
        if (!Double.isNaN(parsed)) return parsed;

        return doubleValue(third, 0.0);
    }

    private static double doubleValue(Object value, double fallback) {
        if (value instanceof Number) {
            return ((Number) value).doubleValue();
        }
        if (value == null || value == JSONObject.NULL) {
            return fallback;
        }
        try {
            return Double.parseDouble(String.valueOf(value).trim());
        } catch (Throwable ignored) {
            return fallback;
        }
    }

    private static JSONObject validate(JSONObject command, JSONObject config) throws JSONException {
        JSONArray allowed = LlmJsonUtils.allowedActionIds(config);
        String unknownAction = config.optString("unknown_action", "unknown");
        String action = command.optString("action", unknownAction);
        JSONArray errors = new JSONArray();
        JSONArray warnings = new JSONArray();

        if (!LlmJsonUtils.arrayContains(allowed, action)) {
            errors.put("action_not_allowed");
            action = LlmJsonUtils.arrayContains(allowed, unknownAction) ? unknownAction : "unknown";
        }

        Object target = command.opt("target");
        if (target == JSONObject.NULL) {
            target = JSONObject.NULL;
        } else if (target != null && !(target instanceof String)) {
            warnings.put("target_coerced_to_string");
            target = String.valueOf(target);
        }

        double confidence = command.optDouble("confidence", 0.0);
        confidence = Math.max(0.0, Math.min(1.0, confidence));
        if (unknownAction.equals(action)) {
            confidence = Math.min(confidence, 0.35);
        }

        JSONObject validated = new JSONObject();
        boolean requiresClarification = command.optBoolean("requires_clarification", false) || unknownAction.equals(action);

        validated.put("ok", errors.length() == 0 && !requiresClarification);
        validated.put("action", action);
        validated.put("target", target != null ? target : JSONObject.NULL);
        validated.put("target_policy", "open_vocabulary");
        validated.put("confidence", confidence);
        validated.put("parameters", command.optJSONObject("parameters") != null ? command.optJSONObject("parameters") : new JSONObject());
        validated.put("requires_clarification", requiresClarification);
        validated.put("reason", command.optString("reason", unknownAction.equals(action) ? "unknown_action" : "validated"));
        validated.put("errors", errors);
        validated.put("warnings", warnings);
        validated.put("source", command.optString("source", "offline_llm"));
        return validated;
    }

    private static String buildBackendFailureJson(String rawText, LlmBackendStatus backendStatus, long durationMs) {
        return buildFailureJson(
                rawText,
                ENGINE_NAME,
                backendStatus != null ? backendStatus.stage : "backend_not_initialized",
                false,
                backendStatus,
                "",
                null,
                backendStatus != null ? backendStatus.error : "Offline LLM backend did not initialize.",
                null,
                durationMs);
    }

    private static String buildFailureJson(
            String rawText,
            String engine,
            String stage,
            boolean inferenceRan,
            LlmBackendStatus backendStatus,
            String llmRaw,
            JSONObject llmCommand,
            String error,
            Throwable throwable,
            long durationMs) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("ok", false);
            payload.put("engine", engine != null ? engine : ENGINE_NAME);
            payload.put("backend", backendStatus != null ? backendStatus.engine : JSONObject.NULL);
            payload.put("stage", stage != null ? stage : "offline_llm_failed");
            payload.put("initialized", backendStatus != null && backendStatus.initialized);
            payload.put("inference_ran", inferenceRan);
            payload.put("backend_status", backendStatus != null ? backendStatus.toJson() : JSONObject.NULL);
            payload.put("input", rawText != null ? rawText : "");
            payload.put("llm_raw", llmRaw != null ? llmRaw : "");
            payload.put("llm_command", llmCommand != null ? llmCommand : JSONObject.NULL);
            payload.put("json_repair_applied", false);
            payload.put("json_repair_notes", new JSONArray());
            payload.put("validated_command", JSONObject.NULL);
            payload.put("durationMs", durationMs);
            payload.put("error", error != null ? error : "");

            if (throwable != null) {
                payload.put("exception_type", throwable.getClass().getName());
                payload.put("traceback", stackTraceToString(throwable));
            }

            return payload.toString();
        } catch (Throwable jsonError) {
            return "{\"ok\":false,\"engine\":\"offline_llm\",\"stage\":"
                    + JSONObject.quote(stage != null ? stage : "offline_llm_failed")
                    + ",\"initialized\":false,\"error\":"
                    + JSONObject.quote(jsonError.getMessage() != null ? jsonError.getMessage() : jsonError.toString())
                    + "}";
        }
    }

    private static String buildErrorJson(String rawText, String stage, Throwable throwable, long durationMs) {
        return buildFailureJson(
                rawText,
                ENGINE_NAME,
                stage,
                false,
                null,
                "",
                null,
                throwableMessage(throwable),
                throwable,
                durationMs);
    }

    private static String buildFailureJson(
            String rawText,
            String engine,
            String stage,
            boolean inferenceRan,
            LlmBackendStatus backendStatus,
            String llmRaw,
            JSONObject llmCommand,
            String error,
            Throwable throwable,
            long durationMs,
            boolean jsonRepairApplied,
            JSONArray jsonRepairNotes) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("ok", false);
            payload.put("engine", engine != null ? engine : ENGINE_NAME);
            payload.put("backend", backendStatus != null ? backendStatus.engine : JSONObject.NULL);
            payload.put("stage", stage != null ? stage : "offline_llm_failed");
            payload.put("initialized", backendStatus != null && backendStatus.initialized);
            payload.put("inference_ran", inferenceRan);
            payload.put("backend_status", backendStatus != null ? backendStatus.toJson() : JSONObject.NULL);
            payload.put("input", rawText != null ? rawText : "");
            payload.put("llm_raw", llmRaw != null ? llmRaw : "");
            payload.put("llm_command", llmCommand != null ? llmCommand : JSONObject.NULL);
            payload.put("json_repair_applied", jsonRepairApplied);
            payload.put("json_repair_notes", jsonRepairNotes != null ? jsonRepairNotes : new JSONArray());
            payload.put("validated_command", JSONObject.NULL);
            payload.put("durationMs", durationMs);
            payload.put("error", error != null ? error : "");

            if (throwable != null) {
                payload.put("exception_type", throwable.getClass().getName());
                payload.put("traceback", stackTraceToString(throwable));
            }

            return payload.toString();
        } catch (Throwable jsonError) {
            return "{\"ok\":false,\"engine\":\"offline_llm\",\"stage\":"
                    + JSONObject.quote(stage != null ? stage : "offline_llm_failed")
                    + ",\"initialized\":false,\"error\":"
                    + JSONObject.quote(jsonError.getMessage() != null ? jsonError.getMessage() : jsonError.toString())
                    + "}";
        }
    }

    private static String stackTraceToString(Throwable throwable) {
        StringWriter writer = new StringWriter();
        throwable.printStackTrace(new PrintWriter(writer));
        return writer.toString();
    }

    private static String throwableMessage(Throwable throwable) {
        if (throwable == null) {
            return "";
        }
        String message = throwable.getMessage();
        return message != null ? message : throwable.toString();
    }

    private static void logLong(String prefix, String message) {
        String value = message != null ? message : "";
        if (value.isEmpty()) {
            Log.d(TAG, prefix + "<empty>");
            return;
        }

        final int chunkSize = 3000;
        for (int offset = 0; offset < value.length(); offset += chunkSize) {
            int end = Math.min(value.length(), offset + chunkSize);
            Log.d(TAG, prefix + value.substring(offset, end));
        }
    }

    private static final class JsonRepairResult {
        final String json;
        final boolean applied;
        final JSONArray notes;

        JsonRepairResult(String json, boolean applied, JSONArray notes) {
            this.json = json != null ? json : "";
            this.applied = applied;
            this.notes = notes != null ? notes : new JSONArray();
        }

        static JsonRepairResult none(String json) {
            return new JsonRepairResult(json, false, new JSONArray());
        }
    }
}
