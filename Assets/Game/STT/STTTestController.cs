using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

        private readonly STTProvider _activeProvider = STTProvider.WhisperCppOffline;
        private MicrophoneRecorder _recorder;
        private STTSessionStorage _storage;
        private STTAndroidBridgeClient _bridgeClient;
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
        private int _requestSequence;
        private int _activeRequestId;
        private readonly Dictionary<int, RequestTiming> _requestTimings = new Dictionary<int, RequestTiming>();

        private void Awake()
        {
            _recorder = new MicrophoneRecorder(sampleRate, maxRecordSeconds);
            _storage = new STTSessionStorage(language);
            _bridgeClient = new STTAndroidBridgeClient();

            SetupDropdown();

            if (shareTxtButton != null)
            {
                shareTxtButton.onClick.AddListener(ShareTxt);
            }

            SetStatus("Hazır");
            SetWhisperResult(string.Empty);
            SetNlpJsonResult(string.Empty);
            SetTimingText(RequestTiming.Empty, 0);
            SetFilesText();
        }

        private void OnDestroy()
        {
            if (shareTxtButton != null)
            {
                shareTxtButton.onClick.RemoveListener(ShareTxt);
            }

            _pipelineCancellation?.Cancel();
            _pipelineCancellation?.Dispose();
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
            if (_busy)
            {
                SetStatus("Şu an işlem devam ediyor, bitince tekrar dene.");
                return;
            }

            int requestId = StartNewRequest();

            try
            {
                _recorder.Start();
                SetStatus("Ses kaydı başlatıldı", requestId);
                SetWhisperResult(string.Empty, requestId);
                SetNlpJsonResult(string.Empty, requestId);
                SetTimingText(GetTiming(requestId), requestId);
            }
            catch (Exception ex)
            {
                SetStatus("Hata: " + ex.Message, requestId);
            }
        }

        public void EndHoldRecordingAndTranscribeSelected()
        {
            if (_busy)
            {
                return;
            }

            StartCoroutine(CoStopSaveTranscribeAndParse(_activeRequestId));
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
            _expectedProviderApi = _activeProvider.ToApiValue();
            _expectedAudioPath = _lastWavPath;
            _pendingCompleted = false;
            _pendingResult = null;

            SetStatus("Whisper işleniyor", requestId);
            RequestTiming timing = GetTiming(requestId);
            timing.totalStopwatch.Restart();
            timing.whisperStopwatch.Restart();
            _bridgeClient.Transcribe(
                _activeProvider,
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

            _storage.AppendModelOutput(_activeProvider, result);

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

            string transcript = result.text?.Trim() ?? string.Empty;
            Debug.Log($"[STT] request_id={requestId} whisper_duration={FormatSeconds(timing.whisperSeconds)} transcript=\"{EscapeForLog(transcript)}\"");
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

            yield return CoRunNlp(wakeWordResult.cleanedCommand, requestId);
        }

        private IEnumerator CoRunNlp(string cleanCommand, int requestId)
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
            SetStatus("NLP’ye gönderiliyor", requestId);
            SetNlpJsonResult("NLP input: " + cleanCommand, requestId);

            RequestTiming timing = GetTiming(requestId);
            timing.nlpStopwatch.Restart();
            Task<SpeechCommandPipelineResult> pipelineTask =
                speechCommandPipeline.ProcessTranscriptAsync(cleanCommand, _pipelineCancellation.Token, requestId.ToString(CultureInfo.InvariantCulture));

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
            string uiJson = BuildNlpJsonResult(cleanCommand, pipelineResult);
            Debug.Log($"[UI] request_id={requestId} writing_result={SummarizeForLog(uiJson)}");
            CompleteTotalTiming(timing, "NLP sonucu alındı");
            SetTimingText(timing, requestId);
            LogTotalDuration(requestId, timing.totalSeconds, timing.finalStatus);
            SetStatus("NLP sonucu alındı", requestId);
            SetNlpJsonResult(uiJson, requestId);
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
                provider = _activeProvider.ToApiValue(),
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

                bool passesGate = LooksLikeDroneCommand(cleanedCommand);
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

        private static bool LooksLikeDroneCommand(string command)
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
                "dur", "in", "kalk", "cik", "metre", "yaklas", "uzaklas",
                "ustune", "altina", "yanina"
            };

            foreach (string keyword in keywords)
            {
                if (ContainsNormalizedKeyword(normalized, keyword))
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
            transcriptText.text = "Whisper Result:\n" + value;
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
                "Whisper: " + FormatTimingValue(timing.whisperSeconds, timing.hasWhisperTiming) + "\n" +
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
                recordButton.interactable = interactable;
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
            LlmCommandResult llm = result != null ? result.llm : null;

            NlpUiResult uiResult = new NlpUiResult
            {
                input = cleanCommand ?? string.Empty,
                ok = command != null && command.ok,
                stage = FirstNonEmpty(command?.stage, llm?.stage),
                action = FirstNonEmpty(command?.action, llm?.validated_command?.action, llm?.normalized_command?.action),
                target = FirstNonEmpty(command?.target, llm?.validated_command?.target, llm?.normalized_command?.target),
                target_policy = command?.target_policy,
                confidence = command != null ? command.confidence : 0f,
                parameters = command?.parameters,
                requires_clarification = command != null && command.requires_clarification,
                reason = FirstNonEmpty(command?.reason, llm?.validated_command?.reason, llm?.normalized_command?.reason),
                error = FirstNonEmpty(command?.error, llm?.error),
                inference_ran = (command != null && command.inference_ran) || (llm != null && llm.inference_ran)
            };

            return JsonUtility.ToJson(uiResult, true);
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

        [Serializable]
        private sealed class NlpUiResult
        {
            public string input;
            public bool ok;
            public string stage;
            public string action;
            public string target;
            public string target_policy;
            public float confidence;
            public CommandParameters parameters;
            public bool requires_clarification;
            public string reason;
            public string error;
            public bool inference_ran;
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
            public double wakeWordSeconds;
            public double nlpSeconds;
            public double totalSeconds;
            public bool hasWhisperTiming;
            public bool hasWakeWordTiming;
            public bool hasNlpTiming;
            public bool hasTotalTiming;
            public bool nlpSkipped;
            public string finalStatus = string.Empty;
            public WakeWordParseResult wakeResult = WakeWordParseResult.NoWakeWord;
        }
    }
}
