package com.mehmet.sttbridge.llm;

import org.json.JSONObject;

public interface OfflineLlmEngine {
    String getEngineName();

    LlmBackendStatus initialize(JSONObject config);

    LlmBackendStatus getStatus();

    String generate(String prompt, LlmGenerationOptions options) throws Exception;
}
