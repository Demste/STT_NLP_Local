using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.STT
{
    public sealed class STTTestController : MonoBehaviour
    {
        [Header("Language")]
        [SerializeField] private string language = "tr";

        [Header("ElevenLabs")]
        [Tooltip("Test için Inspector'dan verilebilir. Daha güvenli seçenek: UI input ile runtime gir.")]
        [SerializeField] private string elevenLabsApiKeyForLocalTest = string.Empty;
        [SerializeField] private TMP_InputField elevenLabsApiKeyInput;

        [Header("Recording")]
        [SerializeField] private int sampleRate = 16000;
        [SerializeField] private int maxRecordSeconds = 30;

        [Header("UI")]
        [SerializeField] private TMP_Dropdown providerDropdown;
        [SerializeField] private Button transcribeLastSelectedButton;
        [SerializeField] private Button runAllProvidersButton;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text transcriptText;
        [SerializeField] private TMP_Text resultsText;
        [SerializeField] private TMP_Text filePathText;

        private MicrophoneRecorder _recorder;
        private TranscriptStorage _storage;
        private STTAndroidBridgeClient _bridgeClient;
        private string _lastRecordingId;
        private string _lastWavPath;
        private bool _busy;

        private bool _pendingCompleted;
        private STTResult _pendingResult;

        private void Awake()
        {
            _recorder = new MicrophoneRecorder(sampleRate, maxRecordSeconds);
            _storage = new TranscriptStorage();
            _bridgeClient = new STTAndroidBridgeClient();

            SetupDropdown();

            if (transcribeLastSelectedButton != null)
            {
                transcribeLastSelectedButton.onClick.AddListener(TranscribeLastWithSelectedProvider);
            }

            if (runAllProvidersButton != null)
            {
                runAllProvidersButton.onClick.AddListener(RunAllProvidersOnLastRecording);
            }

            SetStatus("Hazır. Düğmeye basılı tut ve konuş.");
            SetFilesText();
        }

        private void OnDestroy()
        {
            if (transcribeLastSelectedButton != null)
            {
                transcribeLastSelectedButton.onClick.RemoveListener(TranscribeLastWithSelectedProvider);
            }

            if (runAllProvidersButton != null)
            {
                runAllProvidersButton.onClick.RemoveListener(RunAllProvidersOnLastRecording);
            }
        }

        private void SetupDropdown()
        {
            if (providerDropdown == null) return;
            providerDropdown.ClearOptions();
            providerDropdown.AddOptions(new System.Collections.Generic.List<string>
            {
                STTProvider.VoskTurkishOffline.ToDisplayName(),
                STTProvider.WhisperCppOffline.ToDisplayName(),
                // STTProvider.ElevenLabsScribeV2.ToDisplayName()
            });
            providerDropdown.value = 0;
        }

        public void BeginHoldRecording()
        {
            if (_busy)
            {
                SetStatus("Şu an işlem devam ediyor, bitince tekrar dene.");
                return;
            }

            try
            {
                _recorder.Start();
                SetStatus("Kayıt alınıyor... Parmağını bırakınca duracak.");
                if (transcriptText != null) transcriptText.text = string.Empty;
            }
            catch (Exception ex)
            {
                SetStatus("Kayıt başlatılamadı: " + ex.Message);
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
            try
            {
                clip = _recorder.StopAndGetClip();
            }
            catch (Exception ex)
            {
                _busy = false;
                SetStatus("Kayıt durdurulamadı: " + ex.Message);
                yield break;
            }

            _lastRecordingId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            _lastWavPath = _storage.BuildAudioPath(_lastRecordingId);

            try
            {
                WavEncoder.SaveToFile(clip, _lastWavPath);
                SetStatus("Ses kaydedildi. Seçili modele gönderiliyor...");
                SetFilesText();
            }
            catch (Exception ex)
            {
                _busy = false;
                SetStatus("WAV kaydedilemedi: " + ex.Message);
                yield break;
            }

            yield return CoTranscribeAndStore(GetSelectedProvider(), appendToResults: false);
            _busy = false;
        }

        public void TranscribeLastWithSelectedProvider()
        {
            if (_busy) return;
            if (string.IsNullOrWhiteSpace(_lastWavPath))
            {
                SetStatus("Önce bir ses kaydı al.");
                return;
            }

            StartCoroutine(CoTranscribeLastSelected());
        }

        private IEnumerator CoTranscribeLastSelected()
        {
            _busy = true;
            yield return CoTranscribeAndStore(GetSelectedProvider(), appendToResults: true);
            _busy = false;
        }

        public void RunAllProvidersOnLastRecording()
        {
            if (_busy) return;
            if (string.IsNullOrWhiteSpace(_lastWavPath))
            {
                SetStatus("Önce bir ses kaydı al.");
                return;
            }

            StartCoroutine(CoRunAllProviders());
        }

        private IEnumerator CoRunAllProviders()
        {
            _busy = true;
            if (resultsText != null) resultsText.text = string.Empty;

            STTProvider[] providers =
            {
                STTProvider.VoskTurkishOffline,
                STTProvider.WhisperCppOffline,
                // STTProvider.ElevenLabsScribeV2
            };

            foreach (STTProvider provider in providers)
            {
                yield return CoTranscribeAndStore(provider, appendToResults: true);
            }

            SetStatus("Tüm model testleri tamamlandı.");
            _busy = false;
        }

        private IEnumerator CoTranscribeAndStore(STTProvider provider, bool appendToResults)
        {
            _pendingCompleted = false;
            _pendingResult = null;

            SetStatus($"{provider.ToDisplayName()} çevriliyor...");
            _bridgeClient.Transcribe(
                provider,
                _lastWavPath,
                language,
                GetElevenLabsApiKey(),
                gameObject.name,
                nameof(OnAndroidSTTResult)
            );

            while (!_pendingCompleted) yield return null;

            STTResult result = _pendingResult ?? new STTResult
            {
                provider = provider.ToApiValue(),
                language = language,
                text = string.Empty,
                durationMs = 0,
                audioFile = _lastWavPath,
                error = "Bridge result null döndü."
            };

            string transcriptPath = _storage.BuildTranscriptPath(_lastRecordingId, provider);
            _storage.SaveTranscript(transcriptPath, result);
            _storage.AppendCsv(DateTime.Now.ToString("o"), _lastRecordingId, _lastWavPath, transcriptPath, result);

            string line = result.HasError
                ? $"[{provider.ToDisplayName()}] HATA: {result.error}"
                : $"[{provider.ToDisplayName()}] {result.text} ({result.durationMs} ms)";

            if (transcriptText != null) transcriptText.text = result.HasError ? result.error : result.text;
            if (resultsText != null)
            {
                resultsText.text = appendToResults
                    ? (resultsText.text + "\n" + line).Trim()
                    : line;
            }

            SetStatus(result.HasError ? "Hata oluştu." : "Tamamlandı.");
            SetFilesText(transcriptPath);
        }

        // Android Java bridge UnityPlayer.UnitySendMessage ile burayı çağırır.
        public void OnAndroidSTTResult(string json)
        {
            try
            {
                _pendingResult = JsonUtility.FromJson<STTResult>(json);
                if (_pendingResult == null)
                {
                    _pendingResult = new STTResult
                    {
                        provider = GetSelectedProvider().ToApiValue(),
                        language = language,
                        text = string.Empty,
                        durationMs = 0,
                        audioFile = _lastWavPath,
                        error = "JSON parse sonucu null. Raw: " + json
                    };
                }
            }
            catch (Exception ex)
            {
                _pendingResult = new STTResult
                {
                    provider = GetSelectedProvider().ToApiValue(),
                    language = language,
                    text = string.Empty,
                    durationMs = 0,
                    audioFile = _lastWavPath,
                    error = "JSON parse hatası: " + ex.Message + " Raw: " + json
                };
            }
            finally
            {
                _pendingCompleted = true;
            }
        }

        private STTProvider GetSelectedProvider()
        {
            int value = providerDropdown != null ? providerDropdown.value : 0;
            return value switch
            {
                0 => STTProvider.VoskTurkishOffline,
                1 => STTProvider.WhisperCppOffline,
                // 2 => STTProvider.ElevenLabsScribeV2,
                _ => STTProvider.VoskTurkishOffline
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

        private void SetFilesText(string lastTranscriptPath = null)
        {
            if (filePathText == null) return;
            filePathText.text =
                $"Root: {_storage.RootDirectory}\n" +
                $"Last audio: {_lastWavPath}\n" +
                $"Last transcript: {lastTranscriptPath}\n" +
                $"CSV: {_storage.LogCsvPath}";
        }
    }
}