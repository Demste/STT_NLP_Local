package com.mehmet.sttbridge.llm;

import android.content.Context;
import android.content.res.AssetFileDescriptor;
import android.content.res.AssetManager;

import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

public final class LlmModelResolver {
    private static final String MODEL_DIR = "llm";
    private static final String[] DEFAULT_MODEL_ASSETS = {
            "llm/command-model.gguf",
            "llm/model.gguf",
            "llm/turkish-command.gguf"
    };

    private LlmModelResolver() {}

    public static Result resolve(Context context, JSONObject config) {
        if (context == null) {
            return Result.failed("android_context_null", null, null, "Android Context is null.");
        }

        JSONObject llm = config != null ? config.optJSONObject("llm") : null;

        String explicitPath = llm != null ? llm.optString("model_path", "") : "";
        if (!explicitPath.trim().isEmpty()) {
            File explicitFile = new File(explicitPath.trim());
            if (explicitFile.exists() && explicitFile.isFile() && explicitFile.length() > 0) {
                return Result.ok("model_path", null, explicitFile.getAbsolutePath(), "");
            }
            return Result.failed(
                    "model_asset_not_found",
                    null,
                    explicitFile.getAbsolutePath(),
                    "Configured llm.model_path does not exist or is empty: " + explicitFile.getAbsolutePath());
        }

        String configuredAsset = llm != null ? llm.optString("model_asset", "") : "";
        if (!configuredAsset.trim().isEmpty()) {
            String assetPath = normalizeAssetPath(configuredAsset.trim());
            return copyAssetToPrivateStorage(context, assetPath);
        }

        Result lastMissing = null;
        for (String assetPath : DEFAULT_MODEL_ASSETS) {
            Result result = copyAssetToPrivateStorage(context, assetPath);
            if (result.ok) {
                return result;
            }
            if ("model_extract_failed".equals(result.stage)) {
                return result;
            }
            lastMissing = result;
        }

        return Result.failed(
                "model_asset_not_found",
                null,
                null,
                lastMissing != null && lastMissing.error != null && !lastMissing.error.isEmpty()
                        ? "No bundled GGUF model found. Last checked: " + lastMissing.assetPath
                        : "No bundled GGUF model found under assets/llm/.");
    }

    private static String normalizeAssetPath(String value) {
        return value.startsWith(MODEL_DIR + "/") ? value : MODEL_DIR + "/" + value;
    }

    private static Result copyAssetToPrivateStorage(Context context, String assetPath) {
        AssetManager assets = context.getAssets();
        long assetLength;
        try {
            assetLength = assetLength(assets, assetPath);
        } catch (IOException exc) {
            return Result.failed("model_asset_not_found", assetPath, null, "Model asset not found: " + assetPath);
        }

        File outputDir = new File(context.getFilesDir(), MODEL_DIR);
        if (!outputDir.exists() && !outputDir.mkdirs()) {
            return Result.failed(
                    "model_extract_failed",
                    assetPath,
                    null,
                    "Could not create LLM model directory: " + outputDir.getAbsolutePath());
        }

        File outputFile = new File(outputDir, new File(assetPath).getName());
        if (outputFile.exists() && outputFile.length() == assetLength && assetLength > 0) {
            return Result.ok("model_extracted", assetPath, outputFile.getAbsolutePath(), "");
        }

        File tempFile = new File(outputFile.getAbsolutePath() + ".tmp");
        if (tempFile.exists() && !tempFile.delete()) {
            return Result.failed(
                    "model_extract_failed",
                    assetPath,
                    outputFile.getAbsolutePath(),
                    "Could not remove stale temp model file: " + tempFile.getAbsolutePath());
        }

        try {
            copyAsset(assets, assetPath, tempFile);

            if (tempFile.length() != assetLength) {
                safeDelete(tempFile);
                return Result.failed(
                        "model_extract_failed",
                        assetPath,
                        outputFile.getAbsolutePath(),
                        "Extracted GGUF byte size mismatch. asset=" + assetLength + " extracted=" + tempFile.length());
            }

            if (outputFile.exists() && !outputFile.delete()) {
                safeDelete(tempFile);
                return Result.failed(
                        "model_extract_failed",
                        assetPath,
                        outputFile.getAbsolutePath(),
                        "Could not replace existing extracted model: " + outputFile.getAbsolutePath());
            }

            if (!tempFile.renameTo(outputFile)) {
                safeDelete(tempFile);
                return Result.failed(
                        "model_extract_failed",
                        assetPath,
                        outputFile.getAbsolutePath(),
                        "Could not atomically rename extracted model into place: " + outputFile.getAbsolutePath());
            }

            return Result.ok("model_extracted", assetPath, outputFile.getAbsolutePath(), "");
        } catch (IOException exc) {
            safeDelete(tempFile);
            return Result.failed(
                    "model_extract_failed",
                    assetPath,
                    outputFile.getAbsolutePath(),
                    exc.getMessage() != null ? exc.getMessage() : exc.toString());
        }
    }

    private static long assetLength(AssetManager assets, String assetPath) throws IOException {
        AssetFileDescriptor descriptor = null;
        try {
            descriptor = assets.openFd(assetPath);
            long length = descriptor.getLength();
            if (length >= 0) {
                return length;
            }
        } catch (IOException ignored) {
            // Compressed APK assets cannot be opened with openFd. Fall back to
            // streaming so the resolver can still report extraction failures
            // accurately if Gradle noCompress is misconfigured.
        } finally {
            if (descriptor != null) {
                descriptor.close();
            }
        }

        InputStream input = assets.open(assetPath);
        try {
            long total = 0L;
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.read(buffer)) > 0) {
                total += read;
            }
            return total;
        } finally {
            input.close();
        }
    }

    private static void copyAsset(AssetManager assets, String assetPath, File outputFile) throws IOException {
        InputStream input = null;
        OutputStream output = null;
        try {
            input = assets.open(assetPath);
            output = new FileOutputStream(outputFile);
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.read(buffer)) > 0) {
                output.write(buffer, 0, read);
            }
            output.flush();
        } finally {
            if (output != null) {
                try {
                    output.close();
                } catch (IOException ignored) {
                }
            }
            if (input != null) {
                input.close();
            }
        }
    }

    private static void safeDelete(File file) {
        if (file != null && file.exists()) {
            file.delete();
        }
    }

    public static final class Result {
        public final boolean ok;
        public final String stage;
        public final String assetPath;
        public final String extractedPath;
        public final String error;

        private Result(boolean ok, String stage, String assetPath, String extractedPath, String error) {
            this.ok = ok;
            this.stage = stage;
            this.assetPath = assetPath;
            this.extractedPath = extractedPath;
            this.error = error;
        }

        public static Result ok(String stage, String assetPath, String extractedPath, String error) {
            return new Result(true, stage, assetPath, extractedPath, error);
        }

        public static Result failed(String stage, String assetPath, String extractedPath, String error) {
            return new Result(false, stage, assetPath, extractedPath, error);
        }
    }
}
