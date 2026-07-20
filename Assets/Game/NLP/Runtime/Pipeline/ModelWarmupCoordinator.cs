using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Game.NLP.Android;
using Game.STT;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace Game.NLP
{
    public enum ModelWarmupState
    {
        NotStarted,
        WarmingUp,
        Ready,
        PartiallyReady,
        Failed,
    }

    [Serializable]
    public sealed class ModelWarmupSnapshot
    {
        public ModelWarmupState state;
        public string currentComponent;
        public bool androidSpeechReady;
        public bool onlineSttReady;
        public string requestedSttMode;
        public string networkState;
        public string networkValidated;
        public string onlineSupportStatus;
        public bool onDeviceServiceAvailable;
        public bool offlineTurkishReady;
        public bool offlineSttUsable;
        public string offlineModelStatus;
        public string onDeviceSupportStatus;
        public string selectedSttMode;
        public string sttSelectionReason;
        public bool modelDownloadRequested;
        public string androidSupportMessage;
        public bool whisperReady;
        public bool pythonReady;
        public bool spacyReady;
        public bool llmReady;
        public bool whisperContextReused;
        public bool pythonRuntimeReused;
        public bool spacyModelReused;
        public bool llmModelReused;
        public long warmupStartedAtUnixMs;
        public long whisperDurationMs;
        public long pythonSpacyDurationMs;
        public long llmDurationMs;
        public long totalDurationMs;
        public double memoryBeforeMb;
        public double memoryAfterMb;
        public string failureComponent;
        public string failureMessage;

        public bool CanRecord => androidSpeechReady && pythonReady && spacyReady
            && (state == ModelWarmupState.Ready || state == ModelWarmupState.PartiallyReady);

        public ModelWarmupSnapshot Copy()
        {
            return (ModelWarmupSnapshot)MemberwiseClone();
        }
    }

    [Serializable]
    internal sealed class ModelWarmupBridgeResult
    {
        public bool ok;
        public bool initialized;
        public string stage;
        public string error;
        public long durationMs;
        public bool context_reused;
        public bool python_runtime_reused;
        public bool spacy_model_reused;
        public bool model_reused;
        public bool online_available;
        public string requested_mode;
        public bool standard_service_available;
        public string network_state;
        public string network_validated;
        public string online_support_status;
        public bool online_usable;
        public bool on_device_service_available;
        public string on_device_support_status;
        public bool tr_tr_on_device_ready;
        public bool offline_usable;
        public string offline_model_status;
        public bool model_download_requested;
        public bool support_checked;
        public string selected_mode;
        public string selection_reason;
        public bool warmup_success;
    }

    public sealed class ModelWarmupCoordinator
    {
        private static readonly Lazy<ModelWarmupCoordinator> SharedInstance =
            new Lazy<ModelWarmupCoordinator>(CreateDefault);

        private readonly object sync = new object();
        private readonly Func<string, Task<string>> prepareAndroidSpeechAsync;
        private readonly Func<Task<string>> warmPythonSpacyAsync;
        private readonly Func<string, Task<string>> warmLlmAsync;
        private ModelWarmupSnapshot snapshot = new ModelWarmupSnapshot { state = ModelWarmupState.NotStarted };
        private Task<ModelWarmupSnapshot> warmupTask;
        private int attempts;

        public static ModelWarmupCoordinator Shared => SharedInstance.Value;

        public event Action<ModelWarmupSnapshot> ProgressChanged;

        public ModelWarmupState CurrentState
        {
            get { lock (sync) return snapshot.state; }
        }

        public ModelWarmupSnapshot Snapshot
        {
            get { lock (sync) return snapshot.Copy(); }
        }

        public ModelWarmupCoordinator(
            Func<Task<string>> androidSpeechWarmup,
            Func<Task<string>> pythonSpacyWarmup,
            Func<string, Task<string>> llmWarmup)
            : this(_ => androidSpeechWarmup(), pythonSpacyWarmup, llmWarmup)
        {
        }

        public ModelWarmupCoordinator(
            Func<string, Task<string>> androidSpeechWarmup,
            Func<Task<string>> pythonSpacyWarmup,
            Func<string, Task<string>> llmWarmup)
        {
            prepareAndroidSpeechAsync = androidSpeechWarmup ?? throw new ArgumentNullException(nameof(androidSpeechWarmup));
            warmPythonSpacyAsync = pythonSpacyWarmup ?? throw new ArgumentNullException(nameof(pythonSpacyWarmup));
            warmLlmAsync = llmWarmup ?? throw new ArgumentNullException(nameof(llmWarmup));
        }

        public Task<ModelWarmupSnapshot> EnsureReadyAsync(string configJson, string speechMode = "AUTO")
        {
            lock (sync)
            {
                if (warmupTask == null)
                {
                    attempts = 1;
                    warmupTask = RunWarmupAsync(configJson ?? string.Empty, speechMode ?? "AUTO");
                }

                return warmupTask;
            }
        }

        public Task<ModelWarmupSnapshot> RetryOnceAsync(string configJson, string speechMode = "AUTO")
        {
            lock (sync)
            {
                if ((snapshot.state != ModelWarmupState.Failed && snapshot.state != ModelWarmupState.PartiallyReady)
                    || attempts >= 2)
                {
                    return warmupTask ?? Task.FromResult(snapshot.Copy());
                }

                attempts++;
                warmupTask = RunWarmupAsync(configJson ?? string.Empty, speechMode ?? "AUTO");
                return warmupTask;
            }
        }

        private async Task<ModelWarmupSnapshot> RunWarmupAsync(string configJson, string speechMode)
        {
            Stopwatch total = Stopwatch.StartNew();
            lock (sync)
            {
                snapshot.state = ModelWarmupState.WarmingUp;
                snapshot.currentComponent = string.Empty;
                snapshot.failureComponent = string.Empty;
                snapshot.failureMessage = string.Empty;
                if (snapshot.warmupStartedAtUnixMs == 0)
                {
                    snapshot.warmupStartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    snapshot.memoryBeforeMb = CurrentMemoryMb();
                }
            }
            Publish("", "state=WARMING_UP");

            if (!Snapshot.androidSpeechReady)
            {
                await RunComponentAsync("android_stt", () => prepareAndroidSpeechAsync(speechMode), value =>
                {
                    snapshot.androidSpeechReady = value.ok || value.initialized;
                    snapshot.onlineSttReady = value.online_usable || value.online_available;
                    snapshot.requestedSttMode = value.requested_mode ?? string.Empty;
                    snapshot.networkState = value.network_state ?? string.Empty;
                    snapshot.networkValidated = value.network_validated ?? string.Empty;
                    snapshot.onlineSupportStatus = value.online_support_status ?? string.Empty;
                    snapshot.onDeviceServiceAvailable = value.on_device_service_available;
                    snapshot.offlineTurkishReady = value.tr_tr_on_device_ready;
                    snapshot.offlineSttUsable = value.offline_usable || value.tr_tr_on_device_ready;
                    snapshot.offlineModelStatus = value.offline_model_status ?? string.Empty;
                    snapshot.onDeviceSupportStatus = value.on_device_support_status ?? string.Empty;
                    snapshot.selectedSttMode = value.selected_mode ?? string.Empty;
                    snapshot.sttSelectionReason = value.selection_reason ?? string.Empty;
                    snapshot.modelDownloadRequested = value.model_download_requested;
                    snapshot.androidSupportMessage = value.error ?? string.Empty;
                    snapshot.whisperReady = false;
                    snapshot.whisperContextReused = false;
                    snapshot.whisperDurationMs = 0;
                });
            }

            if (Snapshot.androidSpeechReady && (!Snapshot.pythonReady || !Snapshot.spacyReady))
            {
                await RunComponentAsync("python_spacy", warmPythonSpacyAsync, value =>
                {
                    snapshot.pythonReady = value.ok;
                    snapshot.spacyReady = value.ok;
                    snapshot.pythonRuntimeReused = value.python_runtime_reused;
                    snapshot.spacyModelReused = value.spacy_model_reused;
                    snapshot.pythonSpacyDurationMs = value.durationMs;
                });
            }

            if (Snapshot.androidSpeechReady && !Snapshot.llmReady)
            {
                await RunComponentAsync("llm", () => warmLlmAsync(configJson), value =>
                {
                    snapshot.llmReady = value.ok || value.initialized;
                    snapshot.llmModelReused = value.model_reused;
                    snapshot.llmDurationMs = value.durationMs;
                });
            }

            lock (sync)
            {
                snapshot.currentComponent = string.Empty;
                snapshot.totalDurationMs += total.ElapsedMilliseconds;
                snapshot.memoryAfterMb = CurrentMemoryMb();
                if (snapshot.androidSpeechReady && snapshot.pythonReady && snapshot.spacyReady && snapshot.llmReady)
                {
                    snapshot.state = ModelWarmupState.Ready;
                    snapshot.failureComponent = string.Empty;
                    snapshot.failureMessage = string.Empty;
                }
                else if (snapshot.androidSpeechReady && snapshot.pythonReady && snapshot.spacyReady)
                {
                    snapshot.state = ModelWarmupState.PartiallyReady;
                }
                else
                {
                    snapshot.state = ModelWarmupState.Failed;
                }
            }

            ModelWarmupSnapshot completed = Snapshot;
            Debug.Log(
                "[ModelWarmup] state=" + completed.state.ToString().ToUpperInvariant()
                + " total_duration_ms=" + completed.totalDurationMs
                + " memory_before_mb=" + completed.memoryBeforeMb.ToString("0.0")
                + " memory_after_mb=" + completed.memoryAfterMb.ToString("0.0")
                + " failure_component=" + (completed.failureComponent ?? string.Empty)
                + " failure_message=\"" + (completed.failureMessage ?? string.Empty) + "\"");
            ProgressChanged?.Invoke(completed);
            return completed;
        }

        private async Task RunComponentAsync(
            string component,
            Func<Task<string>> operation,
            Action<ModelWarmupBridgeResult> apply)
        {
            lock (sync) snapshot.currentComponent = component;
            Publish(component, "started=true");
            Stopwatch timer = Stopwatch.StartNew();
            ModelWarmupBridgeResult result;
            try
            {
                string json = await operation();
                result = JsonUtility.FromJson<ModelWarmupBridgeResult>(json ?? string.Empty)
                    ?? new ModelWarmupBridgeResult { ok = false, error = "Warm-up bridge returned invalid JSON." };
            }
            catch (Exception ex)
            {
                result = new ModelWarmupBridgeResult { ok = false, error = ex.Message, stage = "exception" };
            }

            if (result.durationMs <= 0)
            {
                result.durationMs = timer.ElapsedMilliseconds;
            }

            lock (sync)
            {
                apply(result);
                if (!result.ok && !result.initialized)
                {
                    snapshot.failureComponent = component;
                    snapshot.failureMessage = string.IsNullOrWhiteSpace(result.error) ? result.stage : result.error;
                }
            }
            Publish(component,
                "success=" + (result.ok || result.initialized).ToString().ToLowerInvariant()
                + " duration_ms=" + result.durationMs
                + " stage=" + (result.stage ?? string.Empty)
                + ReuseLogFields(component, result));
        }

        private static string ReuseLogFields(string component, ModelWarmupBridgeResult result)
        {
            if (component == "android_stt")
            {
                return " online_available=" + result.online_available.ToString().ToLowerInvariant()
                    + " requested_mode=" + (result.requested_mode ?? string.Empty)
                    + " standard_service_available=" + result.standard_service_available.ToString().ToLowerInvariant()
                    + " network_state=" + (result.network_state ?? string.Empty)
                    + " network_validated=" + (result.network_validated ?? string.Empty)
                    + " online_support_status=" + (result.online_support_status ?? string.Empty)
                    + " online_usable=" + (result.online_usable || result.online_available).ToString().ToLowerInvariant()
                    + " on_device_service_available=" + result.on_device_service_available.ToString().ToLowerInvariant()
                    + " on_device_support_status=" + (result.on_device_support_status ?? string.Empty)
                    + " tr_tr_on_device_ready=" + result.tr_tr_on_device_ready.ToString().ToLowerInvariant()
                    + " offline_usable=" + (result.offline_usable || result.tr_tr_on_device_ready).ToString().ToLowerInvariant()
                    + " offline_model_status=" + (result.offline_model_status ?? string.Empty)
                    + " selected_mode=" + (result.selected_mode ?? string.Empty)
                    + " selection_reason=" + (result.selection_reason ?? string.Empty)
                    + " model_download_requested=" + result.model_download_requested.ToString().ToLowerInvariant();
            }
            if (component == "python_spacy")
            {
                return " python_runtime_reused=" + result.python_runtime_reused.ToString().ToLowerInvariant()
                    + " spacy_model_reused=" + result.spacy_model_reused.ToString().ToLowerInvariant();
            }
            if (component == "llm")
            {
                return " model_reused=" + result.model_reused.ToString().ToLowerInvariant();
            }
            return string.Empty;
        }

        private void Publish(string component, string detail)
        {
            Debug.Log("[ModelWarmup] component=" + component + " " + detail);
            ProgressChanged?.Invoke(Snapshot);
        }

        private static double CurrentMemoryMb()
        {
            return Profiler.GetTotalAllocatedMemoryLong() / (1024d * 1024d);
        }

        private static ModelWarmupCoordinator CreateDefault()
        {
            STTAndroidBridgeClient stt = new STTAndroidBridgeClient();
#if UNITY_ANDROID && !UNITY_EDITOR
            return new ModelWarmupCoordinator(
                mode => stt.PrepareSpeechRecognizerAsync(
                    Enum.TryParse(mode, true, out AndroidSpeechRecognitionMode parsed) ? parsed : AndroidSpeechRecognitionMode.AUTO,
                    CancellationToken.None),
                () => Game.NLP.Android.SpacyNlpAndroidBridgeClient.WarmUpAsync(CancellationToken.None),
                config => Game.NLP.Android.OfflineLlmAndroidBridgeClient.WarmUpAsync(config, CancellationToken.None));
#else
            const string Ready = "{\"ok\":true,\"initialized\":true,\"stage\":\"editor_noop\",\"durationMs\":0}";
            return new ModelWarmupCoordinator(
                _ => Task.FromResult(Ready),
                () => Task.FromResult(Ready),
                _ => Task.FromResult(Ready));
#endif
        }
    }
}
