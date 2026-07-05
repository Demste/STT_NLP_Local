using System;

namespace Game.NLP
{
    [Serializable]
    public sealed class LlmCommandResult
    {
        public bool ok;
        public string engine;
        public string backend;
        public string stage;
        public bool initialized;
        public bool inference_ran;
        public LlmBackendStatus backend_status;
        public string input;
        public string llm_raw;
        public bool json_repair_applied;
        public string[] json_repair_notes;
        public ValidatedCommandResult normalized_command;
        public ValidatedCommandResult validated_command;
        public long durationMs;
        public string error;
        public string exception_type;
        public string traceback;

        public bool HasError => !ok || !string.IsNullOrWhiteSpace(error);
    }

    [Serializable]
    public sealed class LlmBackendStatus
    {
        public bool ok;
        public bool initialized;
        public string engine;
        public string stage;
        public string error;
        public string assetPath;
        public string extractedPath;
        public string modelPath;
        public string nativeLibrary;
    }

    [Serializable]
    public sealed class CommandParameters
    {
        public string relation;
        public string direction;
        public float distance;
        public string unit;
    }
}
