package com.mehmet.sttbridge.llm;

import org.json.JSONObject;
import org.json.JSONArray;

public final class LlmActionFirstContractTest {
    public static void main(String[] args) throws Exception {
        hallucinatedFieldsAreRemoved();
        baselineEvidenceIsSparse();
        missingInfoComesFromCatalog();
        strictSingleObjectParserRejectsProse();
        realDeviceEchoFixtureIsRejected();
        promptIsShortAndActionOnly();
        semanticObjectSlotIsEvidenceGated();
        closedSemanticResultIsEvidenceGated();
        turnRequiresDirectionAndAngleAcrossJavaContract();
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
        assertParserRejects("cevap: {\"action\":\"STOP\"}", "prose-wrapped JSON");
        assertParserRejects("```json\n{\"object\":\"TRUCK\",\"evidence\":\"ağır vasıtayı\"}\n```", "Markdown JSON");
        assertParserRejects("{\"object\":\"TRUCK\",\"evidence\":\"ağır vasıtayı\"} açıklama", "JSON suffix");
        assertParserRejects("{\"object\":\"TRUCK\",\"evidence\":", "truncated JSON");
    }

    private static void realDeviceEchoFixtureIsRejected() throws Exception {
        String raw = "USER_COMMAND_BEGIN\n"
                + "AGIR vasıtayı bul\n"
                + "USER_COMMAND_END\n"
                + "NORMALIZED_COMMAND=AGIR vasıtayı bul\n"
                + "FIXED_ACTION=SEARCH_OBJECT\n"
                + "UNRESOLVED_FIELD=object\n"
                + "RAW_SLOT_EVIDENCE=ağır vasıtayı\n"
                + "ALLOWED_OBJECTS=TRUCK,PICKUP,C";
        assertEquals(212, raw.getBytes(java.nio.charset.StandardCharsets.UTF_8).length);
        assertParserRejects(raw, "real-device 212-byte prompt echo");
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
        assertEquals(24, LlmGenerationOptions.forSlotExtraction(config).maxTokens);
        assertEquals(0.0f, LlmGenerationOptions.forActionClassification(config).temperature);
        assertEquals(0.0f, LlmGenerationOptions.forSlotExtraction(config).temperature);
        String grammar = LlmPromptBuilder.buildObjectGrammar("ağır vasıtayı");
        assertTrue(grammar.contains("object ::="), "object grammar rule missing");
        String genericGrammar = LlmPromptBuilder.buildObjectGrammar("tanımsız aracı");
        assertTrue(genericGrammar.contains("TRUCK") && genericGrammar.contains("EXCAVATOR"), "generic grammar allowlist missing");
        assertTrue(grammar.contains("ağır vasıtayı"), "exact evidence missing from grammar");
        assertTrue(!grammar.contains("EXCAVATOR"), "closed truck hint did not narrow grammar");
        assertTrue(LlmPromptBuilder.buildObjectGrammar("kazı aracının").contains("EXCAVATOR"),
                "closed excavation hint missing from grammar");
        assertTrue(!LlmGenerationOptions.forSlotExtraction(config, "ağır vasıtayı").grammar.isEmpty(),
                "slot grammar was not attached to generation options");
        System.out.println("slot_grammar=" + grammar);
        String slotPrompt = LlmPromptBuilder.buildObjectClassificationPrompt(
                "ağır vasıtayı bul", "FIND_OBJECT", "ağır vasıtayı",
                "{\"compact_evidence\":{\"root_lemma\":\"bul\",\"noun_candidates\":[\"vasıta\"]}}", config);
        assertTrue(slotPrompt.contains("ALLOWED="), "object whitelist missing");
        assertTrue(slotPrompt.contains("RAW=ağır vasıtayı"), "raw object evidence missing");
        assertTrue(slotPrompt.contains("Ağır/yük taşıtı=TRUCK"), "semantic guidance missing");
        assertTrue(slotPrompt.contains("RETURN={\"object\":\"ALLOWED_VALUE\",\"evidence\":\"ağır vasıtayı\"}"), "slot schema missing");
        assertTrue(slotPrompt.contains("CANONICAL_HINT=TRUCK"), "closed semantic hint missing");
        assertTrue(!slotPrompt.contains("NORMALIZED_COMMAND="), "full command leaked into slot prompt");
        assertTrue(!slotPrompt.contains("FIXED_ACTION="), "fixed action leaked into slot prompt");
        assertTrue(!slotPrompt.contains("SPACY_COMPACT="), "spaCy payload leaked into slot prompt");
        assertTrue(!slotPrompt.contains("SEMI_TRUCK\":null"), "full schema leaked into slot prompt");
        assertTrue(LlmPromptBuilder.estimatePromptTokens(slotPrompt) <= 180, "slot prompt estimate exceeds 180 tokens");
        System.out.println("estimated_slot_prompt_tokens=" + LlmPromptBuilder.estimatePromptTokens(slotPrompt));
        String semanticPrompt = LlmPromptBuilder.buildSemanticCommandPrompt(
                "sağ cenaha beş metre yol al",
                "{\"semantic_frame\":{\"raw_text\":\"sağ cenaha beş metre yol al\","
                        + "\"candidate_actions\":[\"MOVE_DIRECTION\"],"
                        + "\"direction_spans\":[{\"type\":\"DIRECTION\",\"text\":\"sağ cenaha\",\"value\":\"RIGHT\"}],"
                        + "\"quantity_spans\":[{\"type\":\"QUANTITY\",\"text\":\"beş metre\",\"value\":5,\"unit\":\"METER\"}],"
                        + "\"predicate_evidence\":{\"MOVE_DIRECTION\":\"yol al\"}},"
                        + "\"compact_evidence\":{\"root_lemma\":\"al\"}}", config);
        assertTrue(semanticPrompt.contains("CANDIDATE_ACTIONS=MOVE_DIRECTION,UNKNOWN"), "narrowed candidates missing");
        assertTrue(semanticPrompt.contains("distance_m"), "action-specific slot schema missing");
        assertTrue(semanticPrompt.contains("action_evidence"), "action evidence contract missing");
        assertTrue(semanticPrompt.contains("TRUSTED_SEMANTIC_FRAME="), "bounded semantic frame missing");
        assertTrue(semanticPrompt.contains("\"RIGHT\"") && semanticPrompt.contains("\"sağ cenaha\""),
                "trusted direction shape missing");
        assertTrue(semanticPrompt.contains("\"beş metre\"") && semanticPrompt.contains(":5"),
                "trusted numeric shape missing");
        assertTrue(LlmPromptBuilder.estimatePromptTokens(semanticPrompt) <= 700, "semantic prompt too large");
        assertEquals(128, LlmGenerationOptions.forSemanticExtraction(config).maxTokens);
    }

    private static void semanticObjectSlotIsEvidenceGated() throws Exception {
        JSONObject baseline = new JSONObject()
                .put("action", "MISSING_INFO")
                .put("target_action", "FIND_OBJECT")
                .put("detected_object_phrase", "ağır vasıtayı")
                .put("semantic_frame", rawObjectFrame("Kartal ağır vasıtayı bul", "ağır vasıtayı"))
                .put("action_evidence", evidenceSummary("FIND_OBJECT"));
        JSONObject valid = new JSONObject().put("object", "TRUCK").put("evidence", "ağır vasıtayı");
        LlmDroneCommandValidator.Result accepted = LlmDroneCommandValidator.validateSemanticSlot(
                valid, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("FIND_OBJECT", accepted.command.getString("action"));
        assertEquals("TRUCK", accepted.command.getString("object"));

        JSONObject extraField = new JSONObject(valid.toString()).put("reason", "heavy vehicle");
        LlmDroneCommandValidator.Result extraRejected = LlmDroneCommandValidator.validateSemanticSlot(
                extraField, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("UNKNOWN", extraRejected.command.getString("action"));

        JSONObject fullCommand = new JSONObject(valid.toString()).put("action", "FIND_OBJECT");
        LlmDroneCommandValidator.Result fullRejected = LlmDroneCommandValidator.validateSemanticSlot(
                fullCommand, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("UNKNOWN", fullRejected.command.getString("action"));

        JSONObject hallucinated = new JSONObject().put("object", "CRANE").put("evidence", "vinci");
        LlmDroneCommandValidator.Result rejected = LlmDroneCommandValidator.validateSemanticSlot(
                hallucinated, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("UNKNOWN", rejected.command.getString("action"));
        assertTrue(rejected.errors.length() > 0, "missing textual evidence must be rejected");

        JSONObject normalizedEvidence = new JSONObject().put("object", "TRUCK").put("evidence", "agir vasitayi");
        LlmDroneCommandValidator.Result normalizedRejected = LlmDroneCommandValidator.validateSemanticSlot(
                normalizedEvidence, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("UNKNOWN", normalizedRejected.command.getString("action"));

        JSONObject outsideAllowlist = new JSONObject().put("object", "AIRPLANE").put("evidence", "ağır vasıtayı");
        LlmDroneCommandValidator.Result outsideRejected = LlmDroneCommandValidator.validateSemanticSlot(
                outsideAllowlist, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("UNKNOWN", outsideRejected.command.getString("action"));

        JSONObject changesAction = new JSONObject(valid.toString()).put("action", "LAND_HOME");
        LlmDroneCommandValidator.Result actionRejected = LlmDroneCommandValidator.validateSemanticSlot(
                changesAction, baseline, "FIND_OBJECT", "Kartal ağır vasıtayı bul", new JSONObject());
        assertEquals("UNKNOWN", actionRejected.command.getString("action"));

        JSONObject none = new JSONObject().put("object", "NONE").put("evidence", "");
        LlmDroneCommandValidator.Result missing = LlmDroneCommandValidator.validateSemanticSlot(
                none, baseline, "FIND_OBJECT", "Kartal bul", new JSONObject());
        assertEquals("UNKNOWN", missing.command.getString("action"));
        assertTrue(!missing.command.has("object"), "NONE must never enter final JSON");
    }

    private static void assertParserRejects(String raw, String label) throws Exception {
        boolean rejected = false;
        try { LlmJsonUtils.parseSingleObject(raw); }
        catch (Exception expected) { rejected = true; }
        assertTrue(rejected, label + " must be rejected");
    }

    private static void closedSemanticResultIsEvidenceGated() throws Exception {
        JSONObject frame = new JSONObject()
                .put("safety_blocked", false)
                .put("predicate_evidence", new JSONObject().put("MOVE_DIRECTION", "yol al"))
                .put("protected_spans", new JSONArray()
                        .put(span("DIRECTION", "sağ cenaha", "RIGHT", null))
                        .put(span("QUANTITY", "beş metre", 5, "METER")));
        JSONObject baseline = new JSONObject()
                .put("action", "UNKNOWN")
                .put("semantic_frame", frame)
                .put("action_evidence", evidenceSummary("MOVE_DIRECTION"));
        JSONObject valid = new JSONObject()
                .put("action", "MOVE_DIRECTION")
                .put("action_evidence", "yol al")
                .put("slots", new JSONObject()
                        .put("direction", semanticSlot("RIGHT", "sağ cenaha"))
                        .put("distance_m", semanticSlot(5, "beş metre")));
        LlmDroneCommandValidator.Result accepted = LlmDroneCommandValidator.validateSemanticResult(
                valid, baseline, "sağ cenaha beş metre yol al", new JSONObject());
        assertEquals("MOVE_DIRECTION", accepted.command.getString("action"));
        assertEquals("RIGHT", accepted.command.getString("direction"));
        assertEquals(5.0, accepted.command.getDouble("distance_m"));

        JSONObject hallucinated = new JSONObject(valid.toString());
        hallucinated.getJSONObject("slots").put("direction", semanticSlot("LEFT", "sağ cenaha"));
        LlmDroneCommandValidator.Result rejected = LlmDroneCommandValidator.validateSemanticResult(
                hallucinated, baseline, "sağ cenaha beş metre yol al", new JSONObject());
        assertEquals("UNKNOWN", rejected.command.getString("action"));
        assertTrue(!rejected.command.has("direction"), "hallucinated direction survived");
        assertTrue(rejected.errors.length() > 0, "semantic mismatch must be explicit");
    }

    private static void turnRequiresDirectionAndAngleAcrossJavaContract() throws Exception {
        LlmDroneCommandValidator.Result missingAngle = LlmDroneCommandValidator.validateBaseline(
                new JSONObject().put("action", "TURN").put("turn_direction", "RIGHT").put("confidence", .84),
                new JSONObject());
        assertEquals("MISSING_INFO", missingAngle.command.getString("action"));
        assertEquals("angle_deg", missingAngle.command.getString("missing_field"));
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

    private static JSONObject semanticSlot(Object value, String evidence) throws Exception {
        return new JSONObject().put("value", value).put("evidence", evidence);
    }

    private static JSONObject span(String type, String text, Object value, String unit) throws Exception {
        JSONObject span = new JSONObject().put("type", type).put("text", text).put("value", value);
        if (unit != null) span.put("unit", unit);
        return span;
    }

    private static JSONObject rawObjectFrame(String raw, String phrase) throws Exception {
        int start = raw.indexOf(phrase);
        JSONObject span = new JSONObject()
                .put("type", "OBJECT")
                .put("field", "object")
                .put("text", phrase)
                .put("normalized", phrase)
                .put("start", start)
                .put("end", start + phrase.length())
                .put("canonical_value", JSONObject.NULL)
                .put("source", "spacy_predicate_object_phrase");
        return new JSONObject()
                .put("raw_text", raw)
                .put("safety_blocked", false)
                .put("object_spans", new JSONArray().put(span));
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
