using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Game.NLP
{
    [Serializable]
    public sealed class SpeechCommandPipelineConfig
    {
        [SerializeField] private string configJson = DefaultConfigJson;

        public string ConfigJson => string.IsNullOrWhiteSpace(configJson) ? DefaultConfigJson : configJson;

        public string ActiveLlmConfigJson => BuildLlmDebugJson(ConfigJson);

        public string ActiveLlmConfigSummary => BuildLlmConfigSummary(ConfigJson);

        public const string DefaultConfigJson =
            "{\"version\":1,"
            + "\"target_policy\":\"open_vocabulary\","
            + "\"unknown_action\":\"unknown\","
            + "\"allowed_actions\":["
            + "{\"id\":\"move_to\",\"description\":\"Move to the target.\"},"
            + "{\"id\":\"move_above\",\"description\":\"Move above or onto the target.\"},"
            + "{\"id\":\"move_below\",\"description\":\"Move below the target.\"},"
            + "{\"id\":\"follow\",\"description\":\"Follow the target.\"},"
            + "{\"id\":\"stop\",\"description\":\"Stop or hover in place.\"},"
            + "{\"id\":\"pick_up\",\"description\":\"Pick up the target.\"},"
            + "{\"id\":\"drop\",\"description\":\"Drop the held item.\"},"
            + "{\"id\":\"look_at\",\"description\":\"Look at the target.\"},"
            + "{\"id\":\"rotate_to\",\"description\":\"Rotate toward the target or direction.\"},"
            + "{\"id\":\"unknown\",\"description\":\"No allowed action fits the utterance.\"}"
            + "],"
            + "\"llm\":{\"max_tokens\":128,\"temperature\":0.1,\"top_p\":0.95,\"top_k\":40,\"context_size\":1024,\"threads\":4}}";

        public static string BuildLlmDebugJson(string sourceConfigJson)
        {
            LlmRuntimeConfig llm = ParseLlmConfig(sourceConfigJson);
            StringBuilder builder = new StringBuilder(128);
            builder.Append("{");
            builder.Append("\"context_size\":").Append(llm.context_size).Append(",");
            builder.Append("\"threads\":").Append(llm.threads).Append(",");
            builder.Append("\"max_tokens\":").Append(llm.max_tokens).Append(",");
            builder.Append("\"temperature\":").Append(FloatString(llm.temperature)).Append(",");
            builder.Append("\"top_p\":").Append(FloatString(llm.top_p)).Append(",");
            builder.Append("\"top_k\":").Append(llm.top_k);
            builder.Append("}");
            return builder.ToString();
        }

        public static string BuildLlmConfigSummary(string sourceConfigJson)
        {
            LlmRuntimeConfig llm = ParseLlmConfig(sourceConfigJson);
            return "LLM context_size=" + llm.context_size
                + " threads=" + llm.threads
                + " max_tokens=" + llm.max_tokens
                + " temperature=" + FloatString(llm.temperature)
                + " top_p=" + FloatString(llm.top_p)
                + " top_k=" + llm.top_k;
        }

        private static LlmRuntimeConfig ParseLlmConfig(string sourceConfigJson)
        {
            LlmRuntimeConfig llm = TryParseLlmConfig(sourceConfigJson);
            if (!IsValidLlmConfig(llm))
            {
                llm = TryParseLlmConfig(DefaultConfigJson);
            }

            return IsValidLlmConfig(llm) ? llm : new LlmRuntimeConfig();
        }

        private static LlmRuntimeConfig TryParseLlmConfig(string sourceConfigJson)
        {
            if (string.IsNullOrWhiteSpace(sourceConfigJson))
            {
                return null;
            }

            string llmJson = ExtractObjectForKey(sourceConfigJson, "llm");
            if (string.IsNullOrWhiteSpace(llmJson))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<LlmRuntimeConfig>(llmJson);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpeechCommandPipelineConfig] LLM config JSON parse failed: " + ex.Message);
                return null;
            }
        }

        private static string ExtractObjectForKey(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            string quotedKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(quotedKey, StringComparison.Ordinal);
            while (keyIndex >= 0)
            {
                int colonIndex = FindNextNonStringChar(json, ':', keyIndex + quotedKey.Length);
                if (colonIndex < 0)
                {
                    return string.Empty;
                }

                int objectStart = FindNextNonWhitespace(json, colonIndex + 1);
                if (objectStart >= 0 && json[objectStart] == '{')
                {
                    int objectEnd = FindMatchingObjectEnd(json, objectStart);
                    return objectEnd > objectStart ? json.Substring(objectStart, objectEnd - objectStart + 1) : string.Empty;
                }

                keyIndex = json.IndexOf(quotedKey, keyIndex + quotedKey.Length, StringComparison.Ordinal);
            }

            return string.Empty;
        }

        private static int FindNextNonStringChar(string value, char target, int startIndex)
        {
            bool inString = false;
            bool escaped = false;

            for (int i = Math.Max(0, startIndex); i < value.Length; i++)
            {
                char c = value[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString && c == target)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindNextNonWhitespace(string value, int startIndex)
        {
            for (int i = Math.Max(0, startIndex); i < value.Length; i++)
            {
                if (!char.IsWhiteSpace(value[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindMatchingObjectEnd(string value, int objectStart)
        {
            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = objectStart; i < value.Length; i++)
            {
                char c = value[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static bool IsValidLlmConfig(LlmRuntimeConfig llm)
        {
            return llm != null
                && llm.context_size > 0
                && llm.threads > 0
                && llm.max_tokens > 0;
        }

        private static string FloatString(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        [Serializable]
        private sealed class LlmRuntimeConfig
        {
            public int context_size = 1024;
            public int threads = 4;
            public int max_tokens = 128;
            public float temperature = 0.1f;
            public float top_p = 0.95f;
            public int top_k = 40;
        }
    }
}
