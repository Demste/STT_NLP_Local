using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Game.NLP
{
    public sealed class SpeechCommandPipeline : MonoBehaviour
    {
        [SerializeField] private SpeechCommandPipelineConfig config = new SpeechCommandPipelineConfig();

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
            Stopwatch total = Stopwatch.StartNew();
            string configJson = ActiveConfigJson;
            string safeTranscript = transcript ?? string.Empty;
            string safeRawTranscript = string.IsNullOrWhiteSpace(rawTranscript) ? safeTranscript : rawTranscript;
            string analysisContextJson = BuildAnalysisContextJson(configJson, safeRawTranscript);
            string logRequestId = string.IsNullOrWhiteSpace(requestId) ? "-" : requestId;

            Debug.Log($"[NLP] request_id={logRequestId} wake_word_suffix={safeTranscript}");

            string nlpJson = await Android.SpacyNlpAndroidBridgeClient.AnalyzeAsync(
                safeTranscript,
                analysisContextJson,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            NlpAnalysisResult nlp = SafeFromJson<NlpAnalysisResult>(nlpJson);
            ValidatedCommandResult baseline = DroneCommandJsonParser.ParseObjectForKey(nlpJson, "drone_command");
            if (nlp != null) nlp.drone_command = baseline;
            bool baselineFinal = nlp != null
                && nlp.baseline_hints != null
                && nlp.baseline_hints.final_parser
                && baseline != null;
            string analysisInput = !string.IsNullOrWhiteSpace(nlp?.analysis_input) ? nlp.analysis_input : safeTranscript;
            Debug.Log($"[NLP] request_id={logRequestId} analysis_input={analysisInput} repair_applied={(nlp != null && nlp.repair_applied)} resolution_status={nlp?.resolution_status}");

            string llmJson;
            if (baselineFinal)
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
                Debug.Log($"[NLP] request_id={logRequestId} llm_called=false resolution_source=baseline");
            }
            else
            {
                Debug.Log($"[NLP] request_id={logRequestId} llm_called=true resolution_source=pending_llm llm_input={analysisInput}");
                llmJson = await Android.OfflineLlmAndroidBridgeClient.ParseCommandAsync(
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
                resolutionSource = baselineFinal ? "baseline" : (llm != null ? llm.resolution_source : "fallback"),
                llmCalled = !baselineFinal && llm != null && llm.llm_called,
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
            result.totalDurationMs = total.ElapsedMilliseconds;

            Debug.Log(
                $"[NLP] request_id={logRequestId} spacy_duration={FormatMs(nlp?.spacy_duration_ms)} "
                + $"baseline_duration={FormatMs(nlp?.baseline_duration_ms)} action_resolution_duration={FormatMs(nlp?.action_resolution_duration_ms)} "
                + $"slot_extraction_duration={FormatMs(nlp?.slot_extraction_duration_ms)} llm_prompt_tokens={(llm != null ? llm.llm_prompt_tokens : 0)} "
                + $"llm_prompt_eval_duration=unavailable llm_first_token_duration=unavailable "
                + $"llm_generation_duration={(llm != null ? llm.llm_generation_duration : 0)}ms validation_duration={(llm != null ? llm.validation_duration : 0)}ms "
                + $"resolution_source={result.resolutionSource} llm_called={result.llmCalled}");
            Debug.Log($"[NLP] request_id={logRequestId} resolution_source={result.resolutionSource} llm_called={result.llmCalled} validated_command={ToJsonForLog(result.validated)}");
            Completed?.Invoke(result);
            return result;
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

        private static string BuildAnalysisContextJson(string configJson, string rawTranscript)
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
                + "\"raw_transcript\":\"" + EscapeJson(rawTranscript ?? string.Empty) + "\"}";
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
        public ValidatedCommandResult validated;
        public string finalJson;
        public string resolutionSource;
        public bool llmCalled;
        public long totalDurationMs;
    }
}
