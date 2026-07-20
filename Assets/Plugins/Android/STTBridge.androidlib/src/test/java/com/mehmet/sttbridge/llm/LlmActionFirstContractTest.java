package com.mehmet.sttbridge.llm;

import org.json.JSONObject;
import org.json.JSONArray;

public final class LlmActionFirstContractTest {
    public static void main(String[] args) throws Exception {
        hallucinatedFieldsAreRemoved();
        baselineEvidenceIsSparse();
        missingInfoComesFromCatalog();
        strictSingleObjectParserRejectsProse();
        promptIsShortAndActionOnly();
        semanticObjectSlotIsEvidenceGated();
        publicSerializerIsFixedShape();
        trustedMetadataSurvivesUnknown();
        actionWithoutEvidenceIsRejected();
        System.out.println("LlmActionFirstContractTest: OK");
    }

    private static void hallucinatedFieldsAreRemoved() throws Exception {
        JSONObject malicious = new JSONObject()
                .put("action", "LAND_HOME")
                .put("direction", "LEFT")
                .put("distance_m", 8)
                .put("object", "TRUCK")
                .put("color", "GREEN")
                .put("coordinate", new JSONObject().put("lat", 36).put("lon", 36));
        LlmDroneCommandValidator.Result result = LlmDroneCommandValidator.validateClassification(malicious, evidence("LAND_HOME"), new JSONObject());
        assertEquals("LAND_HOME", result.command.getString("action"));
        assertEquals(2, result.command.length());
        assertTrue(result.command.has("confidence"), "pipeline confidence missing");
    }

    private static void baselineEvidenceIsSparse() throws Exception {
        JSONObject evidence = new JSONObject().put("action", "MOVE_VERTICAL").put("direction", "UP").put("distance_m", 8).put("confidence", .97);
        LlmDroneCommandValidator.Result result = LlmDroneCommandValidator.validateBaseline(evidence, new JSONObject());
        assertEquals(4, result.command.length());
        assertEquals("UP", result.command.getString("direction"));
        assertEquals(8.0, result.command.getDouble("distance_m"));
    }

    private static void missingInfoComesFromCatalog() throws Exception {
        JSONObject classification = new JSONObject().put("action", "TURN").put("angle_deg", 99);
        LlmDroneCommandValidator.Result result = LlmDroneCommandValidator.validateClassification(classification, evidence("TURN"), new JSONObject());
        assertEquals("MISSING_INFO", result.command.getString("action"));
        assertEquals("TURN", result.command.getString("target_action"));
        assertEquals("turn_direction", result.command.getString("missing_field"));
        assertEquals("Dönüş komutu için yön bilgisi eksik.", result.command.getString("missing_message"));
        assertTrue(!result.command.has("angle_deg"), "LLM slot leaked into final JSON");
    }

    private static void strictSingleObjectParserRejectsProse() throws Exception {
        assertEquals("STOP", LlmJsonUtils.parseSingleObject("{\"action\":\"STOP\"}").getString("action"));
        boolean rejected = false;
        try { LlmJsonUtils.parseSingleObject("cevap: {\"action\":\"STOP\"}"); }
        catch (Exception expected) { rejected = true; }
        assertTrue(rejected, "prose-wrapped JSON must be rejected");
    }

    private static void promptIsShortAndActionOnly() throws Exception {
        String prompt = LlmPromptBuilder.build("8 metre yüksel", "{\"large\":\"spacy payload\"}", new JSONObject());
        assertTrue(prompt.contains("{\"action\":\"ACTION\"}"), "action JSON contract missing");
        assertTrue(!prompt.contains("distance_m"), "parameter schema leaked into action prompt");
        assertTrue(!prompt.contains("coordinate"), "coordinate schema leaked into action prompt");
        assertTrue(!prompt.contains("voice_type"), "voice metadata leaked into action prompt");
        assertTrue(LlmPromptBuilder.estimatePromptTokens(prompt) <= 300, "estimated prompt exceeds 300 tokens");
        System.out.println("estimated_action_prompt_tokens=" + LlmPromptBuilder.estimatePromptTokens(prompt));
        System.out.println("estimated_land_home_output_tokens=" + LlmPromptBuilder.estimateOutputTokens("{\"action\":\"LAND_HOME\"}"));
        JSONObject config = LlmCommandSchema.defaultConfig();
        assertEquals(32, LlmGenerationOptions.forActionClassification(config).maxTokens);
        assertEquals(64, LlmGenerationOptions.forSlotExtraction(config).maxTokens);
        assertEquals(0.0f, LlmGenerationOptions.forActionClassification(config).temperature);
        assertEquals(0.0f, LlmGenerationOptions.forSlotExtraction(config).temperature);
        String slotPrompt = LlmPromptBuilder.buildObjectClassificationPrompt(
                "ağır vasıtayı bul", "ağır vasıtayı",
                "{\"compact_evidence\":{\"root_lemma\":\"bul\",\"noun_candidates\":[\"vasıta\"]}}", config);
        assertTrue(slotPrompt.contains("ALLOWED_OBJECTS="), "object whitelist missing");
        assertTrue(slotPrompt.contains("SPACY_COMPACT="), "compact spaCy evidence missing");
        assertTrue(!slotPrompt.contains("SEMI_TRUCK\":null"), "full schema leaked into slot prompt");
    }

    private static void semanticObjectSlotIsEvidenceGated() throws Exception {
        JSONObject baseline = new JSONObject()
                .put("action", "MISSING_INFO")
                .put("target_action", "FIND_OBJECT")
                .put("detected_object_phrase", "ağır vasıtayı")
                .put("action_evidence", evidenceSummary("FIND_OBJECT"));
        JSONObject valid = new JSONObject().put("object", "TRUCK").put("evidence", "ağır vasıtayı");
        LlmDroneCommandValidator.Result accepted = LlmDroneCommandValidator.validateSemanticSlot(
                valid, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("FIND_OBJECT", accepted.command.getString("action"));
        assertEquals("TRUCK", accepted.command.getString("object"));

        JSONObject hallucinated = new JSONObject().put("object", "CRANE").put("evidence", "vinci");
        LlmDroneCommandValidator.Result rejected = LlmDroneCommandValidator.validateSemanticSlot(
                hallucinated, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("MISSING_INFO", rejected.command.getString("action"));
        assertTrue(rejected.errors.length() > 0, "missing textual evidence must be rejected");

        JSONObject none = new JSONObject().put("object", "NONE").put("evidence", "");
        LlmDroneCommandValidator.Result missing = LlmDroneCommandValidator.validateSemanticSlot(
                none, baseline, "FIND_OBJECT", "Kartal bul", new JSONObject());
        assertEquals("MISSING_INFO", missing.command.getString("action"));
        assertTrue(!missing.command.has("object"), "NONE must never enter final JSON");
    }

    private static void publicSerializerIsFixedShape() throws Exception {
        JSONObject sparse = new JSONObject()
                .put("action", "FIND_OBJECT")
                .put("object", "SEMI_TRUCK")
                .put("confidence", .97);
        JSONObject publicCommand = LlmPublicCommandSerializer.materialize(sparse);
        assertEquals(21, publicCommand.length());
        for (String field : LlmPublicCommandSerializer.PUBLIC_FIELDS) {
            assertTrue(publicCommand.has(field), "public field missing: " + field);
        }
        assertEquals("SEMI_TRUCK", publicCommand.getString("object"));
        assertTrue(publicCommand.isNull("distance_m"), "unused numeric field must be JSON null");
        assertTrue(publicCommand.isNull("coordinate"), "unused object field must be JSON null");

        JSONObject unknown = LlmPublicCommandSerializer.materialize(null);
        assertEquals("UNKNOWN", unknown.getString("action"));
        assertEquals(0.0, unknown.getDouble("confidence"));
    }

    private static void trustedMetadataSurvivesUnknown() throws Exception {
        JSONObject context = new JSONObject()
                .put("voice_type", "NORMAL")
                .put("stt_confidence_level", "GOOD");
        LlmDroneCommandValidator.Result result = LlmDroneCommandValidator.validateBaseline(
                new JSONObject().put("action", "UNKNOWN").put("confidence", .8), context);
        assertEquals("UNKNOWN", result.command.getString("action"));
        assertEquals(0.0, result.command.getDouble("confidence"));
        assertEquals("NORMAL", result.command.getString("voice_type"));
        assertEquals("GOOD", result.command.getString("stt_confidence_level"));
    }

    private static void actionWithoutEvidenceIsRejected() throws Exception {
        LlmDroneCommandValidator.Result result = LlmDroneCommandValidator.validateClassification(
                new JSONObject().put("action", "MOVE_DIRECTION"),
                new JSONObject().put("action", "UNKNOWN").put("action_evidence", evidenceSummary()),
                new JSONObject());
        assertEquals("UNKNOWN", result.command.getString("action"));
        assertTrue(!result.command.has("target_action"), "unsupported LLM action created target_action");
        assertTrue(!result.command.has("missing_field"), "unsupported LLM action created missing_field");
        assertTrue(result.errors.length() > 0, "missing evidence must be explicit");
    }

    private static JSONObject evidence(String action) throws Exception {
        return new JSONObject().put("action", action).put("action_evidence", evidenceSummary(action));
    }

    private static JSONObject evidenceSummary(String... actions) throws Exception {
        JSONArray supported = new JSONArray();
        for (String action : actions) supported.put(action);
        return new JSONObject().put("has_any", actions.length > 0).put("supported_actions", supported);
    }

    private static void assertTrue(boolean value, String message) {
        if (!value) throw new AssertionError(message);
    }

    private static void assertEquals(Object expected, Object actual) {
        if (expected == null ? actual != null : !expected.equals(actual)) {
            throw new AssertionError("expected=" + expected + " actual=" + actual);
        }
    }
}
