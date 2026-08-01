using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Game.NLP
{
    public sealed class SpeechCommandPipeline : MonoBehaviour
    {
        private static readonly TimeSpan SuggestionLifetime = TimeSpan.FromMinutes(2);

        [SerializeField] private SpeechCommandPipelineConfig config = new SpeechCommandPipelineConfig();
        private readonly PendingCommandSuggestionStore pendingSuggestions =
            new PendingCommandSuggestionStore(SuggestionLifetime);
        private Func<string, string, CancellationToken, Task<string>> analyzeAsync =
            Android.SpacyNlpAndroidBridgeClient.AnalyzeAsync;
        private Func<string, string, string, CancellationToken, Task<string>> parseLlmAsync =
            Android.OfflineLlmAndroidBridgeClient.ParseCommandAsync;
        private long processGeneration;

        public event Action<SpeechCommandPipelineResult> Completed;

        public string ActiveConfigJson =>
            config != null ? config.ConfigJson : SpeechCommandPipelineConfig.DefaultConfigJson;

        public string ActiveLlmConfigJson =>
            config != null
                ? config.ActiveLlmConfigJson
                : SpeechCommandPipelineConfig.BuildLlmDebugJson(SpeechCommandPipelineConfig.DefaultConfigJson);

        public string ActiveLlmConfigSummary =>
            config != null
                ? config.ActiveLlmConfigSummary
                : SpeechCommandPipelineConfig.BuildLlmConfigSummary(SpeechCommandPipelineConfig.DefaultConfigJson);

        public async Task<SpeechCommandPipelineResult> ProcessTranscriptAsync(
            string transcript,
            CancellationToken cancellationToken = default,
            string requestId = "",
            string rawTranscript = "")
        {
            long generation = Interlocked.Increment(ref processGeneration);
            pendingSuggestions.Clear();
            Stopwatch total = Stopwatch.StartNew();
            string configJson = ActiveConfigJson;
            string safeTranscript = transcript ?? string.Empty;
            string safeRawTranscript = string.IsNullOrWhiteSpace(rawTranscript) ? safeTranscript : rawTranscript;
            string recoveryRequestKey = Guid.NewGuid().ToString("N");
            string logRequestId = string.IsNullOrWhiteSpace(requestId) ? "-" : requestId;
            string analysisContextJson = BuildAnalysisContextJson(configJson, safeRawTranscript, recoveryRequestKey, logRequestId);

            Debug.Log($"[NLP] request_id={logRequestId} wake_word_suffix={safeTranscript}");

            string nlpJson = await analyzeAsync(
                safeTranscript,
                analysisContextJson,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            NlpAnalysisResult nlp = SafeFromJson<NlpAnalysisResult>(nlpJson);
            ValidatedCommandResult baseline = DroneCommandJsonParser.ParseObjectForKey(nlpJson, "drone_command");
            if (nlp != null) nlp.drone_command = baseline;
            CommandRecoveryResult recovery = CommandRecoveryResult.From(nlp?.baseline_hints, safeTranscript);
            bool baselineFinal = nlp != null
                && nlp.baseline_hints != null
                && nlp.baseline_hints.final_parser
                && baseline != null;
            string baselineResolutionSource = baselineFinal
                && string.Equals(nlp.baseline_hints.resolution_source, "spacy", StringComparison.Ordinal)
                    ? "spacy"
                    : "baseline";
            string analysisInput = !string.IsNullOrWhiteSpace(nlp?.analysis_input) ? nlp.analysis_input : safeTranscript;
            Debug.Log($"[NLP] request_id={logRequestId} analysis_input={analysisInput} repair_applied={(nlp != null && nlp.repair_applied)} resolution_status={nlp?.resolution_status}");
            if (nlp?.semantic_frame != null)
            {
                Debug.Log(
                    $"[NLP-SemanticFrame] request_id={logRequestId} "
                    + $"frame={JsonUtility.ToJson(nlp.semantic_frame)} "
                    + $"action_evidence={JsonUtility.ToJson(nlp.baseline_hints?.action_evidence)}");
            }
            bool actionEvidenceAbsent = !HasAnyActionEvidence(nlp, recovery, baseline);
            string llmJson;
            bool evidencePreGateApplied = !baselineFinal && actionEvidenceAbsent;
            if (evidencePreGateApplied)
            {
                recovery.resolution_status = CommandRecoveryStatus.Unknown;
                llmJson = DroneCommandJsonParser.BuildEvidenceGateEnvelope(
                    safeRawTranscript,
                    safeTranscript,
                    analysisInput,
                    nlp != null && nlp.repair_applied,
                    total.ElapsedMilliseconds);
                Debug.Log($"[NLP] request_id={logRequestId} llm_called=false resolution_source=evidence_gate reason=action_evidence_absent");
            }
            else if (baselineFinal)
            {
                string commandJson = DroneCommandJsonParser.RawObjectForKey(nlpJson, "drone_command");
                llmJson = DroneCommandJsonParser.BuildBaselineEnvelope(
                    safeRawTranscript,
                    safeTranscript,
                    analysisInput,
                    nlp?.resolution_status ?? "RESOLVED",
                    nlp != null && nlp.repair_applied,
                    commandJson,
                    total.ElapsedMilliseconds);
                if (string.Equals(baselineResolutionSource, "spacy", StringComparison.Ordinal))
                {
                    llmJson = llmJson
                        .Replace("\"stage\":\"baseline_resolved\"", "\"stage\":\"spacy_resolved\"")
                        .Replace("\"resolution_source\":\"baseline\"", "\"resolution_source\":\"spacy\"");
                }
                Debug.Log($"[NLP] request_id={logRequestId} llm_called=false resolution_source={baselineResolutionSource}");
            }
            else
            {
                Debug.Log($"[NLP] request_id={logRequestId} llm_called=true resolution_source=pending_llm llm_input={analysisInput}");
                llmJson = await parseLlmAsync(
                    safeTranscript,
                    nlpJson,
                    configJson,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            LlmCommandResult llm = SafeFromJson<LlmCommandResult>(llmJson);
            SpeechCommandPipelineResult result = new SpeechCommandPipelineResult
            {
                transcript = safeTranscript,
                configJson = configJson,
                activeLlmConfigJson = ActiveLlmConfigJson,
                activeLlmConfigSummary = ActiveLlmConfigSummary,
                nlpJson = nlpJson,
                llmJson = llmJson,
                nlp = nlp,
                llm = llm,
                recovery = recovery,
                resolutionSource = evidencePreGateApplied ? "evidence_gate" : (baselineFinal ? baselineResolutionSource : (llm != null ? llm.resolution_source : "fallback")),
                llmCalled = !evidencePreGateApplied && !baselineFinal && llm != null && llm.llm_called,
            };

            result.validated = result.llm != null ? result.llm.public_command : null;
            if (result.llm != null)
            {
                result.llm.internal_validated_command = DroneCommandJsonParser.ParseObjectForKey(llmJson, "internal_validated_command");
                result.llm.public_command = DroneCommandJsonParser.ParseObjectForKey(llmJson, "public_command");
                result.llm.normalized_command = DroneCommandJsonParser.ParseObjectForKey(llmJson, "normalized_command");
                result.llm.validated_command = DroneCommandJsonParser.ParseObjectForKey(llmJson, "validated_command");
                result.validated = result.llm.public_command ?? result.llm.validated_command;
            }

            result.finalJson = DroneCommandJsonParser.RawObjectForKey(llmJson, "public_command");
            if (!evidencePreGateApplied && result.llmCalled && !IsActionSupportedByEvidence(result.validated, nlp, recovery, baseline))
            {
                ApplyEvidenceRejection(result);
            }

            bool llmAttempted = !evidencePreGateApplied && !baselineFinal;
            if (ShouldOfferRecovery(result.validated, nlp, recovery, baselineFinal, llmAttempted))
            {
                PrioritizeRecoverySuggestions(recovery, IntendedAction(result.validated));
                result.validated = null;
                result.finalJson = null;
                result.resolutionSource = "suggestion";
                result.recoveryStatus = CommandRecoveryStatus.NeedsConfirmation;
                result.finalResolutionStatus = CommandRecoveryStatus.NeedsConfirmation;
                recovery.resolution_status = CommandRecoveryStatus.NeedsConfirmation;
                if (generation == Interlocked.Read(ref processGeneration))
                {
                    pendingSuggestions.ReplaceBatch(recoveryRequestKey, recovery.suggestions);
                }
                else
                {
                    recovery.suggestions = Array.Empty<CommandSuggestion>();
                    recovery.resolution_status = CommandRecoveryStatus.InvalidOrStaleSuggestion;
                    result.recoveryStatus = recovery.resolution_status;
                    result.finalResolutionStatus = recovery.resolution_status;
                }
                result.totalDurationMs = total.ElapsedMilliseconds;
                LogRecovery(logRequestId, safeRawTranscript, recovery, result.llmCalled, null, result.finalResolutionStatus);
                Debug.Log($"[NLP] request_id={logRequestId} resolution_source=suggestion llm_called={result.llmCalled} final_action=null");
                Completed?.Invoke(result);
                return result;
            }

            if (string.Equals(recovery.resolution_status, CommandRecoveryStatus.NeedsConfirmation, StringComparison.Ordinal))
            {
                recovery.resolution_status = CommandRecoveryStatus.NoRecovery;
            }

            result.recoveryStatus = recovery.resolution_status;
            result.finalResolutionStatus = FinalResolutionStatus(result.validated);
            if (result.llm != null)
            {
                result.llm.recovery_status = result.recoveryStatus;
                result.llm.final_resolution_status = result.finalResolutionStatus;
                result.llm.resolution_status = result.finalResolutionStatus;
            }
            result.totalDurationMs = total.ElapsedMilliseconds;
            LogRecovery(logRequestId, safeRawTranscript, recovery, result.llmCalled, result.validated?.action, result.finalResolutionStatus);

            Debug.Log(
                $"[NLP] request_id={logRequestId} spacy_duration={FormatMs(nlp?.spacy_duration_ms)} "
                + $"baseline_duration={FormatMs(nlp?.baseline_duration_ms)} action_resolution_duration={FormatMs(nlp?.action_resolution_duration_ms)} "
                + $"slot_extraction_duration={FormatMs(nlp?.slot_extraction_duration_ms)} llm_prompt_tokens={(llm != null ? llm.llm_prompt_tokens : 0)} "
                + $"llm_prompt_eval_duration={(llm != null ? llm.llm_prompt_eval_duration : 0)}ms "
                + $"llm_first_token_duration={(llm != null ? llm.llm_first_token_duration : 0)}ms "
                + $"llm_generation_duration={(llm != null ? llm.llm_generation_duration : 0)}ms "
                + $"llm_parse_duration={(llm != null ? llm.llm_parse_duration : 0)}ms "
                + $"validation_duration={(llm != null ? llm.validation_duration : 0)}ms "
                + $"llm_total_duration={(llm != null ? llm.llm_total_duration : 0)}ms total_nlp_duration={total.ElapsedMilliseconds}ms "
                + $"recovery_status={result.recoveryStatus} final_resolution_status={result.finalResolutionStatus} "
                + $"resolution_source={result.resolutionSource} llm_called={result.llmCalled}");
            Debug.Log($"[NLP] request_id={logRequestId} resolution_source={result.resolutionSource} llm_called={result.llmCalled} validated_command={ToJsonForLog(result.validated)}");
            Completed?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Runs the existing deterministic/recovery/evidence parser for N-best ranking.
        /// This deliberately never calls the LLM, clears/replaces suggestions, raises Completed,
        /// or advances the normal pipeline generation.
        /// </summary>
        public async Task<SpeechCommandCandidateProbe> EvaluateCandidateAsync(
            string transcript,
            string rawTranscript = "",
            CancellationToken cancellationToken = default)
        {
            Stopwatch total = Stopwatch.StartNew();
            string safeTranscript = transcript ?? string.Empty;
            string safeRaw = string.IsNullOrWhiteSpace(rawTranscript) ? safeTranscript : rawTranscript;
            string probeRequestId = "probe-" + Guid.NewGuid().ToString("N");
            string context = BuildAnalysisContextJson(ActiveConfigJson, safeRaw, probeRequestId, probeRequestId);
            string nlpJson = await analyzeAsync(safeTranscript, context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            NlpAnalysisResult nlp = SafeFromJson<NlpAnalysisResult>(nlpJson);
            ValidatedCommandResult baseline = DroneCommandJsonParser.ParseObjectForKey(nlpJson, "drone_command");
            if (nlp != null) nlp.drone_command = baseline;
            CommandRecoveryResult recovery = CommandRecoveryResult.From(nlp?.baseline_hints, safeTranscript);
            bool baselineFinal = nlp?.baseline_hints != null && nlp.baseline_hints.final_parser && baseline != null;
            bool evidence = HasAnyActionEvidence(nlp, recovery, baseline);
            string action = IntendedAction(baseline);
            bool missingInfo = string.Equals(baseline?.action, "MISSING_INFO", StringComparison.Ordinal);
            bool confirmation = string.Equals(recovery.resolution_status, CommandRecoveryStatus.NeedsConfirmation, StringComparison.Ordinal);

            int safetyRank;
            string reason;
            if (baselineFinal && !missingInfo && !string.IsNullOrWhiteSpace(action) && action != "UNKNOWN")
            {
                bool corrected = recovery.correction_applied
                    || string.Equals(recovery.resolution_status, CommandRecoveryStatus.ResolvedAutoCorrection, StringComparison.Ordinal);
                safetyRank = corrected ? 500 : 600;
                reason = corrected ? "AUTO_CORRECTION_RESOLVED" : "DETERMINISTIC_RESOLVED";
            }
            else if (missingInfo && evidence)
            {
                safetyRank = 400;
                reason = "SAFE_MISSING_INFO";
            }
            else if (confirmation)
            {
                safetyRank = 300;
                reason = "NEEDS_CONFIRMATION";
            }
            else
            {
                safetyRank = evidence ? 200 : 100;
                reason = evidence ? "ACTION_EVIDENCE_ONLY" : "UNKNOWN_OR_OUT_OF_DOMAIN";
            }

            return new SpeechCommandCandidateProbe
            {
                transcript = safeTranscript,
                nlp = nlp,
                recovery = recovery,
                deterministic = baseline,
                intendedAction = action,
                safetyRank = safetyRank,
                reason = reason,
                hasActionEvidence = evidence,
                evidenceStrength = nlp?.baseline_hints?.action_evidence?.supported_actions?.Length ?? 0,
                recoveryConfidence = recovery.correction_confidence,
                durationMs = total.ElapsedMilliseconds,
                llmCalled = false,
            };
        }

        public async Task<SuggestionDecisionResult> ConfirmSuggestionAsync(
            string suggestionId,
            CancellationToken cancellationToken = default)
        {
            if (!pendingSuggestions.TryConsume(suggestionId, out CommandSuggestion suggestion))
            {
                return InvalidSuggestionDecision(suggestionId);
            }

            SpeechCommandPipelineResult result = await ProcessTranscriptAsync(
                suggestion.suggested_command,
                cancellationToken,
                "confirmation:" + suggestion.suggestion_id,
                suggestion.suggested_command);
            cancellationToken.ThrowIfCancellationRequested();

            if (result == null
                || result.validated == null
                || string.Equals(result.recovery?.resolution_status, CommandRecoveryStatus.NeedsConfirmation, StringComparison.Ordinal))
            {
                return new SuggestionDecisionResult
                {
                    accepted = false,
                    suggestion_id = suggestionId ?? string.Empty,
                    resolution_status = CommandRecoveryStatus.InvalidOrStaleSuggestion,
                    message = "Öneri güvenli bir komuta çözümlenemedi; komutu tekrar söyleyin.",
                    pipeline_result = null,
                };
            }

            result.recovery.resolution_status = CommandRecoveryStatus.ResolvedAfterConfirmation;
            result.recovery.parser_source = "confirmed_deterministic";
            return new SuggestionDecisionResult
            {
                accepted = true,
                suggestion_id = suggestionId,
                resolution_status = CommandRecoveryStatus.ResolvedAfterConfirmation,
                message = result.validated.action == "MISSING_INFO"
                    ? result.validated.missing_message
                    : "Öneri doğrulandı.",
                pipeline_result = result,
            };
        }

        public SuggestionDecisionResult RejectSuggestion(string suggestionId)
        {
            if (!pendingSuggestions.Reject(suggestionId))
            {
                return InvalidSuggestionDecision(suggestionId);
            }

            return new SuggestionDecisionResult
            {
                accepted = false,
                suggestion_id = suggestionId ?? string.Empty,
                resolution_status = CommandRecoveryStatus.Rejected,
                message = "Öneri reddedildi; komutu tekrar söyleyin.",
                pipeline_result = null,
            };
        }

        public void ResetSuggestions()
        {
            Interlocked.Increment(ref processGeneration);
            pendingSuggestions.Clear();
        }

        private void OnEnable()
        {
            ResetSuggestions();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayModeStateChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnEditorPlayModeStateChanged;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayModeStateChanged;
#endif
            ResetSuggestions();
        }

#if UNITY_EDITOR
        private void OnEditorPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
        {
            ResetSuggestions();
        }
#endif

        private static SuggestionDecisionResult InvalidSuggestionDecision(string suggestionId)
        {
            return new SuggestionDecisionResult
            {
                accepted = false,
                suggestion_id = suggestionId ?? string.Empty,
                resolution_status = CommandRecoveryStatus.InvalidOrStaleSuggestion,
                message = "Öneri geçersiz veya artık güncel değil; komutu tekrar söyleyin.",
                pipeline_result = null,
            };
        }

        private static T SafeFromJson<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpeechCommandPipeline] JSON parse failed for " + typeof(T).Name + ": " + ex.Message);
                return null;
            }
        }

        private static string ToJsonForLog(ValidatedCommandResult command)
        {
            return DroneCommandJsonParser.ToPublicJson(command);
        }

        private static string FormatMs(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "ms" : "unavailable";
        }

        private static string BuildAnalysisContextJson(string configJson, string rawTranscript, string recoveryRequestKey, string requestId)
        {
            string source = string.IsNullOrWhiteSpace(configJson)
                ? SpeechCommandPipelineConfig.DefaultConfigJson
                : configJson.Trim();
            int end = source.LastIndexOf('}');
            if (!source.StartsWith("{", StringComparison.Ordinal) || end <= 0)
            {
                source = SpeechCommandPipelineConfig.DefaultConfigJson;
                end = source.LastIndexOf('}');
            }

            string prefix = source.Substring(0, end).TrimEnd();
            bool needsComma = prefix.Length > 1 && prefix[prefix.Length - 1] != '{';
            return prefix + (needsComma ? "," : string.Empty)
                + "\"raw_transcript\":\"" + EscapeJson(rawTranscript ?? string.Empty) + "\","
                + "\"recovery_request_key\":\"" + EscapeJson(recoveryRequestKey ?? string.Empty) + "\","
                + "\"request_id\":\"" + EscapeJson(requestId ?? string.Empty) + "\"}";
        }

        private static void LogRecovery(
            string requestId,
            string rawTranscript,
            CommandRecoveryResult recovery,
            bool llmCalled,
            string finalAction,
            string finalResolutionStatus)
        {
            if (recovery == null)
            {
                return;
            }

            Debug.Log(
                $"[CommandRecovery] request_id={requestId} raw_transcript=\"{EscapeJson(rawTranscript ?? string.Empty)}\" "
                + $"original_suffix=\"{EscapeJson(recovery.original_suffix)}\" normalized_suffix=\"{EscapeJson(recovery.normalized_suffix)}\" "
                + $"corrected_suffix=\"{EscapeJson(recovery.corrected_suffix)}\" correction_applied={recovery.correction_applied.ToString().ToLowerInvariant()} "
                + $"confidence={recovery.correction_confidence.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} "
                + $"reason={recovery.correction_reason} candidate_action={recovery.candidate_action} candidate_count={recovery.candidate_count} "
                + $"candidate_gap={recovery.candidate_gap.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} "
                + $"parser_input=\"{EscapeJson(recovery.parser_input)}\" parser_source={recovery.parser_source} "
                + $"recovery_status={recovery.resolution_status} final_resolution_status={finalResolutionStatus ?? "UNKNOWN"} "
                + $"llm_called={llmCalled.ToString().ToLowerInvariant()} final_action={finalAction ?? "null"}");
        }

        private static bool HasAnyActionEvidence(
            NlpAnalysisResult nlp,
            CommandRecoveryResult recovery,
            ValidatedCommandResult baseline)
        {
            ActionEvidenceSummary evidence = nlp?.baseline_hints?.action_evidence;
            if (evidence != null
                && (evidence.has_any
                    || (evidence.supported_actions?.Length ?? 0) > 0
                    || (evidence.reasons?.Length ?? 0) > 0
                    || !string.IsNullOrWhiteSpace(evidence.source_text)))
            {
                return evidence.has_any && evidence.supported_actions != null && evidence.supported_actions.Length > 0;
            }

            string deterministicAction = IntendedAction(baseline);
            if (!string.IsNullOrWhiteSpace(deterministicAction) && deterministicAction != "UNKNOWN")
            {
                return true;
            }

            return recovery != null && (recovery.candidate_count > 0 || (recovery.suggestions?.Length ?? 0) > 0);
        }

        private static bool IsActionSupportedByEvidence(
            ValidatedCommandResult command,
            NlpAnalysisResult nlp,
            CommandRecoveryResult recovery,
            ValidatedCommandResult baseline)
        {
            string action = IntendedAction(command);
            if (string.IsNullOrWhiteSpace(action) || action == "UNKNOWN")
            {
                return true;
            }

            ActionEvidenceSummary evidence = nlp?.baseline_hints?.action_evidence;
            if (evidence != null
                && (evidence.has_any
                    || (evidence.supported_actions?.Length ?? 0) > 0
                    || (evidence.reasons?.Length ?? 0) > 0
                    || !string.IsNullOrWhiteSpace(evidence.source_text)))
            {
                return evidence.Supports(action);
            }

            if (string.Equals(IntendedAction(baseline), action, StringComparison.Ordinal)
                || string.Equals(recovery?.candidate_action, action, StringComparison.Ordinal))
            {
                return true;
            }

            if (recovery?.suggestions != null)
            {
                foreach (CommandSuggestion suggestion in recovery.suggestions)
                {
                    if (string.Equals(suggestion?.suggested_action, action, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ShouldOfferRecovery(
            ValidatedCommandResult command,
            NlpAnalysisResult nlp,
            CommandRecoveryResult recovery,
            bool baselineFinal,
            bool llmAttempted)
        {
            if (baselineFinal
                || recovery?.suggestions == null
                || recovery.suggestions.Length == 0)
            {
                return false;
            }

            string action = command?.action ?? "UNKNOWN";
            bool unresolved = command == null
                || string.Equals(action, "UNKNOWN", StringComparison.Ordinal)
                || string.Equals(action, "MISSING_INFO", StringComparison.Ordinal);
            if (!unresolved)
            {
                return false;
            }

            string initialStatus = nlp?.resolution_status ?? string.Empty;
            bool genuinelyMissing = string.Equals(initialStatus, "MISSING_USER_INFO", StringComparison.Ordinal);
            return !genuinelyMissing && llmAttempted;
        }

        private static void PrioritizeRecoverySuggestions(CommandRecoveryResult recovery, string preferredAction)
        {
            if (recovery?.suggestions == null
                || recovery.suggestions.Length < 2
                || string.IsNullOrWhiteSpace(preferredAction)
                || string.Equals(preferredAction, "UNKNOWN", StringComparison.Ordinal))
            {
                return;
            }

            for (int i = 0; i < recovery.suggestions.Length; i++)
            {
                if (!string.Equals(recovery.suggestions[i]?.suggested_action, preferredAction, StringComparison.Ordinal))
                {
                    continue;
                }
                if (i > 0)
                {
                    CommandSuggestion preferred = recovery.suggestions[i];
                    Array.Copy(recovery.suggestions, 0, recovery.suggestions, 1, i);
                    recovery.suggestions[0] = preferred;
                }
                return;
            }
        }

        private static void ApplyEvidenceRejection(SpeechCommandPipelineResult result)
        {
            ValidatedCommandResult unknown = new ValidatedCommandResult { action = "UNKNOWN", confidence = 0d };
            result.validated = unknown;
            result.finalJson = DroneCommandJsonParser.ToPublicJson(unknown);
            result.resolutionSource = "evidence_gate";
            if (result.recovery != null)
            {
                result.recovery.resolution_status = CommandRecoveryStatus.Unknown;
            }
            if (result.llm != null)
            {
                result.llm.stage = "action_evidence_rejected";
                result.llm.resolution_source = "evidence_gate";
                result.llm.resolution_status = CommandRecoveryStatus.Unknown;
                result.llm.public_command = unknown;
                result.llm.validated_command = unknown;
                result.llm.normalized_command = unknown;
                result.llm.internal_validated_command = unknown;
            }
        }

        private static string IntendedAction(ValidatedCommandResult command)
        {
            if (command == null)
            {
                return string.Empty;
            }

            return string.Equals(command.action, "MISSING_INFO", StringComparison.Ordinal)
                ? command.target_action ?? string.Empty
                : command.action ?? string.Empty;
        }

        private static string FinalResolutionStatus(ValidatedCommandResult command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.action) || command.action == "UNKNOWN")
            {
                return CommandRecoveryStatus.Unknown;
            }

            return command.action == "MISSING_INFO" ? CommandRecoveryStatus.MissingInfo : "RESOLVED";
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }

    [Serializable]
    public sealed class SpeechCommandPipelineResult
    {
        public string transcript;
        public string configJson;
        public string activeLlmConfigJson;
        public string activeLlmConfigSummary;
        public string nlpJson;
        public string llmJson;
        public NlpAnalysisResult nlp;
        public LlmCommandResult llm;
        public CommandRecoveryResult recovery;
        public ValidatedCommandResult validated;
        public string finalJson;
        public string resolutionSource;
        public string recoveryStatus;
        public string finalResolutionStatus;
        public bool llmCalled;
        public long totalDurationMs;
    }

    [Serializable]
    public sealed class SpeechCommandCandidateProbe
    {
        public string transcript;
        public NlpAnalysisResult nlp;
        public CommandRecoveryResult recovery;
        public ValidatedCommandResult deterministic;
        public string intendedAction;
        public int safetyRank;
        public string reason;
        public bool hasActionEvidence;
        public int evidenceStrength;
        public double recoveryConfidence;
        public long durationMs;
        public bool llmCalled;
    }
}
