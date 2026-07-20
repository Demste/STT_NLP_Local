using System;

namespace Game.NLP
{
    [Serializable]
    public sealed class LlmCommandResult
    {
        public bool ok;
        public string contract_version;
        public string engine;
        public string backend;
        public string stage;
        public bool initialized;
        public bool inference_ran;
        public LlmBackendStatus backend_status;
        public string input;
        public string raw_input;
        public string wake_word_suffix;
        public string analysis_input;
        public bool repair_applied;
        public string[] repair_candidates;
        public string selected_repair;
        public string[] repair_notes;
        public double repair_duration_ms;
        public string resolution_status;
        public string recovery_status;
        public string final_resolution_status;
        public bool action_llm_called;
        public bool slot_llm_called;
        public string action_llm_raw;
        public string slot_llm_raw;
        public long action_llm_duration_ms;
        public long slot_llm_duration_ms;
        public bool slot_evidence_valid;
        public string detected_object_phrase;
        public string llm_raw;
        public bool json_repair_applied;
        public string[] json_repair_notes;
        public string[] errors;
        public string[] warnings;
        public ValidatedCommandResult normalized_command;
        public ValidatedCommandResult validated_command;
        public ValidatedCommandResult internal_validated_command;
        public ValidatedCommandResult public_command;
        public long durationMs;
        public string error;
        public string exception_type;
        public string traceback;
        public bool llm_called;
        public string resolution_source;
        public bool llm_output_valid;
        public bool balanced_json;
        public string failure_stage;
        public string fallback_action;
        public int llm_prompt_tokens;
        public bool llm_prompt_tokens_exact;
        public int llm_output_tokens;
        public bool llm_output_tokens_exact;
        public long llm_generation_duration;
        public long validation_duration;
        public long validation_duration_ms;
        public long total_duration_ms;
        public bool native_json_early_stop;
        public string native_json_early_stop_reason;
        public bool grammar_constrained;
        public string grammar_constraint_reason;

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

}
