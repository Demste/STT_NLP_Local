using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

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
            string requestId = "")
        {
            string configJson = ActiveConfigJson;
            string safeTranscript = transcript ?? string.Empty;
            string logRequestId = string.IsNullOrWhiteSpace(requestId) ? "-" : requestId;

            Debug.Log($"[NLP] request_id={logRequestId} pipeline_input={safeTranscript}");
            Debug.Log($"[NLP] request_id={logRequestId} spacy_input={safeTranscript}");

            string nlpJson = await Android.SpacyNlpAndroidBridgeClient.AnalyzeAsync(
                safeTranscript,
                configJson,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Debug.Log($"[NLP] request_id={logRequestId} llm_input={safeTranscript}");
            string llmJson = await Android.OfflineLlmAndroidBridgeClient.ParseCommandAsync(
                safeTranscript,
                nlpJson,
                configJson,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SpeechCommandPipelineResult result = new SpeechCommandPipelineResult
            {
                transcript = safeTranscript,
                configJson = configJson,
                activeLlmConfigJson = ActiveLlmConfigJson,
                activeLlmConfigSummary = ActiveLlmConfigSummary,
                nlpJson = nlpJson,
                llmJson = llmJson,
                nlp = SafeFromJson<NlpAnalysisResult>(nlpJson),
                llm = SafeFromJson<LlmCommandResult>(llmJson),
            };

            result.validated = result.llm != null ? result.llm.validated_command : null;
            if (result.validated != null && result.llm != null)
            {
                if (string.IsNullOrWhiteSpace(result.validated.stage))
                {
                    result.validated.stage = result.llm.stage;
                }

                if (string.IsNullOrWhiteSpace(result.validated.error))
                {
                    result.validated.error = result.llm.error;
                }

                result.validated.inference_ran = result.validated.inference_ran || result.llm.inference_ran;
            }

            Debug.Log($"[NLP] request_id={logRequestId} validated_command={ToJsonForLog(result.validated)}");
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
            return command != null ? JsonUtility.ToJson(command) : "null";
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
    }
}
