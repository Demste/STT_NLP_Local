package com.mehmet.sttbridge.llm;

import org.json.JSONObject;

public final class LlmGenerationOptions {
    public final int maxTokens;
    public final float temperature;
    public final float topP;
    public final int topK;
    public final int contextSize;
    public final int threads;
    public final int classificationMaxTokens;
    public final int slotMaxTokens;
    public final float classificationTemperature;
    public final float slotTemperature;

    private LlmGenerationOptions(int maxTokens, float temperature, float topP, int topK, int contextSize, int threads,
                                 int classificationMaxTokens, int slotMaxTokens, float classificationTemperature, float slotTemperature) {
        this.maxTokens = maxTokens;
        this.temperature = temperature;
        this.topP = topP;
        this.topK = topK;
        this.contextSize = contextSize;
        this.threads = threads;
        this.classificationMaxTokens = classificationMaxTokens;
        this.slotMaxTokens = slotMaxTokens;
        this.classificationTemperature = classificationTemperature;
        this.slotTemperature = slotTemperature;
    }

    public static LlmGenerationOptions fromConfig(JSONObject config) {
        JSONObject llm = config != null ? config.optJSONObject("llm") : null;
        int maxTokens = llm != null ? llm.optInt("max_tokens", 128) : 128;
        double temperature = llm != null ? llm.optDouble("temperature", 0.1) : 0.1;
        double topP = llm != null ? llm.optDouble("top_p", 0.95) : 0.95;
        int topK = llm != null ? llm.optInt("top_k", 40) : 40;
        int contextSize = llm != null ? llm.optInt("context_size", 1024) : 1024;
        int threads = llm != null ? llm.optInt("threads", 4) : 4;
        int classificationMaxTokens = llm != null ? llm.optInt("classification_max_tokens", 32) : 32;
        int slotMaxTokens = llm != null ? llm.optInt("slot_max_tokens", 64) : 64;
        double classificationTemperature = llm != null ? llm.optDouble("classification_temperature", 0.0) : 0.0;
        double slotTemperature = llm != null ? llm.optDouble("slot_temperature", 0.0) : 0.0;

        return new LlmGenerationOptions(
                clamp(maxTokens, 16, 512),
                (float) Math.max(0.0, Math.min(2.0, temperature)),
                (float) Math.max(0.05, Math.min(1.0, topP)),
                clamp(topK, 1, 200),
                clamp(contextSize, 512, 8192),
                clamp(threads, 1, 8),
                clamp(classificationMaxTokens, 16, 64),
                clamp(slotMaxTokens, 32, 96),
                clampTemperature(classificationTemperature),
                clampTemperature(slotTemperature));
    }

    public static LlmGenerationOptions forActionClassification(JSONObject config) {
        LlmGenerationOptions base = fromConfig(config);
        return new LlmGenerationOptions(
                base.classificationMaxTokens,
                base.classificationTemperature,
                base.topP,
                base.topK,
                base.contextSize,
                base.threads,
                base.classificationMaxTokens,
                base.slotMaxTokens,
                base.classificationTemperature,
                base.slotTemperature);
    }

    public static LlmGenerationOptions forSlotExtraction(JSONObject config) {
        LlmGenerationOptions base = fromConfig(config);
        return new LlmGenerationOptions(
                base.slotMaxTokens,
                base.slotTemperature,
                base.topP,
                base.topK,
                base.contextSize,
                base.threads,
                base.classificationMaxTokens,
                base.slotMaxTokens,
                base.classificationTemperature,
                base.slotTemperature);
    }

    private static int clamp(int value, int min, int max) {
        return Math.max(min, Math.min(max, value));
    }

    private static float clampTemperature(double value) {
        return (float) Math.max(0.0, Math.min(2.0, value));
    }
}
