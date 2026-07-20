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
            String resolutionSource;
            String stage;
            int promptTokens = 0;
            int outputTokens = 0;
            long actionDuration = 0;
            long slotDuration = 0;
            long validationStarted;

            if ("NEEDS_SLOT_CLASSIFICATION".equals(resolutionStatus)) {
                String action = evidenceAction(baseline);
                String prompt = LlmPromptBuilder.buildObjectClassificationPrompt(
                        input, baseline != null ? baseline.optString("detected_object_phrase", "") : "",
                        spacyAnalysisJson, config);
                long generationStarted = System.currentTimeMillis();
                try {
                    slotRaw = engine.generate(prompt, LlmGenerationOptions.forSlotExtraction(config));
                    logLong("slot LLM output: ", slotRaw);
                } catch (Throwable t) {
                    return failureWithSafeBaseline(
                            failureJson(input, "slot_generation_failed", true, engine.getStatus(), "", null,
                                    "Slot LLM generation failed: " + throwableMessage(t), t,
                                    System.currentTimeMillis() - started, true),
                            diagnostics, baseline, config, false, true, "", "", 0,
                            System.currentTimeMillis() - generationStarted);
                }
                slotDuration = System.currentTimeMillis() - generationStarted;
                try {
                    llmResult = LlmJsonUtils.parseSingleObject(slotRaw);
                } catch (Throwable t) {
                    return failureWithSafeBaseline(
                            failureJson(input, "slot_json_parse", true, engine.getStatus(), slotRaw, null,
                                    "Slot classifier did not return exactly one JSON object: " + throwableMessage(t), t,
                                    System.currentTimeMillis() - started, true),
                            diagnostics, baseline, config, false, true, "", slotRaw, 0, slotDuration);
                }
                validationStarted = System.currentTimeMillis();
                validation = LlmDroneCommandValidator.validateSemanticSlot(llmResult, baseline, action, input, config);
                resolutionSource = "slot_llm";
                stage = "slot_llm_validated";
                promptTokens = LlmPromptBuilder.estimatePromptTokens(prompt);
                outputTokens = LlmPromptBuilder.estimateOutputTokens(slotRaw);
            } else {
                String actionPrompt = LlmPromptBuilder.buildActionClassificationPrompt(input, spacyAnalysisJson, config);
                long generationStarted = System.currentTimeMillis();
                try {
                    actionRaw = engine.generate(actionPrompt, LlmGenerationOptions.forActionClassification(config));
                    logLong("action LLM output: ", actionRaw);
                } catch (Throwable t) {
                    return failureWithDiagnostics(
                            failureJson(input, "action_generation_failed", true, engine.getStatus(), "", null,
                                    "Action LLM generation failed: " + throwableMessage(t), t,
                                    System.currentTimeMillis() - started, true),
                            diagnostics, true, false, "", "", System.currentTimeMillis() - generationStarted, 0);
                }
                actionDuration = System.currentTimeMillis() - generationStarted;
                JSONObject llmAction;
                try {
                    llmAction = LlmJsonUtils.parseSingleObject(actionRaw);
                } catch (Throwable t) {
                    return failureWithDiagnostics(
                            failureJson(input, "action_json_parse", true, engine.getStatus(), actionRaw, null,
                                    "Action classifier did not return exactly one JSON object: " + throwableMessage(t), t,
                                    System.currentTimeMillis() - started, true),
                            diagnostics, true, false, actionRaw, "", actionDuration, 0);
                }
                String action = llmAction.optString("action", "").trim().toUpperCase(java.util.Locale.ROOT);
                String detectedPhrase = detectedObjectPhrase(spacy, baseline);
                if (LlmCommandSchema.supportsObject(action) && !detectedPhrase.isEmpty()) {
                    String slotPrompt = LlmPromptBuilder.buildObjectClassificationPrompt(input, detectedPhrase, spacyAnalysisJson, config);
                    generationStarted = System.currentTimeMillis();
                    try {
                        slotRaw = engine.generate(slotPrompt, LlmGenerationOptions.forSlotExtraction(config));
                        logLong("slot LLM output: ", slotRaw);
                    } catch (Throwable t) {
                        return failureWithDiagnostics(
                                failureJson(input, "slot_generation_failed", true, engine.getStatus(), actionRaw, llmAction,
                                        "Slot LLM generation failed after action classification: " + throwableMessage(t), t,
                                        System.currentTimeMillis() - started, true),
                                diagnostics, true, true, actionRaw, "", actionDuration,
                                System.currentTimeMillis() - generationStarted);
                    }
                    slotDuration = System.currentTimeMillis() - generationStarted;
                    try {
                        llmResult = LlmJsonUtils.parseSingleObject(slotRaw);
                    } catch (Throwable t) {
                        return failureWithDiagnostics(
                                failureJson(input, "slot_json_parse", true, engine.getStatus(), slotRaw, llmAction,
                                        "Slot classifier did not return exactly one JSON object: " + throwableMessage(t), t,
                                        System.currentTimeMillis() - started, true),
                                diagnostics, true, true, actionRaw, slotRaw, actionDuration, slotDuration);
                    }
                    validationStarted = System.currentTimeMillis();
                    validation = LlmDroneCommandValidator.validateSemanticSlot(llmResult, baseline, action, input, config);
                    resolutionSource = "action_llm+slot_llm";
                    stage = "slot_llm_validated";
                    promptTokens = LlmPromptBuilder.estimatePromptTokens(actionPrompt) + LlmPromptBuilder.estimatePromptTokens(slotPrompt);
                    outputTokens = LlmPromptBuilder.estimateOutputTokens(actionRaw) + LlmPromptBuilder.estimateOutputTokens(slotRaw);
                } else {
                    llmResult = llmAction;
                    validationStarted = System.currentTimeMillis();
                    validation = LlmDroneCommandValidator.validateClassification(llmAction, baseline, config);
                    resolutionSource = "action_llm";
                    stage = "action_llm_validated";
                    promptTokens = LlmPromptBuilder.estimatePromptTokens(actionPrompt);
                    outputTokens = LlmPromptBuilder.estimateOutputTokens(actionRaw);
                }
            }

            long validationDuration = System.currentTimeMillis() - validationStarted;
            diagnostics.put("action_llm_called", !actionRaw.isEmpty());
            diagnostics.put("slot_llm_called", !slotRaw.isEmpty());
            diagnostics.put("action_llm_raw", actionRaw.isEmpty() ? JSONObject.NULL : actionRaw);
            diagnostics.put("slot_llm_raw", slotRaw.isEmpty() ? JSONObject.NULL : slotRaw);
            diagnostics.put("action_llm_duration_ms", actionDuration);
            diagnostics.put("slot_llm_duration_ms", slotDuration);
            diagnostics.put("slot_evidence_valid", !slotRaw.isEmpty() && validation.errors.length() == 0 && validation.command.has("object"));
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
