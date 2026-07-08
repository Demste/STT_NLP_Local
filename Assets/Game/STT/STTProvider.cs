namespace Game.STT
{
    public enum STTProvider
    {
        WhisperCppOffline = 0
    }

    public static class STTProviderExtensions
    {
        public static string ToApiValue(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.WhisperCppOffline => "whisper_cpp_offline",
                _ => "whisper_cpp_offline"
            };
        }

        public static string ToDisplayName(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.WhisperCppOffline => "Whisper.cpp Offline",
                _ => provider.ToString()
            };
        }

        public static string ToSafeFileName(this STTProvider provider)
        {
            return provider.ToApiValue().Replace("/", "_").Replace(" ", "_");
        }
    }
}
