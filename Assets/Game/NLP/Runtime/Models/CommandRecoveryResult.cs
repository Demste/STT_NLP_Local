using System;

namespace Game.NLP
{
    public static class CommandRecoveryStatus
    {
        public const string ResolvedOriginal = "RESOLVED_ORIGINAL";
        public const string ResolvedAutoCorrection = "RESOLVED_AUTO_CORRECTION";
        public const string NeedsConfirmation = "NEEDS_CONFIRMATION";
        public const string NoRecovery = "NO_RECOVERY";
        public const string ResolvedAfterConfirmation = "RESOLVED_AFTER_CONFIRMATION";
        public const string MissingInfo = "MISSING_INFO";
        public const string Unknown = "UNKNOWN";
        public const string Rejected = "REJECTED";
        public const string InvalidOrStaleSuggestion = "INVALID_OR_STALE_SUGGESTION";
    }

    [Serializable]
    public sealed class CommandSuggestion
    {
        public string suggestion_id;
        public string message;
        public string suggested_command;
        public string suggested_action;
        public double confidence;
        public string reason;
    }

    [Serializable]
    public sealed class CommandRecoveryResult
    {
        public string original_suffix;
        public string normalized_suffix;
        public string corrected_suffix;
        public bool correction_applied;
        public double correction_confidence;
        public string correction_reason;
        public string candidate_action;
        public int candidate_count;
        public double candidate_gap;
        public string resolution_status;
        public CommandSuggestion[] suggestions;
        public string parser_input;
        public string parser_source;

        public static CommandRecoveryResult From(BaselineCommandHints hints, string fallbackSuffix)
        {
            return new CommandRecoveryResult
            {
                original_suffix = hints != null && !string.IsNullOrWhiteSpace(hints.original_suffix)
                    ? hints.original_suffix
                    : fallbackSuffix ?? string.Empty,
                normalized_suffix = hints?.normalized_suffix ?? string.Empty,
                corrected_suffix = hints?.corrected_suffix ?? string.Empty,
                correction_applied = hints != null && hints.correction_applied,
                correction_confidence = hints?.correction_confidence ?? 0d,
                correction_reason = hints?.correction_reason ?? "NONE",
                candidate_action = hints?.candidate_action ?? string.Empty,
                candidate_count = hints?.candidate_count ?? 0,
                candidate_gap = hints?.candidate_gap ?? 0d,
                resolution_status = hints?.recovery_resolution_status ?? CommandRecoveryStatus.NoRecovery,
                suggestions = hints?.suggestions ?? Array.Empty<CommandSuggestion>(),
                parser_input = hints?.parser_input ?? fallbackSuffix ?? string.Empty,
                parser_source = hints?.parser_source ?? "deterministic_original",
            };
        }
    }

    [Serializable]
    public sealed class SuggestionDecisionResult
    {
        public bool accepted;
        public string suggestion_id;
        public string resolution_status;
        public string message;
        public SpeechCommandPipelineResult pipeline_result;

        public ValidatedCommandResult final_command => pipeline_result?.validated;
        public string final_json => pipeline_result?.finalJson;
    }
}
