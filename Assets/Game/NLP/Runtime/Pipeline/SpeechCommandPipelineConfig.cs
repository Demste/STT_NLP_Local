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
            "{\"version\":3,"
            + "\"unknown_action\":\"UNKNOWN\",\"missing_action\":\"MISSING_INFO\","
            + "\"allowed_actions\":["
            + "{\"id\":\"TAKE_OFF\"},{\"id\":\"LAND_HOME\"},{\"id\":\"LAND_CURRENT\"},{\"id\":\"LAND_AT_WAIT_POINT\"},"
            + "{\"id\":\"MOVE_DIRECTION\"},{\"id\":\"MOVE_VERTICAL\"},{\"id\":\"SET_SPEED\"},{\"id\":\"TURN\"},"
            + "{\"id\":\"FIND_OBJECT\"},{\"id\":\"TRACK_OBJECT\"},{\"id\":\"HOLD_OVER_OBJECT\"},{\"id\":\"OBSERVE_CURRENT\"},"
            + "{\"id\":\"GO_TO_LOCATION\"},{\"id\":\"HOVER\"},{\"id\":\"FIND_MARKER\"},{\"id\":\"STOP\"},{\"id\":\"MISSING_INFO\"},{\"id\":\"UNKNOWN\"}"
            + "],"
            + "\"llm\":{\"max_tokens\":128,\"classification_max_tokens\":32,\"slot_max_tokens\":64,\"temperature\":0.1,\"classification_temperature\":0.0,\"slot_temperature\":0.0,\"top_p\":0.95,\"top_k\":40,\"context_size\":1024,\"threads\":4}}";

        public static string BuildLlmDebugJson(string sourceConfigJson)
        {
            LlmRuntimeConfig llm = ParseLlmConfig(sourceConfigJson);
            StringBuilder builder = new StringBuilder(128);
            builder.Append("{");
            builder.Append("\"context_size\":").Append(llm.context_size).Append(",");
            builder.Append("\"threads\":").Append(llm.threads).Append(",");
            builder.Append("\"max_tokens\":").Append(llm.max_tokens).Append(",");
            builder.Append("\"classification_max_tokens\":").Append(llm.classification_max_tokens).Append(",");
            builder.Append("\"slot_max_tokens\":").Append(llm.slot_max_tokens).Append(",");
            builder.Append("\"temperature\":").Append(FloatString(llm.temperature)).Append(",");
            builder.Append("\"classification_temperature\":").Append(FloatString(llm.classification_temperature)).Append(",");
            builder.Append("\"slot_temperature\":").Append(FloatString(llm.slot_temperature)).Append(",");
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
                + " classification_max_tokens=" + llm.classification_max_tokens
                + " slot_max_tokens=" + llm.slot_max_tokens
                + " temperature=" + FloatString(llm.temperature)
                + " classification_temperature=" + FloatString(llm.classification_temperature)
                + " slot_temperature=" + FloatString(llm.slot_temperature)
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
            public int classification_max_tokens = 32;
            public int slot_max_tokens = 64;
            public float temperature = 0.1f;
            public float classification_temperature = 0f;
            public float slot_temperature = 0f;
            public float top_p = 0.95f;
            public int top_k = 40;
        }
    }
}
