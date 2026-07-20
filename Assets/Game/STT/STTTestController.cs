using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.NLP;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace Game.STT
{
    public sealed class STTTestController : MonoBehaviour
    {
        private const float BridgeTimeoutSeconds = 60f;
        private const float PipelineTimeoutSeconds = 90f;
        private const string WakeWord = "Kartal";
        private const string EmptyTranscriptMessage = "Boş transcript";
        private const string NoDroneCommandMessage = "Drone komutu algılanmadı";
        private const string MissingCommandMessage = "Komut eksik veya anlaşılamadı";
        private const string SkippedText = "skipped";

        [Header("Language")]
        [SerializeField] private string language = "tr";

        [Header("Android Speech Recognizer")]
        [SerializeField] private AndroidSpeechRecognitionMode speechRecognitionMode = AndroidSpeechRecognitionMode.AUTO;

        [Header("Recording")]
        [SerializeField] private int sampleRate = 16000;
        [SerializeField] private int maxRecordSeconds = 30;
        [SerializeField] private float recordingTailSeconds = 0.35f;

        [Header("Pipeline")]
        [SerializeField] private SpeechCommandPipeline speechCommandPipeline;

        [Header("UI")]
        [SerializeField] private Button recordButton;
        [SerializeField] private Button shareTxtButton;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text transcriptText;
        [SerializeField] private TMP_Text nlpJsonResultText;
        [SerializeField] private TMP_Text timingText;
        [SerializeField] private TMP_Text filePathText;
        [SerializeField] private TMP_Dropdown providerDropdown;

        [Header("Command Recovery UI")]
        [SerializeField] private SpeechCommandRecoveryView commandRecoveryView;

        private readonly STTProvider _activeProvider = STTProvider.AndroidSpeechRecognizer;
        private const STTProvider LegacyWhisperProvider = STTProvider.WhisperCppOffline;
        private MicrophoneRecorder _recorder;
        private STTSessionStorage _storage;
        private STTAndroidBridgeClient _bridgeClient;
        private IAndroidSpeechRecognizerClient _speechRecognizerClient;
        private string _lastWavPath;
        private string _lastRecordingLabel;
        private string _expectedProviderApi;
        private string _expectedAudioPath;
        private string _lastRecordingDebugLine;
        private int _recordingIndex;
        private bool _busy;

        private bool _pendingCompleted;
        private STTResult _pendingResult;
        private CancellationTokenSource _pipelineCancellation;
        private CancellationTokenSource _confirmationCancellation;
        private int _requestSequence;
        private int _activeRequestId;
        private int _uiGeneration;
        private bool _confirmationInProgress;
        private string _lastCleanCommand = string.Empty;
        private ModelWarmupCoordinator _warmupCoordinator;
        private readonly Dictionary<int, RequestTiming> _requestTimings = new Dictionary<int, RequestTiming>();
        private readonly Dictionary<int, AndroidSpeechCandidateSelection> _candidateSelections =
            new Dictionary<int, AndroidSpeechCandidateSelection>();
        private string _activeSpeechSessionId = string.Empty;
        private bool _recognitionSessionActive;
        private bool _controllerDestroyed;
        private string _lastGlobalStatusMessage = string.Empty;

        private void Awake()
        {
            _storage = new STTSessionStorage(language);
            _bridgeClient = new STTAndroidBridgeClient();
            _speechRecognizerClient = _bridgeClient;
            _warmupCoordinator = ModelWarmupCoordinator.Shared;

            SetupDropdown();

            if (shareTxtButton != null)
            {
                shareTxtButton.onClick.AddListener(ShareTxt);
            }

            SetStatus("Android konuşma tanıma hazırlanıyor");
            SetRecordButtonInteractable(false);
            SetWhisperResult(string.Empty);
            SetNlpJsonResult(string.Empty);
            SetTimingText(RequestTiming.Empty, 0);
            SetFilesText();
        }

        private void OnEnable()
        {
            _warmupCoordinator ??= ModelWarmupCoordinator.Shared;
            _warmupCoordinator.ProgressChanged += HandleWarmupProgress;
            if (commandRecoveryView != null)
            {
                commandRecoveryView.SuggestionSelected += ConfirmSuggestion;
                commandRecoveryView.SuggestionRejected += RejectSuggestion;
            }
        }

        private void OnDisable()
        {
            if (_warmupCoordinator != null)
            {
                _warmupCoordinator.ProgressChanged -= HandleWarmupProgress;
            }
            if (commandRecoveryView != null)
            {
                commandRecoveryView.SuggestionSelected -= ConfirmSuggestion;
                commandRecoveryView.SuggestionRejected -= RejectSuggestion;
            }

            CancelConfirmation();
            _pipelineCancellation?.Cancel();
            CancelActiveSpeechSession();
        }

        private async void Start()
        {
            try
            {
                ModelWarmupSnapshot result = await _warmupCoordinator.EnsureReadyAsync(
                    speechCommandPipeline != null ? speechCommandPipeline.ActiveConfigJson : SpeechCommandPipelineConfig.DefaultConfigJson,
                    speechRecognitionMode.ToString());
                if (this != null && isActiveAndEnabled)
                {
                    HandleWarmupProgress(result);
                }
            }
            catch (Exception ex)
            {
                if (this != null && isActiveAndEnabled)
                {
                    SetStatus("Model hazırlama başarısız: " + ex.Message);
                    SetRecordButtonInteractable(false);
                }
            }
        }

        private void OnDestroy()
        {
            _controllerDestroyed = true;
            if (shareTxtButton != null)
            {
                shareTxtButton.onClick.RemoveListener(ShareTxt);
            }

            _pipelineCancellation?.Cancel();
            _pipelineCancellation?.Dispose();
            _confirmationCancellation?.Cancel();
            _confirmationCancellation?.Dispose();
            CancelActiveSpeechSession();
            _speechRecognizerClient?.DestroySpeechRecognizer();
        }

        private void OnApplicationQuit()
        {
            _speechRecognizerClient?.DestroySpeechRecognizer();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) CancelActiveSpeechSession();
        }

        private void SetupDropdown()
        {
            if (providerDropdown == null)
            {
                return;
            }

            providerDropdown.ClearOptions();
            providerDropdown.AddOptions(new System.Collections.Generic.List<string>
            {
                _activeProvider.ToDisplayName()
            });
            providerDropdown.value = 0;
            providerDropdown.interactable = false;
            providerDropdown.gameObject.SetActive(false);
        }

        public void BeginHoldRecording()
        {
            ModelWarmupSnapshot warmup = _warmupCoordinator?.Snapshot;
            if (warmup == null || !warmup.CanRecord)
            {
                if (warmup != null && warmup.state == ModelWarmupState.Failed)
                {
                    SetStatus("Sistem hazırlama başarısız: " + FirstNonEmpty(warmup.failureMessage, warmup.failureComponent));
                }
                else
                {
                    SetStatus("Android konuşma tanıma hazırlanıyor, lütfen bekleyin.");
                }
                return;
            }

            if (_busy || _recognitionSessionActive)
            {
                SetStatus("Şu an işlem devam ediyor, bitince tekrar dene.");
                return;
            }

            int requestId = StartNewRequest();

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                SetStatus("Mikrofon izni gerekli. İzin verdikten sonra tekrar basın.", requestId);
                return;
            }
#endif
            RequestTiming timing = GetTiming(requestId);
            timing.totalStopwatch.Restart();
            timing.pressAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _activeSpeechSessionId = "android-stt-" + requestId.ToString(CultureInfo.InvariantCulture)
                + "-" + timing.pressAtUnixMs.ToString(CultureInfo.InvariantCulture);
            _recognitionSessionActive = true;
            SetStatus("Dinleniyor...", requestId);
            SetWhisperResult(string.Empty, requestId);
            SetNlpJsonResult(string.Empty, requestId);
            SetTimingText(timing, requestId);
            _speechRecognizerClient.StartSpeechRecognition(
                _activeSpeechSessionId,
                speechRecognitionMode,
                gameObject.name,
                nameof(OnAndroidSpeechRecognizerEvent));
        }

        public void EndHoldRecordingAndTranscribeSelected()
        {
            if (!_recognitionSessionActive || _busy || string.IsNullOrWhiteSpace(_activeSpeechSessionId))
            {
                return;
            }
            _busy = true;
            RequestTiming timing = GetTiming(_activeRequestId);
            timing.releaseAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SetRecordButtonInteractable(false);
            SetStatus("Ses işleniyor...", _activeRequestId);
            _speechRecognizerClient.StopSpeechRecognition(_activeSpeechSessionId);
        }

        public void OnAndroidSpeechRecognizerEvent(string json)
        {
            if (_controllerDestroyed) return;
            AndroidSpeechRecognitionEvent speechEvent;
            try
            {
                speechEvent = JsonUtility.FromJson<AndroidSpeechRecognitionEvent>(json);
            }
            catch (Exception ex)
            {
                HandleAndroidSpeechError(_activeRequestId, "Konuşma tanıma sonucu okunamadı: " + ex.Message, "JSON_PARSE");
                return;
            }

            if (speechEvent == null || !string.Equals(speechEvent.session_id, _activeSpeechSessionId, StringComparison.Ordinal))
            {
                Debug.LogWarning("[AndroidSTT] stale callback ignored session_id=" + (speechEvent?.session_id ?? "null"));
                return;
            }

            int requestId = _activeRequestId;
            RequestTiming timing = GetTiming(requestId);
            ApplyAndroidTiming(timing, speechEvent);
            if (string.Equals(speechEvent.event_type, "PARTIAL", StringComparison.Ordinal))
            {
                string partial = speechEvent.alternatives != null && speechEvent.alternatives.Length > 0
                    ? speechEvent.alternatives[0]?.text ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(partial))
                {
                    SetWhisperResult(partial, requestId);
                    Debug.Log($"[AndroidSTTPartial] session_id={speechEvent.session_id} elapsed_ms={ElapsedSince(timing.pressAtUnixMs)} text=\"{EscapeForLog(partial)}\"");
                }
                return;
            }

            if (string.Equals(speechEvent.event_type, "ERROR", StringComparison.Ordinal))
            {
                HandleAndroidSpeechError(requestId,
                    FirstNonEmpty(speechEvent.error_message, "Konuşma tanıma başlatılamadı; tekrar deneyin."),
                    speechEvent.error_name);
                return;
            }

            if (string.Equals(speechEvent.event_type, "FINAL", StringComparison.Ordinal))
            {
                _recognitionSessionActive = false;
                HandleAndroidFinalAsync(speechEvent, requestId);
            }
        }

        private async void HandleAndroidFinalAsync(AndroidSpeechRecognitionEvent speechEvent, int requestId)
        {
            try
            {
                if (speechEvent.alternatives == null || speechEvent.alternatives.Length == 0)
                {
                    HandleAndroidSpeechError(requestId, "Konuşma anlaşılamadı", "ERROR_NO_MATCH");
                    return;
                }
                _pipelineCancellation?.Cancel();
                _pipelineCancellation?.Dispose();
                _pipelineCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(PipelineTimeoutSeconds));
                AndroidSpeechCandidateSelection selection = await AndroidSpeechCandidateSelector.SelectAsync(
                    speechEvent.alternatives,
                    suffix => speechCommandPipeline != null
                        ? speechCommandPipeline.EvaluateCandidateAsync(suffix, suffix, _pipelineCancellation.Token)
                        : Task.FromResult<SpeechCommandCandidateProbe>(null),
                    _pipelineCancellation.Token);
                if (!IsLatestRequest(requestId) || _controllerDestroyed) return;
                _candidateSelections[requestId] = selection;
                RequestTiming timing = GetTiming(requestId);
                timing.nbestEvaluationMs = selection.evaluationDurationMs;
                Debug.Log(
                    $"[AndroidSTTFinal] session_id={speechEvent.session_id} alternatives={speechEvent.alternatives.Length} "
                    + $"primary=\"{EscapeForLog(selection.recognizerPrimaryTranscript)}\" selected=\"{EscapeForLog(selection.selectedTranscript)}\" "
                    + $"selected_index={selection.selectedCandidateIndex} android_confidence={FormatConfidence(selection.selectedAndroidConfidence, selection.selectedAndroidConfidenceAvailable)} "
                    + $"confidence_available={BoolText(selection.selectedAndroidConfidenceAvailable)} selection_reason={selection.candidateSelectionReason} "
                    + $"finalization_latency_ms={speechEvent.finalization_latency_ms} nbest_evaluation_ms={selection.evaluationDurationMs}");
                StartCoroutine(CoHandleAndroidFinal(selection, requestId));
            }
            catch (OperationCanceledException)
            {
                if (IsLatestRequest(requestId)) HandleAndroidSpeechError(requestId, "Konuşma işleme zaman aşımına uğradı.", "CANCELLED");
            }
            catch (Exception ex)
            {
                if (IsLatestRequest(requestId)) HandleAndroidSpeechError(requestId, "Konuşma sonucu işlenemedi: " + ex.Message, "NBEST_ERROR");
            }
        }

        private IEnumerator CoHandleAndroidFinal(AndroidSpeechCandidateSelection selection, int requestId)
        {
            string transcript = selection?.selectedTranscript?.Trim() ?? string.Empty;
            SetWhisperResult(transcript, requestId);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                FinishWithoutNlp(requestId, GetTiming(requestId), NoDroneCommandMessage, NoDroneCommandMessage, WakeWordParseResult.NoWakeWord);
                FinishAndroidSession();
                yield break;
            }

            RequestTiming timing = GetTiming(requestId);
            timing.wakeWordStopwatch.Restart();
            WakeWordParseResult wakeWordResult = TryExtractCommandAfterWakeWord(transcript);
            timing.wakeWordStopwatch.Stop();
            timing.wakeWordSeconds = timing.wakeWordStopwatch.Elapsed.TotalSeconds;
            timing.hasWakeWordTiming = true;
            timing.wakeResult = wakeWordResult;
            LogWakeWordDuration(requestId, timing.wakeWordSeconds, wakeWordResult);
            if (!wakeWordResult.foundWakeWord || !wakeWordResult.hasCommand)
            {
                string message = wakeWordResult.foundWakeWord ? MissingCommandMessage : NoDroneCommandMessage;
                FinishWithoutNlp(requestId, timing, message, message, wakeWordResult);
                FinishAndroidSession();
                yield break;
            }
            yield return CoRunNlp(wakeWordResult.cleanedCommand, transcript, requestId);
            FinishAndroidSession();
        }

        private void FinishAndroidSession()
        {
            _busy = false;
            _recognitionSessionActive = false;
            _activeSpeechSessionId = string.Empty;
            SetRecordButtonInteractable(true);
        }

        private void HandleAndroidSpeechError(int requestId, string message, string errorName)
        {
            _recognitionSessionActive = false;
            _busy = false;
            RequestTiming timing = GetTiming(requestId);
            if (timing.totalStopwatch.IsRunning) CompleteTotalTiming(timing, message);
            timing.nlpSkipped = true;
            SetTimingText(timing, requestId);
            SetStatus(message, requestId);
            SetNlpJsonResult(string.Empty, requestId);
            SetRecordButtonInteractable(true);
            Debug.LogError($"[AndroidSTTError] session_id={_activeSpeechSessionId} error_name={errorName} user_message=\"{EscapeForLog(message)}\"");
            _activeSpeechSessionId = string.Empty;
        }

        private void CancelActiveSpeechSession()
        {
            if (_recognitionSessionActive && !string.IsNullOrWhiteSpace(_activeSpeechSessionId))
            {
                _speechRecognizerClient?.CancelSpeechRecognition(_activeSpeechSessionId);
            }
            _recognitionSessionActive = false;
            _busy = false;
            _activeSpeechSessionId = string.Empty;
        }

        private IEnumerator CoStopSaveTranscribeAndParse(int requestId)
        {
            _busy = true;
            SetRecordButtonInteractable(false);

            AudioClip clip;
            if (_recorder.IsRecording && recordingTailSeconds > 0f)
            {
                yield return new WaitForSeconds(recordingTailSeconds);
            }

            try
            {
                clip = _recorder.StopAndGetClip();
                SetStatus("Ses kaydı durduruldu", requestId);
            }
            catch (Exception ex)
            {
                _busy = false;
                SetRecordButtonInteractable(true);
                SetStatus("Hata: " + ex.Message, requestId);
                yield break;
            }

            _lastRecordingLabel = GetNextRecordingLabel();
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            _lastWavPath = _storage.BuildAudioPath(_lastRecordingLabel, timestamp);

            try
            {
                WavEncoder.SaveToFile(clip, _lastWavPath);
                long wavFileSizeBytes = GetFileSizeBytes(_lastWavPath);
                _lastRecordingDebugLine =
                    $"Recorded: {_recorder.LastRecordedDurationSeconds:F2}s, " +
                    $"samples={_recorder.LastRecordedSamples}, " +
                    $"channels={_recorder.LastChannels}, " +
                    $"freq={_recorder.LastFrequency}, " +
                    $"tail={recordingTailSeconds:F2}s, " +
                    $"wav={wavFileSizeBytes} bytes";
                _storage.AppendRecordingHeader(_lastRecordingLabel, _lastWavPath);
                ExportLastAudioToPublicDocuments();
                SetFilesText();
            }
            catch (Exception ex)
            {
                _busy = false;
                SetRecordButtonInteractable(true);
                SetStatus("Hata: WAV kaydedilemedi: " + ex.Message, requestId);
                yield break;
            }

            yield return CoTranscribeWhisperAndHandleResult(requestId);

            _storage.AppendRecordingSeparator();
            ExportResultToPublicDocuments();
            SetFilesText();
            _busy = false;
            SetRecordButtonInteractable(true);
        }

        private IEnumerator CoTranscribeWhisperAndHandleResult(int requestId)
        {
            _expectedProviderApi = LegacyWhisperProvider.ToApiValue();
            _expectedAudioPath = _lastWavPath;
            _pendingCompleted = false;
            _pendingResult = null;

            SetStatus("Whisper işleniyor", requestId);
            RequestTiming timing = GetTiming(requestId);
            timing.totalStopwatch.Restart();
            timing.whisperStopwatch.Restart();
            _bridgeClient.Transcribe(
                LegacyWhisperProvider,
                _lastWavPath,
                language,
                gameObject.name,
                nameof(OnAndroidSTTResult)
            );

            float started = Time.realtimeSinceStartup;
            while (!_pendingCompleted && Time.realtimeSinceStartup - started < BridgeTimeoutSeconds)
            {
                yield return null;
            }

            if (!IsLatestRequest(requestId))
            {
                Debug.Log($"[STT] request_id={requestId} stale Whisper result ignored.");
                yield break;
            }

            timing.whisperStopwatch.Stop();
            timing.whisperSeconds = timing.whisperStopwatch.Elapsed.TotalSeconds;
            timing.hasWhisperTiming = true;

            STTResult result = _pendingResult;
            if (!_pendingCompleted)
            {
                result = BuildErrorResult("Android bridge callback timeout.");
            }
            else if (result == null)
            {
                result = BuildErrorResult("Bridge result null döndü.");
            }

            _storage.AppendModelOutput(LegacyWhisperProvider, result);

            if (result.HasError)
            {
                timing.totalStopwatch.Stop();
                timing.totalSeconds = timing.totalStopwatch.Elapsed.TotalSeconds;
                timing.hasTotalTiming = true;
                timing.finalStatus = "Hata: " + result.error;
                SetTimingText(timing, requestId);
                LogTotalDuration(requestId, timing.totalSeconds, timing.finalStatus);
                SetWhisperResult(string.Empty, requestId);
                SetStatus("Hata: " + result.error, requestId);
                SetNlpJsonResult("Hata: " + result.error, requestId);
                yield break;
            }

            string rawTranscript = result.text ?? string.Empty;
            string transcript = rawTranscript.Trim();
            Debug.Log(
                $"[STT] request_id={requestId} whisper_duration={FormatSeconds(timing.whisperSeconds)} " +
                $"raw_transcript=\"{EscapeForLog(rawTranscript)}\" normalized_transcript=\"{EscapeForLog(transcript)}\"");
            SetStatus("Whisper sonucu alındı", requestId);
            SetWhisperResult(transcript, requestId);
            SetTimingText(timing, requestId);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                FinishWithoutNlp(requestId, timing, "Boş transcript veya ses algılanmadı", string.Empty, WakeWordParseResult.EmptyTranscript);
                yield break;
            }

            SetStatus("Wake word kontrol ediliyor", requestId);
            timing.wakeWordStopwatch.Restart();
            WakeWordParseResult wakeWordResult = TryExtractCommandAfterWakeWord(transcript);
            timing.wakeWordStopwatch.Stop();
            timing.wakeWordSeconds = timing.wakeWordStopwatch.Elapsed.TotalSeconds;
            timing.hasWakeWordTiming = true;
            timing.wakeResult = wakeWordResult;
            LogWakeWordDuration(requestId, timing.wakeWordSeconds, wakeWordResult);
            SetTimingText(timing, requestId);

            if (!wakeWordResult.foundWakeWord)
            {
                FinishWithoutNlp(requestId, timing, NoDroneCommandMessage, NoDroneCommandMessage, wakeWordResult);
                yield break;
            }

            if (!wakeWordResult.hasCommand)
            {
                FinishWithoutNlp(requestId, timing, MissingCommandMessage, MissingCommandMessage, wakeWordResult);
                yield break;
            }

            yield return CoRunNlp(wakeWordResult.cleanedCommand, rawTranscript, requestId);
        }

        private IEnumerator CoRunNlp(string cleanCommand, string rawTranscript, int requestId)
        {
            if (speechCommandPipeline == null)
            {
                RequestTiming missingPipelineTiming = GetTiming(requestId);
                CompleteTotalTiming(missingPipelineTiming, "Hata: NLP pipeline atanmadı.");
                missingPipelineTiming.nlpSkipped = true;
                SetTimingText(missingPipelineTiming, requestId);
                Debug.Log($"[STT] request_id={requestId} nlp_duration={SkippedText}");
                LogTotalDuration(requestId, missingPipelineTiming.totalSeconds, missingPipelineTiming.finalStatus);
                SetStatus("Hata: NLP pipeline atanmadı.", requestId);
                SetNlpJsonResult("Hata: NLP pipeline atanmadı.", requestId);
                yield break;
            }

            _pipelineCancellation?.Cancel();
            _pipelineCancellation?.Dispose();
            _pipelineCancellation = new CancellationTokenSource();
            _pipelineCancellation.CancelAfter(TimeSpan.FromSeconds(PipelineTimeoutSeconds));

            Debug.Log($"[STT] request_id={requestId} nlp_input={cleanCommand}");
            ModelWarmupSnapshot warmup = _warmupCoordinator?.Snapshot;
            Debug.Log(
                $"[NLP] python_preinitialized={(warmup != null && warmup.pythonReady).ToString().ToLowerInvariant()} "
                + $"spacy_model_reused={(warmup != null && warmup.spacyReady).ToString().ToLowerInvariant()} "
                + $"llm_preinitialized={(warmup != null && warmup.llmReady).ToString().ToLowerInvariant()}");
            SetStatus("NLP’ye gönderiliyor", requestId);
            SetNlpJsonResult("NLP input: " + cleanCommand, requestId);

            RequestTiming timing = GetTiming(requestId);
            timing.nlpStopwatch.Restart();
            Task<SpeechCommandPipelineResult> pipelineTask =
                speechCommandPipeline.ProcessTranscriptAsync(
                    cleanCommand,
                    _pipelineCancellation.Token,
                    requestId.ToString(CultureInfo.InvariantCulture),
                    rawTranscript);

            while (!pipelineTask.IsCompleted)
            {
                yield return null;
            }

            if (!IsLatestRequest(requestId))
            {
                Debug.Log($"[STT] request_id={requestId} stale NLP result ignored.");
                yield break;
            }

            timing.nlpStopwatch.Stop();
            timing.nlpSeconds = timing.nlpStopwatch.Elapsed.TotalSeconds;
            timing.hasNlpTiming = true;
            Debug.Log($"[STT] request_id={requestId} nlp_duration={FormatSeconds(timing.nlpSeconds)}");

            if (pipelineTask.IsCanceled)
            {
                CompleteTotalTiming(timing, "Hata: NLP zaman aşımı.");
                SetTimingText(timing, requestId);
                LogTotalDuration(requestId, timing.totalSeconds, timing.finalStatus);
                SetStatus("Hata: NLP zaman aşımı.", requestId);
                SetNlpJsonResult("Hata: NLP zaman aşımı.", requestId);
                yield break;
            }

            if (pipelineTask.IsFaulted)
            {
                string message = pipelineTask.Exception != null
                    ? pipelineTask.Exception.GetBaseException().Message
                    : "Bilinmeyen NLP hatası.";
                CompleteTotalTiming(timing, "Hata: " + message);
                SetTimingText(timing, requestId);
                LogTotalDuration(requestId, timing.totalSeconds, timing.finalStatus);
                SetStatus("Hata: " + message, requestId);
                SetNlpJsonResult("Hata: " + message, requestId);
                yield break;
            }

            SpeechCommandPipelineResult pipelineResult = pipelineTask.Result;
            if (_candidateSelections.TryGetValue(requestId, out AndroidSpeechCandidateSelection selection)
                && pipelineResult?.validated != null)
            {
                pipelineResult.validated.stt_confidence_level = AndroidSttConfidence.ToLevel(
                    selection.selectedAndroidConfidence,
                    selection.selectedAndroidConfidenceAvailable);
                if (string.IsNullOrWhiteSpace(pipelineResult.validated.voice_type))
                {
                    pipelineResult.validated.voice_type = "NORMAL";
                }
                pipelineResult.finalJson = DroneCommandJsonParser.ToPublicJson(pipelineResult.validated);
            }
            _lastCleanCommand = cleanCommand;
            string uiJson = RenderPipelineResult(cleanCommand, pipelineResult, requestId);
            Debug.Log($"[UI] request_id={requestId} writing_result={SummarizeForLog(uiJson)}");
            CompleteTotalTiming(timing, "NLP sonucu alındı");
            SetTimingText(timing, requestId);
            LogTotalDuration(requestId, timing.totalSeconds, timing.finalStatus);
        }

        // Android Java bridge UnityPlayer.UnitySendMessage ile burayı çağırır.
        public void OnAndroidSTTResult(string json)
        {
            try
            {
                STTResult parsed = JsonUtility.FromJson<STTResult>(json);

                if (parsed == null)
                {
                    _pendingResult = BuildErrorResult("JSON parse sonucu null. Raw: " + json);
                    _pendingCompleted = true;
                    return;
                }

                if (!string.IsNullOrEmpty(_expectedProviderApi) && parsed.provider != _expectedProviderApi)
                {
                    Debug.LogWarning($"[STT] Eski/farklı provider callback ignore edildi. Expected={_expectedProviderApi}, Got={parsed.provider}");
                    return;
                }

                if (!string.IsNullOrEmpty(_expectedAudioPath) &&
                    !string.IsNullOrEmpty(parsed.audioFile) &&
                    parsed.audioFile != _expectedAudioPath)
                {
                    Debug.LogWarning($"[STT] Eski/farklı audio callback ignore edildi. Expected={_expectedAudioPath}, Got={parsed.audioFile}");
                    return;
                }

                _pendingResult = parsed;
                _pendingCompleted = true;
            }
            catch (Exception ex)
            {
                _pendingResult = BuildErrorResult("JSON parse hatası: " + ex.Message + " Raw: " + json);
                _pendingCompleted = true;
            }
        }

        public void ShareTxt()
        {
            if (_recordingIndex == 0)
            {
                SetStatus("Paylaşılacak kayıt yok.");
                return;
            }

            if (_storage == null || string.IsNullOrWhiteSpace(_storage.ResultTxtPath) || !File.Exists(_storage.ResultTxtPath))
            {
                SetStatus("Paylaşılacak TXT dosyası yok.");
                return;
            }

            bool started = _bridgeClient.ShareTextFile(_storage.ResultTxtPath);
            SetStatus(started ? "TXT paylaşımı açıldı." : "Share TXT sadece Android build içinde çalışır.");
        }

        private bool ExportLastAudioToPublicDocuments()
        {
            if (_bridgeClient == null || _storage == null || string.IsNullOrWhiteSpace(_lastWavPath)) return false;
            if (!HasUsableFile(_lastWavPath, 44)) return false;
            return _bridgeClient.ExportFileToPublicDocuments(_lastWavPath, _storage.SessionId, "audio/wav");
        }

        private bool ExportResultToPublicDocuments()
        {
            if (_bridgeClient == null || _storage == null || string.IsNullOrWhiteSpace(_storage.ResultTxtPath)) return false;
            if (!HasUsableFile(_storage.ResultTxtPath, 0)) return false;
            return _bridgeClient.ExportFileToPublicDocuments(_storage.ResultTxtPath, _storage.SessionId, "text/plain");
        }

        private static bool HasUsableFile(string path, long minSizeBytes)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            return new FileInfo(path).Length > minSizeBytes;
        }

        private static long GetFileSizeBytes(string path)
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        private string GetNextRecordingLabel()
        {
            int index = _recordingIndex++;

            if (index < 26)
            {
                char label = (char)('a' + index);
                return label.ToString();
            }

            return $"recording_{index + 1:000}";
        }

        private STTResult BuildErrorResult(string error)
        {
            return new STTResult
            {
                provider = LegacyWhisperProvider.ToApiValue(),
                language = language,
                text = string.Empty,
                durationMs = 0,
                audioFile = _lastWavPath,
                error = error
            };
        }

        private static WakeWordParseResult TryExtractCommandAfterWakeWord(string transcript)
        {
            string text = transcript ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return WakeWordParseResult.EmptyTranscript;
            }

            string normalized = NormalizeForWakeMatching(text);
            string normalizedWakeWord = NormalizeForWakeMatching(WakeWord);
            List<WakeWordParseResult> candidates = new List<WakeWordParseResult>();

            for (int index = 0; index <= normalized.Length - normalizedWakeWord.Length; index++)
            {
                int matchIndex = normalized.IndexOf(normalizedWakeWord, index, StringComparison.Ordinal);
                if (matchIndex < 0)
                {
                    break;
                }

                index = matchIndex;
                if (!HasTokenBoundary(normalized, matchIndex - 1) ||
                    !HasTokenBoundary(normalized, matchIndex + normalizedWakeWord.Length))
                {
                    continue;
                }

                string suffix = text.Substring(matchIndex + WakeWord.Length);
                string cleanedCommand = TrimCommandPrefix(suffix);
                if (string.IsNullOrWhiteSpace(cleanedCommand))
                {
                    candidates.Add(new WakeWordParseResult(
                        true,
                        false,
                        string.Empty,
                        matchIndex,
                        "wake_word_found_without_command",
                        false));
                    continue;
                }

                bool passesGate = IsPotentialCommandSuffix(cleanedCommand);
                candidates.Add(new WakeWordParseResult(
                    true,
                    passesGate,
                    passesGate ? cleanedCommand : string.Empty,
                    matchIndex,
                    passesGate ? "command_gate_passed" : "command_gate_rejected",
                    passesGate));
            }

            WakeWordParseResult bestRejected = WakeWordParseResult.NoWakeWord;
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                WakeWordParseResult candidate = candidates[i];
                if (candidate.hasCommand)
                {
                    return candidate;
                }

                if (!bestRejected.foundWakeWord)
                {
                    bestRejected = candidate;
                }
            }

            return bestRejected.foundWakeWord ? bestRejected : WakeWordParseResult.NoWakeWord;
        }

        public static bool TryExtractCommandSuffixForTesting(string transcript, out string commandSuffix)
        {
            WakeWordParseResult result = TryExtractCommandAfterWakeWord(transcript);
            commandSuffix = result.hasCommand ? result.cleanedCommand : string.Empty;
            return result.hasCommand;
        }

        private static bool HasTokenBoundary(string normalized, int index)
        {
            if (index < 0 || index >= normalized.Length)
            {
                return true;
            }

            return !IsTokenChar(normalized[index]);
        }

        private static bool IsTokenChar(char value)
        {
            return char.IsLetterOrDigit(value) || value == '\'' || value == '’' || value == '_';
        }

        private static string TrimCommandPrefix(string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .TrimStart(',', ':', '.', ';', '!', '?', '-', '—')
                .Trim();
        }

        public static bool IsPotentialCommandSuffix(string command)
        {
            string normalized = NormalizeForWakeMatching(command);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            string[] keywords =
            {
                "ileri", "ileriye", "geri", "geriye", "sag", "saga", "sol", "sola",
                "yuksel", "yukari", "alcal", "asagi", "don", "git", "gel", "takip",
                "dur", "in", "kalk", "cik", "uc", "havalan", "metre", "hiz", "hizla", "yaklas", "uzaklas",
                "ustune", "altina", "yanina", "bul", "ara", "izle", "bekle", "tespit",
                "hover", "gozlem", "gozetle", "sabit"
            };

            foreach (string keyword in keywords)
            {
                if (ContainsNormalizedKeyword(normalized, keyword))
                {
                    return true;
                }
            }

            if (HasConservativeFusedCommandSuffix(normalized))
            {
                return true;
            }

            // Wake word zaten açıkça söylendi. Gate semantic son karar verici
            // değildir; şüpheli ama boş olmayan suffix UNKNOWN ayrımı için
            // action-first pipeline'a bırakılır.
            return true;
        }

        private static bool HasConservativeFusedCommandSuffix(string normalized)
        {
            string compact = new string((normalized ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
            string[] roots =
            {
                "tir", "kamyon", "kamyonet", "araba", "otomobil", "vinc", "forklift",
                "ekskavator", "kepce", "yukleyici", "loader", "cekici", "mikser",
                "isaret", "kare", "ucgen", "cizgi", "arti"
            };
            string[] suffixes = { "takipet", "tespitet", "sabitkal", "bekle", "bul", "ara", "izle", "git", "dur", "in" };

            foreach (string suffix in suffixes)
            {
                if (!compact.EndsWith(suffix, StringComparison.Ordinal) || compact.Length <= suffix.Length)
                {
                    continue;
                }

                string stem = compact.Substring(0, compact.Length - suffix.Length);
                if (roots.Any(stem.StartsWith))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsNormalizedKeyword(string normalized, string keyword)
        {
            int searchStart = 0;
            while (searchStart <= normalized.Length - keyword.Length)
            {
                int index = normalized.IndexOf(keyword, searchStart, StringComparison.Ordinal);
                if (index < 0)
                {
                    return false;
                }

                if (HasTokenBoundary(normalized, index - 1) &&
                    HasTokenBoundary(normalized, index + keyword.Length))
                {
                    return true;
                }

                searchStart = index + keyword.Length;
            }

            return false;
        }

        private static string NormalizeForWakeMatching(string value)
        {
            string lower = (value ?? string.Empty)
                .Normalize(NormalizationForm.FormC)
                .ToLower(new CultureInfo("tr-TR"));

            StringBuilder builder = new StringBuilder(lower.Length);
            foreach (char c in lower)
            {
                builder.Append(c switch
                {
                    'ı' => 'i',
                    'İ' => 'i',
                    'ş' => 's',
                    'ğ' => 'g',
                    'ü' => 'u',
                    'ö' => 'o',
                    'ç' => 'c',
                    _ => c
                });
            }

            return builder.ToString();
        }

        private int StartNewRequest()
        {
            _pipelineCancellation?.Cancel();
            CancelConfirmation();
            speechCommandPipeline?.ResetSuggestions();
            commandRecoveryView?.ClearForNewTranscript();
            _uiGeneration++;
            _activeRequestId = ++_requestSequence;
            _pendingCompleted = false;
            _pendingResult = null;
            RequestTiming timing = RequestTiming.Empty;
            timing.requestId = _activeRequestId;
            _requestTimings[_activeRequestId] = timing;
            return _activeRequestId;
        }

        private bool IsLatestRequest(int requestId)
        {
            return requestId == _activeRequestId;
        }

        private void SetStatus(string message, int requestId = 0)
        {
            if (requestId > 0 && !IsLatestRequest(requestId))
            {
                return;
            }

            if (requestId == 0 && string.Equals(_lastGlobalStatusMessage, message, StringComparison.Ordinal))
            {
                return;
            }
            if (requestId == 0)
            {
                _lastGlobalStatusMessage = message ?? string.Empty;
            }

            if (statusText != null)
            {
                statusText.text = message;
            }

            Debug.Log(requestId > 0 ? $"[STT] request_id={requestId} status={message}" : "[STT] " + message);
        }

        private void SetWhisperResult(string transcript, int requestId = 0)
        {
            if (requestId > 0 && !IsLatestRequest(requestId))
            {
                return;
            }

            if (transcriptText == null)
            {
                return;
            }

            string value = string.IsNullOrWhiteSpace(transcript)
                ? EmptyTranscriptMessage
                : transcript.Trim();
            transcriptText.text = "Konuşma Sonucu:\n" + value;
        }

        private void SetNlpJsonResult(string value, int requestId = 0)
        {
            if (requestId > 0 && !IsLatestRequest(requestId))
            {
                return;
            }

            if (nlpJsonResultText != null)
            {
                nlpJsonResultText.text = value ?? string.Empty;
            }
        }

        private void SetTimingText(RequestTiming timing, int requestId)
        {
            if (requestId > 0 && !IsLatestRequest(requestId))
            {
                return;
            }

            if (timingText == null)
            {
                return;
            }

            timingText.text =
                "Timing:\n" +
                "Android STT: " + FormatTimingValue(timing.androidSttSeconds, timing.hasAndroidSttTiming) + "\n" +
                "Release→final: " + FormatMilliseconds(timing.releaseToFinalMs) + "\n" +
                "First partial: " + FormatMilliseconds(timing.firstPartialLatencyMs) + "\n" +
                "N-best: " + FormatMilliseconds(timing.nbestEvaluationMs) + "\n" +
                "Wake word: " + FormatTimingValue(timing.wakeWordSeconds, timing.hasWakeWordTiming) + "\n" +
                "NLP: " + (timing.nlpSkipped ? SkippedText : FormatTimingValue(timing.nlpSeconds, timing.hasNlpTiming)) + "\n" +
                "Total: " + FormatTimingValue(timing.totalSeconds, timing.hasTotalTiming) + "\n" +
                "Wake found: " + BoolText(timing.wakeResult.foundWakeWord) + "\n" +
                "Wake command: " + DisplayTimingCommand(timing.wakeResult);
        }

        private void SetRecordButtonInteractable(bool interactable)
        {
            if (recordButton != null)
            {
                bool modelsReady = _warmupCoordinator == null || _warmupCoordinator.Snapshot.CanRecord;
                recordButton.interactable = interactable && modelsReady;
            }
        }

        private void HandleWarmupProgress(ModelWarmupSnapshot warmup)
        {
            if (warmup == null || !isActiveAndEnabled)
            {
                return;
            }

            switch (warmup.state)
            {
                case ModelWarmupState.WarmingUp:
                    string component = warmup.currentComponent switch
                    {
                        "android_stt" => "Android konuşma tanıma",
                        "python_spacy" => "NLP",
                        "llm" => "LLM",
                        _ => "modeller",
                    };
                    SetStatus("Sistem hazırlanıyor: " + component);
                    break;
                case ModelWarmupState.Ready:
                    SetStatus("Sistem hazır — " + ReadyModeText(warmup));
                    break;
                case ModelWarmupState.PartiallyReady:
                    SetStatus("Sistem kısmen hazır: " + FirstNonEmpty(warmup.failureComponent, "LLM"));
                    break;
                case ModelWarmupState.Failed:
                    SetStatus("Model hazırlama başarısız: " + FirstNonEmpty(warmup.failureMessage, warmup.failureComponent));
                    break;
            }

            if (recordButton != null)
            {
                recordButton.interactable = warmup.CanRecord && !_busy;
            }
        }

        private void SetFilesText()
        {
            if (filePathText == null || _storage == null) return;

            filePathText.text =
                $"App storage root: {_storage.RootDirectory}\n" +
                $"Session: {_storage.SessionId}\n" +
                $"App result: {_storage.ResultTxtPath}\n" +
                $"App last audio: {_lastWavPath}\n" +
                $"Public visible folder: {_storage.PublicFolderDisplayPath}\n" +
                $"Public result: {_storage.PublicResultDisplayPath}\n" +
                $"{_lastRecordingDebugLine}";
        }

        private static string BuildNlpJsonResult(string cleanCommand, SpeechCommandPipelineResult result)
        {
            ValidatedCommandResult command = result != null ? result.validated : null;
            if (result != null && !string.IsNullOrWhiteSpace(result.finalJson))
            {
                return result.finalJson;
            }

            return command != null ? DroneCommandJsonParser.ToPublicJson(command) : string.Empty;
        }

        private string RenderPipelineResult(string cleanCommand, SpeechCommandPipelineResult result, int requestId)
        {
            if (!IsLatestRequest(requestId))
            {
                return string.Empty;
            }

            commandRecoveryView?.Render(result);
            bool needsConfirmation = string.Equals(
                result?.recovery?.resolution_status,
                CommandRecoveryStatus.NeedsConfirmation,
                StringComparison.Ordinal);
            string uiJson = needsConfirmation ? string.Empty : BuildNlpJsonResult(cleanCommand, result);
            SetNlpJsonResult(uiJson, requestId);

            if (needsConfirmation)
            {
                SetStatus("Komut onayı gerekiyor", requestId);
            }
            else if (string.Equals(result?.validated?.action, "MISSING_INFO", StringComparison.Ordinal))
            {
                SetStatus(FirstNonEmpty(result.validated.missing_message, "Komut için ek bilgi gerekiyor."), requestId);
            }
            else if (string.Equals(result?.validated?.action, "UNKNOWN", StringComparison.Ordinal))
            {
                SetStatus("Komut anlaşılamadı. Lütfen tekrar söyleyin.", requestId);
            }
            else
            {
                SetStatus("NLP sonucu alındı", requestId);
            }

            return uiJson;
        }

        private async void ConfirmSuggestion(string suggestionId)
        {
            if (_confirmationInProgress || speechCommandPipeline == null || string.IsNullOrWhiteSpace(suggestionId))
            {
                return;
            }

            int requestId = _activeRequestId;
            int generation = _uiGeneration;
            _confirmationInProgress = true;
            _confirmationCancellation?.Cancel();
            _confirmationCancellation?.Dispose();
            _confirmationCancellation = new CancellationTokenSource();
            _confirmationCancellation.CancelAfter(TimeSpan.FromSeconds(PipelineTimeoutSeconds));
            commandRecoveryView?.SetConfirmationBusy(true);

            try
            {
                SuggestionDecisionResult decision = await speechCommandPipeline.ConfirmSuggestionAsync(
                    suggestionId,
                    _confirmationCancellation.Token);

                if (!isActiveAndEnabled || generation != _uiGeneration || !IsLatestRequest(requestId))
                {
                    return;
                }

                if (decision != null && decision.accepted && decision.pipeline_result != null)
                {
                    RenderPipelineResult(_lastCleanCommand, decision.pipeline_result, requestId);
                    return;
                }

                SetNlpJsonResult(string.Empty, requestId);
                SetStatus("Önerinin süresi doldu; komutu tekrar söyleyin.", requestId);
                commandRecoveryView?.ShowDecisionFailure(decision?.message);
            }
            catch (OperationCanceledException)
            {
                // Yeni kayıt, disable veya timeout bu akışı normal şekilde sonlandırır.
            }
            catch (Exception ex)
            {
                if (isActiveAndEnabled && generation == _uiGeneration && IsLatestRequest(requestId))
                {
                    SetNlpJsonResult(string.Empty, requestId);
                    SetStatus("Öneri doğrulanamadı.", requestId);
                    commandRecoveryView?.ShowDecisionFailure("Öneri doğrulanamadı; komutu tekrar söyleyin.");
                    Debug.LogException(ex);
                }
            }
            finally
            {
                if (generation == _uiGeneration && IsLatestRequest(requestId))
                {
                    _confirmationInProgress = false;
                    commandRecoveryView?.SetConfirmationBusy(false);
                }
            }
        }

        private void RejectSuggestion(string suggestionId)
        {
            if (_confirmationInProgress || speechCommandPipeline == null || string.IsNullOrWhiteSpace(suggestionId))
            {
                return;
            }

            SuggestionDecisionResult decision = speechCommandPipeline.RejectSuggestion(suggestionId);
            SetNlpJsonResult(string.Empty, _activeRequestId);
            if (decision != null && string.Equals(decision.resolution_status, CommandRecoveryStatus.Rejected, StringComparison.Ordinal))
            {
                SetStatus("Öneri reddedildi; komutu tekrar söyleyin.", _activeRequestId);
                commandRecoveryView?.ShowRejected(decision.message);
            }
            else
            {
                SetStatus("Önerinin süresi doldu; komutu tekrar söyleyin.", _activeRequestId);
                commandRecoveryView?.ShowDecisionFailure(decision?.message);
            }
        }

        private void CancelConfirmation()
        {
            _confirmationCancellation?.Cancel();
            _confirmationInProgress = false;
            commandRecoveryView?.SetConfirmationBusy(false);
        }

        private RequestTiming GetTiming(int requestId)
        {
            if (!_requestTimings.TryGetValue(requestId, out RequestTiming timing))
            {
                timing = RequestTiming.Empty;
                timing.requestId = requestId;
                _requestTimings[requestId] = timing;
            }

            return timing;
        }

        private void FinishWithoutNlp(
            int requestId,
            RequestTiming timing,
            string finalStatus,
            string nlpText,
            WakeWordParseResult wakeResult)
        {
            timing.wakeResult = wakeResult;
            timing.nlpSkipped = true;
            CompleteTotalTiming(timing, finalStatus);
            SetTimingText(timing, requestId);
            Debug.Log($"[STT] request_id={requestId} nlp_duration={SkippedText}");
            LogTotalDuration(requestId, timing.totalSeconds, finalStatus);
            SetStatus(finalStatus, requestId);
            SetNlpJsonResult(nlpText, requestId);
        }

        private static void CompleteTotalTiming(RequestTiming timing, string finalStatus)
        {
            timing.totalStopwatch.Stop();
            timing.totalSeconds = timing.totalStopwatch.Elapsed.TotalSeconds;
            timing.hasTotalTiming = true;
            timing.finalStatus = finalStatus ?? string.Empty;
        }

        private static void LogWakeWordDuration(int requestId, double seconds, WakeWordParseResult result)
        {
            string line =
                $"[STT] request_id={requestId} wake_word_duration={FormatSeconds3(seconds)} " +
                $"found={BoolText(result.foundWakeWord)} has_command={BoolText(result.hasCommand)}";

            if (result.foundWakeWord)
            {
                line += $" wake_index={result.wakeWordIndex}";
            }

            if (!string.IsNullOrWhiteSpace(result.cleanedCommand))
            {
                line += $" nlp_input=\"{EscapeForLog(result.cleanedCommand)}\"";
            }

            if (!string.IsNullOrWhiteSpace(result.reason))
            {
                line += $" reason=\"{EscapeForLog(result.reason)}\"";
            }

            Debug.Log(line);
        }

        private static void LogTotalDuration(int requestId, double seconds, string finalStatus)
        {
            Debug.Log($"[STT] request_id={requestId} total_processing_duration={FormatSeconds(seconds)} final_status=\"{EscapeForLog(finalStatus)}\"");
        }

        private static string FormatTimingValue(double seconds, bool hasValue)
        {
            return hasValue ? FormatSeconds(seconds) : "-";
        }

        private static string FormatMilliseconds(long milliseconds)
        {
            return milliseconds >= 0 ? milliseconds.ToString(CultureInfo.InvariantCulture) + " ms" : "-";
        }

        private static string FormatConfidence(float confidence, bool available)
        {
            return available && confidence >= 0f
                ? confidence.ToString("0.00", CultureInfo.InvariantCulture)
                : "unavailable";
        }

        private static long ElapsedSince(long unixMs)
        {
            return unixMs > 0 ? Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - unixMs) : -1L;
        }

        private static string ReadyModeText(ModelWarmupSnapshot warmup)
        {
            if (warmup == null) return "Android STT";
            if (string.Equals(warmup.selectedSttMode, "ONLINE", StringComparison.Ordinal)) return "Online";
            if (string.Equals(warmup.selectedSttMode, "OFFLINE", StringComparison.Ordinal)) return "Offline";
            if (warmup.onlineSttReady && warmup.offlineSttUsable) return "Online / Offline";
            if (warmup.onlineSttReady) return "Online";
            if (warmup.offlineSttUsable || warmup.offlineTurkishReady) return "Offline";
            return "Android STT";
        }

        private static void ApplyAndroidTiming(RequestTiming timing, AndroidSpeechRecognitionEvent speechEvent)
        {
            if (timing == null || speechEvent == null) return;
            if (speechEvent.listening_started_at > 0 && speechEvent.final_result_at > 0)
            {
                timing.androidSttSeconds = Math.Max(0d,
                    (speechEvent.final_result_at - speechEvent.listening_started_at) / 1000d);
                timing.hasAndroidSttTiming = true;
            }
            if (timing.releaseAtUnixMs > 0 && speechEvent.final_result_at > 0)
            {
                timing.releaseToFinalMs = Math.Max(0L, speechEvent.final_result_at - timing.releaseAtUnixMs);
            }
            if (timing.pressAtUnixMs > 0 && speechEvent.first_partial_at > 0)
            {
                timing.firstPartialLatencyMs = Math.Max(0L, speechEvent.first_partial_at - timing.pressAtUnixMs);
            }
        }

        private static string FormatSeconds(double seconds)
        {
            return seconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";
        }

        private static string FormatSeconds3(double seconds)
        {
            return seconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";
        }

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        private static string DisplayTimingCommand(WakeWordParseResult result)
        {
            if (!result.foundWakeWord)
            {
                return "-";
            }

            return string.IsNullOrWhiteSpace(result.cleanedCommand)
                ? "(" + result.reason + ")"
                : result.cleanedCommand;
        }

        private static string EscapeForLog(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string SummarizeForLog(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return "";
            }

            string oneLine = json.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= 240 ? oneLine : oneLine.Substring(0, 240);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private readonly struct WakeWordParseResult
        {
            public static readonly WakeWordParseResult NoWakeWord =
                new WakeWordParseResult(false, false, string.Empty, -1, "wake_word_not_found", false);

            public static readonly WakeWordParseResult EmptyTranscript =
                new WakeWordParseResult(false, false, string.Empty, -1, "empty_transcript", false);

            public readonly bool foundWakeWord;
            public readonly bool hasCommand;
            public readonly string cleanedCommand;
            public readonly int wakeWordIndex;
            public readonly string reason;
            public readonly bool commandGatePassed;

            public WakeWordParseResult(
                bool foundWakeWord,
                bool hasCommand,
                string cleanedCommand,
                int wakeWordIndex,
                string reason,
                bool commandGatePassed)
            {
                this.foundWakeWord = foundWakeWord;
                this.hasCommand = hasCommand;
                this.cleanedCommand = cleanedCommand ?? string.Empty;
                this.wakeWordIndex = wakeWordIndex;
                this.reason = reason ?? string.Empty;
                this.commandGatePassed = commandGatePassed;
            }
        }

        private sealed class RequestTiming
        {
            public static RequestTiming Empty => new RequestTiming();

            public int requestId;
            public readonly Stopwatch totalStopwatch = new Stopwatch();
            public readonly Stopwatch whisperStopwatch = new Stopwatch();
            public readonly Stopwatch wakeWordStopwatch = new Stopwatch();
            public readonly Stopwatch nlpStopwatch = new Stopwatch();
            public double whisperSeconds;
            public double androidSttSeconds;
            public double wakeWordSeconds;
            public double nlpSeconds;
            public double totalSeconds;
            public bool hasWhisperTiming;
            public bool hasAndroidSttTiming;
            public bool hasWakeWordTiming;
            public bool hasNlpTiming;
            public bool hasTotalTiming;
            public bool nlpSkipped;
            public string finalStatus = string.Empty;
            public long pressAtUnixMs;
            public long releaseAtUnixMs;
            public long releaseToFinalMs = -1;
            public long firstPartialLatencyMs = -1;
            public long nbestEvaluationMs = -1;
            public WakeWordParseResult wakeResult = WakeWordParseResult.NoWakeWord;
        }
    }
}
