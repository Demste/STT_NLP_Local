using System;

namespace Game.NLP
{
    [Serializable]
    public sealed class ValidatedCommandResult
    {
        public bool ok;
        public string stage;
        public string action;
        public string target;
        public string target_policy;
        public float confidence;
        public CommandParameters parameters;
        public bool requires_clarification;
        public string reason;
        public string error;
        public bool inference_ran;
        public string[] errors;
        public string[] warnings;
        public string source;
    }
}
