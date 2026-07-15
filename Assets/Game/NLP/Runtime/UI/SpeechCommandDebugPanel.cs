using System.Threading;
using System.Threading.Tasks;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.NLP
{
    public sealed class SpeechCommandDebugPanel : MonoBehaviour
    {
        private const float PipelineTimeoutSeconds = 90f;
        private const int LogChunkSize = 3000;

        [Header("Pipeline")]
        [SerializeField] private SpeechCommandPipeline pipeline;

        [Header("UI")]
        [SerializeField] private TMP_InputField transcriptInput;
        [SerializeField] private Button parseButton;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text commandText;
        [SerializeField] private TMP_Text rawJsonText;

        [Header("Mobile Readability")]
        [SerializeField] private float transcriptFontSize = 24f;
        [SerializeField] private float statusFontSize = 24f;
        [SerializeField] private float commandFontSize = 26f;
        [SerializeField] private float rawJsonFontSize = 24f;
        [SerializeField] private float rawJsonLineSpacing = 4f;

        [Header("Raw JSON Auto Layout")]
        [SerializeField] private RectTransform rawJsonContent;
        [SerializeField] private ScrollRect rawJsonScrollRect;
        [SerializeField] private LayoutElement rawJsonLayoutElement;
        [SerializeField] private float rawJsonMinHeight = 180f;
        [SerializeField] private float rawJsonPadding = 32f;

        private CancellationTokenSource cancellation;
        private bool busy;

        private void Awake()
        {
            ApplyMobileReadableTextSettings();
            EnsureRawJsonLayoutRefs();
        }

        private void OnEnable()
        {
            if (parseButton != null)
            {
                parseButton.onClick.AddListener(ParseCurrentText);
            }
        }

        private void OnDisable()
        {
            if (parseButton != null)
            {
                parseButton.onClick.RemoveListener(ParseCurrentText);
            }

            cancellation?.Cancel();
            SetBusy(false);
        }

        private void OnDestroy()
        {
            cancellation?.Cancel();
            cancellation?.Dispose();
        }

        public async void ParseCurrentText()
        {
            if (!isActiveAndEnabled)
            {
                Debug.Log("[SpeechCommandDebugPanel] ParseCurrentText ignored because panel is disabled.");
                return;
            }

            if (busy)
            {
                return;
            }

            if (pipeline == null)
            {
                SetStatus("Pipeline atanmadı.");
                return;
            }

            string transcript = transcriptInput != null ? transcriptInput.text : string.Empty;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                SetStatus("Komut metni boş.");
                SetCommandText("Komut bekleniyor.");
                SetRawJson(string.Empty);
                return;
            }

            cancellation?.Cancel();
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(System.TimeSpan.FromSeconds(PipelineTimeoutSeconds));

            SetBusy(true);
            SetStatus("Analiz ediliyor... " + CurrentLlmConfigSummary());
            SetCommandText("İşleniyor...");
            SetRawJson(string.Empty);

            try
            {
                Task<SpeechCommandPipelineResult> pipelineTask = pipeline.ProcessTranscriptAsync(transcript, cancellation.Token);
                Task timeoutTask = Task.Delay(System.TimeSpan.FromSeconds(PipelineTimeoutSeconds));
                Task completedTask = await Task.WhenAny(pipelineTask, timeoutTask);

                if (completedTask != pipelineTask)
                {
                    cancellation.Cancel();
                    ObserveTimedOutPipelineTask(pipelineTask);
                    ShowTimeout(transcript);
                    return;
                }

                SpeechCommandPipelineResult result = await pipelineTask;
                ShowResult(result);
            }
            catch (System.OperationCanceledException)
            {
                ShowTimeout(transcript);
            }
            catch (System.Exception ex)
            {
                SetStatus("Hata: " + ex.Message);
                SetCommandText("Pipeline exception\nstage: exception\nerror: " + ex.Message);
                SetRawJson(ex.ToString());
            }
            finally
            {
                SetBusy(false);
            }
        }

        public void ProcessTranscriptFromExternal(string transcript)
        {
            if (transcriptInput != null)
            {
                transcriptInput.text = transcript ?? string.Empty;
            }

            if (!isActiveAndEnabled)
            {
                Debug.Log("[SpeechCommandDebugPanel] ProcessTranscriptFromExternal ignored because panel is disabled.");
                return;
            }

            ParseCurrentText();
        }

        public void Configure(
            SpeechCommandPipeline commandPipeline,
            TMP_InputField input,
            Button button,
            TMP_Text status,
            TMP_Text command,
            TMP_Text rawJson)
        {
            pipeline = commandPipeline;
            transcriptInput = input;

            if (parseButton != null)
            {
                parseButton.onClick.RemoveListener(ParseCurrentText);
            }

            parseButton = button;
            if (Application.isPlaying && isActiveAndEnabled && parseButton != null)
            {
                parseButton.onClick.AddListener(ParseCurrentText);
            }

            statusText = status;
            commandText = command;
            rawJsonText = rawJson;

            ApplyMobileReadableTextSettings();
            EnsureRawJsonLayoutRefs();
            RefreshRawJsonLayout();
        }

        private void ShowResult(SpeechCommandPipelineResult result)
        {
            if (result == null)
            {
                SetStatus("Pipeline sonucu boş.");
                SetCommandText("stage: pipeline_null\nerror: Pipeline sonucu boş.");
                SetRawJson(string.Empty);
                return;
            }

            LlmCommandResult llm = result.llm;
            ValidatedCommandResult command = result != null ? result.validated : null;
            if (command == null)
            {
                string stage = !string.IsNullOrWhiteSpace(llm?.stage) ? llm.stage : "validated_command_null";
                string error = !string.IsNullOrWhiteSpace(llm?.error) ? llm.error : "Final command JSON üretilemedi.";
                string summary =
                    "ok: false"
                    + "\nstage: " + stage
                    + "\nresolution_source: " + DisplayValue(result.resolutionSource)
                    + "\nllm_called: " + BoolText(result.llmCalled)
                    + "\ninference_ran: " + BoolText(llm != null && llm.inference_ran)
                    + "\njson_repair_applied: " + BoolText(llm != null && llm.json_repair_applied)
                    + "\naction: " + DisplayValue(ExtractAction(llm))
                    + "\nconfidence: " + ExtractConfidence(null, llm).ToString("0.00")
                    + "\nerror: " + error;
                string debugJson = BuildDebugJson(result);
                SetStatus("Komut üretilemedi. stage: " + stage);
                SetCommandText(summary);
                SetRawJson(debugJson);
                LogLong("[SpeechCommandDebugPanel] validated_command:", summary);
                LogLong("[SpeechCommandDebugPanel] raw_json:", debugJson);
                LogLong("[SpeechCommandDebugPanel] active_llm_config:", result.activeLlmConfigJson);
                LogLong("[SpeechCommandDebugPanel] active_llm_config_summary:", result.activeLlmConfigSummary);
                return;
            }

            string statusStage = FirstNonEmpty(llm?.stage, "validated");
            string commandSummary = BuildCommandSummary(command, llm, statusStage);
            string rawDebugJson = BuildDebugJson(result);
            SetStatus(llm != null && llm.ok ? "Komut doğrulandı." : "Komut doğrulanamadı. stage: " + statusStage);
            SetCommandText(commandSummary);
            SetRawJson(rawDebugJson);
            LogLong("[SpeechCommandDebugPanel] validated_command:", commandSummary);
            LogLong("[SpeechCommandDebugPanel] raw_json:", rawDebugJson);
            LogLong("[SpeechCommandDebugPanel] active_llm_config:", result.activeLlmConfigJson);
            LogLong("[SpeechCommandDebugPanel] active_llm_config_summary:", result.activeLlmConfigSummary);
        }

        private void ApplyMobileReadableTextSettings()
        {
            if (transcriptInput != null)
            {
                if (transcriptInput.textComponent != null)
                {
                    ApplyReadableTmpSettings(transcriptInput.textComponent, transcriptFontSize, 2f);
                }

                if (transcriptInput.placeholder is TMP_Text placeholderText)
                {
                    ApplyReadableTmpSettings(placeholderText, transcriptFontSize, 2f);
                }
            }

            ApplyReadableTmpSettings(statusText, statusFontSize, 2f);
            ApplyReadableTmpSettings(commandText, commandFontSize, 3f);
            ApplyReadableTmpSettings(rawJsonText, rawJsonFontSize, rawJsonLineSpacing);
        }

        private static void ApplyReadableTmpSettings(TMP_Text text, float fontSize, float lineSpacing)
        {
            if (text == null)
            {
                return;
            }

            text.enableAutoSizing = false;
            text.fontSize = fontSize;
            text.lineSpacing = lineSpacing;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = TextAlignmentOptions.TopLeft;
        }

        private void SetStatus(string value)
        {
            if (statusText != null)
            {
                statusText.text = value;
                statusText.ForceMeshUpdate();
            }
        }

        private void SetCommandText(string value)
        {
            if (commandText != null)
            {
                commandText.text = value;
                commandText.ForceMeshUpdate();
            }
        }

        private void SetRawJson(string value)
        {
            if (rawJsonText == null)
            {
                return;
            }

            ApplyReadableTmpSettings(rawJsonText, rawJsonFontSize, rawJsonLineSpacing);

            rawJsonText.text = value ?? string.Empty;
            rawJsonText.ForceMeshUpdate();

            RefreshRawJsonLayout();
        }

        private void RefreshRawJsonLayout()
        {
            if (rawJsonText == null)
            {
                return;
            }

            EnsureRawJsonLayoutRefs();

            Canvas.ForceUpdateCanvases();
            rawJsonText.ForceMeshUpdate();

            float width = rawJsonText.rectTransform.rect.width;
            if (width <= 1f && rawJsonContent != null)
            {
                width = rawJsonContent.rect.width;
            }

            if (width <= 1f)
            {
                width = 900f;
            }

            float preferredHeight = rawJsonText.GetPreferredValues(
                rawJsonText.text,
                width,
                Mathf.Infinity
            ).y + rawJsonPadding;

            if (rawJsonLayoutElement != null)
            {
                rawJsonLayoutElement.ignoreLayout = false;
                rawJsonLayoutElement.preferredHeight = Mathf.Max(rawJsonMinHeight, preferredHeight);
            }

            if (rawJsonContent != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rawJsonContent);
            }

            if (rawJsonScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                rawJsonScrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private void EnsureRawJsonLayoutRefs()
        {
            if (rawJsonText == null)
            {
                return;
            }

            if (rawJsonLayoutElement == null)
            {
                rawJsonLayoutElement = rawJsonText.GetComponent<LayoutElement>();
                if (rawJsonLayoutElement == null)
                {
                    rawJsonLayoutElement = rawJsonText.gameObject.AddComponent<LayoutElement>();
                }
            }

            if (rawJsonContent == null)
            {
                rawJsonContent = rawJsonText.transform.parent as RectTransform;
            }

            if (rawJsonScrollRect == null)
            {
                rawJsonScrollRect = rawJsonText.GetComponentInParent<ScrollRect>();
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            if (parseButton != null)
            {
                parseButton.interactable = !value;
            }
        }

        private void ShowTimeout(string transcript)
        {
            string activeLlmConfigJson = CurrentLlmDebugJson();
            string activeLlmConfigSummary = CurrentLlmConfigSummary();
            SetStatus("Zaman aşımı. stage: generation_timeout");
            SetCommandText(
                "ok: false"
                + "\nstage: generation_timeout"
                + "\nerror: Pipeline 90 saniye içinde tamamlanmadı."
                + "\ninference_ran: unknown");
            SetRawJson(BuildTimeoutDebugJson(transcript, activeLlmConfigJson, activeLlmConfigSummary));
            Debug.Log("[SpeechCommandDebugPanel] timeout active_llm_config_summary: " + activeLlmConfigSummary);
        }

        private static void ObserveTimedOutPipelineTask(Task<SpeechCommandPipelineResult> pipelineTask)
        {
            if (pipelineTask == null)
            {
                return;
            }

            _ = pipelineTask.ContinueWith(task =>
            {
                if (task.IsFaulted && task.Exception != null)
                {
                    Debug.LogWarning("[SpeechCommandDebugPanel] Timed-out pipeline finished with exception: " + task.Exception.GetBaseException().Message);
                }
            }, TaskScheduler.Default);
        }

        private static string BuildDebugJson(SpeechCommandPipelineResult result)
        {
            StringBuilder builder = new StringBuilder(8192);
            builder.AppendLine("{");
            builder.AppendLine("  \"transcript\": \"" + EscapeForDisplay(result.transcript) + "\",");
            builder.AppendLine("  \"active_llm_config\": " + SafeJsonObject(result.activeLlmConfigJson) + ",");
            builder.AppendLine("  \"active_llm_config_summary\": \"" + EscapeForDisplay(result.activeLlmConfigSummary) + "\",");
            builder.AppendLine("  \"resolution_source\": \"" + EscapeForDisplay(result.resolutionSource) + "\",");
            builder.AppendLine("  \"llm_called\": " + (result.llmCalled ? "true" : "false") + ",");
            builder.AppendLine("  \"final_command\": " + (string.IsNullOrWhiteSpace(result.finalJson) ? "null" : result.finalJson) + ",");
            AppendValidatedSummary(builder, result);
            builder.AppendLine(",");
            AppendJsonField(builder, "nlp_spacy_output", result.nlpJson, false);
            builder.AppendLine(",");
            AppendJsonField(builder, "llm_bridge_output", result.llmJson, false);
            builder.AppendLine();
            builder.Append("}");
            return builder.ToString();
        }

        private static string BuildTimeoutDebugJson(
            string transcript,
            string activeLlmConfigJson,
            string activeLlmConfigSummary)
        {
            StringBuilder builder = new StringBuilder(512);
            builder.AppendLine("{");
            builder.AppendLine("  \"ok\": false,");
            builder.AppendLine("  \"stage\": \"generation_timeout\",");
            builder.AppendLine("  \"inference_ran\": \"unknown\",");
            builder.AppendLine("  \"timeout_seconds\": " + PipelineTimeoutSeconds.ToString("0") + ",");
            builder.AppendLine("  \"active_llm_config\": " + SafeJsonObject(activeLlmConfigJson) + ",");
            builder.AppendLine("  \"active_llm_config_summary\": \"" + EscapeForDisplay(activeLlmConfigSummary) + "\",");
            builder.AppendLine("  \"transcript\": \"" + EscapeForDisplay(transcript) + "\"");
            builder.Append("}");
            return builder.ToString();
        }

        private string CurrentLlmConfigSummary()
        {
            return pipeline != null
                ? pipeline.ActiveLlmConfigSummary
                : SpeechCommandPipelineConfig.BuildLlmConfigSummary(SpeechCommandPipelineConfig.DefaultConfigJson);
        }

        private string CurrentLlmDebugJson()
        {
            return pipeline != null
                ? pipeline.ActiveLlmConfigJson
                : SpeechCommandPipelineConfig.BuildLlmDebugJson(SpeechCommandPipelineConfig.DefaultConfigJson);
        }

        private static string SafeJsonObject(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "{}";
            }

            string trimmed = value.Trim();
            return trimmed.StartsWith("{") && trimmed.EndsWith("}") ? trimmed : "{}";
        }

        private static string BuildCommandSummary(ValidatedCommandResult command, LlmCommandResult llm, string stage)
        {
            string action = FirstNonEmpty(command?.action, ExtractAction(llm));
            float confidence = ExtractConfidence(command, llm);
            string error = llm?.error;
            string errors = JoinArray(llm?.errors);
            string warnings = JoinArray(llm?.warnings);
            bool inferenceRan = llm != null && llm.inference_ran;

            StringBuilder builder = new StringBuilder(512);
            builder.Append("ok: ").Append(BoolText(llm != null && llm.ok));
            builder.Append("\ncontract_version: ").Append(DisplayValue(llm?.contract_version));
            builder.Append("\nstage: ").Append(DisplayValue(FirstNonEmpty(stage, llm?.stage)));
            builder.Append("\nresolution_source: ").Append(DisplayValue(llm?.resolution_source));
            builder.Append("\nresolution_status: ").Append(DisplayValue(llm?.resolution_status));
            builder.Append("\nllm_called: ").Append(BoolText(llm != null && llm.llm_called));
            builder.Append("\naction_llm_called: ").Append(BoolText(llm != null && llm.action_llm_called));
            builder.Append("\nslot_llm_called: ").Append(BoolText(llm != null && llm.slot_llm_called));
            builder.Append("\nslot_evidence_valid: ").Append(BoolText(llm != null && llm.slot_evidence_valid));
            builder.Append("\nanalysis_input: ").Append(DisplayValue(llm?.analysis_input));
            builder.Append("\ndetected_object_phrase: ").Append(DisplayValue(llm?.detected_object_phrase));
            builder.Append("\ninference_ran: ").Append(BoolText(inferenceRan));
            builder.Append("\nllm_output_valid: ").Append(BoolText(llm != null && llm.llm_output_valid));
            builder.Append("\nbalanced_json: ").Append(BoolText(llm != null && llm.balanced_json));
            builder.Append("\ngrammar_constrained: ").Append(BoolText(llm != null && llm.grammar_constrained));
            if (llm != null && llm.llm_called)
            {
                builder.Append("\nllm_prompt_tokens: ").Append(llm.llm_prompt_tokens).Append(llm.llm_prompt_tokens_exact ? "" : " (estimate)");
                builder.Append("\nllm_output_tokens: ").Append(llm.llm_output_tokens).Append(llm.llm_output_tokens_exact ? "" : " (estimate)");
                builder.Append("\nllm_generation_duration: ").Append(llm.llm_generation_duration).Append(" ms");
            }
            builder.Append("\njson_repair_applied: ").Append(BoolText(llm != null && llm.json_repair_applied));
            builder.Append("\naction: ").Append(DisplayValue(action));
            builder.Append("\nconfidence: ").Append(confidence.ToString("0.00"));
            if (command != null)
            {
                builder.Append("\ndirection: ").Append(DisplayValue(command.direction));
                builder.Append("\nturn_direction: ").Append(DisplayValue(command.turn_direction));
                builder.Append("\ndistance_m: ").Append(DisplayNullable(command.distance_m));
                builder.Append("\nangle_deg: ").Append(DisplayNullable(command.angle_deg));
                builder.Append("\nspeed_kmh: ").Append(DisplayNullable(command.speed_kmh));
                builder.Append("\nobject: ").Append(DisplayValue(command.@object));
                builder.Append("\nmarker: ").Append(DisplayValue(command.color)).Append("/").Append(DisplayValue(command.shape)).Append(" #").Append(command.marker_index?.ToString() ?? "null");
                builder.Append("\nlocation: ").Append(DisplayValue(command.location_keyword));
                builder.Append("\nmissing: ").Append(DisplayValue(command.missing_field)).Append(" - ").Append(DisplayValue(command.missing_message));
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                builder.Append("\nerror: ").Append(error);
            }
            if (!string.IsNullOrWhiteSpace(errors))
            {
                builder.Append("\nerrors: ").Append(errors);
            }
            if (!string.IsNullOrWhiteSpace(warnings))
            {
                builder.Append("\nwarnings: ").Append(warnings);
            }
            return builder.ToString();
        }

        private static void AppendValidatedSummary(StringBuilder builder, SpeechCommandPipelineResult result)
        {
            ValidatedCommandResult command = result != null ? result.validated : null;
            LlmCommandResult llm = result != null ? result.llm : null;
            string stage = llm?.stage;

            builder.AppendLine("  \"validated_summary\": {");
            builder.AppendLine("    \"ok\": " + ((llm != null && llm.ok) ? "true" : "false") + ",");
            builder.AppendLine("    \"stage\": \"" + EscapeForDisplay(stage) + "\",");
            builder.AppendLine("    \"resolution_source\": \"" + EscapeForDisplay(result?.resolutionSource) + "\",");
            builder.AppendLine("    \"llm_called\": " + ((result != null && result.llmCalled) ? "true" : "false") + ",");
            builder.AppendLine("    \"inference_ran\": " + ((llm != null && llm.inference_ran) ? "true" : "false") + ",");
            builder.AppendLine("    \"llm_output_valid\": " + ((llm != null && llm.llm_output_valid) ? "true" : "false") + ",");
            builder.AppendLine("    \"balanced_json\": " + ((llm != null && llm.balanced_json) ? "true" : "false") + ",");
            builder.AppendLine("    \"failure_stage\": \"" + EscapeForDisplay(llm?.failure_stage) + "\",");
            builder.AppendLine("    \"json_repair_applied\": " + ((llm != null && llm.json_repair_applied) ? "true" : "false") + ",");
            builder.AppendLine("    \"action\": \"" + EscapeForDisplay(FirstNonEmpty(command?.action, ExtractAction(llm))) + "\",");
            builder.AppendLine("    \"confidence\": " + ExtractConfidence(command, llm).ToString("0.00") + ",");
            builder.AppendLine("    \"error\": \"" + EscapeForDisplay(llm?.error) + "\",");
            builder.AppendLine("    \"errors\": \"" + EscapeForDisplay(JoinArray(llm?.errors)) + "\",");
            builder.AppendLine("    \"warnings\": \"" + EscapeForDisplay(JoinArray(llm?.warnings)) + "\",");
            builder.AppendLine("    \"public_command\": " + DroneCommandJsonParser.ToPublicJson(command));
            builder.Append("  }");
        }

        private static void AppendJsonField(StringBuilder builder, string name, string rawJson, bool trailingComma)
        {
            builder.Append("  \"").Append(name).Append("\": ");
            string value = string.IsNullOrWhiteSpace(rawJson) ? "null" : rawJson.Trim();
            if (LooksLikeJson(value))
            {
                builder.Append(IndentMultiline(PrettyJson(value), 2));
            }
            else
            {
                builder.Append(QuoteMultiline(value));
            }
            if (trailingComma)
            {
                builder.Append(",");
            }
        }

        private static string PrettyJson(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "null";
            }

            StringBuilder builder = new StringBuilder(value.Length + 256);
            int indent = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (escaped)
                {
                    builder.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    builder.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append(c);
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    builder.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '{':
                    case '[':
                        builder.Append(c);
                        builder.Append('\n');
                        AppendIndent(builder, ++indent);
                        break;
                    case '}':
                    case ']':
                        builder.Append('\n');
                        AppendIndent(builder, --indent);
                        builder.Append(c);
                        break;
                    case ',':
                        builder.Append(c);
                        builder.Append('\n');
                        AppendIndent(builder, indent);
                        break;
                    case ':':
                        builder.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(c))
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }

            return builder.ToString();
        }

        private static string IndentMultiline(string value, int spaces)
        {
            string indent = new string(' ', spaces);
            return value.Replace("\n", "\n" + indent);
        }

        private static void AppendIndent(StringBuilder builder, int indent)
        {
            for (int i = 0; i < indent; i++)
            {
                builder.Append("  ");
            }
        }

        private static bool LooksLikeJson(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            return (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                || (trimmed.StartsWith("[") && trimmed.EndsWith("]"));
        }

        private static string ExtractAction(LlmCommandResult llm)
        {
            return FirstNonEmpty(
                llm?.validated_command?.action,
                llm?.normalized_command?.action);
        }

        private static float ExtractConfidence(ValidatedCommandResult command, LlmCommandResult llm)
        {
            if (command != null && command.confidence > 0d)
            {
                return (float)command.confidence;
            }
            if (llm?.validated_command != null && llm.validated_command.confidence > 0d)
            {
                return (float)llm.validated_command.confidence;
            }
            if (llm?.normalized_command != null && llm.normalized_command.confidence > 0d)
            {
                return (float)llm.normalized_command.confidence;
            }
            return 0f;
        }

        private static string DisplayNullable(double? value) => value.HasValue ? value.Value.ToString("0.###") : "null";

        private static string JoinArray(string[] values)
        {
            if (values == null || values.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(values[i].Trim());
            }

            return builder.ToString();
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
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static string DisplayValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        private static void LogLong(string tag, string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                Debug.Log(tag + " <empty>");
                return;
            }

            for (int offset = 0; offset < message.Length; offset += LogChunkSize)
            {
                int length = Mathf.Min(LogChunkSize, message.Length - offset);
                Debug.Log(tag + " " + message.Substring(offset, length));
            }
        }

        private static string QuoteMultiline(string value)
        {
            return "\"" + EscapeForDisplay(value ?? string.Empty) + "\"";
        }

        private static string EscapeForDisplay(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
