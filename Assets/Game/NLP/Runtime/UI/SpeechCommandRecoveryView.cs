using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.NLP
{
    public sealed class SpeechCommandRecoveryView : MonoBehaviour
    {
        private const string UnknownMessage = "Komut anlaşılamadı. Lütfen tekrar söyleyin.";

        [Header("Auto Correction")]
        [SerializeField] private GameObject correctionInfoPanel;
        [SerializeField] private TMP_Text correctionInfoText;

        [Header("Suggestions")]
        [SerializeField] private GameObject suggestionPanel;
        [SerializeField] private TMP_Text suggestionMessageText;
        [SerializeField] private RectTransform suggestionOptionsRoot;
        [SerializeField] private CommandSuggestionOptionView suggestionOptionTemplate;
        [SerializeField] private TMP_Text suggestionStatusText;
        [SerializeField] private Button rejectSuggestionButton;

        [Header("Feedback and Debug")]
        [SerializeField] private TMP_Text userFeedbackText;
        [SerializeField] private TMP_Text recoveryDebugText;

        private readonly List<CommandSuggestionOptionView> optionViews = new List<CommandSuggestionOptionView>();
        private string rejectSuggestionId = string.Empty;
        private bool confirmationBusy;

        public event Action<string> SuggestionSelected;
        public event Action<string> SuggestionRejected;

        public bool IsSuggestionPanelVisible => suggestionPanel != null && suggestionPanel.activeSelf;
        public bool IsCorrectionVisible => correctionInfoPanel != null && correctionInfoPanel.activeSelf;
        public bool IsConfirmationBusy => confirmationBusy;
        public int ActiveOptionCount => optionViews.Count;
        public string UserFeedback => userFeedbackText != null ? userFeedbackText.text : string.Empty;

        private void Awake()
        {
            InitializeView();
        }

        private void OnEnable()
        {
            if (rejectSuggestionButton != null)
            {
                rejectSuggestionButton.onClick.RemoveListener(HandleReject);
                rejectSuggestionButton.onClick.AddListener(HandleReject);
            }
        }

        private void OnDisable()
        {
            if (rejectSuggestionButton != null)
            {
                rejectSuggestionButton.onClick.RemoveListener(HandleReject);
            }

            SetConfirmationBusy(false);
        }

        public void Configure(
            GameObject correctionPanel,
            TMP_Text correctionText,
            GameObject confirmationPanel,
            TMP_Text confirmationMessage,
            RectTransform optionsRoot,
            CommandSuggestionOptionView optionTemplate,
            TMP_Text confirmationStatus,
            Button rejectButton,
            TMP_Text feedbackText,
            TMP_Text debugText)
        {
            if (rejectSuggestionButton != null)
            {
                rejectSuggestionButton.onClick.RemoveListener(HandleReject);
            }

            correctionInfoPanel = correctionPanel;
            correctionInfoText = correctionText;
            suggestionPanel = confirmationPanel;
            suggestionMessageText = confirmationMessage;
            suggestionOptionsRoot = optionsRoot;
            suggestionOptionTemplate = optionTemplate;
            suggestionStatusText = confirmationStatus;
            rejectSuggestionButton = rejectButton;
            userFeedbackText = feedbackText;
            recoveryDebugText = debugText;

            if (rejectSuggestionButton != null)
            {
                rejectSuggestionButton.onClick.AddListener(HandleReject);
            }

            InitializeView();
        }

        public void Render(SpeechCommandPipelineResult result)
        {
            ClearResultUi();
            if (result == null)
            {
                ShowUserFeedback("Pipeline sonucu alınamadı.");
                return;
            }

            CommandRecoveryResult recovery = result.recovery;
            RenderRecoveryDebug(recovery);
            string resolutionStatus = recovery?.resolution_status ?? string.Empty;

            if (string.Equals(resolutionStatus, CommandRecoveryStatus.NeedsConfirmation, StringComparison.Ordinal))
            {
                ShowSuggestions(recovery);
                return;
            }

            if (string.Equals(resolutionStatus, CommandRecoveryStatus.ResolvedAutoCorrection, StringComparison.Ordinal)
                || (recovery != null && recovery.correction_applied))
            {
                ShowCorrection(recovery);
            }

            string action = result.validated?.action ?? string.Empty;
            if (string.Equals(action, "MISSING_INFO", StringComparison.Ordinal)
                || string.Equals(resolutionStatus, CommandRecoveryStatus.MissingInfo, StringComparison.Ordinal))
            {
                ShowUserFeedback(FirstNonEmpty(result.validated?.missing_message, "Komut için ek bilgi gerekiyor."));
            }
            else if (string.Equals(action, "UNKNOWN", StringComparison.Ordinal)
                || string.Equals(resolutionStatus, CommandRecoveryStatus.Unknown, StringComparison.Ordinal)
                || string.Equals(resolutionStatus, "OUT_OF_DOMAIN", StringComparison.Ordinal))
            {
                ShowUserFeedback(UnknownMessage);
            }
        }

        public void ClearForNewTranscript()
        {
            ClearResultUi();
            SetText(recoveryDebugText, string.Empty);
            SetActive(recoveryDebugText, false);
        }

        public void SetConfirmationBusy(bool busy)
        {
            confirmationBusy = busy;
            foreach (CommandSuggestionOptionView option in optionViews)
            {
                if (option != null)
                {
                    option.SetInteractable(!busy);
                }
            }

            if (rejectSuggestionButton != null)
            {
                rejectSuggestionButton.interactable = !busy;
            }

            SetText(suggestionStatusText, busy ? "Doğrulanıyor…" : string.Empty);
        }

        public void ShowDecisionFailure(string message)
        {
            ClearSuggestions();
            ShowUserFeedback(FirstNonEmpty(message, "Önerinin süresi doldu; komutu tekrar söyleyin."));
        }

        public void ShowRejected(string message)
        {
            ClearSuggestions();
            ShowUserFeedback(FirstNonEmpty(message, "Öneri reddedildi; komutu tekrar söyleyin."));
        }

        private void InitializeView()
        {
            if (suggestionOptionTemplate != null)
            {
                suggestionOptionTemplate.gameObject.SetActive(false);
            }

            ClearResultUi();
            SetText(recoveryDebugText, string.Empty);
            SetActive(recoveryDebugText, false);
        }

        private void ClearResultUi()
        {
            SetActive(correctionInfoPanel, false);
            SetText(correctionInfoText, string.Empty);
            ClearSuggestions();
            SetText(userFeedbackText, string.Empty);
            SetActive(userFeedbackText, false);
        }

        private void ShowCorrection(CommandRecoveryResult recovery)
        {
            if (recovery == null)
            {
                return;
            }

            string original = FirstNonEmpty(recovery.original_suffix, recovery.normalized_suffix);
            string corrected = FirstNonEmpty(recovery.corrected_suffix, recovery.parser_input);
            string confidence = recovery.correction_confidence > 0d
                ? " (" + recovery.correction_confidence.ToString("0.00") + ")"
                : string.Empty;
            SetText(correctionInfoText,
                "Algılanan komut düzeltildi: ‘" + original + "’ → ‘" + corrected + "’" + confidence);
            SetActive(correctionInfoPanel, true);
        }

        private void ShowSuggestions(CommandRecoveryResult recovery)
        {
            CommandSuggestion[] suggestions = recovery?.suggestions ?? Array.Empty<CommandSuggestion>();
            if (suggestions.Length == 0 || suggestionOptionTemplate == null || suggestionOptionsRoot == null)
            {
                ShowUserFeedback("Öneri oluşturulamadı; komutu tekrar söyleyin.");
                return;
            }

            SetActive(suggestionPanel, true);
            SetText(suggestionMessageText,
                suggestions.Length == 1
                    ? FirstNonEmpty(suggestions[0].message, "Bu komutu mu demek istediniz?")
                    : "Aşağıdaki komutlardan birini mi demek istediniz?");

            foreach (CommandSuggestion suggestion in suggestions)
            {
                if (suggestion == null || string.IsNullOrWhiteSpace(suggestion.suggestion_id))
                {
                    continue;
                }

                CommandSuggestionOptionView option = Instantiate(suggestionOptionTemplate, suggestionOptionsRoot);
                option.gameObject.name = "Suggestion_" + suggestion.suggestion_id;
                option.gameObject.SetActive(true);
                option.Bind(suggestion, HandleSuggestionSelected);
                optionViews.Add(option);
                if (string.IsNullOrWhiteSpace(rejectSuggestionId))
                {
                    rejectSuggestionId = suggestion.suggestion_id;
                }
            }
        }

        private void HandleSuggestionSelected(string suggestionId)
        {
            if (confirmationBusy || string.IsNullOrWhiteSpace(suggestionId))
            {
                return;
            }

            SetConfirmationBusy(true);
            SuggestionSelected?.Invoke(suggestionId);
        }

        private void HandleReject()
        {
            if (confirmationBusy || string.IsNullOrWhiteSpace(rejectSuggestionId))
            {
                return;
            }

            SuggestionRejected?.Invoke(rejectSuggestionId);
        }

        private void ClearSuggestions()
        {
            SetConfirmationBusy(false);
            foreach (CommandSuggestionOptionView option in optionViews)
            {
                if (option == null)
                {
                    continue;
                }

                option.Clear();
                if (Application.isPlaying)
                {
                    Destroy(option.gameObject);
                }
                else
                {
                    DestroyImmediate(option.gameObject);
                }
            }

            optionViews.Clear();
            rejectSuggestionId = string.Empty;
            SetText(suggestionMessageText, string.Empty);
            SetText(suggestionStatusText, string.Empty);
            SetActive(suggestionPanel, false);
        }

        private void ShowUserFeedback(string message)
        {
            SetText(userFeedbackText, message);
            SetActive(userFeedbackText, !string.IsNullOrWhiteSpace(message));
        }

        private void RenderRecoveryDebug(CommandRecoveryResult recovery)
        {
            if (recovery == null)
            {
                SetText(recoveryDebugText, "Recovery\nStatus: NO_RECOVERY");
                SetActive(recoveryDebugText, true);
                return;
            }

            StringBuilder text = new StringBuilder(256);
            text.AppendLine("Recovery");
            text.AppendLine("Original: " + Display(recovery.original_suffix));
            text.AppendLine("Corrected: " + Display(recovery.corrected_suffix));
            text.AppendLine("Applied: " + recovery.correction_applied.ToString().ToLowerInvariant());
            text.AppendLine("Confidence: " + recovery.correction_confidence.ToString("0.00"));
            text.AppendLine("Reason: " + Display(recovery.correction_reason));
            text.AppendLine("Status: " + Display(recovery.resolution_status));
            text.AppendLine("Action: " + Display(recovery.candidate_action));
            text.Append("Suggestions: ").Append(recovery.suggestions?.Length ?? 0);
            SetText(recoveryDebugText, text.ToString());
            SetActive(recoveryDebugText, true);
        }

        private static void SetText(TMP_Text target, string value)
        {
            if (target != null)
            {
                target.text = value ?? string.Empty;
            }
        }

        private static void SetActive(Component target, bool active)
        {
            if (target != null)
            {
                target.gameObject.SetActive(active);
            }
        }

        private static void SetActive(GameObject target, bool active)
        {
            if (target != null)
            {
                target.SetActive(active);
            }
        }

        private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }
    }
}
