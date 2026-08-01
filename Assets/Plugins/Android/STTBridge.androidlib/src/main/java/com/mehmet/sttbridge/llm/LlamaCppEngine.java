package com.mehmet.sttbridge.llm;

import android.content.Context;
import android.util.Log;

import org.json.JSONObject;

public final class LlamaCppEngine implements OfflineLlmEngine {
    private static final String TAG = "LlamaCppEngine";
    private static final String ENGINE_NAME = "llama_cpp";
    private static final String NATIVE_LIBRARY = "llama_command";

    private static boolean loadTried;
    private static boolean nativeLoaded;
    private static String nativeLoadError = "";

    private final Context appContext;
    private LlmBackendStatus status = LlmBackendStatus.failed(
            ENGINE_NAME,
            "not_initialized",
            "LlamaCppEngine.initialize must be called before generation.",
            null,
            NATIVE_LIBRARY);
    private long nativeHandle;
    private GenerationMetrics lastGenerationMetrics = GenerationMetrics.empty();

    public static final class GenerationMetrics {
        public final int promptTokens;
        public final int outputTokens;
        public final long promptEvalMs;
        public final long firstTokenMs;
        public final long generationMs;
        public final long totalMs;
        public final boolean jsonEarlyStop;

        private GenerationMetrics(int promptTokens, int outputTokens, long promptEvalMs,
                                  long firstTokenMs, long generationMs, long totalMs,
                                  boolean jsonEarlyStop) {
            this.promptTokens = promptTokens;
            this.outputTokens = outputTokens;
            this.promptEvalMs = promptEvalMs;
            this.firstTokenMs = firstTokenMs;
            this.generationMs = generationMs;
            this.totalMs = totalMs;
            this.jsonEarlyStop = jsonEarlyStop;
        }

        public static GenerationMetrics empty() {
            return new GenerationMetrics(0, 0, 0, 0, 0, 0, false);
        }

        static GenerationMetrics fromJson(String value) {
            try {
                JSONObject json = new JSONObject(value != null ? value : "{}");
                return new GenerationMetrics(
                        json.optInt("prompt_tokens", 0), json.optInt("output_tokens", 0),
                        json.optLong("prompt_eval_ms", 0), json.optLong("first_token_ms", 0),
                        json.optLong("generation_ms", 0), json.optLong("total_ms", 0),
                        json.optBoolean("json_early_stop", false));
            } catch (Throwable ignored) {
                return empty();
            }
        }
    }

    public LlamaCppEngine(Context context) {
        appContext = context != null ? context.getApplicationContext() : null;
    }

    @Override
    public String getEngineName() {
        return ENGINE_NAME;
    }

    @Override
    public synchronized LlmBackendStatus initialize(JSONObject config) {
        if (status.initialized) {
            return status;
        }

        if (appContext == null) {
            status = LlmBackendStatus.failed(
                    ENGINE_NAME,
                    "android_context_null",
                    "Android Context is null; cannot initialize offline LLM backend.",
                    null,
                    NATIVE_LIBRARY);
            return status;
        }

        if (!ensureNativeLoaded()) {
            status = LlmBackendStatus.failed(
                    ENGINE_NAME,
                    "native_library_load_failed",
                    "Native llama.cpp backend lib" + NATIVE_LIBRARY + ".so is not linked or could not load: " + nativeLoadError,
                    null,
                    NATIVE_LIBRARY);
            return status;
        }

        LlmModelResolver.Result model = LlmModelResolver.resolve(appContext, config);
        if (!model.ok) {
            status = LlmBackendStatus.failed(
                    ENGINE_NAME,
                    model.stage,
                    model.error,
                    model.assetPath,
                    model.extractedPath,
                    NATIVE_LIBRARY);
            return status;
        }

        LlmGenerationOptions options = LlmGenerationOptions.fromConfig(config);
        try {
            nativeHandle = nativeInit(model.extractedPath, options.contextSize, options.threads);
            if (nativeHandle == 0L) {
                status = LlmBackendStatus.failed(
                        ENGINE_NAME,
                        "native_init_failed",
                        "llama.cpp native init returned handle 0.",
                        model.assetPath,
                        model.extractedPath,
                        NATIVE_LIBRARY);
                return status;
            }

            status = LlmBackendStatus.initialized(ENGINE_NAME, model.assetPath, model.extractedPath, NATIVE_LIBRARY);
            return status;
        } catch (Throwable t) {
            status = LlmBackendStatus.failed(
                    ENGINE_NAME,
                    "native_init_failed",
                    throwableMessage(t),
                    model.assetPath,
                    model.extractedPath,
                    NATIVE_LIBRARY);
            return status;
        }
    }

    @Override
    public synchronized LlmBackendStatus getStatus() {
        return status;
    }

    @Override
    public synchronized String generate(String prompt, LlmGenerationOptions options) throws Exception {
        if (!status.initialized || nativeHandle == 0L) {
            throw new IllegalStateException("llama.cpp backend is not initialized. stage=" + status.stage + " error=" + status.error);
        }

        try {
            lastGenerationMetrics = GenerationMetrics.empty();
            Log.i(TAG, "[NLP-LlamaCpp] nativeGenerate called max_tokens="
                    + (options != null ? options.maxTokens : 128)
                    + " grammar_constrained=" + (options != null && !options.grammar.isEmpty()));
            String result = nativeGenerate(
                    nativeHandle,
                    prompt != null ? prompt : "",
                    options != null ? options.maxTokens : 128,
                    options != null ? options.temperature : 0.1f,
                    options != null ? options.topP : 0.95f,
                    options != null ? options.topK : 40,
                    options != null ? options.grammar : "");
            lastGenerationMetrics = GenerationMetrics.fromJson(nativeGetLastMetrics(nativeHandle));
            Log.i(TAG, "[NLP-LlamaCpp] prompt_tokens=" + lastGenerationMetrics.promptTokens
                    + " output_tokens=" + lastGenerationMetrics.outputTokens
                    + " prompt_eval_ms=" + lastGenerationMetrics.promptEvalMs
                    + " first_token_ms=" + lastGenerationMetrics.firstTokenMs
                    + " generation_ms=" + lastGenerationMetrics.generationMs
                    + " total_ms=" + lastGenerationMetrics.totalMs
                    + " json_early_stop=" + lastGenerationMetrics.jsonEarlyStop);
            if (result == null || result.trim().isEmpty()) {
                throw new IllegalStateException("llama.cpp generation returned empty output.");
            }
            if (result.startsWith("NATIVE_ERROR:")) {
                throw new IllegalStateException(result);
            }
            return result;
        } catch (Throwable t) {
            throw new IllegalStateException("llama.cpp generation failed: " + throwableMessage(t), t);
        }
    }

    public synchronized GenerationMetrics getLastGenerationMetrics() {
        return lastGenerationMetrics;
    }

    public synchronized void close() {
        if (nativeHandle != 0L) {
            try {
                nativeFree(nativeHandle);
            } finally {
                nativeHandle = 0L;
                status = LlmBackendStatus.failed(
                        ENGINE_NAME,
                        "closed",
                        "llama.cpp backend has been closed.",
                        status.assetPath,
                        status.extractedPath,
                        NATIVE_LIBRARY);
            }
        }
    }

    private static synchronized boolean ensureNativeLoaded() {
        if (loadTried) {
            return nativeLoaded;
        }

        loadTried = true;
        try {
            System.loadLibrary(NATIVE_LIBRARY);
            nativeLoaded = true;
            nativeLoadError = "";
        } catch (Throwable t) {
            nativeLoaded = false;
            nativeLoadError = throwableMessage(t);
        }
        return nativeLoaded;
    }

    private static String throwableMessage(Throwable throwable) {
        if (throwable == null) {
            return "";
        }
        String message = throwable.getMessage();
        return message != null ? message : throwable.toString();
    }

    private static native long nativeInit(String modelPath, int contextSize, int threads);

    private static native String nativeGenerate(long handle, String prompt, int maxTokens, float temperature,
                                                float topP, int topK, String grammar);

    private static native String nativeGetLastMetrics(long handle);

    private static native void nativeFree(long handle);
}
