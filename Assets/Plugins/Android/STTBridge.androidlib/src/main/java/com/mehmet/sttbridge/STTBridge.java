package com.mehmet.sttbridge;

import android.app.Activity;
import android.content.ClipData;
import android.content.ContentResolver;
import android.content.ContentUris;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import android.util.Log;

import androidx.core.content.FileProvider;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;

public final class STTBridge {
    private static final String TAG = "STTBridge";


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

    public static void forceRequestTurkishOnDeviceModel() {
        AndroidSpeechRecognizerBridge.forceRequestTurkishOnDeviceModel();
    }

    public static String requestTurkishOnDeviceModel(String unityGameObject, String unityCallbackMethod) {
        return AndroidSpeechRecognizerBridge.requestTurkishOnDeviceModel(unityGameObject, unityCallbackMethod);
    }

    public static String observeTurkishOnDeviceModel(String unityGameObject, String unityCallbackMethod) {
        return AndroidSpeechRecognizerBridge.observeTurkishOnDeviceModel(unityGameObject, unityCallbackMethod);
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

}
