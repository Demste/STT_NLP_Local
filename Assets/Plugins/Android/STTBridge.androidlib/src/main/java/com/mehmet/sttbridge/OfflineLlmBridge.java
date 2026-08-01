package com.mehmet.sttbridge;

import android.content.Context;
import android.util.Log;

import com.mehmet.sttbridge.llm.LlamaCppEngine;
import com.mehmet.sttbridge.llm.LlmBackendStatus;
import com.mehmet.sttbridge.llm.LlmCommandSchema;
import com.mehmet.sttbridge.llm.LlmDroneCommandValidator;
import com.mehmet.sttbridge.llm.LlmGenerationOptions;
import com.mehmet.sttbridge.llm.LlmJsonUtils;
import com.mehmet.sttbridge.llm.LlmPromptBuilder;
import com.mehmet.sttbridge.llm.LlmPublicCommandSerializer;
import com.mehmet.sttbridge.llm.OfflineLlmEngine;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.PrintWriter;
import java.io.StringWriter;

/** Unity-callable action-first offline LLM bridge. */
public final class OfflineLlmBridge {
    private static final String TAG = "OfflineLlmBridge";
    private static final String ENGINE_NAME = "offline_llm";
    private static final String CONTRACT_VERSION = "action_first_v3_20260713";
    private static final Object ENGINE_LOCK = new Object();
    private static OfflineLlmEngine cachedEngine;
    private static String cachedEngineConfigKey = "";

    private OfflineLlmBridge() {}

    /** Controlled warm-up: initializes and caches the model without generating. */
    public static String warmUp(Context context, String configJson) {
        long started = System.currentTimeMillis();
        try {
            if (context == null) throw new IllegalArgumentException("Context is null.");
            JSONObject config = LlmCommandSchema.mergeWithDefault(configJson);
            String configKey = initializationConfigKey(config);
            boolean modelReused;
            synchronized (ENGINE_LOCK) {
                modelReused = cachedEngine != null
                        && cachedEngine.getStatus().initialized
                        && configKey.equals(cachedEngineConfigKey);
            }
            OfflineLlmEngine engine = acquireEngine(context.getApplicationContext(), config);
            LlmBackendStatus status = engine.getStatus();
            JSONObject payload = new JSONObject();
            payload.put("ok", status.initialized);
            payload.put("contract_version", CONTRACT_VERSION);
            payload.put("stage", status.initialized ? "model_warmed" : status.stage);
            payload.put("initialized", status.initialized);
            payload.put("model_reused", modelReused);
            payload.put("inference_ran", false);
            payload.put("backend_status", status.toJson());
            payload.put("durationMs", System.currentTimeMillis() - started);
            if (!status.initialized) payload.put("error", status.error);
            return payload.toString();
        } catch (Throwable t) {
            return failureJson("", "warm_up_failed", false, null, "", null, throwableMessage(t), t, System.currentTimeMillis() - started, false);
        }
    }

    public static String runSmokePrompt(Context context, String configJson) {
        long started = System.currentTimeMillis();
        try {
            if (context == null) throw new IllegalArgumentException("Context is null.");
            JSONObject config = LlmCommandSchema.mergeWithDefault(configJson);
            OfflineLlmEngine engine = acquireEngine(context.getApplicationContext(), config);
            if (!engine.getStatus().initialized) {
                return backendFailureJson("", engine.getStatus(), System.currentTimeMillis() - started);
            }
            String prompt = LlmPromptBuilder.buildSmokePrompt();
            String raw = engine.generate(prompt, LlmGenerationOptions.forActionClassification(config));
            JSONObject payload = new JSONObject();
            payload.put("ok", true);
            payload.put("contract_version", CONTRACT_VERSION);
            payload.put("engine", ENGINE_NAME);
            payload.put("backend", engine.getEngineName());
            payload.put("stage", "smoke_generated");
            payload.put("initialized", true);
            payload.put("inference_ran", true);
            payload.put("backend_status", engine.getStatus().toJson());
            payload.put("llm_raw", raw);
            payload.put("llm_prompt_tokens", LlmPromptBuilder.estimatePromptTokens(prompt));
            payload.put("llm_prompt_tokens_exact", false);
            payload.put("llm_output_tokens", LlmPromptBuilder.estimateOutputTokens(raw));
            payload.put("llm_output_tokens_exact", false);
            payload.put("durationMs", System.currentTimeMillis() - started);
            return payload.toString();
        } catch (Throwable t) {
            return failureJson("", "smoke_generation_failed", true, null, "", null, throwableMessage(t), t, System.currentTimeMillis() - started, true);
        }
    }

    public static String parseCommand(Context context, String rawText, String spacyAnalysisJson, String configJson) {
        long started = System.currentTimeMillis();
        try {
            if (context == null) throw new IllegalArgumentException("Context is null.");
            JSONObject config = LlmCommandSchema.mergeWithDefault(configJson);
            JSONObject spacy = parseObjectOrEmpty(spacyAnalysisJson);
            String input = canonicalInput(spacy, rawText);
            String resolutionStatus = resolutionStatus(spacy);
            JSONObject baseline = baselineEvidence(spacy);
            JSONObject diagnostics = baseDiagnostics(spacy, rawText, input, resolutionStatus);

            if (isBaselineFinal(spacy) && baseline != null) {
                long validationStarted = System.currentTimeMillis();
                LlmDroneCommandValidator.Result validation = LlmDroneCommandValidator.validateBaseline(baseline, config);
                diagnostics.put("action_llm_called", false);
                diagnostics.put("slot_llm_called", false);
                diagnostics.put("slot_evidence_valid", false);
                return enrich(successJson(input, "baseline_resolved", null, null, "", baseline, validation,
                        false, "baseline", 0, 0, System.currentTimeMillis() - validationStarted,
                        System.currentTimeMillis() - started, spacy), diagnostics);
            }

            if (input.isEmpty()) {
                return unknownJson(input, "empty_or_unclear_command", System.currentTimeMillis() - started, spacy);
            }

            if (!hasActionEvidence(spacy, baseline)) {
                diagnostics.put("recovery_status", recoveryStatus(spacy));
                diagnostics.put("final_resolution_status", "UNKNOWN");
                diagnostics.put("action_llm_called", false);
                diagnostics.put("slot_llm_called", false);
                diagnostics.put("slot_evidence_valid", false);
                return enrich(unknownJson(input, "action_evidence_absent", System.currentTimeMillis() - started, spacy), diagnostics);
            }

            if ("NEEDS_SLOT_CLASSIFICATION".equals(resolutionStatus)) {
                return classifyObjectSlot(context, config, spacy, spacyAnalysisJson, input, baseline, diagnostics, started);
            }

            OfflineLlmEngine engine = acquireEngine(context.getApplicationContext(), config);
            if (!engine.getStatus().initialized) {
                return failureWithDiagnostics(
                        backendFailureJson(input, engine.getStatus(), System.currentTimeMillis() - started),
                        diagnostics, false, false, "", "", 0, 0);
            }

            String actionRaw = "";
            String slotRaw = "";
            JSONObject llmResult;
            LlmDroneCommandValidator.Result validation;
            String resolutionSource = "semantic_llm";
            String stage = "semantic_llm_validated";
            int promptTokens = 0;
            int outputTokens = 0;
            long actionDuration = 0;
            long slotDuration = 0;
            long validationStarted;
            String semanticPrompt = LlmPromptBuilder.buildSemanticCommandPrompt(input, spacyAnalysisJson, config);
            long generationStarted = System.currentTimeMillis();
            try {
                actionRaw = engine.generate(semanticPrompt, LlmGenerationOptions.forSemanticExtraction(config));
                logLong("semantic LLM output: ", actionRaw);
            } catch (Throwable t) {
                return failureWithDiagnostics(
                        failureJson(input, "semantic_generation_failed", true, engine.getStatus(), "", null,
                                "Semantic LLM generation failed: " + throwableMessage(t), t,
                                System.currentTimeMillis() - started, true),
                        diagnostics, true, false, "", "", System.currentTimeMillis() - generationStarted, 0);
            }
            actionDuration = System.currentTimeMillis() - generationStarted;
            try {
                llmResult = LlmJsonUtils.parseSingleObject(actionRaw);
            } catch (Throwable t) {
                return failureWithDiagnostics(
                        failureJson(input, "semantic_json_parse", true, engine.getStatus(), actionRaw, null,
                                "Semantic parser did not return exactly one JSON object: " + throwableMessage(t), t,
                                System.currentTimeMillis() - started, true),
                        diagnostics, true, false, actionRaw, "", actionDuration, 0);
            }
            validationStarted = System.currentTimeMillis();
            validation = LlmDroneCommandValidator.validateSemanticResult(llmResult, baseline, input, config);
            promptTokens = LlmPromptBuilder.estimatePromptTokens(semanticPrompt);
            outputTokens = LlmPromptBuilder.estimateOutputTokens(actionRaw);

            long validationDuration = System.currentTimeMillis() - validationStarted;
            diagnostics.put("action_llm_called", !actionRaw.isEmpty());
            diagnostics.put("slot_llm_called", !slotRaw.isEmpty());
            diagnostics.put("action_llm_raw", actionRaw.isEmpty() ? JSONObject.NULL : actionRaw);
            diagnostics.put("slot_llm_raw", slotRaw.isEmpty() ? JSONObject.NULL : slotRaw);
            diagnostics.put("action_llm_duration_ms", actionDuration);
            diagnostics.put("slot_llm_duration_ms", slotDuration);
            diagnostics.put("slot_evidence_valid", validation.errors.length() == 0);
            String combinedRaw = !slotRaw.isEmpty() ? slotRaw : actionRaw;
            return enrich(successJson(input, stage, engine, engine.getStatus(), combinedRaw, llmResult, validation,
                    true, resolutionSource, promptTokens, outputTokens, validationDuration,
                    System.currentTimeMillis() - started, spacy, actionDuration + slotDuration), diagnostics);
        } catch (Throwable t) {
            Log.e(TAG, "Offline LLM bridge failed.", t);
            return failureJson(rawText, "offline_llm_bridge_failed", false, null, "", null,
                    throwableMessage(t), t, System.currentTimeMillis() - started, false);
        }
    }

    private static String classifyObjectSlot(
            Context context, JSONObject config, JSONObject spacy, String spacyAnalysisJson,
            String input, JSONObject baseline, JSONObject diagnostics, long started) throws Exception {
        String fixedAction = evidenceAction(baseline);
        String rawEvidence = unresolvedObjectPhrase(spacy);
        String requestId = requestId(spacy);
        Log.i(TAG, "[NLP-SlotRouting] request_id=" + requestId
                + " pre_llm_resolution_status=NEEDS_SLOT_CLASSIFICATION llm_task=slot_classification"
                + " resolution_status=NEEDS_SLOT_CLASSIFICATION fixed_action=" + fixedAction
                + " unresolved_field=object raw_slot_evidence=" + JSONObject.quote(rawEvidence)
                + " action_llm_called=false slot_llm_started=true");

        if (!LlmCommandSchema.supportsObject(fixedAction) || rawEvidence.isEmpty()) {
            diagnostics.put("action_llm_called", false);
            diagnostics.put("slot_llm_called", false);
            diagnostics.put("slot_evidence_valid", false);
            diagnostics.put("fixed_action", fixedAction);
            diagnostics.put("unresolved_field", "object");
            diagnostics.put("raw_slot_evidence", rawEvidence);
            return enrich(unknownJson(input, "slot_evidence_absent", System.currentTimeMillis() - started, spacy), diagnostics);
        }

        OfflineLlmEngine engine = acquireEngine(context.getApplicationContext(), config);
        if (!engine.getStatus().initialized) {
            return failureWithDiagnostics(
                    backendFailureJson(input, engine.getStatus(), System.currentTimeMillis() - started),
                    diagnostics, false, false, "", "", 0, 0);
        }

        String prompt = LlmPromptBuilder.buildObjectClassificationPrompt(
                input, fixedAction, rawEvidence, spacyAnalysisJson, config);
        String slotRaw;
        long generationStartedNanos = System.nanoTime();
        try {
            slotRaw = engine.generate(prompt, LlmGenerationOptions.forSlotExtraction(config, rawEvidence));
        } catch (Throwable t) {
            long generationDuration = elapsedMs(generationStartedNanos);
            logSlotFinal(requestId, "generation_failed", throwableMessage(t), "", "", "not_run", "not_run",
                    true, "UNKNOWN", "fallback");
            return failureWithDiagnostics(
                    withLlmMetrics(failureJson(input, "slot_generation_failed", true, engine.getStatus(), "", null,
                            "Object slot LLM generation failed: " + throwableMessage(t), t,
                            System.currentTimeMillis() - started, true), generationMetrics(engine, generationDuration),
                            0, 0, generationDuration),
                    diagnostics, false, true, "", "", 0,
                    generationDuration);
        }
        long generationDuration = elapsedMs(generationStartedNanos);
        LlamaCppEngine.GenerationMetrics metrics = generationMetrics(engine, generationDuration);
        Log.i(TAG, "[NLP-SlotRouting] request_id=" + requestId
                + " llm_raw_output=" + safeSingleLine(slotRaw)
                + " raw_output_chars=" + slotRaw.length()
                + " slot_llm_called=true");

        JSONObject slotResult;
        long parseStartedNanos = System.nanoTime();
        try {
            slotResult = LlmJsonUtils.parseSingleObject(slotRaw);
        } catch (Throwable t) {
            long parseDuration = elapsedMs(parseStartedNanos);
            String rejectReason = throwableMessage(t);
            Log.i(TAG, "[NLP-SlotRouting] request_id=" + requestId
                    + " parse_result=rejected parse_reject_reason=" + JSONObject.quote(rejectReason)
                    + " llm_candidate_value= llm_evidence=");
            logSlotFinal(requestId, "rejected", rejectReason, "", "", "not_run", "not_run",
                    true, "UNKNOWN", "fallback");
            return failureWithDiagnostics(
                    withLlmMetrics(failureJson(input, "slot_json_parse", true, engine.getStatus(), slotRaw, null,
                            "Object slot classifier did not return exactly one JSON object: " + throwableMessage(t), t,
                            System.currentTimeMillis() - started, true), metrics, parseDuration, 0,
                            generationDuration + parseDuration),
                    diagnostics, false, true, "", slotRaw, 0, generationDuration);
        }
        long parseDuration = elapsedMs(parseStartedNanos);
        String candidateValue = slotResult.optString("object", "");
        String candidateEvidence = slotResult.optString("evidence", "");
        Log.i(TAG, "[NLP-SlotRouting] request_id=" + requestId
                + " parse_result=accepted parse_reject_reason=NONE"
                + " llm_candidate_value=" + candidateValue
                + " llm_evidence=" + JSONObject.quote(candidateEvidence));

        long validationStartedNanos = System.nanoTime();
        LlmDroneCommandValidator.Result validation = LlmDroneCommandValidator.validateSemanticSlot(
                slotResult, baseline, fixedAction, input, config);
        long validationDuration = elapsedMs(validationStartedNanos);
        boolean valid = validation.errors.length() == 0
                && fixedAction.equals(validation.command.optString("action", ""))
                && validation.command.has("object");
        diagnostics.put("action_llm_called", false);
        diagnostics.put("slot_llm_called", true);
        diagnostics.put("slot_evidence_valid", valid);
        diagnostics.put("fixed_action", fixedAction);
        diagnostics.put("unresolved_field", "object");
        diagnostics.put("raw_slot_evidence", rawEvidence);
        diagnostics.put("action_llm_raw", JSONObject.NULL);
        diagnostics.put("slot_llm_raw", slotRaw);
        diagnostics.put("action_llm_duration_ms", 0);
        diagnostics.put("slot_llm_duration_ms", generationDuration);
        if (!valid) diagnostics.put("failure_stage", "slot_validation");
        String finalAction = validation.command.optString("action", "UNKNOWN");
        String validatorRejectReason = joinJsonArray(validation.errors);
        String resolutionSource = valid ? "llm" : "slot_llm_rejected";
        Log.i(TAG, "[NLP-SlotRouting] request_id=" + requestId
                + " slot_llm_called=true slot_evidence_valid=" + valid
                + " validator_result=" + (validation.errors.length() == 0 ? "accepted" : "rejected")
                + " validator_reject_reason=" + (validatorRejectReason.isEmpty() ? "NONE" : JSONObject.quote(validatorRejectReason))
                + " final_action=" + finalAction
                + " final_object=" + validation.command.optString("object", "")
                + " final_resolution_status=" + finalResolutionStatus(validation.command)
                + " resolution_source=" + resolutionSource);
        String resultJson = successJson(
                input, valid ? "slot_llm_validated" : "slot_llm_rejected",
                engine, engine.getStatus(), slotRaw, slotResult, validation,
                true, resolutionSource, metrics.promptTokens,
                metrics.outputTokens, validationDuration,
                System.currentTimeMillis() - started, spacy, generationDuration);
        resultJson = withLlmMetrics(resultJson, metrics, parseDuration, validationDuration,
                generationDuration + parseDuration + validationDuration);
        return enrich(resultJson, diagnostics);
    }

    private static long elapsedMs(long startedNanos) {
        return Math.max(0L, (System.nanoTime() - startedNanos) / 1_000_000L);
    }

    private static LlamaCppEngine.GenerationMetrics generationMetrics(OfflineLlmEngine engine, long javaDurationMs) {
        if (engine instanceof LlamaCppEngine) {
            return ((LlamaCppEngine) engine).getLastGenerationMetrics();
        }
        return LlamaCppEngine.GenerationMetrics.empty();
    }

    private static String withLlmMetrics(String json, LlamaCppEngine.GenerationMetrics metrics,
                                         long parseDuration, long validationDuration, long totalLlmDuration) {
        try {
            JSONObject payload = new JSONObject(json);
            payload.put("llm_prompt_tokens", metrics.promptTokens);
            payload.put("llm_prompt_tokens_exact", metrics.promptTokens > 0);
            payload.put("llm_output_tokens", metrics.outputTokens);
            payload.put("llm_output_tokens_exact", metrics.outputTokens > 0);
            payload.put("llm_prompt_eval_duration", metrics.promptEvalMs);
            payload.put("llm_prompt_eval_duration_ms", metrics.promptEvalMs);
            payload.put("llm_first_token_duration", metrics.firstTokenMs);
            payload.put("llm_first_token_duration_ms", metrics.firstTokenMs);
            payload.put("llm_generation_duration", metrics.generationMs);
            payload.put("llm_generation_duration_ms", metrics.generationMs);
            payload.put("llm_native_total_duration_ms", metrics.totalMs);
            payload.put("llm_parse_duration", parseDuration);
            payload.put("llm_parse_duration_ms", parseDuration);
            payload.put("validation_duration", validationDuration);
            payload.put("validation_duration_ms", validationDuration);
            payload.put("llm_total_duration", totalLlmDuration);
            payload.put("llm_total_duration_ms", totalLlmDuration);
            payload.put("native_json_early_stop", metrics.jsonEarlyStop);
            payload.put("native_json_early_stop_reason", metrics.jsonEarlyStop
                    ? "balanced_root_json_completed" : "eog_or_max_tokens");
            payload.put("grammar_constrained", true);
            payload.put("grammar_constraint_reason", "slot_exact_schema_allowlist_and_raw_evidence");
            return payload.toString();
        } catch (Throwable ignored) {
            return json;
        }
    }

    private static String safeSingleLine(String raw) {
        return JSONObject.quote(raw != null ? raw : "");
    }

    private static String joinJsonArray(JSONArray values) {
        if (values == null || values.length() == 0) return "";
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < values.length(); i++) {
            if (result.length() > 0) result.append('|');
            result.append(values.optString(i, ""));
        }
        return result.toString();
    }

    private static void logSlotFinal(String requestId, String parseResult, String parseRejectReason,
                                     String candidateValue, String evidence, String validatorResult,
                                     String validatorRejectReason, boolean slotCalled,
                                     String finalStatus, String resolutionSource) {
        Log.i(TAG, "[NLP-SlotRouting] request_id=" + requestId
                + " parse_result=" + parseResult
                + " parse_reject_reason=" + (parseRejectReason.isEmpty() ? "NONE" : JSONObject.quote(parseRejectReason))
                + " llm_candidate_value=" + candidateValue
                + " llm_evidence=" + JSONObject.quote(evidence)
                + " validator_result=" + validatorResult
                + " validator_reject_reason=" + (validatorRejectReason.isEmpty() ? "NONE" : JSONObject.quote(validatorRejectReason))
                + " slot_llm_called=" + slotCalled
                + " final_resolution_status=" + finalStatus
                + " resolution_source=" + resolutionSource);
    }

    private static String successJson(
            String input, String stage, OfflineLlmEngine engine, LlmBackendStatus status,
            String llmRaw, JSONObject llmCommand, LlmDroneCommandValidator.Result validation,
            boolean inferenceRan, String resolutionSource, int promptTokens, int outputTokens,
            long validationDuration, long durationMs, JSONObject spacy, long... generationDuration) throws Exception {
        JSONObject payload = new JSONObject();
        payload.put("ok", true);
        payload.put("contract_version", CONTRACT_VERSION);
        payload.put("engine", ENGINE_NAME);
        payload.put("backend", engine != null ? engine.getEngineName() : JSONObject.NULL);
        payload.put("stage", stage);
        payload.put("initialized", status != null && status.initialized);
        payload.put("inference_ran", inferenceRan);
        payload.put("llm_called", inferenceRan);
        payload.put("resolution_source", resolutionSource);
        payload.put("backend_status", status != null ? status.toJson() : JSONObject.NULL);
        payload.put("input", input);
        payload.put("llm_raw", llmRaw != null ? llmRaw : "");
        payload.put("llm_command", llmCommand != null ? llmCommand : JSONObject.NULL);
        JSONObject internalValidated = validation.command;
        JSONObject publicCommand = LlmPublicCommandSerializer.materialize(internalValidated);
        String finalResolutionStatus = finalResolutionStatus(internalValidated);
        payload.put("resolution_status", finalResolutionStatus);
        payload.put("final_resolution_status", finalResolutionStatus);
        payload.put("internal_validated_command", internalValidated);
        payload.put("public_command", publicCommand);
        payload.put("normalized_command", publicCommand);
        payload.put("validated_command", publicCommand);
        payload.put("errors", validation.errors);
        payload.put("warnings", validation.warnings);
        payload.put("json_repair_applied", false);
        payload.put("json_repair_notes", new JSONArray());
        boolean outputValid = validation.errors.length() == 0;
        payload.put("llm_output_valid", outputValid);
        payload.put("balanced_json", true);
        if (!outputValid) {
            payload.put("failure_stage", "action_schema");
            payload.put("fallback_action", "UNKNOWN");
        }
        payload.put("llm_prompt_tokens", promptTokens);
        payload.put("llm_prompt_tokens_exact", false);
        payload.put("llm_output_tokens", outputTokens);
        payload.put("llm_output_tokens_exact", false);
        payload.put("llm_prompt_eval_duration", JSONObject.NULL);
        payload.put("llm_first_token_duration", JSONObject.NULL);
        payload.put("llm_generation_duration", generationDuration.length > 0 ? generationDuration[0] : 0);
        payload.put("validation_duration", validationDuration);
        payload.put("validation_duration_ms", validationDuration);
        payload.put("native_json_early_stop", false);
        payload.put("native_json_early_stop_reason", "prebuilt_jni_has_no_early_stop_or_grammar_contract");
        payload.put("grammar_constrained", false);
        payload.put("grammar_constraint_reason", "llama_cpp_supports_grammar_but_prebuilt_jni_does_not_expose_it");
        copyTiming(spacy, payload, "spacy_duration_ms");
        copyTiming(spacy, payload, "baseline_duration_ms");
        copyTiming(spacy, payload, "action_resolution_duration_ms");
        copyTiming(spacy, payload, "slot_extraction_duration_ms");
        payload.put("durationMs", durationMs);
        payload.put("total_duration_ms", durationMs);
        return payload.toString();
    }

    private static String unknownJson(String input, String stage, long durationMs, JSONObject spacy) throws Exception {
        LlmDroneCommandValidator.Result result = LlmDroneCommandValidator.validateClassification(
                new JSONObject().put("action", "UNKNOWN"), null, new JSONObject());
        return successJson(input, stage, null, null, "", new JSONObject().put("action", "UNKNOWN"), result,
                false, "fallback", 0, 0, 0, durationMs, spacy);
    }

    private static String backendFailureJson(String input, LlmBackendStatus status, long durationMs) {
        return failureJson(input, status != null ? status.stage : "backend_not_initialized", false, status, "", null,
                status != null ? status.error : "Offline LLM backend did not initialize.", null, durationMs, true);
    }

    private static String failureJson(String input, String stage, boolean inferenceRan, LlmBackendStatus status,
                                      String llmRaw, JSONObject llmCommand, String error, Throwable throwable,
                                      long durationMs, boolean llmCalled) {
        try {
            boolean balanced = LlmJsonUtils.isBalancedRootObject(llmRaw);
            JSONObject payload = new JSONObject();
            payload.put("ok", false);
            payload.put("contract_version", CONTRACT_VERSION);
            payload.put("engine", ENGINE_NAME);
            payload.put("backend", status != null ? status.engine : JSONObject.NULL);
            payload.put("stage", stage);
            payload.put("failure_stage", stage);
            payload.put("initialized", status != null && status.initialized);
            payload.put("inference_ran", inferenceRan);
            payload.put("llm_called", llmCalled);
            payload.put("resolution_source", "fallback");
            payload.put("backend_status", status != null ? status.toJson() : JSONObject.NULL);
            payload.put("input", input != null ? input : "");
            payload.put("llm_raw", llmRaw != null ? llmRaw : "");
            payload.put("llm_command", llmCommand != null ? llmCommand : JSONObject.NULL);
            JSONObject internalValidated = LlmCommandSchema.defaultCommand();
            JSONObject publicCommand = LlmPublicCommandSerializer.materialize(internalValidated);
            payload.put("internal_validated_command", internalValidated);
            payload.put("public_command", publicCommand);
            payload.put("normalized_command", publicCommand);
            payload.put("validated_command", publicCommand);
            payload.put("errors", new JSONArray().put(stage));
            payload.put("warnings", new JSONArray());
            payload.put("json_repair_applied", false);
            payload.put("json_repair_notes", new JSONArray());
            payload.put("llm_output_valid", false);
            payload.put("balanced_json", balanced);
            payload.put("fallback_action", "UNKNOWN");
            payload.put("grammar_constrained", false);
            payload.put("grammar_constraint_reason", "llama_cpp_supports_grammar_but_prebuilt_jni_does_not_expose_it");
            payload.put("durationMs", durationMs);
            payload.put("total_duration_ms", durationMs);
            payload.put("error", error != null ? error : "");
            if (throwable != null) {
                payload.put("exception_type", throwable.getClass().getName());
                payload.put("traceback", stackTraceToString(throwable));
            }
            return payload.toString();
        } catch (Throwable jsonError) {
            return "{\"ok\":false,\"stage\":" + JSONObject.quote(stage) + ",\"error\":" + JSONObject.quote(throwableMessage(jsonError)) + "}";
        }
    }

    private static JSONObject parseObjectOrEmpty(String json) {
        try { return new JSONObject(json != null && !json.trim().isEmpty() ? json : "{}"); }
        catch (Throwable ignored) { return new JSONObject(); }
    }

    private static JSONObject baselineCandidate(JSONObject spacy) {
        JSONObject hints = spacy != null ? spacy.optJSONObject("baseline_hints") : null;
        JSONObject value = hints != null ? hints.optJSONObject("command_candidate") : null;
        return value != null ? value : (spacy != null ? spacy.optJSONObject("drone_command") : null);
    }

    private static JSONObject baselineEvidence(JSONObject spacy) {
        JSONObject candidate = baselineCandidate(spacy);
        if (candidate == null) return null;
        try {
            JSONObject copy = new JSONObject(candidate.toString());
            JSONObject hints = spacy != null ? spacy.optJSONObject("baseline_hints") : null;
            if (hints != null) {
                copy.put("detected_object_phrase", hints.optString("detected_object_phrase", ""));
                copy.put("action_evidence", hints.optJSONObject("action_evidence"));
                copy.put("evidence_supported_actions", hints.optJSONArray("evidence_supported_actions"));
            }
            copy.put("analysis_input", spacy != null ? spacy.optString("analysis_input", "") : "");
            copy.put("semantic_frame", spacy != null ? spacy.optJSONObject("semantic_frame") : JSONObject.NULL);
            copy.put("protected_spans", spacy != null ? spacy.optJSONArray("protected_spans") : JSONObject.NULL);
            return copy;
        } catch (Throwable ignored) {
            return candidate;
        }
    }

    private static String evidenceAction(JSONObject evidence) {
        if (evidence == null) return "UNKNOWN";
        String action = evidence.optString("action", "UNKNOWN").trim().toUpperCase(java.util.Locale.ROOT);
        return "MISSING_INFO".equals(action)
                ? evidence.optString("target_action", "UNKNOWN").trim().toUpperCase(java.util.Locale.ROOT)
                : action;
    }

    private static String detectedObjectPhrase(JSONObject spacy, JSONObject baseline) {
        String value = baseline != null ? baseline.optString("detected_object_phrase", "").trim() : "";
        if (!value.isEmpty()) return value;
        JSONObject compact = spacy != null ? spacy.optJSONObject("compact_evidence") : null;
        JSONArray candidates = compact != null ? compact.optJSONArray("object_phrase_candidates") : null;
        return candidates != null && candidates.length() > 0 ? candidates.optString(0, "").trim() : "";
    }

    private static String unresolvedObjectPhrase(JSONObject spacy) {
        JSONObject frame = spacy != null ? spacy.optJSONObject("semantic_frame") : null;
        JSONArray spans = frame != null ? frame.optJSONArray("object_spans") : null;
        if (spans == null) return "";
        for (int i = 0; i < spans.length(); i++) {
            JSONObject span = spans.optJSONObject(i);
            if (span == null || !"OBJECT".equals(span.optString("type", ""))) continue;
            if (!"object".equals(span.optString("field", ""))) continue;
            if (!"spacy_predicate_object_phrase".equals(span.optString("source", ""))) continue;
            if (span.has("value") && !span.isNull("value")) continue;
            if (span.has("canonical_value") && !span.isNull("canonical_value")) continue;
            String text = span.optString("text", "").trim();
            if (!text.isEmpty()) return text;
        }
        return "";
    }

    private static String requestId(JSONObject spacy) {
        String value = spacy != null ? spacy.optString("request_id", "").trim() : "";
        if (!value.isEmpty()) return value;
        try {
            JSONObject context = new JSONObject(spacy != null ? spacy.optString("context_json", "{}") : "{}");
            value = context.optString("request_id", "").trim();
        } catch (Throwable ignored) {
        }
        return value.isEmpty() ? "-" : value;
    }

    private static String canonicalInput(JSONObject spacy, String rawText) {
        String value = spacy != null ? spacy.optString("analysis_input", "").trim() : "";
        if (value.isEmpty() && spacy != null) value = spacy.optString("input", "").trim();
        if (value.isEmpty()) value = rawText != null ? rawText.trim() : "";
        return value;
    }

    private static String resolutionStatus(JSONObject spacy) {
        String value = spacy != null ? spacy.optString("resolution_status", "") : "";
        JSONObject hints = spacy != null ? spacy.optJSONObject("baseline_hints") : null;
        if (value.isEmpty() && hints != null) value = hints.optString("resolution_status", "");
        return value.isEmpty() ? "NEEDS_ACTION_CLASSIFICATION" : value;
    }

    private static JSONObject baseDiagnostics(JSONObject spacy, String rawText, String input, String resolutionStatus) throws Exception {
        JSONObject value = new JSONObject();
        value.put("raw_input", spacy != null ? spacy.optString("raw_input", rawText != null ? rawText : "") : (rawText != null ? rawText : ""));
        value.put("wake_word_suffix", spacy != null ? spacy.optString("wake_word_suffix", rawText != null ? rawText : "") : (rawText != null ? rawText : ""));
        value.put("analysis_input", input);
        value.put("recovery_status", recoveryStatus(spacy));
        value.put("repair_applied", spacy != null && spacy.optBoolean("repair_applied", false));
        value.put("repair_candidates", spacy != null ? spacy.optJSONArray("repair_candidates") : new JSONArray());
        value.put("selected_repair", spacy != null ? spacy.optString("selected_repair", "") : "");
        value.put("repair_notes", spacy != null ? spacy.optJSONArray("repair_notes") : new JSONArray());
        value.put("repair_duration_ms", spacy != null ? spacy.optDouble("repair_duration_ms", 0.0) : 0.0);
        JSONObject hints = spacy != null ? spacy.optJSONObject("baseline_hints") : null;
        value.put("baseline_candidate", hints != null ? hints.optJSONObject("command_candidate") : JSONObject.NULL);
        value.put("detected_object_phrase", detectedObjectPhrase(spacy, baselineEvidence(spacy)));
        return value;
    }

    private static boolean hasActionEvidence(JSONObject spacy, JSONObject baseline) {
        JSONObject hints = spacy != null ? spacy.optJSONObject("baseline_hints") : null;
        JSONObject evidence = hints != null ? hints.optJSONObject("action_evidence") : null;
        JSONArray supported = evidence != null ? evidence.optJSONArray("supported_actions") : null;
        if (supported == null && hints != null) supported = hints.optJSONArray("evidence_supported_actions");
        if (evidence != null) return evidence.optBoolean("has_any", false) && supported != null && supported.length() > 0;
        if (supported != null) return supported.length() > 0;
        String action = evidenceAction(baseline);
        return action != null && !action.isEmpty() && !"UNKNOWN".equals(action);
    }

    private static String recoveryStatus(JSONObject spacy) {
        JSONObject hints = spacy != null ? spacy.optJSONObject("baseline_hints") : null;
        String value = hints != null ? hints.optString("recovery_resolution_status", "") : "";
        return value.isEmpty() ? "NO_RECOVERY" : value;
    }

    private static String finalResolutionStatus(JSONObject command) {
        String action = command != null ? command.optString("action", "UNKNOWN") : "UNKNOWN";
        if ("MISSING_INFO".equals(action)) return "MISSING_INFO";
        return "UNKNOWN".equals(action) ? "UNKNOWN" : "RESOLVED";
    }

    private static String enrich(String json, JSONObject extra) {
        try {
            JSONObject payload = new JSONObject(json);
            JSONArray names = extra.names();
            if (names != null) for (int i = 0; i < names.length(); i++) {
                String name = names.getString(i);
                payload.put(name, extra.get(name));
            }
            return payload.toString();
        } catch (Throwable ignored) {
            return json;
        }
    }

    private static String failureWithDiagnostics(
            String json, JSONObject diagnostics, boolean actionCalled, boolean slotCalled,
            String actionRaw, String slotRaw, long actionDuration, long slotDuration) {
        try {
            diagnostics.put("action_llm_called", actionCalled);
            diagnostics.put("slot_llm_called", slotCalled);
            diagnostics.put("action_llm_raw", actionRaw != null && !actionRaw.isEmpty() ? actionRaw : JSONObject.NULL);
            diagnostics.put("slot_llm_raw", slotRaw != null && !slotRaw.isEmpty() ? slotRaw : JSONObject.NULL);
            diagnostics.put("action_llm_duration_ms", actionDuration);
            diagnostics.put("slot_llm_duration_ms", slotDuration);
            diagnostics.put("slot_evidence_valid", false);
        } catch (Throwable ignored) {
            // The original explicit failure payload remains authoritative.
        }
        return enrich(json, diagnostics);
    }

    /**
     * A failed semantic slot classifier must stay an explicit outer failure,
     * while retaining the trusted deterministic MISSING_INFO candidate. This
     * never accepts or repairs semantic values from malformed model output.
     */
    private static String failureWithSafeBaseline(
            String json, JSONObject diagnostics, JSONObject baseline, JSONObject config,
            boolean actionCalled, boolean slotCalled, String actionRaw, String slotRaw,
            long actionDuration, long slotDuration) {
        String enriched = failureWithDiagnostics(
                json, diagnostics, actionCalled, slotCalled, actionRaw, slotRaw,
                actionDuration, slotDuration);
        try {
            JSONObject payload = new JSONObject(enriched);
            LlmDroneCommandValidator.Result validation =
                    LlmDroneCommandValidator.validateBaseline(baseline, config);
            JSONObject internalValidated = validation.command;
            JSONObject publicCommand = LlmPublicCommandSerializer.materialize(internalValidated);
            payload.put("internal_validated_command", internalValidated);
            payload.put("public_command", publicCommand);
            payload.put("normalized_command", publicCommand);
            payload.put("validated_command", publicCommand);
            payload.put("fallback_action", internalValidated.optString("action", "UNKNOWN"));
            JSONArray warnings = payload.optJSONArray("warnings");
            if (warnings == null) warnings = new JSONArray();
            warnings.put("trusted_baseline_preserved_after_slot_failure");
            payload.put("warnings", warnings);
            return payload.toString();
        } catch (Throwable ignored) {
            return enriched;
        }
    }

    private static boolean isBaselineFinal(JSONObject spacy) {
        JSONObject hints = spacy != null ? spacy.optJSONObject("baseline_hints") : null;
        return hints != null && hints.optBoolean("final_parser", false);
    }

    private static void copyTiming(JSONObject source, JSONObject target, String key) throws Exception {
        if (source != null && source.has(key) && !source.isNull(key)) target.put(key, source.get(key));
    }

    private static OfflineLlmEngine acquireEngine(Context appContext, JSONObject config) {
        String configKey = initializationConfigKey(config);
        synchronized (ENGINE_LOCK) {
            if (cachedEngine != null && cachedEngine.getStatus().initialized && configKey.equals(cachedEngineConfigKey)) {
                return cachedEngine;
            }
            OfflineLlmEngine engine = new LlamaCppEngine(appContext);
            LlmBackendStatus status = engine.initialize(config);
            if (status.initialized) {
                if (cachedEngine instanceof LlamaCppEngine) ((LlamaCppEngine) cachedEngine).close();
                cachedEngine = engine;
                cachedEngineConfigKey = configKey;
            }
            return engine;
        }
    }

    private static String initializationConfigKey(JSONObject config) {
        JSONObject llm = config != null ? config.optJSONObject("llm") : null;
        if (llm == null) return "{}";
        return llm.optString("model_path", "") + "|" + llm.optString("model_asset", "") + "|"
                + llm.optInt("context_size", 1024) + "|" + llm.optInt("threads", 4);
    }

    private static String stackTraceToString(Throwable throwable) {
        StringWriter writer = new StringWriter();
        throwable.printStackTrace(new PrintWriter(writer));
        return writer.toString();
    }

    private static String throwableMessage(Throwable throwable) {
        if (throwable == null) return "";
        return throwable.getMessage() != null ? throwable.getMessage() : throwable.toString();
    }

    private static void logLong(String prefix, String message) {
        String value = message != null ? message : "";
        for (int offset = 0; offset < value.length(); offset += 3000) {
            Log.d(TAG, prefix + value.substring(offset, Math.min(value.length(), offset + 3000)));
        }
    }
}
