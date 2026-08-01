using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.NLP.EditorTests
{
    public sealed class CommandRecoveryTests
    {
        private GameObject owner;
        private SpeechCommandPipeline pipeline;
        private readonly List<string> analyzedInputs = new List<string>();

        [SetUp]
        public void SetUp()
        {
            owner = new GameObject("CommandRecoveryTests.Pipeline");
            pipeline = owner.AddComponent<SpeechCommandPipeline>();
            SetBackend(FakeAnalyzeAsync);
        }

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
            analyzedInputs.Clear();
        }

        [Test]
        public async Task ValidConfirmationRerunsPipelineAndProducesValidatedFinalCommand()
        {
            SpeechCommandPipelineResult pending = await SeedPendingAsync("ambiguous", "batch-one-stop");
            Assert.IsNull(pending.finalJson);
            Assert.IsNull(pending.validated);

            SuggestionDecisionResult decision = await pipeline.ConfirmSuggestionAsync("batch-one-stop");

            Assert.IsTrue(decision.accepted);
            Assert.AreEqual(CommandRecoveryStatus.ResolvedAfterConfirmation, decision.resolution_status);
            Assert.AreEqual("STOP", decision.final_command.action);
            Assert.IsNotEmpty(decision.final_json);
            CollectionAssert.AreEqual(new[] { "ambiguous", "bekle" }, analyzedInputs);
        }

        [Test]
        public async Task RejectionProducesNoFinalCommand()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");

            SuggestionDecisionResult decision = pipeline.RejectSuggestion("batch-one-stop");

            Assert.IsFalse(decision.accepted);
            Assert.AreEqual(CommandRecoveryStatus.Rejected, decision.resolution_status);
            Assert.IsNull(decision.final_command);
            Assert.IsNull(decision.final_json);
            CollectionAssert.AreEqual(new[] { "ambiguous" }, analyzedInputs);
        }

        [Test]
        public void ExpiredSuggestionIdIsStale()
        {
            PendingCommandSuggestionStore store = new PendingCommandSuggestionStore(TimeSpan.FromMilliseconds(1));
            store.ReplaceBatch("old", new[] { Suggestion("old-id", "bekle", "STOP") });
            Thread.Sleep(10);

            Assert.IsFalse(store.TryConsume("old-id", out _));
            Assert.AreEqual(0, store.Count);
        }

        [Test]
        public async Task UnknownSuggestionIdIsRejectedSafely()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");

            SuggestionDecisionResult decision = await pipeline.ConfirmSuggestionAsync("not-a-member");

            Assert.IsFalse(decision.accepted);
            Assert.AreEqual(CommandRecoveryStatus.InvalidOrStaleSuggestion, decision.resolution_status);
            Assert.IsNull(decision.final_command);
            CollectionAssert.AreEqual(new[] { "ambiguous" }, analyzedInputs);
        }

        [Test]
        public async Task NewTranscriptInvalidatesOldBatch()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");
            SpeechCommandPipelineResult replacement = await pipeline.ProcessTranscriptAsync("eve dön");
            Assert.AreEqual("LAND_HOME", replacement.validated.action);

            SuggestionDecisionResult stale = await pipeline.ConfirmSuggestionAsync("batch-one-stop");

            Assert.IsFalse(stale.accepted);
            Assert.AreEqual(CommandRecoveryStatus.InvalidOrStaleSuggestion, stale.resolution_status);
            Assert.IsNull(stale.final_command);
        }

        [Test]
        public async Task ConfirmationConsumesWholeBatch()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");

            SuggestionDecisionResult first = await pipeline.ConfirmSuggestionAsync("batch-one-stop");
            SuggestionDecisionResult sibling = await pipeline.ConfirmSuggestionAsync("batch-one-hover");

            Assert.IsTrue(first.accepted);
            Assert.IsFalse(sibling.accepted);
            Assert.AreEqual(CommandRecoveryStatus.InvalidOrStaleSuggestion, sibling.resolution_status);
        }

        [Test]
        public async Task RejectionClearsWholeBatch()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");

            Assert.AreEqual(CommandRecoveryStatus.Rejected, pipeline.RejectSuggestion("batch-one-stop").resolution_status);
            SuggestionDecisionResult sibling = await pipeline.ConfirmSuggestionAsync("batch-one-hover");

            Assert.IsFalse(sibling.accepted);
            Assert.AreEqual(CommandRecoveryStatus.InvalidOrStaleSuggestion, sibling.resolution_status);
        }

        [Test]
        public async Task SecondConfirmationOfSameIdFails()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");

            SuggestionDecisionResult first = await pipeline.ConfirmSuggestionAsync("batch-one-stop");
            SuggestionDecisionResult second = await pipeline.ConfirmSuggestionAsync("batch-one-stop");

            Assert.IsTrue(first.accepted);
            Assert.IsFalse(second.accepted);
            Assert.AreEqual(CommandRecoveryStatus.InvalidOrStaleSuggestion, second.resolution_status);
        }

        [Test]
        public async Task CancellationAfterConsumptionProducesNoFinalCommand()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await pipeline.ConfirmSuggestionAsync("batch-one-stop", cancellation.Token));

            SuggestionDecisionResult retry = await pipeline.ConfirmSuggestionAsync("batch-one-stop");
            Assert.IsFalse(retry.accepted);
            Assert.IsNull(retry.final_command);
        }

        [Test]
        public async Task ConcurrentDoubleConfirmationCanSucceedOnlyOnce()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");

            SuggestionDecisionResult[] results = await Task.WhenAll(
                pipeline.ConfirmSuggestionAsync("batch-one-stop"),
                pipeline.ConfirmSuggestionAsync("batch-one-stop"));

            Assert.AreEqual(1, results.Count(result => result.accepted));
            Assert.AreEqual(1, results.Count(result => !result.accepted));
            Assert.AreEqual(1, analyzedInputs.Count(value => value == "bekle"));
        }

        [Test]
        public async Task DisableEnableAndExplicitResetLeaveNoPendingState()
        {
            await SeedPendingAsync("ambiguous", "batch-one-stop");
            InvokeLifecycle("OnDisable");
            InvokeLifecycle("OnEnable");
            Assert.AreEqual(CommandRecoveryStatus.InvalidOrStaleSuggestion,
                (await pipeline.ConfirmSuggestionAsync("batch-one-stop")).resolution_status);

            await SeedPendingAsync("ambiguous-two", "batch-two-stop");
            pipeline.ResetSuggestions();
            Assert.AreEqual(CommandRecoveryStatus.InvalidOrStaleSuggestion,
                (await pipeline.ConfirmSuggestionAsync("batch-two-stop")).resolution_status);

            await SeedPendingAsync("ambiguous", "batch-one-stop");
            InvokeEditorPlayModeChange(PlayModeStateChange.ExitingEditMode);
            Assert.AreEqual(CommandRecoveryStatus.InvalidOrStaleSuggestion,
                (await pipeline.ConfirmSuggestionAsync("batch-one-stop")).resolution_status);
        }

        [Test]
        public async Task NeedsConfirmationPublishesNoFinalJsonAndNoValidatedCommand()
        {
            SpeechCommandPipelineResult pending = await SeedPendingAsync("ambiguous", "batch-one-stop");

            Assert.AreEqual(CommandRecoveryStatus.NeedsConfirmation, pending.recovery.resolution_status);
            Assert.IsNull(pending.finalJson);
            Assert.IsNull(pending.validated);
            Assert.IsTrue(pending.llmCalled);
        }

        [Test]
        public void CanonicalJsonHasExactly21FieldsAndNullMissingNumbersUnderUnitySerializer()
        {
            CommandRecoveryResult recovery = new CommandRecoveryResult
            {
                resolution_status = CommandRecoveryStatus.NeedsConfirmation,
                suggestions = new[] { Suggestion("id", "bekle", "STOP") },
            };
            string recoveryJson = JsonUtility.ToJson(recovery);
            StringAssert.Contains("suggestions", recoveryJson);

            string finalJson = DroneCommandJsonParser.ToPublicJson(new ValidatedCommandResult
            {
                action = "MISSING_INFO",
                direction = "LEFT",
                target_action = "MOVE_DIRECTION",
                missing_field = "distance_m",
                missing_message = "Yatay hareket komutu için metre bilgisi eksik.",
                confidence = .96d,
            });
            ValidatedCommandResult unityRoundTrip = JsonUtility.FromJson<ValidatedCommandResult>(finalJson);
            ValidatedCommandResult contractRoundTrip = DroneCommandJsonParser.ParseObjectForKey(
                "{\"public_command\":" + finalJson + "}", "public_command");

            Assert.AreEqual(21, CountTopLevelFields(finalJson));
            StringAssert.Contains("\"distance_m\":null", finalJson);
            StringAssert.Contains("\"angle_deg\":null", finalJson);
            StringAssert.DoesNotContain("suggestion", finalJson);
            StringAssert.DoesNotContain("correction_", finalJson);
            Assert.IsNull(unityRoundTrip.distance_m);
            Assert.IsNull(unityRoundTrip.angle_deg);
            Assert.IsNull(contractRoundTrip.distance_m);
            Assert.IsNull(contractRoundTrip.angle_deg);
            Assert.AreEqual("LEFT", contractRoundTrip.direction);
        }

        private async Task<SpeechCommandPipelineResult> SeedPendingAsync(string transcript, string expectedId)
        {
            SpeechCommandPipelineResult result = await pipeline.ProcessTranscriptAsync(transcript);
            Assert.AreEqual(CommandRecoveryStatus.NeedsConfirmation, result.recovery.resolution_status);
            Assert.IsTrue(result.recovery.suggestions.Any(value => value.suggestion_id == expectedId));
            return result;
        }

        private Task<string> FakeAnalyzeAsync(string text, string contextJson, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            analyzedInputs.Add(text);
            if (text == "ambiguous")
            {
                return Task.FromResult(SuggestionEnvelope(
                    text,
                    Suggestion("batch-one-stop", "bekle", "STOP"),
                    Suggestion("batch-one-hover", "havada bekle", "HOVER")));
            }
            if (text == "ambiguous-two")
            {
                return Task.FromResult(SuggestionEnvelope(
                    text,
                    Suggestion("batch-two-stop", "bekle", "STOP")));
            }
            if (text == "bekle")
            {
                return Task.FromResult(ResolvedEnvelope(text, "STOP"));
            }
            if (text == "havada bekle")
            {
                return Task.FromResult(ResolvedEnvelope(text, "HOVER"));
            }
            if (text == "eve dön")
            {
                return Task.FromResult(ResolvedEnvelope(text, "LAND_HOME"));
            }
            return Task.FromResult(ResolvedEnvelope(text, "UNKNOWN"));
        }

        private void SetBackend(Func<string, string, CancellationToken, Task<string>> analyze)
        {
            FieldInfo analyzeField = typeof(SpeechCommandPipeline).GetField("analyzeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo llmField = typeof(SpeechCommandPipeline).GetField("parseLlmAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(analyzeField);
            Assert.NotNull(llmField);
            analyzeField.SetValue(pipeline, analyze);
            llmField.SetValue(pipeline, new Func<string, string, string, CancellationToken, Task<string>>(
                (text, nlp, config, token) => Task.FromResult(UnknownLlmEnvelope())));
        }

        private void InvokeLifecycle(string methodName)
        {
            MethodInfo method = typeof(SpeechCommandPipeline).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(pipeline, null);
        }

        private void InvokeEditorPlayModeChange(PlayModeStateChange state)
        {
            MethodInfo method = typeof(SpeechCommandPipeline).GetMethod(
                "OnEditorPlayModeStateChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(pipeline, new object[] { state });
        }

        private static string SuggestionEnvelope(string input, params CommandSuggestion[] suggestions)
        {
            string suggestionsJson = string.Join(",", suggestions.Select(SuggestionJson));
            return "{\"ok\":true,\"analysis_input\":\"" + input + "\",\"resolution_status\":\"NEEDS_CONFIRMATION\","
                + "\"baseline_hints\":{\"final_parser\":false,\"resolution_status\":\"NEEDS_ACTION_CLASSIFICATION\","
                + "\"command_candidate\":{\"action\":\"UNKNOWN\",\"confidence\":0},"
                + "\"original_suffix\":\"" + input + "\",\"normalized_suffix\":\"" + input + "\","
                + "\"correction_applied\":false,\"correction_confidence\":0.8,"
                + "\"correction_reason\":\"MULTIPLE_CLOSE_CANDIDATES\",\"candidate_count\":" + suggestions.Length + ","
                + "\"recovery_resolution_status\":\"NEEDS_CONFIRMATION\",\"suggestions\":[" + suggestionsJson + "],"
                + "\"parser_input\":\"" + input + "\",\"parser_source\":\"deterministic_original\"},"
                + "\"drone_command\":{\"action\":\"UNKNOWN\",\"confidence\":0}}";
        }

        private static string UnknownLlmEnvelope()
        {
            string envelope = DroneCommandJsonParser.BuildBaselineEnvelope(
                "ambiguous", "ambiguous", "ambiguous", "UNKNOWN", false,
                "{\"action\":\"UNKNOWN\",\"confidence\":0}", 1);
            return envelope
                .Replace("\"stage\":\"baseline_resolved\"", "\"stage\":\"semantic_llm_validated\"")
                .Replace("\"llm_called\":false", "\"llm_called\":true")
                .Replace("\"inference_ran\":false", "\"inference_ran\":true")
                .Replace("\"resolution_source\":\"baseline\"", "\"resolution_source\":\"semantic_llm\"");
        }

        private static string ResolvedEnvelope(string input, string action)
        {
            double confidence = action == "UNKNOWN" ? 0d : .98d;
            string command = "{\"action\":\"" + action + "\",\"confidence\":" + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            return "{\"ok\":true,\"analysis_input\":\"" + input + "\",\"resolution_status\":\"RESOLVED\","
                + "\"baseline_hints\":{\"final_parser\":true,\"resolution_status\":\"RESOLVED\","
                + "\"command_candidate\":" + command + ",\"original_suffix\":\"" + input + "\","
                + "\"normalized_suffix\":\"" + input + "\",\"correction_applied\":false,"
                + "\"correction_reason\":\"NONE\",\"recovery_resolution_status\":\"RESOLVED_ORIGINAL\","
                + "\"suggestions\":[],\"parser_input\":\"" + input + "\",\"parser_source\":\"deterministic_original\"},"
                + "\"drone_command\":" + command + "}";
        }

        private static string SuggestionJson(CommandSuggestion suggestion)
        {
            return "{\"suggestion_id\":\"" + suggestion.suggestion_id + "\","
                + "\"message\":\"" + suggestion.message + "\","
                + "\"suggested_command\":\"" + suggestion.suggested_command + "\","
                + "\"suggested_action\":\"" + suggestion.suggested_action + "\","
                + "\"confidence\":" + suggestion.confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                + "\"reason\":\"" + suggestion.reason + "\"}";
        }

        private static CommandSuggestion Suggestion(string id, string command, string action)
        {
            return new CommandSuggestion
            {
                suggestion_id = id,
                suggested_command = command,
                suggested_action = action,
                confidence = .8,
                reason = "MULTIPLE_CLOSE_CANDIDATES",
                message = command + "?",
            };
        }

        private static int CountTopLevelFields(string json)
        {
            int count = 0;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            foreach (char c in json)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ':' && depth == 1) count++;
            }
            return count;
        }
    }
}
