using System;

namespace Game.STT
{
    [Serializable]
    public sealed class STTResult
    {
        public string provider;
        public string language;
        public string text;
        public long durationMs;
        public string audioFile;
        public string error;

        public bool HasError => !string.IsNullOrWhiteSpace(error);
    }
}
