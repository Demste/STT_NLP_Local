using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.NLP.Android
{
    public static class OfflineLlmAndroidBridgeClient
    {
        private const string BridgeClassName = "com.mehmet.sttbridge.OfflineLlmBridge";
        private const string UnityPlayerClassName = "com.unity3d.player.UnityPlayer";

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

        public static Task<string> RunSmokePromptAsync(
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
                    string result = bridge.CallStatic<string>(
                        "runSmokePrompt",
                        activity,
                        configJson ?? string.Empty);

                    cancellationToken.ThrowIfCancellationRequested();
                    return string.IsNullOrEmpty(result)
                        ? BuildUnavailableJson(string.Empty, "android_bridge_returned_empty", "Android offline LLM smoke bridge returned an empty response.")
                        : result;
                }
                finally
                {
                    AndroidJNI.DetachCurrentThread();
                }
            }, cancellationToken);
#else
            return Task.FromResult(BuildUnavailableJson(
                string.Empty,
                "editor_mock",
                "Offline LLM smoke bridge runs only in an Android device build."));
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
                + "\"backend_status\":{\"ok\":false,\"initialized\":false,\"engine\":\"offline_llm\",\"stage\":\"" + escapedStage + "\",\"error\":\"" + escapedError + "\",\"modelPath\":null,\"nativeLibrary\":null},"
                + "\"input\":\"" + EscapeJson(text ?? string.Empty) + "\","
                + "\"llm_raw\":\"\","
                + "\"llm_command\":null,"
                + "\"validated_command\":null,"
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
