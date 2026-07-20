#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Game.STT;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Game.NLP.EditorTests
{
    public sealed class CommandRecoveryUiTests
    {
        private GameObject owner;
        private SpeechCommandRecoveryView view;
        private TMP_Text feedback;
        private TMP_Text debug;
        private TMP_Text jsonText;
        private TMP_Text statusText;

        [SetUp]
        public void SetUp()
        {
            owner = new GameObject("CommandRecoveryUiTests");
            owner.SetActive(false);
            view = BuildView(owner.transform, out feedback, out debug);
            owner.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void NeedsConfirmationShowsCandidatesAndPublishesNoFinalJson()
        {
            SpeechCommandPipelineResult result = PendingResult(Suggestion("one", "bekle"));

            view.Render(result);

            Assert.IsTrue(view.IsSuggestionPanelVisible);
            Assert.AreEqual(1, view.ActiveOptionCount);
            Assert.IsNull(result.finalJson);
            Assert.IsNull(result.validated);
        }

        [Test]
        public void CandidateClickUsesSuggestionIdInsteadOfIndex()
        {
            string selected = null;
            view.SuggestionSelected += value => selected = value;
            view.Render(PendingResult(Suggestion("first-id", "bekle"), Suggestion("second-id", "havada bekle")));

            ActiveOption("second-id").Button.onClick.Invoke();

            Assert.AreEqual("second-id", selected);
        }

        [Test]
        public void RejectCallbackCanClearUiAndProducesNoFinalCommand()
        {
            string rejected = null;
            view.SuggestionRejected += value =>
            {
                rejected = value;
                view.ShowRejected("Öneri reddedildi; komutu tekrar söyleyin.");
            };
            view.Render(PendingResult(Suggestion("reject-id", "bekle")));

            Button reject = owner.GetComponentsInChildren<Button>(true)
                .Single(value => value.gameObject.name == "RejectSuggestionButton");
            reject.onClick.Invoke();

            Assert.AreEqual("reject-id", rejected);
            Assert.IsFalse(view.IsSuggestionPanelVisible);
            Assert.AreEqual(0, view.ActiveOptionCount);
            StringAssert.Contains("tekrar söyleyin", view.UserFeedback);
        }

        [Test]
        public void NewResultClearsOldCandidateObjects()
        {
            view.Render(PendingResult(Suggestion("old-a", "bekle"), Suggestion("old-b", "havada bekle")));
            Assert.AreEqual(2, view.ActiveOptionCount);

            view.Render(ResolvedResult("STOP"));

            Assert.AreEqual(0, view.ActiveOptionCount);
            Assert.IsFalse(view.IsSuggestionPanelVisible);
            Assert.IsFalse(owner.GetComponentsInChildren<CommandSuggestionOptionView>(true)
                .Any(option => option.SuggestionId.StartsWith("old-", StringComparison.Ordinal)));
        }

        [Test]
        public void RapidDoubleClickStartsOnlyOneConfirmation()
        {
            int calls = 0;
            view.SuggestionSelected += _ => calls++;
            view.Render(PendingResult(Suggestion("double-id", "bekle")));
            Button button = ActiveOption("double-id").Button;

            button.onClick.Invoke();
            button.onClick.Invoke();

            Assert.AreEqual(1, calls);
            Assert.IsTrue(view.IsConfirmationBusy);
            Assert.IsFalse(button.interactable);
        }

        [Test]
        public void OutOfDomainUsesFriendlyMessageWithoutTechnicalStatus()
        {
            SpeechCommandPipelineResult result = ResolvedResult("UNKNOWN");
            result.recovery.resolution_status = "OUT_OF_DOMAIN";

            view.Render(result);

            Assert.AreEqual("Komut anlaşılamadı. Lütfen tekrar söyleyin.", feedback.text);
            StringAssert.DoesNotContain("OUT_OF_DOMAIN", feedback.text);
        }

        [Test]
        public async Task StaleConfirmationCannotOverwriteNewTranscriptUi()
        {
            SpeechCommandPipeline pipeline = owner.AddComponent<SpeechCommandPipeline>();
            STTTestController controller = owner.AddComponent<STTTestController>();
            jsonText = CreateText("NlpJson", owner.transform);
            statusText = CreateText("Status", owner.transform);
            SetField(controller, "speechCommandPipeline", pipeline);
            SetField(controller, "commandRecoveryView", view);
            SetField(controller, "nlpJsonResultText", jsonText);
            SetField(controller, "statusText", statusText);
            Invoke(controller, "OnDisable");
            Invoke(controller, "OnEnable");
            SetField(controller, "_activeRequestId", 1);
            SetField(controller, "_requestSequence", 1);
            SetField(controller, "_uiGeneration", 1);
            SetField(controller, "_lastCleanCommand", "ambiguous");

            TaskCompletionSource<bool> confirmationStarted = new TaskCompletionSource<bool>();
            TaskCompletionSource<string> delayedConfirmation = new TaskCompletionSource<string>();
            SetPipelineBackend(pipeline, (text, context, token) =>
            {
                if (text == "ambiguous")
                {
                    return Task.FromResult(PendingEnvelope());
                }

                confirmationStarted.TrySetResult(true);
                return delayedConfirmation.Task;
            });

            SpeechCommandPipelineResult pending = await pipeline.ProcessTranscriptAsync("ambiguous");
            view.Render(pending);
            Invoke(controller, "ConfirmSuggestion", "stale-id");
            Task startedOrTimeout = await Task.WhenAny(confirmationStarted.Task, Task.Delay(1000));
            Assert.AreSame(confirmationStarted.Task, startedOrTimeout, "Confirmation did not start.");

            Invoke(controller, "StartNewRequest");
            jsonText.text = "NEW_TRANSCRIPT_RESULT";
            delayedConfirmation.SetResult(ResolvedEnvelope("bekle", "STOP"));
            await Task.Delay(50);

            Assert.AreEqual("NEW_TRANSCRIPT_RESULT", jsonText.text);
            Assert.IsFalse(view.IsSuggestionPanelVisible);
            Assert.IsEmpty(view.UserFeedback);
        }

        [Test]
        public void TestSceneRecoveryUiHasCompleteReferencesAndNoMissingScripts()
        {
            UnityEngine.SceneManagement.Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/Test.unity", OpenSceneMode.Single);
            STTTestController controller = UnityEngine.Object.FindFirstObjectByType<STTTestController>();
            Assert.NotNull(controller);

            SerializedObject controllerObject = new SerializedObject(controller);
            SpeechCommandRecoveryView sceneView = controllerObject.FindProperty("commandRecoveryView").objectReferenceValue as SpeechCommandRecoveryView;
            Assert.NotNull(sceneView, "STTTestController.commandRecoveryView");

            SerializedObject viewObject = new SerializedObject(sceneView);
            string[] requiredViewReferences =
            {
                "correctionInfoPanel", "correctionInfoText", "suggestionPanel", "suggestionMessageText",
                "suggestionOptionsRoot", "suggestionOptionTemplate", "suggestionStatusText",
                "rejectSuggestionButton", "userFeedbackText", "recoveryDebugText",
            };
            foreach (string propertyName in requiredViewReferences)
            {
                Assert.NotNull(viewObject.FindProperty(propertyName).objectReferenceValue, propertyName);
            }

            CommandSuggestionOptionView template = viewObject.FindProperty("suggestionOptionTemplate").objectReferenceValue as CommandSuggestionOptionView;
            SerializedObject templateObject = new SerializedObject(template);
            Assert.NotNull(templateObject.FindProperty("button").objectReferenceValue, "suggestion template button");
            Assert.NotNull(templateObject.FindProperty("label").objectReferenceValue, "suggestion template label");

            int missingScripts = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Sum(transform => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject));
            Assert.AreEqual(0, missingScripts, "Test.unity contains Missing Script components.");
        }

        private CommandSuggestionOptionView ActiveOption(string id)
        {
            return owner.GetComponentsInChildren<CommandSuggestionOptionView>(true)
                .Single(option => option.gameObject.activeInHierarchy && option.SuggestionId == id);
        }

        private static SpeechCommandRecoveryView BuildView(Transform parent, out TMP_Text feedbackText, out TMP_Text debugText)
        {
            GameObject correctionPanel = Child("CorrectionInfoPanel", parent);
            TMP_Text correctionText = CreateText("CorrectionInfoText", correctionPanel.transform);
            GameObject suggestionPanel = Child("SuggestionPanel", parent);
            TMP_Text message = CreateText("SuggestionMessageText", suggestionPanel.transform);
            RectTransform options = Child("SuggestionOptions", suggestionPanel.transform).GetComponent<RectTransform>();

            GameObject templateObject = Child("SuggestionOptionTemplate", options);
            Button templateButton = templateObject.AddComponent<Button>();
            TMP_Text templateLabel = CreateText("Label", templateObject.transform);
            CommandSuggestionOptionView template = templateObject.AddComponent<CommandSuggestionOptionView>();
            template.Configure(templateButton, templateLabel);

            TMP_Text suggestionStatus = CreateText("SuggestionStatusText", suggestionPanel.transform);
            GameObject rejectObject = Child("RejectSuggestionButton", suggestionPanel.transform);
            Button reject = rejectObject.AddComponent<Button>();
            feedbackText = CreateText("UserFeedbackText", parent);
            debugText = CreateText("RecoveryDebugText", parent);

            SpeechCommandRecoveryView recoveryView = parent.gameObject.AddComponent<SpeechCommandRecoveryView>();
            recoveryView.Configure(
                correctionPanel,
                correctionText,
                suggestionPanel,
                message,
                options,
                template,
                suggestionStatus,
                reject,
                feedbackText,
                debugText);
            return recoveryView;
        }

        private static GameObject Child(string name, Transform parent)
        {
            GameObject child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            return child;
        }

        private static TMP_Text CreateText(string name, Transform parent)
        {
            GameObject child = Child(name, parent);
            return child.AddComponent<TextMeshProUGUI>();
        }

        private static CommandSuggestion Suggestion(string id, string command)
        {
            return new CommandSuggestion
            {
                suggestion_id = id,
                message = command + "?",
                suggested_command = command,
                suggested_action = "STOP",
                confidence = .81d,
            };
        }

        private static SpeechCommandPipelineResult PendingResult(params CommandSuggestion[] suggestions)
        {
            return new SpeechCommandPipelineResult
            {
                recovery = new CommandRecoveryResult
                {
                    resolution_status = CommandRecoveryStatus.NeedsConfirmation,
                    original_suffix = "ambiguous",
                    suggestions = suggestions,
                },
                finalJson = null,
                validated = null,
            };
        }

        private static SpeechCommandPipelineResult ResolvedResult(string action)
        {
            ValidatedCommandResult command = new ValidatedCommandResult { action = action, confidence = .9d };
            return new SpeechCommandPipelineResult
            {
                recovery = new CommandRecoveryResult
                {
                    resolution_status = CommandRecoveryStatus.ResolvedOriginal,
                    original_suffix = "bekle",
                    corrected_suffix = "bekle",
                    suggestions = Array.Empty<CommandSuggestion>(),
                },
                validated = command,
                finalJson = DroneCommandJsonParser.ToPublicJson(command),
            };
        }

        private static void SetPipelineBackend(
            SpeechCommandPipeline pipeline,
            Func<string, string, CancellationToken, Task<string>> analyze)
        {
            SetField(pipeline, "analyzeAsync", analyze);
            SetField(pipeline, "parseLlmAsync", new Func<string, string, string, CancellationToken, Task<string>>(
                (text, nlp, config, token) => Task.FromException<string>(new AssertionException("LLM must not be called."))));
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, fieldName);
            field.SetValue(target, value);
        }

        private static object Invoke(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, methodName);
            return method.Invoke(target, null);
        }

        private static object Invoke(object target, string methodName, object argument)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, methodName);
            return method.Invoke(target, new[] { argument });
        }

        private static string PendingEnvelope()
        {
            return "{\"ok\":true,\"analysis_input\":\"ambiguous\",\"resolution_status\":\"NEEDS_CONFIRMATION\","
                + "\"baseline_hints\":{\"final_parser\":true,\"resolution_status\":\"NEEDS_CONFIRMATION\","
                + "\"command_candidate\":{\"action\":\"UNKNOWN\",\"confidence\":0},"
                + "\"original_suffix\":\"ambiguous\",\"normalized_suffix\":\"ambiguous\","
                + "\"correction_applied\":false,\"recovery_resolution_status\":\"NEEDS_CONFIRMATION\","
                + "\"suggestions\":[{\"suggestion_id\":\"stale-id\",\"message\":\"bekle?\","
                + "\"suggested_command\":\"bekle\",\"suggested_action\":\"STOP\",\"confidence\":0.8}],"
                + "\"parser_input\":\"ambiguous\",\"parser_source\":\"deterministic_original\"},"
                + "\"drone_command\":{\"action\":\"UNKNOWN\",\"confidence\":0}}";
        }

        private static string ResolvedEnvelope(string input, string action)
        {
            string command = "{\"action\":\"" + action + "\",\"confidence\":0.98}";
            return "{\"ok\":true,\"analysis_input\":\"" + input + "\",\"resolution_status\":\"RESOLVED\","
                + "\"baseline_hints\":{\"final_parser\":true,\"resolution_status\":\"RESOLVED\","
                + "\"command_candidate\":" + command + ",\"original_suffix\":\"" + input + "\","
                + "\"normalized_suffix\":\"" + input + "\",\"correction_applied\":false,"
                + "\"recovery_resolution_status\":\"RESOLVED_ORIGINAL\",\"suggestions\":[],"
                + "\"parser_input\":\"" + input + "\",\"parser_source\":\"deterministic_original\"},"
                + "\"drone_command\":" + command + "}";
        }
    }
}
#endif
