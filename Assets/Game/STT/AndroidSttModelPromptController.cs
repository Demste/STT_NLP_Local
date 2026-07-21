using System;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.STT
{
    public sealed class AndroidSttModelPromptController : MonoBehaviour
    {
        private const string PromptTitle = "Türkçe çevrimdışı konuşma modeli";
        private const string PromptMessage =
            "Sesli komutları internet olmadan kullanabilmek için Türkçe konuşma modelinin "
            + "bir kez indirilmesi gerekiyor. İndirme yaklaşık 25 saniye sürebilir.";
        private const string NetworkMessage = "Modeli indirmek için Wi-Fi veya mobil veriyi açın.";
        private const string DownloadingMessage = "Türkçe çevrimdışı konuşma modeli hazırlanıyor…";
        private const string ReadyMessage = "Türkçe çevrimdışı konuşma modeli hazır.";

        private STTAndroidBridgeClient _bridge;
        private Action<string> _statusSink;
        private bool _warmupComplete;
        private bool _declinedThisSession;
        private bool _downloadActive;
        private bool _resumeCheckRunning;
        private string _modelStatus = "UNKNOWN";

        private GameObject _modal;
        private TMP_Text _title;
        private TMP_Text _message;
        private TMP_Text _spinner;
        private Button _downloadButton;
        private Button _declineButton;
        private Coroutine _closeRoutine;

        public void Configure(STTAndroidBridgeClient bridge, Action<string> statusSink)
        {
            _bridge = bridge;
            _statusSink = statusSink;
        }

        public void EvaluateAfterWarmup(Game.NLP.ModelWarmupSnapshot snapshot)
        {
            if (snapshot == null) return;
            _warmupComplete = true;
            Evaluate(snapshot.offlineModelStatus, snapshot.offlineTurkishReady);
        }

        private void Evaluate(string status, bool installed)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _modelStatus = installed ? "INSTALLED" : Normalize(status);
            if (IsInstalled(_modelStatus))
            {
                Hide();
                return;
            }
            if (IsPending(_modelStatus))
            {
                ShowDownloading();
                _bridge?.ObserveTurkishOnDeviceModel(gameObject.name, nameof(OnAndroidSpeechModelEvent));
                return;
            }
            if (!_declinedThisSession)
            {
                ShowPrompt();
            }
#endif
        }

        private void ShowPrompt()
        {
            EnsureModal();
            if (_modal == null) return;
            _downloadActive = false;
            _title.text = PromptTitle;
            _message.text = PromptMessage;
            _spinner.gameObject.SetActive(false);
            _downloadButton.gameObject.SetActive(true);
            _declineButton.gameObject.SetActive(true);
            _downloadButton.interactable = true;
            _modal.SetActive(true);
            _modal.transform.SetAsLastSibling();
            Debug.Log("[AndroidSTTModelUI] event=popup_shown language=tr-TR model_status=" + _modelStatus);
        }

        private void ShowDownloading()
        {
            EnsureModal();
            if (_modal == null) return;
            _downloadActive = true;
            _title.text = "Model indiriliyor";
            _message.text = DownloadingMessage;
            _spinner.gameObject.SetActive(true);
            _downloadButton.gameObject.SetActive(false);
            _declineButton.gameObject.SetActive(false);
            _modal.SetActive(true);
            _modal.transform.SetAsLastSibling();
        }

        private void OnDownloadClicked()
        {
            _downloadButton.interactable = false;
            AndroidSttModelActionResult result;
            try
            {
                string json = _bridge.RequestTurkishOnDeviceModel(
                    gameObject.name, nameof(OnAndroidSpeechModelEvent));
                result = JsonUtility.FromJson<AndroidSttModelActionResult>(json ?? string.Empty);
            }
            catch (Exception ex)
            {
                result = new AndroidSttModelActionResult { ok = false, error = ex.Message };
            }

            Debug.Log("[AndroidSTTModelUI] event=user_accepted language=tr-TR network_validated="
                + (result != null && result.network_validated).ToString().ToLowerInvariant());
            if (result == null)
            {
                ShowRetry("Model hazırlama isteği başlatılamadı.");
                return;
            }
            _modelStatus = Normalize(result.model_status);
            if (IsInstalled(_modelStatus))
            {
                ShowReadyAndClose();
            }
            else if (!result.ok && !result.network_validated)
            {
                ShowRetry(NetworkMessage);
            }
            else if (!result.ok)
            {
                ShowRetry(string.IsNullOrWhiteSpace(result.error)
                    ? "Model hazırlama isteği başlatılamadı." : result.error);
            }
            else
            {
                ShowDownloading();
            }
        }

        private void OnDeclineClicked()
        {
            _declinedThisSession = true;
            Debug.Log("[AndroidSTTModelUI] event=user_declined language=tr-TR model_status=" + _modelStatus);
            Hide();
            _statusSink?.Invoke("Çevrimdışı model kurulmadı; internet yokken konuşma tanıma kullanılamaz.");
        }

        public void OnAndroidSpeechModelEvent(string json)
        {
            AndroidSttModelEvent modelEvent;
            try
            {
                modelEvent = JsonUtility.FromJson<AndroidSttModelEvent>(json ?? string.Empty);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AndroidSTTModelUI] invalid_event=" + ex.Message);
                return;
            }
            if (modelEvent == null) return;
            _modelStatus = Normalize(modelEvent.model_status);
            if (modelEvent.installed || string.Equals(modelEvent.event_name, "installed", StringComparison.Ordinal))
            {
                ShowReadyAndClose();
            }
            else if (string.Equals(modelEvent.event_name, "download_failed", StringComparison.Ordinal))
            {
                ShowRetry("Model hazırlanamadı. İnternet bağlantısını kontrol edip tekrar deneyin.");
            }
            else if (modelEvent.pending || IsPending(_modelStatus))
            {
                ShowDownloading();
            }
        }

        private void ShowReadyAndClose()
        {
            EnsureModal();
            if (_modal == null) return;
            _downloadActive = false;
            _modelStatus = "INSTALLED";
            _title.text = PromptTitle;
            _message.text = ReadyMessage;
            _spinner.gameObject.SetActive(false);
            _downloadButton.gameObject.SetActive(false);
            _declineButton.gameObject.SetActive(false);
            _modal.SetActive(true);
            _statusSink?.Invoke(ReadyMessage);
            if (_closeRoutine != null) StopCoroutine(_closeRoutine);
            _closeRoutine = StartCoroutine(CloseAfterDelay());
        }

        private IEnumerator CloseAfterDelay()
        {
            yield return new WaitForSecondsRealtime(1.5f);
            Hide();
            _closeRoutine = null;
        }

        private void ShowRetry(string message)
        {
            EnsureModal();
            if (_modal == null) return;
            _downloadActive = false;
            _title.text = PromptTitle;
            _message.text = message;
            _spinner.gameObject.SetActive(false);
            _downloadButton.gameObject.SetActive(true);
            _declineButton.gameObject.SetActive(true);
            _downloadButton.interactable = true;
            _modal.SetActive(true);
        }

        private async void OnApplicationPause(bool paused)
        {
            if (paused || !_warmupComplete || _resumeCheckRunning || _declinedThisSession) return;
            _resumeCheckRunning = true;
            try
            {
                await Task.Yield();
                string json = await _bridge.PrepareSpeechRecognizerAsync(AndroidSpeechRecognitionMode.AUTO);
                if (this == null || !isActiveAndEnabled) return;
                AndroidSpeechSupportResult support = JsonUtility.FromJson<AndroidSpeechSupportResult>(json ?? string.Empty);
                if (support != null) Evaluate(support.offline_model_status, support.tr_tr_on_device_ready);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AndroidSTTModelUI] resume_check_failed=" + ex.Message);
            }
            finally
            {
                _resumeCheckRunning = false;
            }
        }

        private void Update()
        {
            if (_downloadActive && _spinner != null && _spinner.gameObject.activeSelf)
            {
                _spinner.rectTransform.localEulerAngles = new Vector3(0f, 0f, -Time.unscaledTime * 180f);
            }
        }

        private void Hide()
        {
            _downloadActive = false;
            if (_modal != null) _modal.SetActive(false);
        }

        private void EnsureModal()
        {
            if (_modal != null) return;
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            _modal = CreateObject("AndroidSttModelModal", canvas.transform);
            Stretch(_modal.GetComponent<RectTransform>());
            Image overlay = _modal.AddComponent<Image>();
            overlay.color = new Color(0f, 0f, 0f, 0.78f);

            GameObject dialog = CreateObject("AndroidSttModelDialog", _modal.transform);
            RectTransform dialogRect = dialog.GetComponent<RectTransform>();
            dialogRect.anchorMin = dialogRect.anchorMax = dialogRect.pivot = new Vector2(0.5f, 0.5f);
            dialogRect.sizeDelta = new Vector2(1180f, 700f);
            dialog.AddComponent<Image>().color = new Color(0.09f, 0.11f, 0.14f, 1f);

            _title = CreateText("Title", dialog.transform, PromptTitle, 50, FontStyles.Bold);
            SetRect(_title.rectTransform, new Vector2(0.08f, 0.72f), new Vector2(0.92f, 0.92f));
            _message = CreateText("Message", dialog.transform, PromptMessage, 34, FontStyles.Normal);
            SetRect(_message.rectTransform, new Vector2(0.09f, 0.31f), new Vector2(0.91f, 0.72f));
            _spinner = CreateText("Spinner", dialog.transform, "◌", 76, FontStyles.Bold);
            SetRect(_spinner.rectTransform, new Vector2(0.44f, 0.16f), new Vector2(0.56f, 0.31f));

            _downloadButton = CreateButton("DownloadButton", dialog.transform, "İndir", new Color(0.12f, 0.42f, 0.92f, 1f));
            SetRect(_downloadButton.GetComponent<RectTransform>(), new Vector2(0.12f, 0.08f), new Vector2(0.46f, 0.27f));
            _downloadButton.onClick.AddListener(OnDownloadClicked);
            _declineButton = CreateButton("DeclineButton", dialog.transform, "Şimdi Değil", new Color(0.25f, 0.28f, 0.34f, 1f));
            SetRect(_declineButton.GetComponent<RectTransform>(), new Vector2(0.54f, 0.08f), new Vector2(0.88f, 0.27f));
            _declineButton.onClick.AddListener(OnDeclineClicked);
            _modal.SetActive(false);
        }

        private static bool IsInstalled(string status) => status == "INSTALLED" || status == "READY";
        private static bool IsPending(string status) => status == "REQUESTED" || status == "PENDING";
        private static string Normalize(string status) => string.IsNullOrWhiteSpace(status)
            ? "UNKNOWN" : status.Trim().ToUpperInvariant();

        private static GameObject CreateObject(string name, Transform parent)
        {
            GameObject value = new GameObject(name, typeof(RectTransform));
            value.layer = 5;
            value.transform.SetParent(parent, false);
            return value;
        }

        private static TMP_Text CreateText(string name, Transform parent, string value, float size, FontStyles style)
        {
            TextMeshProUGUI text = CreateObject(name, parent).AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(string name, Transform parent, string label, Color color)
        {
            GameObject owner = CreateObject(name, parent);
            owner.AddComponent<Image>().color = color;
            Button button = owner.AddComponent<Button>();
            TMP_Text text = CreateText("Label", owner.transform, label, 34, FontStyles.Bold);
            Stretch(text.rectTransform);
            return button;
        }

        private static void Stretch(RectTransform rect) => SetRect(rect, Vector2.zero, Vector2.one);

        private static void SetRect(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }
}
