# Whisper Android native build

This directory builds only `libstt_whisper.so` for the Unity Android bridge.
It keeps the Java/C# STT API stable while providing an explicit persistent
native lifecycle: init, transcribe and free.

The reproducible source revision is official `ggml-org/whisper.cpp` v1.9.1:

```text
f049fff95a089aa9969deb009cdd4892b3e74916
```

## Multilingual Turkish model asset

The only supported/default model is the official multilingual `ggml-base.bin`.
Turkish transcription must not use `ggml-base.en.bin` or any other `.en`
model. Download the base model manually with the vendored official whisper.cpp
helper and copy it into the Android library assets:

```bash
Tools/third_party/whisper.cpp/models/download-ggml-model.sh base
cp Tools/third_party/whisper.cpp/models/ggml-base.bin \
  Assets/Plugins/Android/STTBridge.androidlib/src/main/assets/whisper/ggml-base.bin
```

The exact required path is:

```text
Assets/Plugins/Android/STTBridge.androidlib/src/main/assets/whisper/ggml-base.bin
```

The bridge never downloads a model at runtime and never falls back to small,
tiny, or `.en` models. If `ggml-base.bin` is missing, transcription fails
explicitly with `Whisper base model not found. Expected
assets/whisper/ggml-base.bin`. An optional `ggml-base.bin.sha256` sidecar,
containing only the 64-character SHA-256 hex digest, enables integrity
verification while the model is copied into the app's private files directory.

Whisper models are large. Keep only this one model in the Android assets; do
not add small/tiny variants alongside it. This repository routes Whisper
`.bin` assets through Git LFS. If the base model is distributed outside Git,
store it in the team's Drive/artifact storage and use the manual copy command
above after checkout.

The native command-mode parameters are Turkish language, translation off,
greedy decoding (`temperature = 0.0`), timestamps off, single-segment output,
no aggressive token/character limit, and the Turkish drone-command initial
prompt in `whisper_jni.cpp`.

Prepare the ignored third-party checkout once:

```bash
git clone --depth 1 --branch v1.9.1 \
  https://github.com/ggml-org/whisper.cpp.git \
  Tools/third_party/whisper.cpp
```

Build with Unity 6000.3.9f1's NDK:

```bash
ANDROID_NDK_HOME=/Applications/Unity/Hub/Editor/6000.3.9f1/PlaybackEngines/AndroidPlayer/NDK \
  Tools/whisper-android/build_android_arm64.sh
```

The build is Android 24+, `arm64-v8a`, Release, C++17, `-O3`, `-DNDEBUG`,
ggml-compatible fast math (`-ffast-math -fno-finite-math-only`), section GC
and LTO when supported. It uses the generic ARMv8-A
baseline (NEON/ASIMD and ARM FMA) rather than a phone-specific `-mcpu` value.
OpenMP and non-CPU backends are disabled. `compile_commands.json` remains in
the ignored build directory so the effective compiler flags can be audited.

The first run preserves the original prebuilt as
`libstt_whisper.so.backup`, then installs a stripped Release library in the
plugin's `jniLibs/arm64-v8a` directory.

Changes to `whisper_jni.cpp` do not modify the existing prebuilt
`libstt_whisper.so`. To activate a new prompt or native inference config, run
the build command above in a separate native-build task and then build the APK.

Runtime verification filters:

```text
adb logcat -s STTBridge STTWhisper Unity
```

- `STTBridge/[WhisperModel]`: expected base asset and private runtime path
- `STTWhisper/[WhisperConfig]`: actual native inference parameters
- `STTBridge/[WhisperTiming]`: total `durationMs`
- `STTBridge/[WhisperResult]`: unmodified raw transcript
- `Unity/[STT]`: separate raw and trim-normalized transcripts

TODO: If Whisper still returns command-word errors, a Turkish STT correction
or command-suggestion layer can be inserted after `rawTranscript` and before
the parser in a separate task.
