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
            int whisper = 0, python = 0, llm = 0;
            ModelWarmupCoordinator coordinator = Coordinator(
                () => { whisper++; return Task.FromResult(ReadyJson); },
                () => { python++; return Task.FromResult(ReadyJson); },
                _ => { llm++; return Task.FromResult(ReadyJson); });

            Task<ModelWarmupSnapshot> first = coordinator.EnsureReadyAsync("{}");
            Task<ModelWarmupSnapshot> second = coordinator.EnsureReadyAsync("{}");
            ModelWarmupSnapshot result = await first;

            Assert.AreSame(first, second);
            Assert.AreEqual(ModelWarmupState.Ready, result.state);
            Assert.AreEqual(1, whisper);
            Assert.AreEqual(1, python);
            Assert.AreEqual(1, llm);
        }

        [Test]
        public async Task ConcurrentCallersAwaitSameSharedTask()
        {
            TaskCompletionSource<string> whisperGate = NewGate();
            ModelWarmupCoordinator coordinator = Coordinator(
                () => whisperGate.Task,
                () => Task.FromResult(ReadyJson),
                _ => Task.FromResult(ReadyJson));

            Task<ModelWarmupSnapshot> first = coordinator.EnsureReadyAsync("{}");
            Task<ModelWarmupSnapshot> second = coordinator.EnsureReadyAsync("{}");
            Assert.AreSame(first, second);
            Assert.AreEqual(ModelWarmupState.WarmingUp, coordinator.CurrentState);

            whisperGate.SetResult(ReadyJson);
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
                () => Step("whisper"),
                () => Step("python_spacy"),
                _ => Step("llm"));

            await coordinator.EnsureReadyAsync("{}");

            CollectionAssert.AreEqual(new[] { "whisper", "python_spacy", "llm" }, order);
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

            StringAssert.Contains("Modeller hazırlanıyor", status.text);
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
            Assert.AreEqual("whisper", result.failureComponent);
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
            int whisper = 0, python = 0, llm = 0;
            ModelWarmupCoordinator coordinator = Coordinator(
                () => { whisper++; return Task.FromResult(ReadyJson); },
                () => { python++; return Task.FromResult(ReadyJson); },
                _ => { llm++; return Task.FromResult(llm == 1 ? FailedJson : ReadyJson); });

            Assert.AreEqual(ModelWarmupState.PartiallyReady, (await coordinator.EnsureReadyAsync("{}")).state);
            Assert.AreEqual(ModelWarmupState.Ready, (await coordinator.RetryOnceAsync("{}")).state);
            await coordinator.RetryOnceAsync("{}");

            Assert.AreEqual(1, whisper);
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

            Assert.IsTrue(result.whisperContextReused);
            Assert.IsTrue(result.pythonRuntimeReused);
            Assert.IsTrue(result.spacyModelReused);
            Assert.IsTrue(result.llmModelReused);
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
