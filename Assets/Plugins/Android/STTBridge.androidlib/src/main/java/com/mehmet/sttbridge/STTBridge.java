package com.mehmet.sttbridge;

import android.app.Activity;
import android.content.ClipData;
import android.content.ContentResolver;
import android.content.ContentUris;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.res.AssetManager;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import android.util.Log;

import androidx.core.content.FileProvider;

import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class STTBridge {
    private static final String TAG = "STTBridge";

    private static final String PROVIDER_WHISPER = "whisper_cpp_offline";

    private static final String WHISPER_ASSET_DIR = "whisper";
    private static final String WHISPER_MODEL_ASSET = WHISPER_ASSET_DIR + "/ggml-base.bin";
    private static final String WHISPER_EXPECTED_ASSET_PATH = "assets/" + WHISPER_MODEL_ASSET;
    private static final String WHISPER_MODEL_OUTPUT_DIR = "stt_models/whisper";
    private static final int WHISPER_THREAD_COUNT = 4;
    private static final Object WHISPER_STATE_LOCK = new Object();
    private static final ExecutorService WHISPER_EXECUTOR = Executors.newSingleThreadExecutor(runnable -> {
        Thread thread = new Thread(runnable, "stt-whisper-worker");
        thread.setDaemon(true);
        return thread;
    });

    private static boolean whisperLoadTried;
    private static boolean whisperNativeLoaded;
    private static long whisperNativeHandle;
    private static String whisperLoadedModelPath = "";

    private STTBridge() {}

    // Backwards-compatible entry points for the active Android system recognizer.
    public static String prepareSpeechRecognizer(String requestedMode) {
        return AndroidSpeechRecognizerBridge.prepareSpeechRecognizer(requestedMode);
    }

    public static void startSpeechRecognition(String sessionId, String requestedMode,
                                              String unityGameObject, String unityCallbackMethod) {
        AndroidSpeechRecognizerBridge.startListening(sessionId, requestedMode, unityGameObject, unityCallbackMethod);
    }

    public static void stopSpeechRecognition(String sessionId) {
        AndroidSpeechRecognizerBridge.stopListening(sessionId);
    }

    public static void cancelSpeechRecognition(String sessionId) {
        AndroidSpeechRecognizerBridge.cancel(sessionId);
    }

    public static void destroySpeechRecognizer() {
        AndroidSpeechRecognizerBridge.destroy();
    }

    public static boolean exportFileToPublicDocuments(final String sourcePath, final String sessionId, final String mimeType) {
        Activity activity = getUnityActivity();

        if (activity == null) {
            Log.e(TAG, "Public export failed: Unity activity null.");
            return false;
        }

        try {
            if (sourcePath == null || sourcePath.trim().isEmpty()) {
                Log.e(TAG, "Public export failed: empty source path.");
                return false;
            }

            if (sessionId == null || sessionId.trim().isEmpty()) {
                Log.e(TAG, "Public export failed: empty session id.");
                return false;
            }

            File sourceFile = new File(sourcePath);

            if (!sourceFile.exists() || !sourceFile.isFile()) {
                Log.e(TAG, "Public export failed: source missing: " + sourcePath);
                return false;
            }

            String safeMimeType = mimeType != null && !mimeType.trim().isEmpty()
                    ? mimeType.trim()
                    : "application/octet-stream";

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                return exportFileToMediaStore(
                        activity,
                        sourceFile,
                        sessionId.trim(),
                        safeMimeType,
                        Environment.DIRECTORY_DOCUMENTS
                ) || exportFileToMediaStore(
                        activity,
                        sourceFile,
                        sessionId.trim(),
                        safeMimeType,
                        Environment.DIRECTORY_DOWNLOADS
                );
            }

            return exportFileToLegacyPublicDirectory(sourceFile, sessionId.trim(), Environment.DIRECTORY_DOCUMENTS);
        } catch (Throwable t) {
            Log.e(TAG, "Public export failed.", t);
            return false;
        }
    }

    public static boolean exportTextFileToPublicDocuments(final String sourcePath, final String sessionId) {
        return exportFileToPublicDocuments(sourcePath, sessionId, "text/plain");
    }

    public static boolean exportAudioFileToPublicDocuments(final String sourcePath, final String sessionId) {
        return exportFileToPublicDocuments(sourcePath, sessionId, "audio/wav");
    }

    public static boolean shareTextFile(final String txtPath) {
        Activity activity = getUnityActivity();

        if (activity == null) {
            Log.e(TAG, "TXT share failed: Unity activity null.");
            return false;
        }

        try {
            if (txtPath == null || txtPath.trim().isEmpty()) {
                Log.e(TAG, "TXT share failed: empty path.");
                return false;
            }

            File file = new File(txtPath);

            if (!file.exists()) {
                Log.e(TAG, "TXT share failed: file missing: " + txtPath);
                return false;
            }

            String authority = activity.getPackageName() + ".sttfileprovider";
            Uri uri = FileProvider.getUriForFile(activity, authority, file);

            Intent sendIntent = new Intent(Intent.ACTION_SEND);
            sendIntent.setType("text/plain");
            sendIntent.putExtra(Intent.EXTRA_STREAM, uri);
            sendIntent.putExtra(Intent.EXTRA_SUBJECT, "PartTime STT result.txt");
            sendIntent.setClipData(ClipData.newRawUri("result.txt", uri));
            sendIntent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);

            Intent chooser = Intent.createChooser(sendIntent, "Share TXT");
            chooser.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            activity.startActivity(chooser);
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "TXT share failed.", t);
            return false;
        }
    }

    private static boolean exportFileToMediaStore(
            Context context,
            File sourceFile,
            String sessionId,
            String mimeType,
            String publicRootDirectory) {

        Uri collection = MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY);
        ContentResolver resolver = context.getContentResolver();
        String relativePath = publicRootDirectory + "/PartTime/STT/" + sessionId + "/";
        String displayName = sourceFile.getName();

        Uri existingUri = findMediaStoreFile(resolver, collection, relativePath, displayName);

        if (existingUri != null) {
            try {
                resolver.delete(existingUri, null, null);
            } catch (Throwable t) {
                Log.e(TAG, "Public export existing item delete failed: " + existingUri, t);
                return false;
            }
        }

        ContentValues values = new ContentValues();
        values.put(MediaStore.MediaColumns.DISPLAY_NAME, displayName);
        values.put(MediaStore.MediaColumns.MIME_TYPE, mimeType);
        values.put(MediaStore.MediaColumns.RELATIVE_PATH, relativePath);
        values.put(MediaStore.MediaColumns.IS_PENDING, 1);

        Uri uri = null;

        try {
            uri = resolver.insert(collection, values);

            if (uri == null) {
                Log.e(TAG, "Public export insert returned null for: " + relativePath + displayName);
                return false;
            }

            OutputStream output = resolver.openOutputStream(uri, "w");

            if (output == null) {
                Log.e(TAG, "Public export output stream null for: " + uri);
                resolver.delete(uri, null, null);
                return false;
            }

            try {
                copyFileToOutputStream(sourceFile, output);
            } finally {
                try { output.close(); } catch (IOException ignored) {}
            }

            ContentValues completeValues = new ContentValues();
            completeValues.put(MediaStore.MediaColumns.IS_PENDING, 0);
            resolver.update(uri, completeValues, null, null);
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "Public export MediaStore failed: " + publicRootDirectory, t);

            if (uri != null) {
                try { resolver.delete(uri, null, null); } catch (Throwable ignored) {}
            }

            return false;
        }
    }

    private static Uri findMediaStoreFile(ContentResolver resolver, Uri collection, String relativePath, String displayName) {
        Cursor cursor = null;

        try {
            String[] projection = { MediaStore.MediaColumns._ID };
            String selection = MediaStore.MediaColumns.RELATIVE_PATH + "=? AND " +
                    MediaStore.MediaColumns.DISPLAY_NAME + "=?";
            String[] selectionArgs = { relativePath, displayName };
            cursor = resolver.query(collection, projection, selection, selectionArgs, null);

            if (cursor != null && cursor.moveToFirst()) {
                long id = cursor.getLong(0);
                return ContentUris.withAppendedId(collection, id);
            }
        } catch (Throwable t) {
            Log.e(TAG, "Public export existing item query failed.", t);
        } finally {
            if (cursor != null) cursor.close();
        }

        return null;
    }

    private static boolean exportFileToLegacyPublicDirectory(File sourceFile, String sessionId, String publicRootDirectory) {
        try {
            File documents = Environment.getExternalStoragePublicDirectory(publicRootDirectory);
            File targetDir = new File(new File(new File(documents, "PartTime"), "STT"), sessionId);

            if (!targetDir.exists() && !targetDir.mkdirs()) {
                Log.e(TAG, "Public export legacy mkdir failed: " + targetDir);
                return false;
            }

            File targetFile = new File(targetDir, sourceFile.getName());
            OutputStream output = new FileOutputStream(targetFile, false);

            try {
                copyFileToOutputStream(sourceFile, output);
            } finally {
                try { output.close(); } catch (IOException ignored) {}
            }

            return true;
        } catch (Throwable t) {
            Log.e(TAG, "Public export legacy failed.", t);
            return false;
        }
    }

    private static void copyFileToOutputStream(File sourceFile, OutputStream output) throws IOException {
        InputStream input = new BufferedInputStream(new FileInputStream(sourceFile));

        try {
            byte[] buffer = new byte[8192];
            int read;

            while ((read = input.read(buffer)) > 0) {
                output.write(buffer, 0, read);
            }

            output.flush();
        } finally {
            try { input.close(); } catch (IOException ignored) {}
        }
    }

    public static void transcribe(
            final String provider,
            final String wavPath,
            final String language,
            final String unityGameObject,
            final String unityCallbackMethod) {

        WHISPER_EXECUTOR.execute(() -> {
            long started = System.currentTimeMillis();
            String text = "";
            String error = "";

            try {
                if (PROVIDER_WHISPER.equals(provider)) {
                    text = transcribeWhisper(wavPath, normalizeLanguage(language));
                } else {
                    throw new IllegalArgumentException("Bilinmeyen provider: " + provider);
                }
            } catch (Throwable t) {
                Log.e(TAG, "STT failed", t);
                error = t.getMessage() != null ? t.getMessage() : t.toString();
            }

            long durationMs = System.currentTimeMillis() - started;
            Log.i(TAG, String.format(Locale.US, "[WhisperTiming] durationMs=%d", durationMs));
            Log.i(TAG, String.format(
                    Locale.US,
                    "[WhisperResult] rawTranscript=\"%s\" error=\"%s\"",
                    forLog(text),
                    forLog(error)
            ));
            String json = buildResultJson(provider, normalizeLanguage(language), text, durationMs, wavPath, error);
            sendUnityMessage(unityGameObject, unityCallbackMethod, json);
        });
    }

    private static String transcribeWhisper(String wavPath, String language) throws Exception {
        synchronized (WHISPER_STATE_LOCK) {
            long initStarted = System.currentTimeMillis();
            ensureWhisperNativeLoaded();

            Context context = getUnityAppContextOrThrow();
            Log.i(TAG, "[WhisperModel] expected=" + WHISPER_EXPECTED_ASSET_PATH);
            File modelFile = resolveWhisperModel(context);
            String modelPath = modelFile.getAbsolutePath();
            String normalizedLanguage = normalizeLanguage(language);
            boolean modelAlreadyLoaded = whisperNativeHandle != 0L && modelPath.equals(whisperLoadedModelPath);

            if (!modelAlreadyLoaded) {
                if (whisperNativeHandle != 0L) {
                    whisperFreeNative(whisperNativeHandle);
                    whisperNativeHandle = 0L;
                    whisperLoadedModelPath = "";
                }

                whisperNativeHandle = whisperInitNative(modelPath, WHISPER_THREAD_COUNT);
                if (whisperNativeHandle == 0L) {
                    throw new IllegalStateException("whisper.cpp model context init sıfır handle döndürdü: " + modelPath);
                }
                whisperLoadedModelPath = modelPath;
            }

            long initDurationMs = modelAlreadyLoaded ? 0L : System.currentTimeMillis() - initStarted;
            long inferenceStarted = System.currentTimeMillis();

            String nativeJson = whisperTranscribeNative(
                    whisperNativeHandle,
                    wavPath,
                    normalizedLanguage
            );
            WhisperNativeResult nativeResult = WhisperNativeResult.parse(nativeJson);
            long inferenceDurationMs = System.currentTimeMillis() - inferenceStarted;

            if (!nativeResult.ok) {
                throw new IllegalStateException(nativeResult.error.isEmpty()
                        ? "whisper.cpp native transcription failed."
                        : nativeResult.error);
            }
            double audioDurationMs = nativeResult.audioDurationSec * 1000.0;
            double realtimeFactor = audioDurationMs > 0.0 ? inferenceDurationMs / audioDurationMs : 0.0;
            Log.i(TAG, String.format(
                    Locale.US,
                    "[InferenceTiming] whisper_context_reused=%s whisper_init_duration_ms=%d whisper_inference_duration_ms=%d audio_duration_ms=%.3f realtime_factor=%.4f native_whisper_full_ms=%.3f",
                    modelAlreadyLoaded, initDurationMs, inferenceDurationMs, audioDurationMs, realtimeFactor, nativeResult.whisperFullMs));
            return nativeResult.text;
        }
    }

    /** Initializes and caches the Whisper model/context without dummy inference. */
    public static String warmUpWhisper() {
        long started = System.currentTimeMillis();
        synchronized (WHISPER_STATE_LOCK) {
            try {
                ensureWhisperNativeLoaded();
                Context context = getUnityAppContextOrThrow();
                File modelFile = resolveWhisperModel(context);
                String modelPath = modelFile.getAbsolutePath();
                boolean reused = whisperNativeHandle != 0L && modelPath.equals(whisperLoadedModelPath);
                if (!reused) {
                    if (whisperNativeHandle != 0L) {
                        whisperFreeNative(whisperNativeHandle);
                        whisperNativeHandle = 0L;
                        whisperLoadedModelPath = "";
                    }
                    whisperNativeHandle = whisperInitNative(modelPath, WHISPER_THREAD_COUNT);
                    if (whisperNativeHandle == 0L) {
                        throw new IllegalStateException("whisper.cpp warm-up returned handle 0: " + modelPath);
                    }
                    whisperLoadedModelPath = modelPath;
                }
                JSONObject payload = new JSONObject();
                payload.put("ok", true);
                payload.put("stage", "whisper_warmed");
                payload.put("initialized", true);
                payload.put("context_reused", reused);
                payload.put("durationMs", System.currentTimeMillis() - started);
                return payload.toString();
            } catch (Throwable t) {
                Log.e(TAG, "Whisper warm-up failed.", t);
                try {
                    JSONObject payload = new JSONObject();
                    payload.put("ok", false);
                    payload.put("stage", "whisper_warm_up_failed");
                    payload.put("initialized", false);
                    payload.put("context_reused", false);
                    payload.put("durationMs", System.currentTimeMillis() - started);
                    payload.put("error", t.getMessage() != null ? t.getMessage() : t.toString());
                    return payload.toString();
                } catch (Throwable ignored) {
                    return "{\"ok\":false,\"stage\":\"whisper_warm_up_failed\"}";
                }
            }
        }
    }

    public static void releaseWhisper() {
        synchronized (WHISPER_STATE_LOCK) {
            if (whisperNativeHandle == 0L) return;

            long handle = whisperNativeHandle;
            whisperNativeHandle = 0L;
            whisperLoadedModelPath = "";
            try {
                whisperFreeNative(handle);
            } catch (Throwable t) {
                Log.e(TAG, "Whisper native context release failed.", t);
            }
        }
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

    private static native long whisperInitNative(String modelPath, int threadCount);
    private static native String whisperTranscribeNative(long contextHandle, String wavPath, String language);
    private static native void whisperFreeNative(long contextHandle);

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

    private static File resolveWhisperModel(Context context) throws Exception {
        AssetManager assets = context.getAssets();
        File outputDir = new File(context.getFilesDir(), WHISPER_MODEL_OUTPUT_DIR);

        if (!outputDir.exists() && !outputDir.mkdirs()) {
            throw new IOException("Klasör oluşturulamadı: " + outputDir);
        }

        if (!assetExists(assets, WHISPER_MODEL_ASSET)) {
            throw new IOException(
                    "Whisper base model not found. Expected " + WHISPER_EXPECTED_ASSET_PATH);
        }

        String expectedHash = readOptionalModelHash(assets, WHISPER_MODEL_ASSET);

        File outputFile = new File(outputDir, new File(WHISPER_MODEL_ASSET).getName());
        File markerFile = new File(outputDir, outputFile.getName() + ".sha256");
        boolean outputExists = outputFile.isFile() && outputFile.length() > 0L;
        boolean hashAvailable = !expectedHash.isEmpty();
        boolean validExisting = outputExists;
        if (outputExists && hashAvailable) {
            validExisting = expectedHash.equals(readSmallTextFile(markerFile));
            if (!validExisting && expectedHash.equals(sha256File(outputFile))) {
                writeSmallTextFile(markerFile, expectedHash);
                validExisting = true;
            }
        }
        if (!validExisting) {
            copyModelAsset(assets, WHISPER_MODEL_ASSET, expectedHash, outputFile, markerFile);
        }

        return outputFile;
    }

    private static boolean assetExists(AssetManager assets, String assetPath) {
        try (InputStream ignored = assets.open(assetPath)) {
            return true;
        } catch (IOException ignored) {
            return false;
        }
    }

    private static String readOptionalModelHash(AssetManager assets, String modelAsset) throws IOException {
        String hashAsset = modelAsset + ".sha256";
        if (!assetExists(assets, hashAsset)) return "";

        String expectedHash;
        try (InputStream hashInput = assets.open(hashAsset)) {
            expectedHash = readUtf8(hashInput).trim().toLowerCase(Locale.ROOT);
        }
        if (!expectedHash.matches("[0-9a-f]{64}")) {
            throw new IOException("Whisper model SHA-256 geçersiz: " + hashAsset);
        }
        return expectedHash;
    }

    private static void copyModelAsset(
            AssetManager assets,
            String assetPath,
            String expectedHash,
            File outputFile,
            File markerFile) throws Exception {

        File tempFile = new File(outputFile.getParentFile(), outputFile.getName() + ".tmp");
        if (tempFile.exists() && !tempFile.delete()) {
            throw new IOException("Eski Whisper temp model silinemedi: " + tempFile);
        }

        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (InputStream input = new BufferedInputStream(assets.open(assetPath));
             FileOutputStream output = new FileOutputStream(tempFile, false)) {
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.read(buffer)) > 0) {
                output.write(buffer, 0, read);
                digest.update(buffer, 0, read);
            }
            output.flush();
            output.getFD().sync();
        } catch (IOException t) {
            if (tempFile.exists()) tempFile.delete();
            throw t;
        }

        String actualHash = toHex(digest.digest());
        if (!expectedHash.isEmpty() && !expectedHash.equals(actualHash)) {
            tempFile.delete();
            throw new IOException("Whisper model SHA-256 uyuşmuyor. expected=" + expectedHash + " actual=" + actualHash);
        }
        if (tempFile.length() == 0L) {
            tempFile.delete();
            throw new IOException("Whisper model asset boş kopyalandı: " + assetPath);
        }
        if (outputFile.exists() && !outputFile.delete()) {
            tempFile.delete();
            throw new IOException("Eski Whisper model dosyası değiştirilemedi: " + outputFile);
        }
        if (!tempFile.renameTo(outputFile)) {
            tempFile.delete();
            throw new IOException("Whisper model temp dosyası etkinleştirilemedi: " + outputFile);
        }

        if (!expectedHash.isEmpty()) {
            writeSmallTextFile(markerFile, expectedHash);
        }
    }

    private static String readUtf8(InputStream input) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = input.read(buffer)) > 0) {
            output.write(buffer, 0, read);
        }
        return output.toString(StandardCharsets.UTF_8.name());
    }

    private static String readSmallTextFile(File file) {
        if (file == null || !file.isFile() || file.length() <= 0L || file.length() > 1024L) return "";
        try (InputStream input = new FileInputStream(file)) {
            return readUtf8(input).trim().toLowerCase(Locale.ROOT);
        } catch (IOException ignored) {
            return "";
        }
    }

    private static void writeSmallTextFile(File file, String value) throws IOException {
        File temp = new File(file.getParentFile(), file.getName() + ".tmp");
        try (FileOutputStream output = new FileOutputStream(temp, false)) {
            output.write(value.getBytes(StandardCharsets.UTF_8));
            output.flush();
            output.getFD().sync();
        }
        if (file.exists() && !file.delete()) {
            temp.delete();
            throw new IOException("Whisper hash marker değiştirilemedi: " + file);
        }
        if (!temp.renameTo(file)) {
            temp.delete();
            throw new IOException("Whisper hash marker etkinleştirilemedi: " + file);
        }
    }

    private static String toHex(byte[] bytes) {
        StringBuilder builder = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) {
            builder.append(String.format(Locale.ROOT, "%02x", value & 0xff));
        }
        return builder.toString();
    }

    private static String sha256File(File file) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (InputStream input = new BufferedInputStream(new FileInputStream(file))) {
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.read(buffer)) > 0) {
                digest.update(buffer, 0, read);
            }
        }
        return toHex(digest.digest());
    }

    private static final class WhisperNativeResult {
        final boolean ok;
        final String text;
        final String error;
        final double audioPrepareMs;
        final double whisperFullMs;
        final double resultReadMs;
        final double jniTotalMs;
        final double audioDurationSec;

        WhisperNativeResult(JSONObject object) {
            ok = object.optBoolean("ok", false);
            text = object.optString("text", "");
            error = object.optString("error", "");
            audioPrepareMs = object.optDouble("audioPrepareMs", 0.0);
            whisperFullMs = object.optDouble("whisperFullMs", 0.0);
            resultReadMs = object.optDouble("resultReadMs", 0.0);
            jniTotalMs = object.optDouble("jniTotalMs", 0.0);
            audioDurationSec = object.optDouble("audioDurationSec", 0.0);
        }

        static WhisperNativeResult parse(String json) throws Exception {
            if (json == null || json.trim().isEmpty()) {
                throw new IllegalStateException("Whisper native result boş döndü.");
            }
            return new WhisperNativeResult(new JSONObject(json));
        }
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

    private static String forLog(String value) {
        return safe(value).replace("\r", "\\r").replace("\n", "\\n");
    }

}
