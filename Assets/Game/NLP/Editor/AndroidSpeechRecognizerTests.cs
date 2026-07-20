#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Threading.Tasks;
using Game.STT;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.NLP.EditorTests
{
    public sealed class AndroidSpeechRecognizerTests
    {
        [Test]
        public void DefaultControllerModeIsAuto()
        {
            GameObject owner = new GameObject("AndroidSttDefaultMode");
            STTTestController controller = owner.AddComponent<STTTestController>();
            FieldInfo field = typeof(STTTestController).GetField("speechRecognitionMode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.AreEqual(AndroidSpeechRecognitionMode.AUTO, field.GetValue(controller));
            UnityEngine.Object.DestroyImmediate(owner);
        }

        [TestCase(-1f, false, null)]
        [TestCase(0.85f, true, "GOOD")]
        [TestCase(0.65f, true, "MEDIUM")]
        [TestCase(0.30f, true, "BAD")]
        public void AndroidConfidenceMapsWithoutInventingUnavailableValue(float confidence, bool available, string expected)
        {
            Assert.AreEqual(expected, AndroidSttConfidence.ToLevel(confidence, available));
        }

        [Test]
        public void AndroidConfidenceLevelDoesNotOverwriteCommandConfidence()
        {
            ValidatedCommandResult command = new ValidatedCommandResult { action = "LAND_HOME", confidence = 0.97 };
            command.stt_confidence_level = AndroidSttConfidence.ToLevel(0.30f, true);
            Assert.AreEqual("BAD", command.stt_confidence_level);
            Assert.AreEqual(0.97, command.confidence);
        }

        [Test]
        public async Task FirstUnknownSecondDeterministicSelectsSecond()
        {
            AndroidSpeechAlternative[] alternatives =
            {
                Alt("Kartal bugün hava güzel", 0, 0.91f),
                Alt("Kartal eve dön", 1, 0.84f),
            };
            AndroidSpeechCandidateSelection selected = await AndroidSpeechCandidateSelector.SelectAsync(
                alternatives,
                suffix => Task.FromResult(suffix == "eve dön" ? Probe(600, "DETERMINISTIC_RESOLVED") : Probe(100, "UNKNOWN_OR_OUT_OF_DOMAIN")));

            Assert.AreEqual("Kartal eve dön", selected.selectedTranscript);
            Assert.AreEqual(1, selected.selectedCandidateIndex);
            Assert.AreEqual("Kartal bugün hava güzel", selected.recognizerPrimaryTranscript);
        }

        [Test]
        public async Task DeterministicSafetyBeatsHigherAndroidConfidence()
        {
            AndroidSpeechCandidateSelection selected = await AndroidSpeechCandidateSelector.SelectAsync(
                new[] { Alt("Kartal anlamsız", 0, 0.98f), Alt("Kartal eve dön", 1, 0.45f) },
                suffix => Task.FromResult(suffix == "eve dön" ? Probe(600, "DETERMINISTIC_RESOLVED") : Probe(100, "UNKNOWN_OR_OUT_OF_DOMAIN")));

            Assert.AreEqual(1, selected.selectedCandidateIndex);
            Assert.AreEqual(0.45f, selected.selectedAndroidConfidence);
        }

        [Test]
        public async Task DirectCleanPhraseBeatsRecoveryForSameAction()
        {
            AndroidSpeechCandidateSelection selected = await AndroidSpeechCandidateSelector.SelectAsync(
                new[] { Alt("Kartal ev edin", 0, 0.89f), Alt("Kartal eve dön", 1, 0.84f) },
                suffix => Task.FromResult(suffix == "eve dön"
                    ? Probe(600, "DETERMINISTIC_RESOLVED", "LAND_HOME")
                    : Probe(500, "AUTO_CORRECTION_RESOLVED", "LAND_HOME")));

            Assert.AreEqual("Kartal eve dön", selected.selectedTranscript);
            Assert.AreEqual("DETERMINISTIC_RESOLVED", selected.candidateSelectionReason);
        }

        [Test]
        public async Task CandidateWithoutExactWakeWordCannotBeSelected()
        {
            int evaluations = 0;
            AndroidSpeechCandidateSelection selected = await AndroidSpeechCandidateSelector.SelectAsync(
                new[] { Alt("eve dön", 0, 0.99f), Alt("Kartal sağa git", 1, 0.60f) },
                suffix => { evaluations++; return Task.FromResult(Probe(400, "SAFE_MISSING_INFO")); });

            Assert.AreEqual(1, selected.selectedCandidateIndex);
            Assert.AreEqual(1, evaluations);
        }

        [Test]
        public async Task DuplicateHypothesesAreEvaluatedOnceAndAtMostFiveUnique()
        {
            int evaluations = 0;
            AndroidSpeechAlternative[] alternatives =
            {
                Alt("Kartal eve dön", 0, 0.8f), Alt("  KARTAL   EVE DÖN ", 1, 0.7f),
                Alt("Kartal sağa git", 2, 0.6f), Alt("Kartal sola git", 3, 0.6f),
                Alt("Kartal ileri git", 4, 0.6f), Alt("Kartal geri git", 5, 0.6f),
                Alt("Kartal yüksel", 6, 0.6f),
            };
            await AndroidSpeechCandidateSelector.SelectAsync(alternatives,
                _ => { evaluations++; return Task.FromResult(Probe(400, "SAFE_MISSING_INFO")); });
            Assert.AreEqual(5, evaluations);
        }

        [Test]
        public async Task RankingProbeContractNeverCallsLlm()
        {
            SpeechCommandCandidateProbe probe = Probe(600, "DETERMINISTIC_RESOLVED");
            AndroidSpeechCandidateSelection selected = await AndroidSpeechCandidateSelector.SelectAsync(
                new[] { Alt("Kartal eve dön", 0, -1f, false) }, _ => Task.FromResult(probe));

            Assert.IsFalse(selected.probe.llmCalled);
            Assert.IsFalse(selected.selectedAndroidConfidenceAvailable);
            Assert.AreEqual(-1f, selected.selectedAndroidConfidence);
        }

        [Test]
        public void JsonPayloadKeepsPrimaryAlternativesAndUnavailableConfidence()
        {
            const string json = "{\"session_id\":\"s1\",\"event_type\":\"FINAL\",\"final_result\":true,"
                + "\"alternatives\":[{\"text\":\"Kartal eve dön\",\"rank\":0,\"source_index\":0,"
                + "\"confidence\":-1,\"confidence_available\":false}]}";
            AndroidSpeechRecognitionEvent result = JsonUtility.FromJson<AndroidSpeechRecognitionEvent>(json);
            Assert.AreEqual("s1", result.session_id);
            Assert.AreEqual("Kartal eve dön", result.alternatives[0].text);
            Assert.AreEqual(-1f, result.alternatives[0].confidence);
            Assert.IsFalse(result.alternatives[0].confidence_available);
        }

        [TestCase(AndroidSpeechRecognitionMode.ONLINE)]
        [TestCase(AndroidSpeechRecognitionMode.OFFLINE)]
        public async Task FakeBridgeReceivesSerializedRequestedMode(AndroidSpeechRecognitionMode mode)
        {
            ModelWarmupCoordinator warmup = ReadyWarmup();
            await warmup.EnsureReadyAsync("{}", mode.ToString());
            GameObject owner = new GameObject("AndroidSttFakeMode");
            owner.SetActive(false);
            STTTestController controller = owner.AddComponent<STTTestController>();
            FakeSpeechClient fake = new FakeSpeechClient();
            SetField(controller, "_warmupCoordinator", warmup);
            SetField(controller, "_speechRecognizerClient", fake);
            SetField(controller, "speechRecognitionMode", mode);
            owner.SetActive(true);

            controller.BeginHoldRecording();

            Assert.AreEqual(1, fake.startCalls);
            Assert.AreEqual(mode, fake.lastMode);
            UnityEngine.Object.DestroyImmediate(owner);
        }

        [Test]
        public async Task RapidDoublePressStartsOnlyOneFakeSessionAndDisableCancelsIt()
        {
            ModelWarmupCoordinator warmup = ReadyWarmup();
            await warmup.EnsureReadyAsync("{}");
            GameObject owner = new GameObject("AndroidSttFakeRapid");
            owner.SetActive(false);
            STTTestController controller = owner.AddComponent<STTTestController>();
            FakeSpeechClient fake = new FakeSpeechClient();
            SetField(controller, "_warmupCoordinator", warmup);
            SetField(controller, "_speechRecognizerClient", fake);
            owner.SetActive(true);

            controller.BeginHoldRecording();
            controller.BeginHoldRecording();
            typeof(STTTestController).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, null);

            Assert.AreEqual(1, fake.startCalls);
            Assert.AreEqual(1, fake.cancelCalls);
            UnityEngine.Object.DestroyImmediate(owner);
        }

        [Test]
        public async Task PartialCallbackUpdatesTranscriptButDoesNotWriteNlpOutput()
        {
            ModelWarmupCoordinator warmup = ReadyWarmup();
            await warmup.EnsureReadyAsync("{}");
            GameObject owner = new GameObject("AndroidSttFakePartial");
            owner.SetActive(false);
            STTTestController controller = owner.AddComponent<STTTestController>();
            FakeSpeechClient fake = new FakeSpeechClient();
            TMP_Text transcript = new GameObject("Transcript", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            TMP_Text nlp = new GameObject("Nlp", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            transcript.transform.SetParent(owner.transform, false);
            nlp.transform.SetParent(owner.transform, false);
            SetField(controller, "_warmupCoordinator", warmup);
            SetField(controller, "_speechRecognizerClient", fake);
            SetField(controller, "transcriptText", transcript);
            SetField(controller, "nlpJsonResultText", nlp);
            owner.SetActive(true);
            controller.BeginHoldRecording();

            controller.OnAndroidSpeechRecognizerEvent(
                "{\"session_id\":\"" + fake.lastSessionId + "\",\"event_type\":\"PARTIAL\","
                + "\"alternatives\":[{\"text\":\"Kartal eve\",\"confidence\":-1,\"confidence_available\":false}]}");

            StringAssert.Contains("Kartal eve", transcript.text);
            Assert.AreEqual(string.Empty, nlp.text);
            UnityEngine.Object.DestroyImmediate(owner);
        }

        [Test]
        public void OnlineReadyUiDoesNotAskForOfflineLanguagePack()
        {
            GameObject owner = new GameObject("AndroidSttOnlineReadyUi");
            GameObject statusObject = new GameObject("Status", typeof(RectTransform));
            statusObject.transform.SetParent(owner.transform, false);
            TMP_Text status = statusObject.AddComponent<TextMeshProUGUI>();
            STTTestController controller = owner.AddComponent<STTTestController>();
            SetField(controller, "statusText", status);
            ModelWarmupSnapshot snapshot = new ModelWarmupSnapshot
            {
                state = ModelWarmupState.Ready,
                androidSpeechReady = true,
                onlineSttReady = true,
                onlineSupportStatus = "CHECK_UNAVAILABLE",
                selectedSttMode = "ONLINE",
                onDeviceServiceAvailable = true,
                offlineTurkishReady = false,
                offlineSttUsable = false,
                pythonReady = true,
                spacyReady = true,
                llmReady = true,
            };

            typeof(STTTestController).GetMethod("HandleWarmupProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { snapshot });

            StringAssert.Contains("Sistem hazır — Online", status.text);
            StringAssert.DoesNotContain("indir", status.text.ToLowerInvariant());
            UnityEngine.Object.DestroyImmediate(owner);
        }

        [Test]
        public void DuplicateGlobalStatusIsNotLoggedTwice()
        {
            GameObject owner = new GameObject("AndroidSttStatusDedup");
            owner.SetActive(false);
            STTTestController controller = owner.AddComponent<STTTestController>();
            MethodInfo setStatus = typeof(STTTestController).GetMethod(
                "SetStatus", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(setStatus);
            LogAssert.Expect(LogType.Log, "[STT] Sistem hazır — Online");
            setStatus.Invoke(controller, new object[] { "Sistem hazır — Online", 0 });
            setStatus.Invoke(controller, new object[] { "Sistem hazır — Online", 0 });
            LogAssert.NoUnexpectedReceived();
            UnityEngine.Object.DestroyImmediate(owner);
        }

        private static AndroidSpeechAlternative Alt(string text, int rank, float confidence, bool available = true)
        {
            return new AndroidSpeechAlternative
            {
                text = text,
                rank = rank,
                source_index = rank,
                confidence = confidence,
                confidence_available = available && confidence >= 0f,
            };
        }

        private static SpeechCommandCandidateProbe Probe(int rank, string reason, string action = "LAND_HOME")
        {
            return new SpeechCommandCandidateProbe
            {
                safetyRank = rank,
                reason = reason,
                intendedAction = action,
                hasActionEvidence = rank >= 200,
                evidenceStrength = rank >= 200 ? 1 : 0,
                recoveryConfidence = reason.Contains("CORRECTION", StringComparison.Ordinal) ? 0.9 : 0,
                llmCalled = false,
            };
        }

        private static ModelWarmupCoordinator ReadyWarmup()
        {
            const string ready = "{\"ok\":true,\"initialized\":true,\"stage\":\"ready\",\"durationMs\":1,\"online_available\":true}";
            return new ModelWarmupCoordinator(
                () => Task.FromResult(ready),
                () => Task.FromResult(ready),
                _ => Task.FromResult(ready));
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, name);
            field.SetValue(target, value);
        }

        private sealed class FakeSpeechClient : IAndroidSpeechRecognizerClient
        {
            public int startCalls;
            public int cancelCalls;
            public string lastSessionId;
            public AndroidSpeechRecognitionMode lastMode;

            public Task<string> PrepareSpeechRecognizerAsync(AndroidSpeechRecognitionMode mode,
                System.Threading.CancellationToken cancellationToken = default) => Task.FromResult("{\"ok\":true}");
            public void StartSpeechRecognition(string sessionId, AndroidSpeechRecognitionMode mode,
                string unityGameObjectName, string unityCallbackMethod)
            {
                startCalls++;
                lastSessionId = sessionId;
                lastMode = mode;
            }
            public void StopSpeechRecognition(string sessionId) { }
            public void CancelSpeechRecognition(string sessionId) { cancelCalls++; }
            public void DestroySpeechRecognizer() { }
        }
    }
}
#endif
