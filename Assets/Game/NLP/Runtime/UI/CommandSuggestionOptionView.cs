using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.NLP
{
    public sealed class CommandSuggestionOptionView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private TMP_Text label;

        private string suggestionId = string.Empty;
        private Action<string> selected;

        public string SuggestionId => suggestionId;
        public Button Button => button;

        private void OnEnable()
        {
            if (button != null)
            {
                button.onClick.RemoveListener(HandleClick);
                button.onClick.AddListener(HandleClick);
            }
        }

        private void OnDisable()
        {
            if (button != null)
            {
                button.onClick.RemoveListener(HandleClick);
            }
        }

        public void Configure(Button optionButton, TMP_Text optionLabel)
        {
            if (button != null)
            {
                button.onClick.RemoveListener(HandleClick);
            }

            button = optionButton;
            label = optionLabel;

            if (button != null)
            {
                button.onClick.AddListener(HandleClick);
            }
        }

        public void Bind(CommandSuggestion suggestion, Action<string> onSelected)
        {
            suggestionId = suggestion?.suggestion_id ?? string.Empty;
            selected = onSelected;

            if (button != null)
            {
                button.onClick.RemoveListener(HandleClick);
                button.onClick.AddListener(HandleClick);
            }

            if (label != null)
            {
                string mainText = FirstNonEmpty(suggestion?.message, suggestion?.suggested_command, suggestionId);
                label.text = suggestion != null && suggestion.confidence > 0d
                    ? mainText + "  (" + suggestion.confidence.ToString("0.00") + ")"
                    : mainText;
            }

            SetInteractable(true);
        }

        public void SetInteractable(bool interactable)
        {
            if (button != null)
            {
                button.interactable = interactable;
            }
        }

        public void Clear()
        {
            selected = null;
            suggestionId = string.Empty;
        }

        private void HandleClick()
        {
            if (button == null || !button.interactable || string.IsNullOrWhiteSpace(suggestionId))
            {
                return;
            }

            selected?.Invoke(suggestionId);
        }

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
