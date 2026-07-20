using System;

namespace Game.NLP
{
    [Serializable]
    public sealed class NlpAnalysisResult
    {
        public bool ok;
        public string engine;
        public string model;
        public string spacy_version;
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
        public string context_json;
        public NlpRootToken root;
        public NlpToken[] tokens;
        public NlpEntity[] entities;
        public NlpAnalysis analysis;
        public BaselineCommandHints baseline_hints;
        public ValidatedCommandResult drone_command;
        public SemanticParserStatus semantic_parser;
        public string stage;
        public string error;
        public string exception_type;
        public string traceback;
        public double spacy_duration_ms;
        public double baseline_duration_ms;
        public double action_resolution_duration_ms;
        public double slot_extraction_duration_ms;
        public double validation_duration_ms;
        public double total_duration_ms;
        public string resolution_source;
        public bool llm_called;

        public bool HasError => !ok || !string.IsNullOrWhiteSpace(error);
    }

    [Serializable]
    public sealed class NlpAnalysis
    {
        public NlpToken[] tokens;
        public NlpEntity[] entities;
        public NlpRootToken root;
        public NlpCandidate[] action_candidates;
        public NlpCandidate[] noun_candidates;
        public NlpTargetCandidate[] object_phrase_candidates;
        public NlpCandidate[] spatial_phrase_candidates;
    }

    [Serializable]
    public sealed class NlpToken
    {
        public int i;
        public string text;
        public string lemma;
        public string pos;
        public string tag;
        public string dep;
        public string morph;
        public string head;
        public int head_i;
        public bool is_alpha;
        public bool is_stop;
        public string normalized_text;
        public string normalized_lemma;
    }

    [Serializable]
    public sealed class NlpRootToken
    {
        public int i;
        public string text;
        public string lemma;
        public string pos;
        public string tag;
        public string dep;
        public string morph;
        public string normalized_text;
        public string normalized_lemma;
    }

    [Serializable]
    public sealed class NlpEntity
    {
        public string text;
        public string label;
        public int start;
        public int end;
    }

    [Serializable]
    public sealed class NlpCandidate
    {
        public int i;
        public string text;
        public string lemma;
        public string pos;
        public string dep;
        public string normalized_text;
        public string normalized_lemma;
        public string source;
    }

    [Serializable]
    public sealed class NlpTargetCandidate
    {
        public string text;
        public string lemma_text;
        public int start;
        public int end;
        public int head_i;
        public string head_text;
        public string normalized;
        public string source;
    }

    [Serializable]
    public sealed class BaselineCommandHints
    {
        public bool final_parser;
        public string resolution_status;
        public string domain;
        public string resolution_source;
        public bool llm_called;
        public ValidatedCommandResult command_candidate;
        public string detected_object_phrase;
        public string[] action_candidates;
        public string[] direction_candidates;
        public string[] turn_direction_candidates;
        public double[] distance_candidates;
        public double[] duration_candidates;
        public double[] angle_candidates;
        public double[] speed_candidates;
        public string[] object_candidates;
        public string[] color_candidates;
        public string[] shape_candidates;
        public long[] marker_index_candidates;
        public string[] location_candidates;
        public DroneCoordinate[] coordinate_candidates;
        public string[] observe_mode_candidates;
        public double baseline_duration_ms;
        public double action_resolution_duration_ms;
        public double slot_extraction_duration_ms;
        public double validation_duration_ms;
        public string original_suffix;
        public string normalized_suffix;
        public string corrected_suffix;
        public bool correction_applied;
        public double correction_confidence;
        public string correction_reason;
        public string candidate_action;
        public int candidate_count;
        public double candidate_gap;
        public string recovery_resolution_status;
        public CommandSuggestion[] suggestions;
        public string parser_input;
        public string parser_source;
        public ActionEvidenceSummary action_evidence;
        public string[] evidence_supported_actions;
    }

    [Serializable]
    public sealed class ActionEvidenceSummary
    {
        public bool has_any;
        public string[] supported_actions;
        public string[] reasons;
        public string source_text;

        public bool Supports(string action)
        {
            if (string.IsNullOrWhiteSpace(action) || supported_actions == null)
            {
                return false;
            }

            foreach (string supported in supported_actions)
            {
                if (string.Equals(supported, action, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Serializable]
    public sealed class SemanticParserStatus
    {
        public string engine;
        public string status;
        public string message;
    }
}
