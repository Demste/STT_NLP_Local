using System;

namespace Game.STT
{
    public enum AndroidSpeechRecognitionMode
    {
        AUTO = 0,
        ONLINE = 1,
        OFFLINE = 2,
    }

    public enum AndroidSpeechSessionState
    {
        IDLE,
        STARTING,
        LISTENING,
        STOPPING,
        WAITING_FINAL_RESULT,
        COMPLETED,
        CANCELLED,
        ERROR,
        DESTROYED,
    }

    [Serializable]
    public sealed class AndroidSpeechAlternative
    {
        public string text;
        public int rank;
        public int source_index;
        public float confidence = -1f;
        public bool confidence_available;
    }

    [Serializable]
    public sealed class AndroidSpeechRecognitionEvent
    {
        public string session_id;
        public string parent_session_id;
        public string event_type;
        public string recognition_mode_requested;
        public string recognition_mode_used;
        public string recognizer_service;
        public string state;
        public bool partial;
        public bool final_result;
        public AndroidSpeechAlternative[] alternatives;
        public bool confidence_available;
        public int error_code;
        public string error_name;
        public string error_message;
        public long listening_started_at;
        public long speech_start_at;
        public long speech_end_at;
        public long first_partial_at;
        public long final_result_at;
        public long finalization_latency_ms = -1;
        public int attempt;
        public bool android_on_device_retry;
    }

    [Serializable]
    public sealed class AndroidSpeechSupportResult
    {
        public bool ok;
        public bool initialized;
        public string stage;
        public string error;
        public long durationMs;
        public bool online_available;
        public string requested_mode;
        public bool standard_service_available;
        public string network_state;
        public string network_validated;
        public string online_support_status;
        public bool online_usable;
        public bool on_device_service_available;
        public string on_device_support_status;
        public bool tr_tr_on_device_ready;
        public bool offline_usable;
        public string offline_model_status;
        public bool model_download_requested;
        public bool support_checked;
        public string selected_mode;
        public string selection_reason;
        public bool warmup_success;
    }

    [Serializable]
    public sealed class AndroidSttModelActionResult
    {
        public bool ok;
        public string model_status;
        public bool network_validated;
        public string provider;
        public string error;
    }

    [Serializable]
    public sealed class AndroidSttModelEvent
    {
        public string event_name;
        public string provider;
        public string model_status;
        public bool installed;
        public bool pending;
        public string reason;
    }

    public static class AndroidSttConfidence
    {
        public const float GoodThreshold = 0.80f;
        public const float MediumThreshold = 0.50f;

        public static string ToLevel(float confidence, bool available)
        {
            if (!available || confidence < 0f) return null;
            if (confidence >= GoodThreshold) return "GOOD";
            if (confidence >= MediumThreshold) return "MEDIUM";
            return "BAD";
        }
    }
}
