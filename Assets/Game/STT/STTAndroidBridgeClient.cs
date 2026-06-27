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
            string elevenLabsApiKey,
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
                elevenLabsApiKey ?? string.Empty,
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
    }
}
