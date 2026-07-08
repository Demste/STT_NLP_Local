using UnityEngine;

namespace Game.STT
{
    public sealed class STTAndroidBridgeClient
    {
        private const string BridgeClassName = "com.mehmet.sttbridge.STTBridge";

        public void Transcribe(
            STTProvider provider,
            string wavPath,
            string language,
            string unityGameObjectName,
            string unityCallbackMethod)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            bridge.CallStatic(
                "transcribe",
                provider.ToApiValue(),
                wavPath,
                language,
                unityGameObjectName,
                unityCallbackMethod
            );
#else
            string json = JsonUtility.ToJson(new STTResult
            {
                provider = provider.ToApiValue(),
                language = language,
                text = string.Empty,
                durationMs = 0,
                audioFile = wavPath,
                error = "Android bridge sadece Android cihaz/build içinde çalışır. Editor/PC için ayrı proxy gerekir."
            });
            GameObject target = GameObject.Find(unityGameObjectName);
            if (target != null) target.SendMessage(unityCallbackMethod, json, SendMessageOptions.DontRequireReceiver);
#endif
        }

        public bool ShareTextFile(string txtPath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            return bridge.CallStatic<bool>("shareTextFile", txtPath);
#else
            return false;
#endif
        }

        public bool ExportFileToPublicDocuments(string sourcePath, string sessionId, string mimeType)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            return bridge.CallStatic<bool>("exportFileToPublicDocuments", sourcePath, sessionId, mimeType);
#else
            return false;
#endif
        }
    }
}
