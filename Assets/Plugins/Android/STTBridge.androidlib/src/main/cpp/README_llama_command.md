# llama_command native backend

This project ships the command LLM as a prebuilt native library in `src/main/jniLibs`.
There is no Android CMake build configured in `build.gradle`.

The Java side is prepared for a prebuilt llama.cpp JNI library:

- `src/main/jniLibs/arm64-v8a/libllama_command.so`
- JNI owner: `com.mehmet.sttbridge.llm.LlamaCppEngine`
- Exported methods:
  - `nativeInit(String modelPath, int contextSize, int threads): long`
  - `nativeGenerate(long handle, String prompt, int maxTokens, float temperature): String`
  - `nativeFree(long handle): void`

Recommended next integration path:

1. Build llama.cpp for Android `arm64-v8a` outside Unity/Gradle first.
2. Add a thin JNI adapter named `libllama_command.so`.
3. Place it under `src/main/jniLibs/arm64-v8a`.
4. Place the GGUF model under `src/main/assets/llm/command-model.gguf`.
5. Run the current STT smoke test and then the NLP pipeline test on device.

This avoids enabling a new CMake pipeline inside the Android library until the prebuilt
backend is known-good.
