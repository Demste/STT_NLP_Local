Whisper model assets
====================

Only supported/default model: ggml-base.bin (official multilingual base model)
Do not use ggml-base.en.bin or any other .en model for Turkish transcription.

Place the model here:
Assets/Plugins/Android/STTBridge.androidlib/src/main/assets/whisper/ggml-base.bin

There is no fallback lookup. If ggml-base.bin is missing, STT returns:
Whisper base model not found. Expected assets/whisper/ggml-base.bin

ggml-tiny-q5_1.bin, ggml-small.bin, ggml-tiny.bin, and .en models are not
defaults or fallbacks and should not be added beside the base model. Whisper
models are large; keep only ggml-base.bin in this asset directory.

Models are not downloaded at runtime. Use Git LFS or team Drive/artifact
storage for ggml-base.bin. An optional ggml-base.bin.sha256 sidecar containing
the 64-character SHA-256 hex digest enables copy-time integrity verification.
