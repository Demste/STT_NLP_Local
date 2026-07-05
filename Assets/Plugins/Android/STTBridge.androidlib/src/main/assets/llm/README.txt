This folder contains the offline command-understanding GGUF model.

Model files are tracked with Git LFS. Developers should have Git LFS installed
before cloning/pulling this repository. If the model is missing after clone:

git lfs install
git lfs pull

Expected default model:
- command-model.gguf

Default Java lookup order:
- assets/llm/command-model.gguf
- assets/llm/model.gguf
- assets/llm/turkish-command.gguf

You can also pass a runtime config object:
{
  "llm": {
    "model_asset": "command-model.gguf",
    "model_path": "/absolute/filesystem/path/to/model.gguf",
    "context_size": 2048,
    "threads": 4,
    "max_tokens": 192,
    "temperature": 0.1
  }
}

Native backend contract:
- Android library name: libllama_command.so
- Java loader name: llama_command
- ABI currently expected by build.gradle: arm64-v8a
- JNI class: com.mehmet.sttbridge.llm.LlamaCppEngine
- JNI methods:
  - nativeInit(String modelPath, int contextSize, int threads): long
  - nativeGenerate(long handle, String prompt, int maxTokens, float temperature): String
  - nativeFree(long handle): void

If the native library or model is absent, OfflineLlmBridge must return ok=false.
