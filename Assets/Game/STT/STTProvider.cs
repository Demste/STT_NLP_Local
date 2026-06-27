namespace Game.STT
{
    public enum STTProvider
    {
        VoskTurkishOffline = 0,
        WhisperCppOffline = 1,
        ElevenLabsScribeV2 = 2
    }

    public static class STTProviderExtensions
    {
        public static string ToApiValue(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.VoskTurkishOffline => "vosk_tr_offline",
                STTProvider.WhisperCppOffline => "whisper_cpp_offline",
                STTProvider.ElevenLabsScribeV2 => "elevenlabs_scribe_v2",
                _ => "vosk_tr_offline"
            };
        }

        public static string ToDisplayName(this STTProvider provider)
        {
            return provider switch
            {
                STTProvider.VoskTurkishOffline => "Vosk Turkish Small Offline",
                STTProvider.WhisperCppOffline => "Whisper.cpp Offline",
                STTProvider.ElevenLabsScribeV2 => "ElevenLabs Scribe v2",
                _ => provider.ToString()
            };
        }

        public static string ToSafeFileName(this STTProvider provider)
        {
            return provider.ToApiValue().Replace("/", "_").Replace(" ", "_");
        }
    }
}
