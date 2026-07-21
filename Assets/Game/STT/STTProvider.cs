namespace Game.STT
{
    public enum STTProvider
    {
        AndroidSpeechRecognizer = 0,
    }

    public static class STTProviderExtensions
    {
        public static string ToApiValue(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.AndroidSpeechRecognizer => "android_speech_recognizer",
                _ => "android_speech_recognizer"
            };
        }

        public static string ToDisplayName(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.AndroidSpeechRecognizer => "Android SpeechRecognizer",
                _ => provider.ToString()
            };
        }

        public static string ToSafeFileName(this STTProvider provider)
        {
            return provider.ToApiValue().Replace("/", "_").Replace(" ", "_");
        }
    }
}
