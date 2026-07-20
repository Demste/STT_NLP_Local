#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Game.STT;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Game.NLP.EditorTests
{
    public sealed class ModelWarmupTests
    {
        private const string ReadyJson = "{\"ok\":true,\"initialized\":true,\"stage\":\"ready\",\"durationMs\":1}";
        private const string ReusedJson = "{\"ok\":true,\"initialized\":true,\"stage\":\"ready\",\"durationMs\":1,"
            + "\"context_reused\":true,\"python_runtime_reused\":true,\"spacy_model_reused\":true,\"model_reused\":true}";
        private const string FailedJson = "{\"ok\":false,\"initialized\":false,\"stage\":\"failed\",\"error\":\"forced failure\",\"durationMs\":1}";

        [Test]
        public async Task EnsureReadyTwiceInitializesEveryComponentOnce()
        {
            int androidStt = 0, python = 0, llm = 0;
            ModelWarmupCoordinator coordinator = Coordinator(
                () => { androidStt++; return Task.FromResult(ReadyJson); },
                () => { python++; return Task.FromResult(ReadyJson); },
                _ => { llm++; return Task.FromResult(ReadyJson); });

            Task<ModelWarmupSnapshot> first = coordinator.EnsureReadyAsync("{}");
            Task<ModelWarmupSnapshot> second = coordinator.EnsureReadyAsync("{}");
            ModelWarmupSnapshot result = await first;

            Assert.AreSame(first, second);
            Assert.AreEqual(ModelWarmupState.Ready, result.state);
            Assert.AreEqual(1, androidStt);
            Assert.AreEqual(1, python);
            Assert.AreEqual(1, llm);
        }

        [Test]
        public async Task ConcurrentCallersAwaitSameSharedTask()
        {
            TaskCompletionSource<string> androidSttGate = NewGate();
            ModelWarmupCoordinator coordinator = Coordinator(
                () => androidSttGate.Task,
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(ReadyJson));

            Task<ModelWarmupSnapshot> first = coordinator.EnsureReadyAsync("{}");
            Task<ModelWarmupSnapshot> second = coordinator.EnsureReadyAsync("{}");
            Assert.AreSame(first, second);
            Assert.AreEqual(ModelWarmupState.WarmingUp, coordinator.CurrentState);

            androidSttGate.SetResult(ReadyJson);
            Assert.AreEqual(ModelWarmupState.Ready, (await first).state);
        }

        [Test]
        public async Task HeavyInitializationRunsSequentiallyInRequiredOrder()
        {
            List<string> order = new List<string>();
            bool running = false;
            async Task<string> Step(string name)
            {
                Assert.IsFalse(running, "Warm-up components ran in parallel.");
                running = true;
                order.Add(name);
                await Task.Yield();
                running = false;
                return ReadyJson;
            }
            ModelWarmupCoordinator coordinator = Coordinator(
                () => Step("android_stt"),
                () => Step("python_spacy"),
                _ => Step("llm"));

            await coordinator.EnsureReadyAsync("{}");

            CollectionAssert.AreEqual(new[] { "android_stt", "python_spacy", "llm" }, order);
        }

        [Test]
        public async Task OnEnableAndNewTranscriptEquivalentCallsDoNotReloadModels()
        {
            int calls = 0;
            ModelWarmupCoordinator coordinator = Coordinator(
                () => { calls++; return Task.FromResult(ReadyJson); },
                () => { calls++; return Task.FromResult(ReadyJson); },
                _ => { calls++; return Task.FromResult(ReadyJson); });

            await coordinator.EnsureReadyAsync("{}");
            await coordinator.EnsureReadyAsync("{}");
            await coordinator.EnsureReadyAsync("{}");

            Assert.AreEqual(3, calls);
        }

        [Test]
        public async Task WarmupInProgressBlocksRecordWithVisibleStatus()
        {
            TaskCompletionSource<string> gate = NewGate();
            ModelWarmupCoordinator coordinator = Coordinator(
                () => gate.Task,
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(ReadyJson));
            coordinator.EnsureReadyAsync("{}");

            GameObject owner = new GameObject("WarmupRecordBlock");
            GameObject statusObject = new GameObject("Status", typeof(RectTransform));
            statusObject.transform.SetParent(owner.transform, false);
            TMP_Text status = statusObject.AddComponent<TextMeshProUGUI>();
            STTTestController controller = owner.AddComponent<STTTestController>();
            SetField(controller, "statusText", status);
            SetField(controller, "_warmupCoordinator", coordinator);

            controller.BeginHoldRecording();

            StringAssert.Contains("Android konuşma tanıma hazırlanıyor", status.text);
            gate.SetResult(ReadyJson);
            await coordinator.EnsureReadyAsync("{}");
            UnityEngine.Object.DestroyImmediate(owner);
        }

        [Test]
        public async Task RequiredComponentFailureIsExplicitAndCannotRecord()
        {
            ModelWarmupCoordinator coordinator = Coordinator(
                () => Task.FromResult(FailedJson),
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(ReadyJson));

            ModelWarmupSnapshot result = await coordinator.EnsureReadyAsync("{}");

            Assert.AreEqual(ModelWarmupState.Failed, result.state);
            Assert.IsFalse(result.CanRecord);
            Assert.AreEqual("android_stt", result.failureComponent);
            StringAssert.Contains("forced failure", result.failureMessage);
        }

        [Test]
        public async Task LlmFailureAllowsExplicitPartialReadyDeterministicMode()
        {
            ModelWarmupCoordinator coordinator = Coordinator(
                () => Task.FromResult(ReadyJson),
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(FailedJson));

            ModelWarmupSnapshot result = await coordinator.EnsureReadyAsync("{}");

            Assert.AreEqual(ModelWarmupState.PartiallyReady, result.state);
            Assert.IsTrue(result.CanRecord);
            Assert.IsFalse(result.llmReady);
            Assert.AreEqual("llm", result.failureComponent);
        }

        [Test]
        public async Task RetryAfterPartialFailureRetriesOnlyFailedLlmOnce()
        {
            int androidStt = 0, python = 0, llm = 0;
            ModelWarmupCoordinator coordinator = Coordinator(
                () => { androidStt++; return Task.FromResult(ReadyJson); },
                () => { python++; return Task.FromResult(ReadyJson); },
                _ => { llm++; return Task.FromResult(llm == 1 ? FailedJson : ReadyJson); });

            Assert.AreEqual(ModelWarmupState.PartiallyReady, (await coordinator.EnsureReadyAsync("{}")).state);
            Assert.AreEqual(ModelWarmupState.Ready, (await coordinator.RetryOnceAsync("{}")).state);
            await coordinator.RetryOnceAsync("{}");

            Assert.AreEqual(1, androidStt);
            Assert.AreEqual(1, python);
            Assert.AreEqual(2, llm);
        }

        [Test]
        public async Task CallerCancellationDoesNotCancelSharedApplicationWarmup()
        {
            TaskCompletionSource<string> gate = NewGate();
            ModelWarmupCoordinator coordinator = Coordinator(
                () => gate.Task,
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(ReadyJson));
            Task<ModelWarmupSnapshot> shared = coordinator.EnsureReadyAsync("{}");

            Task caller = Task.Delay(1);
            await caller;
            Assert.IsFalse(shared.IsCanceled);
            gate.SetResult(ReadyJson);

            Assert.AreEqual(ModelWarmupState.Ready, (await shared).state);
        }

        [Test]
        public async Task BridgeReuseMetadataIsExposedBySnapshot()
        {
            ModelWarmupCoordinator coordinator = Coordinator(
                () => Task.FromResult(ReusedJson),
                () => Task.FromResult(ReusedJson),
                _ => Task.FromResult(ReusedJson));

            ModelWarmupSnapshot result = await coordinator.EnsureReadyAsync("{}");

            Assert.IsFalse(result.whisperReady);
            Assert.IsFalse(result.whisperContextReused);
            Assert.IsTrue(result.pythonRuntimeReused);
            Assert.IsTrue(result.spacyModelReused);
            Assert.IsTrue(result.llmModelReused);
        }

        [Test]
        public async Task ActiveWarmupPreparesAndroidAndNeverMarksWhisperInitialized()
        {
            string requestedMode = null;
            const string support = "{\"ok\":true,\"initialized\":true,\"stage\":\"android_speech_recognizer_ready\","
                + "\"online_available\":true,\"on_device_service_available\":true,\"tr_tr_on_device_ready\":false,"
                + "\"model_download_requested\":true,\"durationMs\":1}";
            ModelWarmupCoordinator coordinator = new ModelWarmupCoordinator(
                mode => { requestedMode = mode; return Task.FromResult(support); },
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(ReadyJson));

            ModelWarmupSnapshot result = await coordinator.EnsureReadyAsync("{}", "AUTO");

            Assert.AreEqual("AUTO", requestedMode);
            Assert.IsTrue(result.androidSpeechReady);
            Assert.IsTrue(result.onlineSttReady);
            Assert.IsTrue(result.onDeviceServiceAvailable);
            Assert.IsFalse(result.offlineTurkishReady);
            Assert.IsTrue(result.modelDownloadRequested);
            Assert.IsFalse(result.whisperReady);
            Assert.AreEqual(0, result.whisperDurationMs);
        }

        [Test]
        public async Task CannotCheckSupportWithStandardServiceKeepsAutoWarmupReady()
        {
            const string supportUnknown = "{\"ok\":true,\"initialized\":true,"
                + "\"stage\":\"android_speech_recognizer_ready\",\"standard_service_available\":true,"
                + "\"online_support_status\":\"CHECK_UNAVAILABLE\",\"online_usable\":true,"
                + "\"online_available\":true,\"on_device_service_available\":true,"
                + "\"on_device_support_status\":\"UNAVAILABLE\",\"tr_tr_on_device_ready\":false,"
                + "\"offline_usable\":false,\"selected_mode\":\"ONLINE\",\"warmup_success\":true,"
                + "\"model_download_requested\":false,\"durationMs\":1,\"error\":\"\"}";
            ModelWarmupCoordinator coordinator = new ModelWarmupCoordinator(
                _ => Task.FromResult(supportUnknown),
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(ReadyJson));

            ModelWarmupSnapshot result = await coordinator.EnsureReadyAsync("{}", "AUTO");

            Assert.AreEqual(ModelWarmupState.Ready, result.state);
            Assert.IsTrue(result.CanRecord);
            Assert.IsTrue(result.onlineSttReady);
            Assert.AreEqual("CHECK_UNAVAILABLE", result.onlineSupportStatus);
            Assert.AreEqual("ONLINE", result.selectedSttMode);
            Assert.IsFalse(result.offlineTurkishReady);
            Assert.IsFalse(result.modelDownloadRequested);
            Assert.AreEqual(string.Empty, result.failureMessage);
        }

        [Test]
        public async Task AutoStandardServiceIgnoresUnknownNetworkAndOfflineDownloadFailure()
        {
            const string support = "{\"ok\":true,\"initialized\":true,"
                + "\"stage\":\"android_speech_recognizer_ready\",\"requested_mode\":\"AUTO\","
                + "\"standard_service_available\":true,\"network_state\":\"UNKNOWN\","
                + "\"network_validated\":\"unknown\",\"online_usable\":true,"
                + "\"on_device_service_available\":true,\"tr_tr_on_device_ready\":false,"
                + "\"offline_usable\":false,\"offline_model_status\":\"FAILED\","
                + "\"selected_mode\":\"ONLINE\",\"selection_reason\":\"STANDARD_SERVICE_AVAILABLE\","
                + "\"model_download_requested\":true,\"warmup_success\":true,\"durationMs\":1}";
            ModelWarmupCoordinator coordinator = new ModelWarmupCoordinator(
                _ => Task.FromResult(support),
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(ReadyJson));

            ModelWarmupSnapshot result = await coordinator.EnsureReadyAsync("{}", "AUTO");

            Assert.AreEqual(ModelWarmupState.Ready, result.state);
            Assert.IsTrue(result.CanRecord);
            Assert.AreEqual("UNKNOWN", result.networkState);
            Assert.AreEqual("unknown", result.networkValidated);
            Assert.AreEqual("FAILED", result.offlineModelStatus);
            Assert.AreEqual("ONLINE", result.selectedSttMode);
            Assert.AreEqual("STANDARD_SERVICE_AVAILABLE", result.sttSelectionReason);
        }

        [Test]
        public async Task GenuineAndroidSttFailureSkipsHeavyWarmups()
        {
            int pythonCalls = 0;
            int llmCalls = 0;
            ModelWarmupCoordinator coordinator = Coordinator(
                () => Task.FromResult(FailedJson),
                () => { pythonCalls++; return Task.FromResult(ReadyJson); },
                _ => { llmCalls++; return Task.FromResult(ReadyJson); });

            ModelWarmupSnapshot result = await coordinator.EnsureReadyAsync("{}");

            Assert.AreEqual(ModelWarmupState.Failed, result.state);
            Assert.AreEqual(0, pythonCalls);
            Assert.AreEqual(0, llmCalls);
        }

        private static ModelWarmupCoordinator Coordinator(
            Func<Task<string>> whisper,
            Func<Task<string>> python,
            Func<string, Task<string>> llm)
        {
            return new ModelWarmupCoordinator(whisper, python, llm);
        }

        private static TaskCompletionSource<string> NewGate()
        {
            return new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, name);
            field.SetValue(target, value);
        }
    }
}
#endif
