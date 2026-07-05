package com.mehmet.sttbridge.llm;

import org.json.JSONException;
import org.json.JSONObject;

public final class LlmBackendStatus {
    public final boolean ok;
    public final boolean initialized;
    public final String engine;
    public final String stage;
    public final String error;
    public final String assetPath;
    public final String extractedPath;
    public final String modelPath;
    public final String nativeLibrary;

    private LlmBackendStatus(
            boolean ok,
            boolean initialized,
            String engine,
            String stage,
            String error,
            String assetPath,
            String extractedPath,
            String modelPath,
            String nativeLibrary) {
        this.ok = ok;
        this.initialized = initialized;
        this.engine = engine;
        this.stage = stage;
        this.error = error;
        this.assetPath = assetPath;
        this.extractedPath = extractedPath;
        this.modelPath = modelPath;
        this.nativeLibrary = nativeLibrary;
    }

    public static LlmBackendStatus initialized(String engine, String assetPath, String extractedPath, String nativeLibrary) {
        return new LlmBackendStatus(true, true, engine, "initialized", "", assetPath, extractedPath, extractedPath, nativeLibrary);
    }

    public static LlmBackendStatus failed(String engine, String stage, String error, String modelPath, String nativeLibrary) {
        return new LlmBackendStatus(false, false, engine, stage, error, null, null, modelPath, nativeLibrary);
    }

    public static LlmBackendStatus failed(
            String engine,
            String stage,
            String error,
            String assetPath,
            String extractedPath,
            String nativeLibrary) {
        return new LlmBackendStatus(false, false, engine, stage, error, assetPath, extractedPath, extractedPath, nativeLibrary);
    }

    public JSONObject toJson() throws JSONException {
        JSONObject object = new JSONObject();
        object.put("ok", ok);
        object.put("initialized", initialized);
        object.put("engine", engine != null ? engine : "offline_llm");
        object.put("stage", stage != null ? stage : "unknown");
        object.put("error", error != null ? error : "");
        object.put("assetPath", assetPath != null ? assetPath : JSONObject.NULL);
        object.put("extractedPath", extractedPath != null ? extractedPath : JSONObject.NULL);
        object.put("modelPath", modelPath != null ? modelPath : JSONObject.NULL);
        object.put("nativeLibrary", nativeLibrary != null ? nativeLibrary : JSONObject.NULL);
        return object;
    }
}
