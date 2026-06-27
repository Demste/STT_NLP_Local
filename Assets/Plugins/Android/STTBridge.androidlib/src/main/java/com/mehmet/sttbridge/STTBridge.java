package com.mehmet.sttbridge;

import android.app.Activity;
import android.content.Context;
import android.content.res.AssetManager;
import android.util.Log;

import org.json.JSONObject;
import org.vosk.Model;
import org.vosk.Recognizer;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Locale;

public final class STTBridge {
    private static final String TAG = "STTBridge";

    private static final String PROVIDER_VOSK = "vosk_tr_offline";
    private static final String PROVIDER_WHISPER = "whisper_cpp_offline";
    private static final String PROVIDER_ELEVENLABS = "elevenlabs_scribe_v2";

    private static final String VOSK_MODEL_ASSET_DIR = "vosk-model-small-tr-0.3";
    private static final String WHISPER_ASSET_DIR = "whisper";

    private static Model voskModel;
    private static boolean whisperLoadTried;
    private static boolean whisperNativeLoaded;

    private STTBridge() {}

    public static void transcribe(
            final String provider,
            final String wavPath,
            final String language,
            final String elevenLabsApiKey,
            final String unityGameObject,
            final String unityCallbackMethod) {

        new Thread(() -> {
            long started = System.currentTimeMillis();
            String text = "";
            String error = "";

            try {
                if (PROVIDER_VOSK.equals(provider)) {
                    text = transcribeVosk(wavPath);
                } else if (PROVIDER_WHISPER.equals(provider)) {
                    text = transcribeWhisper(wavPath, normalizeLanguage(language));
                } else if (PROVIDER_ELEVENLABS.equals(provider)) {
                    text = transcribeElevenLabs(wavPath, normalizeLanguage(language), elevenLabsApiKey);
                } else {
                    throw new IllegalArgumentException("Bilinmeyen provider: " + provider);
                }
            } catch (Throwable t) {
                Log.e(TAG, "STT failed", t);
                error = t.getMessage() != null ? t.getMessage() : t.toString();
            }

            long durationMs = System.currentTimeMillis() - started;
            String json = buildResultJson(provider, normalizeLanguage(language), text, durationMs, wavPath, error);
            sendUnityMessage(unityGameObject, unityCallbackMethod, json);
        }, "stt-bridge-thread").start();
    }

    private static String transcribeVosk(String wavPath) throws Exception {
        Context context = getUnityAppContextOrThrow();
        File modelDir = copyAssetDirectoryToFiles(context, VOSK_MODEL_ASSET_DIR, "stt_models/" + VOSK_MODEL_ASSET_DIR);

        File finalMdl = new File(modelDir, "final.mdl");
        File hclrFst = new File(modelDir, "HCLr.fst");
        File grFst = new File(modelDir, "Gr.fst");

        if (!finalMdl.exists() || !hclrFst.exists() || !grFst.exists()) {
            throw new IllegalStateException(
                    "Vosk modeli eksik veya yanlış klasörde. Beklenen dosyalar: final.mdl, HCLr.fst, Gr.fst. " +
                    "Model içeriği assets/" + VOSK_MODEL_ASSET_DIR + " altında direkt durmalı."
            );
        }

        if (voskModel == null) {
            voskModel = new Model(modelDir.getAbsolutePath());
        }

        WavInfo wavInfo = WavInfo.read(wavPath);
        Recognizer recognizer = new Recognizer(voskModel, wavInfo.sampleRate);
        FileInputStream fis = null;

        try {
            fis = new FileInputStream(wavPath);
            long skipped = fis.skip(wavInfo.dataOffset);

            while (skipped < wavInfo.dataOffset) {
                long more = fis.skip(wavInfo.dataOffset - skipped);
                if (more <= 0) break;
                skipped += more;
            }

            byte[] buffer = new byte[4096];
            int read;

            while ((read = fis.read(buffer)) > 0) {
                recognizer.acceptWaveForm(buffer, read);
            }

            String json = recognizer.getFinalResult();
            return extractText(json);
        } finally {
            try { recognizer.close(); } catch (Throwable ignored) {}
            if (fis != null) try { fis.close(); } catch (IOException ignored) {}
        }
    }

    private static String transcribeWhisper(String wavPath, String language) throws Exception {
        ensureWhisperNativeLoaded();

        Context context = getUnityAppContextOrThrow();
        File modelFile = copyFirstExistingAssetFile(
                context,
                new String[] {
                        WHISPER_ASSET_DIR + "/ggml-base.bin",
                        WHISPER_ASSET_DIR + "/ggml-small.bin",
                        WHISPER_ASSET_DIR + "/ggml-tiny.bin"
                },
                "stt_models/whisper"
        );

        if (modelFile == null || !modelFile.exists()) {
            throw new IllegalStateException("whisper.cpp modeli eksik. assets/whisper/ggml-base.bin veya ggml-small.bin veya ggml-tiny.bin koy.");
        }

        return whisperTranscribeNative(modelFile.getAbsolutePath(), wavPath, language);
    }

    private static String transcribeElevenLabs(String wavPath, String language, String apiKey) throws Exception {
        if (apiKey == null || apiKey.trim().isEmpty()) {
            throw new IllegalStateException("ElevenLabs API key eksik. Unity Inspector veya UI input üzerinden ver.");
        }

        String boundary = "----UnitySTTBoundary" + System.currentTimeMillis();
        URL url = new URL("https://api.elevenlabs.io/v1/speech-to-text");
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();

        connection.setConnectTimeout(30000);
        connection.setReadTimeout(120000);
        connection.setDoOutput(true);
        connection.setRequestMethod("POST");
        connection.setRequestProperty("xi-api-key", apiKey.trim());
        connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=" + boundary);

        OutputStream output = new BufferedOutputStream(connection.getOutputStream());
        writeFormField(output, boundary, "model_id", "scribe_v2");
        writeFormField(output, boundary, "language_code", language);
        writeFormField(output, boundary, "tag_audio_events", "false");
        writeFormField(output, boundary, "diarize", "false");
        writeFileField(output, boundary, "file", "audio.wav", "audio/wav", new File(wavPath));
        output.write(("--" + boundary + "--\r\n").getBytes(StandardCharsets.UTF_8));
        output.flush();
        output.close();

        int code = connection.getResponseCode();
        InputStream input = code >= 200 && code < 300 ? connection.getInputStream() : connection.getErrorStream();
        String response = readAll(input);

        if (code < 200 || code >= 300) {
            throw new IOException("ElevenLabs HTTP " + code + ": " + response);
        }

        return extractText(response);
    }

    private static void ensureWhisperNativeLoaded() {
        if (whisperLoadTried) {
            if (!whisperNativeLoaded) {
                throw new IllegalStateException("libstt_whisper.so yüklenemedi. arm64-v8a jniLibs altına ekle.");
            }
            return;
        }

        whisperLoadTried = true;

        try {
            System.loadLibrary("stt_whisper");
            whisperNativeLoaded = true;
        } catch (Throwable t) {
            whisperNativeLoaded = false;
            throw new IllegalStateException("libstt_whisper.so yüklenemedi: " + t.getMessage());
        }
    }

    private static native String whisperTranscribeNative(String modelPath, String wavPath, String language);

    private static Activity getUnityActivity() {
        try {
            Class<?> unityPlayerClass = Class.forName("com.unity3d.player.UnityPlayer");
            Field currentActivityField = unityPlayerClass.getField("currentActivity");
            Object activity = currentActivityField.get(null);

            if (activity instanceof Activity) {
                return (Activity) activity;
            }
        } catch (Throwable t) {
            Log.e(TAG, "Unity currentActivity alınamadı.", t);
        }

        return null;
    }

    private static Context getUnityAppContextOrThrow() {
        Activity activity = getUnityActivity();

        if (activity == null) {
            throw new IllegalStateException("UnityPlayer.currentActivity null. STTBridge Unity Activity hazır olmadan çağrılmış olabilir.");
        }

        return activity.getApplicationContext();
    }

    private static void sendUnityMessage(final String unityGameObject, final String unityCallbackMethod, final String json) {
        try {
            final Class<?> unityPlayerClass = Class.forName("com.unity3d.player.UnityPlayer");
            final Method unitySendMessageMethod = unityPlayerClass.getMethod(
                    "UnitySendMessage",
                    String.class,
                    String.class,
                    String.class
            );

            Activity activity = getUnityActivity();

            if (activity != null) {
                activity.runOnUiThread(() -> {
                    try {
                        unitySendMessageMethod.invoke(
                                null,
                                unityGameObject != null ? unityGameObject : "",
                                unityCallbackMethod != null ? unityCallbackMethod : "",
                                json != null ? json : ""
                        );
                    } catch (Throwable t) {
                        Log.e(TAG, "UnitySendMessage invoke başarısız.", t);
                    }
                });
            } else {
                unitySendMessageMethod.invoke(
                        null,
                        unityGameObject != null ? unityGameObject : "",
                        unityCallbackMethod != null ? unityCallbackMethod : "",
                        json != null ? json : ""
                );
            }
        } catch (Throwable t) {
            Log.e(TAG, "UnitySendMessage hazırlanamadı.", t);
        }
    }

    private static File copyAssetDirectoryToFiles(Context context, String assetDir, String outputRelativeDir) throws IOException {
        File outputDir = new File(context.getFilesDir(), outputRelativeDir);

        if (!outputDir.exists() && !outputDir.mkdirs()) {
            throw new IOException("Model klasörü oluşturulamadı: " + outputDir);
        }

        copyAssetDirectoryRecursive(context.getAssets(), assetDir, outputDir);
        return outputDir;
    }

    private static void copyAssetDirectoryRecursive(AssetManager assets, String assetPath, File outputDir) throws IOException {
        String[] children = assets.list(assetPath);

        if (children == null || children.length == 0) {
            copyAssetFile(assets, assetPath, outputDir);
            return;
        }

        if (!outputDir.exists() && !outputDir.mkdirs()) {
            throw new IOException("Klasör oluşturulamadı: " + outputDir);
        }

        for (String child : children) {
            String childAssetPath = assetPath + "/" + child;
            File childOutput = new File(outputDir, child);
            String[] nested = assets.list(childAssetPath);

            if (nested != null && nested.length > 0) {
                copyAssetDirectoryRecursive(assets, childAssetPath, childOutput);
            } else {
                copyAssetFile(assets, childAssetPath, outputDir);
            }
        }
    }

    private static void copyAssetFile(AssetManager assets, String assetFilePath, File outputDir) throws IOException {
        File outputFile = new File(outputDir, new File(assetFilePath).getName());

        if (outputFile.exists() && outputFile.length() > 0) return;

        InputStream input = assets.open(assetFilePath);
        OutputStream output = new FileOutputStream(outputFile);

        try {
            byte[] buffer = new byte[8192];
            int read;

            while ((read = input.read(buffer)) > 0) {
                output.write(buffer, 0, read);
            }

            output.flush();
        } finally {
            try { output.close(); } catch (IOException ignored) {}
            try { input.close(); } catch (IOException ignored) {}
        }
    }

    private static File copyFirstExistingAssetFile(Context context, String[] assetFilePaths, String outputRelativeDir) throws IOException {
        AssetManager assets = context.getAssets();
        File outputDir = new File(context.getFilesDir(), outputRelativeDir);

        if (!outputDir.exists() && !outputDir.mkdirs()) {
            throw new IOException("Klasör oluşturulamadı: " + outputDir);
        }

        for (String assetPath : assetFilePaths) {
            try {
                InputStream testInput = assets.open(assetPath);
                testInput.close();

                File outputFile = new File(outputDir, new File(assetPath).getName());

                if (!outputFile.exists() || outputFile.length() == 0) {
                    InputStream input = assets.open(assetPath);
                    OutputStream output = new FileOutputStream(outputFile);

                    try {
                        byte[] buffer = new byte[8192];
                        int read;

                        while ((read = input.read(buffer)) > 0) {
                            output.write(buffer, 0, read);
                        }

                        output.flush();
                    } finally {
                        try { output.close(); } catch (IOException ignored) {}
                        try { input.close(); } catch (IOException ignored) {}
                    }
                }

                return outputFile;
            } catch (IOException ignored) {
                // Try next model name.
            }
        }

        return null;
    }

    private static void writeFormField(OutputStream output, String boundary, String name, String value) throws IOException {
        output.write(("--" + boundary + "\r\n").getBytes(StandardCharsets.UTF_8));
        output.write(("Content-Disposition: form-data; name=\"" + name + "\"\r\n\r\n").getBytes(StandardCharsets.UTF_8));
        output.write(((value != null ? value : "") + "\r\n").getBytes(StandardCharsets.UTF_8));
    }

    private static void writeFileField(OutputStream output, String boundary, String name, String filename, String contentType, File file) throws IOException {
        output.write(("--" + boundary + "\r\n").getBytes(StandardCharsets.UTF_8));
        output.write(("Content-Disposition: form-data; name=\"" + name + "\"; filename=\"" + filename + "\"\r\n").getBytes(StandardCharsets.UTF_8));
        output.write(("Content-Type: " + contentType + "\r\n\r\n").getBytes(StandardCharsets.UTF_8));

        InputStream input = new BufferedInputStream(new FileInputStream(file));

        try {
            byte[] buffer = new byte[8192];
            int read;

            while ((read = input.read(buffer)) > 0) {
                output.write(buffer, 0, read);
            }
        } finally {
            try { input.close(); } catch (IOException ignored) {}
        }

        output.write("\r\n".getBytes(StandardCharsets.UTF_8));
    }

    private static String readAll(InputStream input) throws IOException {
        if (input == null) return "";

        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int read;

        while ((read = input.read(buffer)) > 0) {
            output.write(buffer, 0, read);
        }

        return output.toString("UTF-8");
    }

    private static String extractText(String json) throws Exception {
        JSONObject object = new JSONObject(json);
        return object.optString("text", "");
    }

    private static String normalizeLanguage(String language) {
        if (language == null || language.trim().isEmpty()) return "tr";

        String value = language.trim().toLowerCase(Locale.ROOT);

        if (value.startsWith("tr")) return "tr";

        return value;
    }

    private static String buildResultJson(String provider, String language, String text, long durationMs, String audioFile, String error) {
        try {
            JSONObject object = new JSONObject();
            object.put("provider", provider != null ? provider : "");
            object.put("language", language != null ? language : "tr");
            object.put("text", text != null ? text : "");
            object.put("durationMs", durationMs);
            object.put("audioFile", audioFile != null ? audioFile : "");
            object.put("error", error != null ? error : "");
            return object.toString();
        } catch (Exception e) {
            return "{\"provider\":\"" + safe(provider) + "\",\"language\":\"tr\",\"text\":\"\",\"durationMs\":0,\"audioFile\":\"\",\"error\":\"JSON build error\"}";
        }
    }

    private static String safe(String value) {
        return value == null ? "" : value.replace("\\", "\\\\").replace("\"", "\\\"");
    }

    private static final class WavInfo {
        final int sampleRate;
        final int channels;
        final int bitsPerSample;
        final long dataOffset;

        WavInfo(int sampleRate, int channels, int bitsPerSample, long dataOffset) {
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.bitsPerSample = bitsPerSample;
            this.dataOffset = dataOffset;
        }

        static WavInfo read(String wavPath) throws IOException {
            DataInputStream input = new DataInputStream(new FileInputStream(wavPath));

            try {
                byte[] header = new byte[12];
                input.readFully(header);

                if (!"RIFF".equals(new String(header, 0, 4, StandardCharsets.US_ASCII)) ||
                        !"WAVE".equals(new String(header, 8, 4, StandardCharsets.US_ASCII))) {
                    throw new IOException("WAV değil: " + wavPath);
                }

                int sampleRate = 16000;
                int channels = 1;
                int bitsPerSample = 16;
                long offset = 12;

                while (true) {
                    byte[] chunk = new byte[8];
                    input.readFully(chunk);
                    offset += 8;

                    String id = new String(chunk, 0, 4, StandardCharsets.US_ASCII);
                    int size = leInt(chunk, 4);

                    if ("fmt ".equals(id)) {
                        byte[] fmt = new byte[size];
                        input.readFully(fmt);
                        offset += size;

                        channels = leShort(fmt, 2);
                        sampleRate = leInt(fmt, 4);
                        bitsPerSample = leShort(fmt, 14);
                    } else if ("data".equals(id)) {
                        return new WavInfo(sampleRate, channels, bitsPerSample, offset);
                    } else {
                        long skipped = input.skip(size);
                        offset += skipped;

                        while (skipped < size) {
                            long more = input.skip(size - skipped);

                            if (more <= 0) {
                                throw new IOException("WAV chunk skip başarısız: " + id);
                            }

                            skipped += more;
                            offset += more;
                        }
                    }
                }
            } finally {
                input.close();
            }
        }

        private static int leInt(byte[] data, int offset) {
            return (data[offset] & 0xff) |
                    ((data[offset + 1] & 0xff) << 8) |
                    ((data[offset + 2] & 0xff) << 16) |
                    ((data[offset + 3] & 0xff) << 24);
        }

        private static int leShort(byte[] data, int offset) {
            return (data[offset] & 0xff) | ((data[offset + 1] & 0xff) << 8);
        }
    }
}
