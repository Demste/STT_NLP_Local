using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.STT
{
    public sealed class STTTestController : MonoBehaviour
    {
        private const float BridgeTimeoutSeconds = 60f;

        [Header("Language")]
        [SerializeField] private string language = "tr";

        [Header("ElevenLabs")]
        [Tooltip("Test icin Inspector'dan verilebilir. Daha guvenli secenek: UI input ile runtime gir.")]
        [SerializeField] private string elevenLabsApiKeyForLocalTest = string.Empty;
        [SerializeField] private TMP_InputField elevenLabsApiKeyInput;

        [Header("Recording")]
        [SerializeField] private int sampleRate = 16000;
        [SerializeField] private int maxRecordSeconds = 30;
        [SerializeField] private float recordingTailSeconds = 0.35f;

        [Header("UI")]
        [SerializeField] private TMP_Dropdown providerDropdown;
        [SerializeField] private Button shareTxtButton;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text transcriptText;
        [SerializeField] private TMP_Text resultsText;
        [SerializeField] private TMP_Text filePathText;

        private MicrophoneRecorder _recorder;
        private STTSessionStorage _storage;
        private STTAndroidBridgeClient _bridgeClient;
        private string _lastWavPath;
        private string _lastRecordingLabel;
        private string _expectedProviderApi;
        private string _expectedAudioPath;
        private string _lastRecordingDebugLine;
        private int _recordingIndex;
        private STTProvider _activeProvider;
        private bool _busy;

        private bool _pendingCompleted;
        private STTResult _pendingResult;

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

            SetStatus("Hazir. Bas ve Konus dugmesine basili tut.");
            SetFilesText();
        }

        private void OnDestroy()
        {
            if (shareTxtButton != null)
            {
                shareTxtButton.onClick.RemoveListener(ShareTxt);
            }
        }

        private void SetupDropdown()
        {
            if (providerDropdown == null) return;

            providerDropdown.ClearOptions();
            providerDropdown.AddOptions(new List<string>
            {
                "Both (Vosk + Whisper)",
                STTProvider.VoskTurkishOffline.ToDisplayName(),
                STTProvider.WhisperCppOffline.ToDisplayName()
            });
            providerDropdown.value = 0;
        }

        public void BeginHoldRecording()
        {
            if (_busy)
            {
                SetStatus("Su an islem devam ediyor, bitince tekrar dene.");
                return;
            }

            try
            {
                _recorder.Start();
                SetStatus("Kayit basladi...");
                if (transcriptText != null) transcriptText.text = string.Empty;
            }
            catch (Exception ex)
            {
                SetStatus("Kayit baslatilamadi: " + ex.Message);
            }
        }

        public void EndHoldRecordingAndTranscribeSelected()
        {
            if (_busy) return;
            StartCoroutine(CoStopSaveAndTranscribeSelected());
        }

        private IEnumerator CoStopSaveAndTranscribeSelected()
        {
            _busy = true;

            AudioClip clip;
            if (_recorder.IsRecording && recordingTailSeconds > 0f)
            {
                SetStatus("Kayit bitiriliyor...");
                yield return new WaitForSeconds(recordingTailSeconds);
            }

            try
            {
                clip = _recorder.StopAndGetClip();
            }
            catch (Exception ex)
            {
                _busy = false;
                SetStatus("Kayit durdurulamadi: " + ex.Message);
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
                bool audioExported = ExportLastAudioToPublicDocuments();
                SetStatus(audioExported
                    ? "Ses kaydedildi ve public Documents klasorune kopyalandi."
                    : "Kayit app storage'a yazildi ama public Documents export basarisiz oldu. Share TXT ile paylasabilirsin.");
                SetFilesText();
            }
            catch (Exception ex)
            {
                _busy = false;
                SetStatus("WAV kaydedilemedi: " + ex.Message);
                yield break;
            }

            foreach (STTProvider provider in GetSelectedProviders())
            {
                yield return CoTranscribeAndStore(provider);
            }

            _storage.AppendRecordingSeparator();
            bool resultExported = ExportResultToPublicDocuments();
            SetFilesText();
            SetStatus(resultExported
                ? "result.txt guncellendi ve Documents klasorune kopyalandi."
                : "Kayit app storage'a yazildi ama public Documents export basarisiz oldu. Share TXT ile paylasabilirsin.");
            _busy = false;
        }

        private IEnumerator CoTranscribeAndStore(STTProvider provider)
        {
            _activeProvider = provider;
            _expectedProviderApi = provider.ToApiValue();
            _expectedAudioPath = _lastWavPath;
            _pendingCompleted = false;
            _pendingResult = null;

            SetStatus($"{provider.ToDisplayName()} cevriliyor...");
            _bridgeClient.Transcribe(
                provider,
                _lastWavPath,
                language,
                GetElevenLabsApiKey(),
                gameObject.name,
                nameof(OnAndroidSTTResult)
            );

            float started = Time.realtimeSinceStartup;
            while (!_pendingCompleted && Time.realtimeSinceStartup - started < BridgeTimeoutSeconds)
            {
                yield return null;
            }

            STTResult result = _pendingResult;
            if (!_pendingCompleted)
            {
                result = BuildErrorResult(provider, "Android bridge callback timeout.");
            }
            else if (result == null)
            {
                result = BuildErrorResult(provider, "Bridge result null dondu.");
            }

            _storage.AppendModelOutput(provider, result);

            string modelOutput = result.HasError ? "HATA: " + result.error : result.text;
            string line = $"{_lastRecordingLabel} {provider.ToDisplayName()}: {modelOutput} ({result.durationMs} ms)";

            if (transcriptText != null) transcriptText.text = result.HasError ? result.error : result.text;
            if (resultsText != null) resultsText.text = (resultsText.text + "\n" + line).Trim();
        }

        // Android Java bridge UnityPlayer.UnitySendMessage ile burayi cagirir.
        public void OnAndroidSTTResult(string json)
        {
            try
            {
                STTResult parsed = JsonUtility.FromJson<STTResult>(json);

                if (parsed == null)
                {
                    _pendingResult = BuildErrorResult(_activeProvider, "JSON parse sonucu null. Raw: " + json);
                    _pendingCompleted = true;
                    return;
                }

                if (!string.IsNullOrEmpty(_expectedProviderApi) && parsed.provider != _expectedProviderApi)
                {
                    Debug.LogWarning($"[STT] Eski/farkli provider callback ignore edildi. Expected={_expectedProviderApi}, Got={parsed.provider}");
                    return;
                }

                if (!string.IsNullOrEmpty(_expectedAudioPath) &&
                    !string.IsNullOrEmpty(parsed.audioFile) &&
                    parsed.audioFile != _expectedAudioPath)
                {
                    Debug.LogWarning($"[STT] Eski/farkli audio callback ignore edildi. Expected={_expectedAudioPath}, Got={parsed.audioFile}");
                    return;
                }

                _pendingResult = parsed;
                _pendingCompleted = true;
            }
            catch (Exception ex)
            {
                _pendingResult = BuildErrorResult(_activeProvider, "JSON parse hatasi: " + ex.Message + " Raw: " + json);
                _pendingCompleted = true;
            }
        }

        public void ShareTxt()
        {
            if (_recordingIndex == 0)
            {
                SetStatus("Paylasilacak kayit yok.");
                return;
            }

            if (_storage == null || string.IsNullOrWhiteSpace(_storage.ResultTxtPath) || !System.IO.File.Exists(_storage.ResultTxtPath))
            {
                SetStatus("Paylasilacak TXT dosyasi yok.");
                return;
            }

            bool started = _bridgeClient.ShareTextFile(_storage.ResultTxtPath);
            SetStatus(started ? "TXT paylasimi acildi." : "Share TXT sadece Android build icinde calisir.");
        }

        private STTProvider[] GetSelectedProviders()
        {
            int value = providerDropdown != null ? providerDropdown.value : 0;
            return value switch
            {
                0 => new[] { STTProvider.VoskTurkishOffline, STTProvider.WhisperCppOffline },
                1 => new[] { STTProvider.VoskTurkishOffline },
                2 => new[] { STTProvider.WhisperCppOffline },
                _ => new[] { STTProvider.VoskTurkishOffline, STTProvider.WhisperCppOffline }
            };
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

        private STTResult BuildErrorResult(STTProvider provider, string error)
        {
            return new STTResult
            {
                provider = provider.ToApiValue(),
                language = language,
                text = string.Empty,
                durationMs = 0,
                audioFile = _lastWavPath,
                error = error
            };
        }

        private string GetElevenLabsApiKey()
        {
            if (elevenLabsApiKeyInput != null && !string.IsNullOrWhiteSpace(elevenLabsApiKeyInput.text))
            {
                return elevenLabsApiKeyInput.text.Trim();
            }

            return elevenLabsApiKeyForLocalTest?.Trim() ?? string.Empty;
        }

        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
            Debug.Log("[STT] " + message);
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
    }
}
