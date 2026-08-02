using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.NLP.Android
{
    public static class OfflineLlmAndroidBridgeClient
    {
        private const string BridgeClassName = "com.mehmet.sttbridge.OfflineLlmBridge";
        private const string UnityPlayerClassName = "com.unity3d.player.UnityPlayer";
        private const string UnknownInternalCommandJson = "{\"action\":\"UNKNOWN\",\"confidence\":0.0}";
        private const string UnknownCommandJson = "{\"action\":\"UNKNOWN\",\"direction\":null,\"turn_direction\":null,\"distance_m\":null,\"duration_s\":null,\"duration_min\":null,\"angle_deg\":null,\"speed_kmh\":null,\"object\":null,\"target_action\":null,\"color\":null,\"shape\":null,\"marker_index\":null,\"location_keyword\":null,\"coordinate\":null,\"observe_mode\":null,\"voice_type\":null,\"stt_confidence_level\":null,\"missing_field\":null,\"missing_message\":null,\"confidence\":0.0}";

        public static bool IsAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public static Task<string> ParseCommandAsync(
            string rawText,
            string spacyAnalysisJson,
            string configJson,
            CancellationToken cancellationToken = default)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                AndroidJNI.AttachCurrentThread();

                try
                {
                    using AndroidJavaClass unityPlayer = new AndroidJavaClass(UnityPlayerClassName);
                    using AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                    if (activity == null)
                    {
                        return BuildUnavailableJson(rawText, "android_activity_null", "UnityPlayer.currentActivity is null.");
                    }

                    using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
                    string result = bridge.CallStatic<string>(
                        "parseCommand",
                        activity,
                        rawText ?? string.Empty,
                        spacyAnalysisJson ?? string.Empty,
                        configJson ?? string.Empty);

                    cancellationToken.ThrowIfCancellationRequested();
                    return string.IsNullOrEmpty(result)
                        ? BuildUnavailableJson(rawText, "android_bridge_returned_empty", "Android offline LLM bridge returned an empty response.")
                        : result;
                }
                finally
                {
                    AndroidJNI.DetachCurrentThread();
                }
            }, cancellationToken);
#else
            return Task.FromResult(BuildUnavailableJson(
                rawText,
                "editor_mock",
                "Offline LLM bridge runs only in an Android device build."));
#endif
        }

        public static Task<string> WarmUpAsync(
            string configJson,
            CancellationToken cancellationToken = default)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                AndroidJNI.AttachCurrentThread();
                try
                {
                    using AndroidJavaClass unityPlayer = new AndroidJavaClass(UnityPlayerClassName);
                    using AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    if (activity == null)
                    {
                        return BuildUnavailableJson(string.Empty, "android_activity_null", "UnityPlayer.currentActivity is null.");
                    }
                    using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
                    string result = bridge.CallStatic<string>("warmUp", activity, configJson ?? string.Empty);
                    cancellationToken.ThrowIfCancellationRequested();
                    return string.IsNullOrEmpty(result)
                        ? BuildUnavailableJson(string.Empty, "android_bridge_returned_empty", "Android offline LLM warm-up bridge returned an empty response.")
                        : result;
                }
                finally
                {
                    AndroidJNI.DetachCurrentThread();
                }
            }, cancellationToken);
#else
            return Task.FromResult(BuildUnavailableJson(string.Empty, "editor_mock", "Offline LLM warm-up runs only in an Android device build."));
#endif
        }

        private static string BuildUnavailableJson(string text, string stage, string error)
        {
            string escapedStage = EscapeJson(stage);
            string escapedError = EscapeJson(error);
            return "{"
                + "\"ok\":false,"
                + "\"engine\":\"offline_llm\","
                + "\"stage\":\"" + escapedStage + "\","
                + "\"initialized\":false,"
                + "\"inference_ran\":false,"
                + "\"llm_called\":false,"
                + "\"resolution_source\":\"fallback\","
                + "\"backend_status\":{\"ok\":false,\"initialized\":false,\"engine\":\"offline_llm\",\"stage\":\"" + escapedStage + "\",\"error\":\"" + escapedError + "\",\"modelPath\":null,\"nativeLibrary\":null},"
                + "\"input\":\"" + EscapeJson(text ?? string.Empty) + "\","
                + "\"llm_raw\":\"\","
                + "\"llm_command\":null,"
                + "\"internal_validated_command\":" + UnknownInternalCommandJson + ","
                + "\"public_command\":" + UnknownCommandJson + ","
                + "\"normalized_command\":" + UnknownCommandJson + ","
                + "\"validated_command\":" + UnknownCommandJson + ","
                + "\"errors\":[\"" + escapedStage + "\"],"
                + "\"warnings\":[],"
                + "\"durationMs\":0,"
                + "\"error\":\"" + escapedError + "\""
                + "}";
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\b", "\\b")
                .Replace("\f", "\\f")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
