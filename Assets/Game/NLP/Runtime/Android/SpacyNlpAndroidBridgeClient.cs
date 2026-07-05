using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.NLP.Android
{
    public static class SpacyNlpAndroidBridgeClient
    {
        private const string BridgeClassName = "com.mehmet.sttbridge.SpacyNlpBridge";
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

        public static Task<string> AnalyzeAsync(
            string text,
            string contextJson = "",
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
                        return BuildUnavailableJson(text, "android_activity_null", "real_spacy", "UnityPlayer.currentActivity is null.");
                    }

                    using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
                    string result = bridge.CallStatic<string>(
                        "analyzeText",
                        activity,
                        text ?? string.Empty,
                        contextJson ?? string.Empty);

                    cancellationToken.ThrowIfCancellationRequested();
                    return string.IsNullOrEmpty(result)
                        ? BuildUnavailableJson(text, "android_bridge_returned_empty", "real_spacy", "Android spaCy bridge returned an empty response.")
                        : result;
                }
                finally
                {
                    AndroidJNI.DetachCurrentThread();
                }
            }, cancellationToken);
#else
            return Task.FromResult(BuildUnavailableJson(
                text,
                "editor_mock",
                "editor_mock",
                "spaCy Chaquopy bridge runs only in an Android device build."));
#endif
        }

        private static string BuildUnavailableJson(string text, string stage, string engine, string error)
        {
            return "{"
                + "\"ok\":false,"
                + "\"engine\":\"" + EscapeJson(engine) + "\","
                + "\"model\":\"tr_core_news_md\","
                + "\"stage\":\"" + EscapeJson(stage) + "\","
                + "\"input\":\"" + EscapeJson(text ?? string.Empty) + "\","
                + "\"error\":\"" + EscapeJson(error) + "\""
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

