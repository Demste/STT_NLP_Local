package com.mehmet.sttbridge.llm;

import org.json.JSONObject;

import java.nio.charset.StandardCharsets;

/** Builds bounded action-only and object-only classification prompts. */
public final class LlmPromptBuilder {
    private static final String ACTION_SYSTEM =
            "Türkçe drone komutunun niyetini sınıflandır. Yalnız kullanıcı metnindeki kanıtı kullan; slot uydurma. "
            + "FIND_OBJECT=belirtilen fiziksel nesneyi arama veya tespit; TRACK_OBJECT=belirtilen nesneyi sürekli takip; "
            + "HOLD_OVER_OBJECT=belirtilen nesnenin üzerinde konumu koruma; OBSERVE_CURRENT=mevcut konumdan çevreyi gözlemleme; "
            + "HOVER=havada sabit kalma; STOP=mevcut hareketi durdurma; LAND_HOME=başlangıç/eve iniş; "
            + "LAND_CURRENT=mevcut yere iniş; LAND_AT_WAIT_POINT=bekleme noktasına iniş; MOVE_VERTICAL=dikey mesafeli hareket; "
            + "MOVE_DIRECTION=yatay yön ve mesafe hareketi. Diğer izinliler TAKE_OFF,SET_SPEED,TURN,GO_TO_LOCATION,FIND_MARKER. "
            + "Niyet yoksa UNKNOWN. Yalnız {\"action\":\"ACTION\"} biçiminde tek JSON nesnesi yaz.";

    private static final String OBJECT_SYSTEM =
            "Türkçe komutta açıkça söylenen fiziksel nesneyi sınıflandır. Yalnız verilen whitelist içinden seç. "
            + "Nesne söylenmediyse veya eşleşmiyorsa NONE seç. evidence kullanıcı komutundaki kısa ve aynen bulunan ifade olmalı. "
            + "Yalnız {\"object\":\"VALUE\",\"evidence\":\"metin\"} biçiminde tek JSON nesnesi yaz.";

    private LlmPromptBuilder() {}

    public static String build(String rawText, String spacyAnalysisJson, JSONObject config) {
        return buildActionClassificationPrompt(rawText, spacyAnalysisJson, config);
    }

    public static String buildActionClassificationPrompt(String analysisInput, String spacyAnalysisJson, JSONObject config) {
        return chat(ACTION_SYSTEM, analysisInput, compactEvidence(spacyAnalysisJson));
    }

    public static String buildObjectClassificationPrompt(String analysisInput, String detectedPhrase, String spacyAnalysisJson, JSONObject config) {
        String evidence = compactEvidence(spacyAnalysisJson);
        String user = "ALLOWED_OBJECTS=" + LlmCommandSchema.objectCsv() + ",NONE\n"
                + "DETECTED_OBJECT_PHRASE=" + safe(detectedPhrase) + "\n" + evidence;
        return chat(OBJECT_SYSTEM, analysisInput, user);
    }

    public static String buildSmokePrompt() {
        return build("15 metre yüksel", "{}", null);
    }

    /** Native JNI currently exposes no tokenizer count; this is marked as an estimate in diagnostics. */
    public static int estimatePromptTokens(String prompt) {
        int bytes = safe(prompt).getBytes(StandardCharsets.UTF_8).length;
        return Math.max(1, (bytes + 3) / 4);
    }

    public static int estimateOutputTokens(String output) {
        int bytes = safe(output).getBytes(StandardCharsets.UTF_8).length;
        return Math.max(0, (bytes + 3) / 4);
    }

    static String classificationInstruction() { return ACTION_SYSTEM; }

    private static String chat(String system, String command, String evidence) {
        return "<|im_start|>system\n" + system + "\n<|im_end|>\n"
                + "<|im_start|>user\nUSER_COMMAND_BEGIN\n" + safe(command)
                + "\nUSER_COMMAND_END\n" + safe(evidence)
                + "\n<|im_end|>\n<|im_start|>assistant\n";
    }

    private static String compactEvidence(String spacyAnalysisJson) {
        try {
            JSONObject root = new JSONObject(safe(spacyAnalysisJson).trim().isEmpty() ? "{}" : spacyAnalysisJson);
            JSONObject compact = root.optJSONObject("compact_evidence");
            return compact != null ? "SPACY_COMPACT=" + compact.toString() : "SPACY_COMPACT={}";
        } catch (Throwable ignored) {
            return "SPACY_COMPACT={}";
        }
    }
    private static String safe(String value) { return value != null ? value : ""; }
}
