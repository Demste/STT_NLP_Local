#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Game.NLP.EditorTests
{
    public sealed class CommandEvidenceGateTests
    {
        private GameObject owner;
        private SpeechCommandPipeline pipeline;
        private int llmCalls;
        private string analysisJson;
        private string llmJson;

        [SetUp]
        public void SetUp()
        {
            llmCalls = 0;
            analysisJson = string.Empty;
            llmJson = string.Empty;
            owner = new GameObject("CommandEvidenceGateTests");
            pipeline = owner.AddComponent<SpeechCommandPipeline>();
            SetBackend();
        }

        [TearDown]
        public void TearDown()
        {
            if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
        }

        [Test]
        public async Task SadinHasNoEvidenceSkipsLlmAndReturnsSparseUnknown()
        {
            analysisJson = AnalysisEnvelope("sadın", false);
            llmJson = LlmEnvelope("MOVE_DIRECTION", "RIGHT", "distance_m");

            SpeechCommandPipelineResult result = await pipeline.ProcessTranscriptAsync("sadın");

            Assert.AreEqual(0, llmCalls);
            Assert.IsFalse(result.llmCalled);
            Assert.AreEqual("UNKNOWN", result.validated.action);
            Assert.IsNull(result.validated.target_action);
            Assert.IsNull(result.validated.missing_field);
            Assert.LessOrEqual(result.validated.confidence, .35d);
            Assert.AreEqual("UNKNOWN", result.finalResolutionStatus);
            Assert.AreEqual("evidence_gate", result.resolutionSource);
            StringAssert.Contains("\"target_action\":null", result.finalJson);
        }

        [Test]
        public async Task WeatherWithoutEvidenceSkipsLlm()
        {
            analysisJson = AnalysisEnvelope("bugün hava çok güzel", false);
            llmJson = LlmEnvelope("TAKE_OFF", null, null);

            SpeechCommandPipelineResult result = await pipeline.ProcessTranscriptAsync("bugün hava çok güzel");

            Assert.AreEqual(0, llmCalls);
            Assert.AreEqual("UNKNOWN", result.validated.action);
        }

        [Test]
        public async Task FakeLlmMoveDirectionIsRejectedWhenOnlyTurnEvidenceExists()
        {
            analysisJson = AnalysisEnvelope("belirsiz dönüş", true, "TURN");
            llmJson = LlmEnvelope("MOVE_DIRECTION", "RIGHT", "distance_m");

            SpeechCommandPipelineResult result = await pipeline.ProcessTranscriptAsync("belirsiz dönüş");

            Assert.AreEqual(1, llmCalls);
            Assert.IsTrue(result.llmCalled);
            Assert.AreEqual("UNKNOWN", result.validated.action);
            Assert.IsNull(result.validated.direction);
            Assert.IsNull(result.validated.target_action);
            Assert.IsNull(result.validated.missing_field);
            Assert.AreEqual("action_evidence_rejected", result.llm.stage);
            Assert.AreEqual("UNKNOWN", result.finalResolutionStatus);
        }

        [Test]
        public async Task MeaningfulAmbiguousInputMayUseLlmWhenActionIsSupported()
        {
            analysisJson = AnalysisEnvelope("sağ tarafa hareket", true, "MOVE_DIRECTION", "TURN");
            llmJson = LlmEnvelope("MOVE_DIRECTION", "RIGHT", "distance_m");

            SpeechCommandPipelineResult result = await pipeline.ProcessTranscriptAsync("sağ tarafa hareket");

            Assert.AreEqual(1, llmCalls);
            Assert.AreEqual("MISSING_INFO", result.validated.action);
            Assert.AreEqual("MOVE_DIRECTION", result.validated.target_action);
            Assert.AreEqual("RIGHT", result.validated.direction);
            Assert.AreEqual("distance_m", result.validated.missing_field);
            Assert.AreEqual("MISSING_INFO", result.finalResolutionStatus);
        }

        [Test]
        public async Task LlmCannotInventMissingInfoFromUnsupportedAction()
        {
            analysisJson = AnalysisEnvelope("sadın", true, "TURN");
            llmJson = LlmEnvelope("MOVE_DIRECTION", null, "direction");

            SpeechCommandPipelineResult result = await pipeline.ProcessTranscriptAsync("sadın");

            Assert.AreEqual(1, llmCalls);
            Assert.AreEqual("UNKNOWN", result.validated.action);
            Assert.IsNull(result.validated.target_action);
            Assert.IsNull(result.validated.missing_field);
        }

        private void SetBackend()
        {
            SetField("analyzeAsync", new Func<string, string, CancellationToken, Task<string>>(
                (text, context, token) => Task.FromResult(analysisJson)));
            SetField("parseLlmAsync", new Func<string, string, string, CancellationToken, Task<string>>(
                (text, nlp, config, token) =>
                {
                    llmCalls++;
                    return Task.FromResult(llmJson);
                }));
        }

        private void SetField(string name, object value)
        {
            FieldInfo field = typeof(SpeechCommandPipeline).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, name);
            field.SetValue(pipeline, value);
        }

        private static string AnalysisEnvelope(string input, bool hasEvidence, params string[] supported)
        {
            string actions = string.Join("\",\"", supported ?? Array.Empty<string>());
            string actionArray = supported != null && supported.Length > 0 ? "[\"" + actions + "\"]" : "[]";
            return "{\"ok\":true,\"analysis_input\":\"" + input + "\",\"resolution_status\":\"NEEDS_ACTION_CLASSIFICATION\","
                + "\"baseline_hints\":{\"final_parser\":false,\"resolution_status\":\"NEEDS_ACTION_CLASSIFICATION\","
                + "\"command_candidate\":{\"action\":\"UNKNOWN\",\"confidence\":0},"
                + "\"original_suffix\":\"" + input + "\",\"normalized_suffix\":\"" + input + "\","
                + "\"correction_applied\":false,\"correction_confidence\":0,\"correction_reason\":\"LOW_CONFIDENCE\","
                + "\"candidate_count\":0,\"recovery_resolution_status\":\"UNKNOWN\",\"suggestions\":[],"
                + "\"action_evidence\":{\"has_any\":" + hasEvidence.ToString().ToLowerInvariant() + ","
                + "\"supported_actions\":" + actionArray + ",\"reasons\":[],\"source_text\":\"" + input + "\"},"
                + "\"evidence_supported_actions\":" + actionArray + ","
                + "\"parser_input\":\"" + input + "\",\"parser_source\":\"deterministic_original\"},"
                + "\"drone_command\":{\"action\":\"UNKNOWN\",\"confidence\":0}}";
        }

        private static string LlmEnvelope(string intendedAction, string direction, string missingField)
        {
            string command;
            if (!string.IsNullOrWhiteSpace(missingField))
            {
                command = "{\"action\":\"MISSING_INFO\",\"target_action\":\"" + intendedAction + "\","
                    + (direction != null ? "\"direction\":\"" + direction + "\"," : string.Empty)
                    + "\"missing_field\":\"" + missingField + "\",\"missing_message\":\"Eksik\",\"confidence\":0.76}";
            }
            else
            {
                command = "{\"action\":\"" + intendedAction + "\",\"confidence\":0.76}";
            }

            string envelope = DroneCommandJsonParser.BuildBaselineEnvelope("input", "input", "input", "RESOLVED", false, command, 1);
            return envelope
                .Replace("\"stage\":\"baseline_resolved\"", "\"stage\":\"action_llm_validated\"")
                .Replace("\"llm_called\":false", "\"llm_called\":true")
                .Replace("\"inference_ran\":false", "\"inference_ran\":true")
                .Replace("\"resolution_source\":\"baseline\"", "\"resolution_source\":\"action_llm\"");
        }
    }
}
#endif
