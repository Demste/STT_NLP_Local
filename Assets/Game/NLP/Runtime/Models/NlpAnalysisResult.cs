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
        public string context_json;
        public NlpRootToken root;
        public NlpToken[] tokens;
        public NlpEntity[] entities;
        public NlpAnalysis analysis;
        public BaselineCommandHints baseline_hints;
        public LegacyDroneCommand drone_command;
        public SemanticParserStatus semantic_parser;
        public string stage;
        public string error;
        public string exception_type;
        public string traceback;

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
        public NlpTargetCandidate[] target_candidates;
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
        public string action_hint;
        public string target_hint;
        public string spatial_relation_hint;
        public string spatial_relation_text;
        public float confidence;
        public string reason;
        public bool final_parser;
    }

    [Serializable]
    public sealed class LegacyDroneCommand
    {
        public string action;
        public string target;
        public string target_text;
        public string color;
        public string color_text;
        public string spatial_relation;
        public string spatial_relation_text;
        public float confidence;
        public string reason;
        public bool deprecated;
        public bool final_parser;
    }

    [Serializable]
    public sealed class SemanticParserStatus
    {
        public string engine;
        public string status;
        public string message;
    }
}
