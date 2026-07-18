# Whisper Android native build

This directory builds only `libstt_whisper.so` for the Unity Android bridge.
It keeps the Java/C# STT API stable while providing an explicit persistent
native lifecycle: init, transcribe and free.

The reproducible source revision is official `ggml-org/whisper.cpp` v1.9.1:

```text
f049fff95a089aa9969deb009cdd4892b3e74916
```

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
