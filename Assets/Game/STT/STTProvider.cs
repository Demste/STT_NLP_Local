namespace Game.STT
{
    public enum STTProvider
    {
        WhisperCppOffline = 0,
        AndroidSpeechRecognizer = 1,
    }

    public static class STTProviderExtensions
    {
        public static string ToApiValue(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.WhisperCppOffline => "whisper_cpp_offline",
                STTProvider.AndroidSpeechRecognizer => "android_speech_recognizer",
                _ => "whisper_cpp_offline"
            };
        }

        public static string ToDisplayName(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.WhisperCppOffline => "Whisper.cpp Offline",
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
