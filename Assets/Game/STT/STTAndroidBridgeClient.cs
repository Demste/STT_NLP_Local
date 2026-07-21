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

        public void ForceRequestTurkishOnDeviceModel()
        {
#if UNITY_ANDROID && !UNITY_EDITOR && DEVELOPMENT_BUILD
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            bridge.CallStatic("forceRequestTurkishOnDeviceModel");
#endif
        }

        public string RequestTurkishOnDeviceModel(string unityGameObjectName, string unityCallbackMethod)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            return bridge.CallStatic<string>("requestTurkishOnDeviceModel", unityGameObjectName, unityCallbackMethod)
                ?? string.Empty;
#else
            return "{\"ok\":true,\"model_status\":\"INSTALLED\",\"network_validated\":true}";
#endif
        }

        public string ObserveTurkishOnDeviceModel(string unityGameObjectName, string unityCallbackMethod)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass bridge = new AndroidJavaClass(BridgeClassName);
            return bridge.CallStatic<string>("observeTurkishOnDeviceModel", unityGameObjectName, unityCallbackMethod)
                ?? string.Empty;
#else
            return "{\"ok\":true,\"model_status\":\"INSTALLED\",\"network_validated\":true}";
#endif
        }

    }
}
