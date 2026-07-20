using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.STT
{
    public interface IAndroidSpeechRecognizerClient
    {
        Task<string> PrepareSpeechRecognizerAsync(AndroidSpeechRecognitionMode mode, CancellationToken cancellationToken = default);
        void StartSpeechRecognition(string sessionId, AndroidSpeechRecognitionMode mode, string unityGameObjectName, string unityCallbackMethod);
        void StopSpeechRecognition(string sessionId);
        void CancelSpeechRecognition(string sessionId);
        void DestroySpeechRecognizer();
    }

    public sealed class STTAndroidBridgeClient : IAndroidSpeechRecognizerClient
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

        public void ReleaseWhisper()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            bridge.CallStatic("releaseWhisper");
#endif
        }

        public Task<string> PrepareSpeechRecognizerAsync(
            AndroidSpeechRecognitionMode mode,
            CancellationToken cancellationToken = default)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                AndroidJNI.AttachCurrentThread();
                try
                {
                    using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
                    string result = bridge.CallStatic<string>("prepareSpeechRecognizer", mode.ToString());
                    cancellationToken.ThrowIfCancellationRequested();
                    return result ?? string.Empty;
                }
                finally
                {
                    AndroidJNI.DetachCurrentThread();
                }
            }, cancellationToken);
#else
            return Task.FromResult(
                "{\"ok\":true,\"initialized\":true,\"stage\":\"editor_noop\",\"durationMs\":0,"
                + "\"online_available\":true,\"on_device_service_available\":false,"
                + "\"tr_tr_on_device_ready\":false,\"model_download_requested\":false,\"support_checked\":true}");
#endif
        }

        public void StartSpeechRecognition(
            string sessionId,
            AndroidSpeechRecognitionMode mode,
            string unityGameObjectName,
            string unityCallbackMethod)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            bridge.CallStatic("startSpeechRecognition", sessionId, mode.ToString(), unityGameObjectName, unityCallbackMethod);
#endif
        }

        public void StopSpeechRecognition(string sessionId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            bridge.CallStatic("stopSpeechRecognition", sessionId);
#endif
        }

        public void CancelSpeechRecognition(string sessionId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            bridge.CallStatic("cancelSpeechRecognition", sessionId);
#endif
        }

        public void DestroySpeechRecognizer()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            bridge.CallStatic("destroySpeechRecognizer");
#endif
        }

        public Task<string> WarmUpWhisperAsync(CancellationToken cancellationToken = default)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                AndroidJNI.AttachCurrentThread();
                try
                {
                    using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
                    string result = bridge.CallStatic<string>("warmUpWhisper");
                    cancellationToken.ThrowIfCancellationRequested();
                    return result ?? string.Empty;
                }
                finally
                {
                    AndroidJNI.DetachCurrentThread();
                }
            }, cancellationToken);
#else
            return Task.FromResult("{\"ok\":true,\"stage\":\"editor_noop\",\"initialized\":true,\"context_reused\":true,\"durationMs\":0}");
#endif
        }
    }
}
