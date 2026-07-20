using Game.NLP;
using Game.STT;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.NLP.Editor
{
    public static class SpeechCommandDebugSceneBuilder
    {
        private const string MenuPath = "Tools/NLP/Create Speech Command Debug Panel";
        private const string RootName = "NLP_Command_Debug_Root";
        private const string PipelineName = "SpeechCommandPipeline";
        private const string TestPhrase = "ileri git";
        private const string TestScenePath = "Assets/Scenes/Test.unity";
        private const string RecoveryRootName = "CommandRecoveryUI";

        [MenuItem(MenuPath)]
        public static void CreateSpeechCommandDebugPanel()
        {
            Canvas canvas = EnsureCanvas();
            EnsureEventSystem();

            Transform oldRoot = FindInScene(RootName);
            if (oldRoot != null)
            {
                Undo.DestroyObjectImmediate(oldRoot.gameObject);
            }

            GameObject root = CreateUiObject(RootName, canvas.transform);
            Stretch(root.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            SpeechCommandPipeline pipeline = CreatePipeline(root.transform);
            BuiltPanel panel = CreatePanel(root.transform);

            SpeechCommandDebugPanel debugPanel = panel.panelObject.AddComponent<SpeechCommandDebugPanel>();
            debugPanel.Configure(
                pipeline,
                panel.transcriptInput,
                panel.parseButton,
                panel.statusText,
                panel.commandText,
                panel.rawJsonText);

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[NLP] Speech command debug panel created/updated in active scene. Scene was marked dirty but not saved.");
        }

        [MenuItem("Tools/NLP/Integrate Recovery UI Into Test Scene")]
        public static void IntegrateRecoveryUiIntoTestScene()
        {
            UnityEngine.SceneManagement.Scene scene = EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
            STTTestController controller = Object.FindFirstObjectByType<STTTestController>();
            if (controller == null)
            {
                throw new MissingReferenceException("Test.unity içinde STTTestController bulunamadı.");
            }

            SerializedObject controllerObject = new SerializedObject(controller);
            TMP_Text jsonText = controllerObject.FindProperty("nlpJsonResultText").objectReferenceValue as TMP_Text;
            if (jsonText == null || jsonText.transform.parent == null)
            {
                throw new MissingReferenceException("STTTestController.nlpJsonResultText veya mevcut ScrollView/Content bağlantısı eksik.");
            }

            Transform content = jsonText.transform.parent;
            Transform oldRoot = content.Find(RecoveryRootName);
            if (oldRoot != null)
            {
                Object.DestroyImmediate(oldRoot.gameObject);
            }

            GameObject root = CreateUiObject(RecoveryRootName, content);
            root.transform.SetSiblingIndex(jsonText.transform.GetSiblingIndex());
            VerticalLayoutGroup rootLayout = root.AddComponent<VerticalLayoutGroup>();
            rootLayout.spacing = 12f;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;
            rootLayout.childForceExpandWidth = true;
            rootLayout.childForceExpandHeight = false;
            ContentSizeFitter rootFitter = root.AddComponent<ContentSizeFitter>();
            rootFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject correctionPanel = CreateRecoveryPanel("CorrectionInfoPanel", root.transform, new Color(0.10f, 0.32f, 0.24f, 0.96f), 96f);
            TMP_Text correctionText = CreatePaddedText("CorrectionInfoText", correctionPanel.transform, 24, FontStyles.Normal, new Color(0.88f, 1f, 0.91f, 1f));

            GameObject suggestionPanel = CreateRecoveryPanel("SuggestionPanel", root.transform, new Color(0.13f, 0.18f, 0.30f, 0.98f), 320f);
            VerticalLayoutGroup suggestionLayout = suggestionPanel.AddComponent<VerticalLayoutGroup>();
            suggestionLayout.padding = new RectOffset(18, 18, 18, 18);
            suggestionLayout.spacing = 10f;
            suggestionLayout.childControlWidth = true;
            suggestionLayout.childControlHeight = true;
            suggestionLayout.childForceExpandWidth = true;
            suggestionLayout.childForceExpandHeight = false;
            ContentSizeFitter suggestionFitter = suggestionPanel.AddComponent<ContentSizeFitter>();
            suggestionFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            TMP_Text suggestionTitle = CreateLayoutText("SuggestionTitleText", suggestionPanel.transform, "Bu komutu mu demek istediniz?", 27, FontStyles.Bold, Color.white, 48f);
            suggestionTitle.alignment = TextAlignmentOptions.Left;
            TMP_Text suggestionMessage = CreateLayoutText("SuggestionMessageText", suggestionPanel.transform, string.Empty, 24, FontStyles.Normal, new Color(0.86f, 0.91f, 1f, 1f), 62f);

            GameObject optionsObject = CreateUiObject("SuggestionOptions", suggestionPanel.transform);
            VerticalLayoutGroup optionsLayout = optionsObject.AddComponent<VerticalLayoutGroup>();
            optionsLayout.spacing = 10f;
            optionsLayout.childControlWidth = true;
            optionsLayout.childControlHeight = true;
            optionsLayout.childForceExpandWidth = true;
            optionsLayout.childForceExpandHeight = false;
            ContentSizeFitter optionsFitter = optionsObject.AddComponent<ContentSizeFitter>();
            optionsFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            CommandSuggestionOptionView optionTemplate = CreateSuggestionTemplate(optionsObject.transform);
            TMP_Text suggestionStatus = CreateLayoutText("SuggestionStatusText", suggestionPanel.transform, string.Empty, 23, FontStyles.Italic, new Color(0.85f, 0.90f, 1f, 1f), 42f);
            Button rejectButton = CreateRecoveryButton("RejectSuggestionButton", suggestionPanel.transform, "Reddet ve tekrar söyle", new Color(0.48f, 0.16f, 0.18f, 1f));

            TMP_Text feedbackText = CreateLayoutText("UserFeedbackText", root.transform, string.Empty, 25, FontStyles.Bold, new Color(1f, 0.88f, 0.55f, 1f), 88f);
            TMP_Text debugText = CreateLayoutText("RecoveryDebugText", root.transform, string.Empty, 22, FontStyles.Normal, new Color(0.68f, 0.88f, 1f, 1f), 255f);

            SpeechCommandRecoveryView view = root.AddComponent<SpeechCommandRecoveryView>();
            view.Configure(
                correctionPanel,
                correctionText,
                suggestionPanel,
                suggestionMessage,
                optionsObject.GetComponent<RectTransform>(),
                optionTemplate,
                suggestionStatus,
                rejectButton,
                feedbackText,
                debugText);

            controllerObject.FindProperty("commandRecoveryView").objectReferenceValue = view;
            controllerObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(view);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, TestScenePath))
            {
                throw new UnityException("Test.unity kaydedilemedi.");
            }

            Debug.Log("[NLP] Test.unity recovery UI integrated and saved.");
        }

        private static GameObject CreateRecoveryPanel(string name, Transform parent, Color color, float minHeight)
        {
            GameObject panel = CreateUiObject(name, parent);
            Image image = panel.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            LayoutElement layout = panel.AddComponent<LayoutElement>();
            layout.minHeight = minHeight;
            layout.preferredHeight = minHeight;
            return panel;
        }

        private static TMP_Text CreatePaddedText(string name, Transform parent, int size, FontStyles style, Color color)
        {
            TMP_Text text = CreateText(name, parent, string.Empty, size, style);
            text.color = color;
            text.margin = new Vector4(18f, 12f, 18f, 12f);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            Stretch(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return text;
        }

        private static TMP_Text CreateLayoutText(string name, Transform parent, string value, int size, FontStyles style, Color color, float height)
        {
            TMP_Text text = CreateText(name, parent, value, size, style);
            text.color = color;
            text.textWrappingMode = TextWrappingModes.Normal;
            LayoutElement layout = text.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
            return text;
        }

        private static CommandSuggestionOptionView CreateSuggestionTemplate(Transform parent)
        {
            GameObject templateObject = CreateUiObject("SuggestionOptionTemplate", parent);
            Image image = templateObject.AddComponent<Image>();
            image.color = new Color(0.16f, 0.45f, 0.82f, 1f);
            image.raycastTarget = true;
            Button button = templateObject.AddComponent<Button>();
            LayoutElement layout = templateObject.AddComponent<LayoutElement>();
            layout.minHeight = 76f;
            layout.preferredHeight = 76f;
            TMP_Text label = CreateText("Label", templateObject.transform, string.Empty, 24, FontStyles.Bold);
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.margin = new Vector4(16f, 8f, 16f, 8f);
            Stretch(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            CommandSuggestionOptionView option = templateObject.AddComponent<CommandSuggestionOptionView>();
            option.Configure(button, label);
            templateObject.SetActive(false);
            return option;
        }

        private static Button CreateRecoveryButton(string name, Transform parent, string labelText, Color color)
        {
            GameObject buttonObject = CreateUiObject(name, parent);
            Image image = buttonObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            Button button = buttonObject.AddComponent<Button>();
            LayoutElement layout = buttonObject.AddComponent<LayoutElement>();
            layout.minHeight = 76f;
            layout.preferredHeight = 76f;
            TMP_Text label = CreateText("Label", buttonObject.transform, labelText, 24, FontStyles.Bold);
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            Stretch(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return button;
        }

        private static Canvas EnsureCanvas()
        {
            Canvas canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas != null)
            {
                return canvas;
            }

            GameObject canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create NLP Debug Canvas");

            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;

            return canvas;
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null)
            {
                return;
            }

            GameObject eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(eventSystem, "Create NLP Debug EventSystem");
        }

        private static SpeechCommandPipeline CreatePipeline(Transform root)
        {
            GameObject pipelineObject = new GameObject(PipelineName);
            Undo.RegisterCreatedObjectUndo(pipelineObject, "Create Speech Command Pipeline");
            pipelineObject.transform.SetParent(root, false);
            return pipelineObject.AddComponent<SpeechCommandPipeline>();
        }

        private static BuiltPanel CreatePanel(Transform root)
        {
            GameObject panelObject = CreateUiObject("Panel_Background", root);
            Image panelImage = panelObject.AddComponent<Image>();
            panelImage.color = new Color(0.07f, 0.08f, 0.09f, 0.94f);
            panelImage.raycastTarget = true;
            Stretch(panelObject.GetComponent<RectTransform>(), new Vector2(0.04f, 0.04f), new Vector2(0.96f, 0.96f), Vector2.zero, Vector2.zero);

            TMP_Text titleText = CreateText("TitleText", panelObject.transform, "Speech Command Debug", 36, FontStyles.Bold);
            SetRect(titleText.rectTransform, new Vector2(0.04f, 0.91f), new Vector2(0.96f, 0.985f), Vector2.zero, Vector2.zero);

            TMP_InputField transcriptInput = CreateInput(panelObject.transform);
            SetRect(transcriptInput.GetComponent<RectTransform>(), new Vector2(0.04f, 0.80f), new Vector2(0.72f, 0.895f), Vector2.zero, Vector2.zero);

            Button parseButton = CreateButton(panelObject.transform);
            SetRect(parseButton.GetComponent<RectTransform>(), new Vector2(0.74f, 0.80f), new Vector2(0.96f, 0.895f), Vector2.zero, Vector2.zero);

            TMP_Text statusText = CreateText("StatusText", panelObject.transform, "Hazır.", 26, FontStyles.Normal);
            statusText.color = new Color(0.83f, 0.90f, 1f, 1f);
            SetRect(statusText.rectTransform, new Vector2(0.04f, 0.70f), new Vector2(0.96f, 0.78f), Vector2.zero, Vector2.zero);

            TMP_Text commandText = CreateText("CommandText", panelObject.transform, "Komut bekleniyor.", 25, FontStyles.Normal);
            commandText.color = new Color(0.92f, 0.94f, 0.88f, 1f);
            SetRect(commandText.rectTransform, new Vector2(0.04f, 0.50f), new Vector2(0.96f, 0.69f), Vector2.zero, Vector2.zero);

            TMP_Text rawJsonText = CreateRawJsonScrollView(panelObject.transform);

            return new BuiltPanel
            {
                panelObject = panelObject,
                transcriptInput = transcriptInput,
                parseButton = parseButton,
                statusText = statusText,
                commandText = commandText,
                rawJsonText = rawJsonText,
            };
        }

        private static TMP_InputField CreateInput(Transform parent)
        {
            GameObject inputObject = CreateUiObject("TranscriptInput", parent);
            Image background = inputObject.AddComponent<Image>();
            background.color = new Color(0.96f, 0.97f, 0.98f, 1f);
            background.raycastTarget = true;

            TMP_InputField input = inputObject.AddComponent<TMP_InputField>();
            input.text = TestPhrase;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.textViewport = CreateInputViewport(inputObject.transform);
            input.textComponent = CreateInputText(input.textViewport, "InputText", string.Empty, 26, new Color(0.08f, 0.09f, 0.10f, 1f));
            input.placeholder = CreateInputText(input.textViewport, "Placeholder", TestPhrase, 26, new Color(0.35f, 0.38f, 0.40f, 0.65f));
            return input;
        }

        private static RectTransform CreateInputViewport(Transform parent)
        {
            GameObject viewport = CreateUiObject("TextViewport", parent);
            viewport.AddComponent<RectMask2D>();
            RectTransform rect = viewport.GetComponent<RectTransform>();
            Stretch(rect, Vector2.zero, Vector2.one, new Vector2(18f, 8f), new Vector2(-18f, -8f));
            return rect;
        }

        private static TMP_Text CreateInputText(Transform parent, string name, string value, int size, Color color)
        {
            TMP_Text text = CreateText(name, parent, value, size, FontStyles.Normal);
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            Stretch(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return text;
        }

        private static Button CreateButton(Transform parent)
        {
            GameObject buttonObject = CreateUiObject("ParseButton", parent);
            Image image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.18f, 0.44f, 0.92f, 1f);
            image.raycastTarget = true;

            Button button = buttonObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(0.24f, 0.52f, 1f, 1f);
            colors.pressedColor = new Color(0.12f, 0.32f, 0.70f, 1f);
            colors.disabledColor = new Color(0.22f, 0.25f, 0.29f, 0.7f);
            button.colors = colors;

            TMP_Text label = CreateText("Text", buttonObject.transform, "Parse", 28, FontStyles.Bold);
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            Stretch(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return button;
        }

        private static TMP_Text CreateRawJsonScrollView(Transform parent)
        {
            GameObject scrollObject = CreateUiObject("RawJsonScrollView", parent);
            Image scrollImage = scrollObject.AddComponent<Image>();
            scrollImage.color = new Color(0.015f, 0.017f, 0.020f, 0.98f);
            scrollImage.raycastTarget = true;
            ScrollRect scrollRect = scrollObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            SetRect(scrollObject.GetComponent<RectTransform>(), new Vector2(0.04f, 0.04f), new Vector2(0.96f, 0.48f), Vector2.zero, Vector2.zero);

            GameObject viewport = CreateUiObject("Viewport", scrollObject.transform);
            Image viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.02f);
            viewportImage.raycastTarget = true;
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            Stretch(viewport.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(10f, 10f), new Vector2(-10f, -10f));

            GameObject content = CreateUiObject("Content", viewport.transform);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.offsetMin = new Vector2(0f, -900f);
            contentRect.offsetMax = new Vector2(0f, 0f);
            VerticalLayoutGroup layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            TMP_Text rawJsonText = CreateText("RawJsonText", content.transform, string.Empty, 19, FontStyles.Normal);
            rawJsonText.color = new Color(0.78f, 0.92f, 0.82f, 1f);
            rawJsonText.alignment = TextAlignmentOptions.TopLeft;
            rawJsonText.enableWordWrapping = true;
            rawJsonText.raycastTarget = false;

            scrollRect.viewport = viewport.GetComponent<RectTransform>();
            scrollRect.content = contentRect;
            return rawJsonText;
        }

        private static TMP_Text CreateText(string name, Transform parent, string value, int size, FontStyles style)
        {
            GameObject textObject = CreateUiObject(name, parent);
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Left;
            text.enableWordWrapping = true;
            text.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null)
            {
                text.font = TMP_Settings.defaultFontAsset;
            }
            return text;
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(gameObject, "Create NLP Debug UI");
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }

        private static Transform FindInScene(string name)
        {
            foreach (GameObject root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                Transform match = FindChildRecursive(root.transform, name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name)
            {
                return parent;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform match = FindChildRecursive(parent.GetChild(i), name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private sealed class BuiltPanel
        {
            public GameObject panelObject;
            public TMP_InputField transcriptInput;
            public Button parseButton;
            public TMP_Text statusText;
            public TMP_Text commandText;
            public TMP_Text rawJsonText;
        }
    }
}
