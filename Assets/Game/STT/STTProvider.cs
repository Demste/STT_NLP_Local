namespace Game.STT
{
    public enum STTProvider
    {
        AndroidSpeechRecognizer = 0,
    }

    public static class STTProviderExtensions
    {
        public static string ToDisplayName(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.AndroidSpeechRecognizer => "Android SpeechRecognizer",
                _ => provider.ToString()
            };
        }
    }
}
