package com.mehmet.sttbridge;

import android.Manifest;
import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.speech.RecognitionListener;
import android.speech.ModelDownloadListener;
import android.speech.RecognitionSupport;
import android.speech.RecognitionSupportCallback;
import android.speech.RecognizerIntent;
import android.speech.SpeechRecognizer;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

/** Android system SpeechRecognizer lifecycle. All recognizer calls run on Unity's Activity UI thread. */
public final class AndroidSpeechRecognizerBridge {
    private static final String TAG = "AndroidSTT";
    private static final String LANGUAGE = "tr-TR";
    private static final int MAX_RESULTS = 5;
    private static final long SUPPORT_TIMEOUT_MS = 7000L;
    private static final long MODEL_RECHECK_DELAY_MS = 15000L;
    private static final long MODEL_REQUEST_RETRY_MS = 24L * 60L * 60L * 1000L;
    private static final long MODEL_FAILURE_RETRY_MS = 6L * 60L * 60L * 1000L;
    private static final String MODEL_PREFS = "android_stt_model_download";
    private static final String PREF_REQUESTED_AT = "requested_at";
    private static final String PREF_LAST_RESULT = "last_result";
    private static final String PREF_LAST_ERROR = "last_error";
    private static final String PREF_LANGUAGE_TAG = "language_tag";
    private static final Object LOCK = new Object();

    private enum State { IDLE, STARTING, LISTENING, STOPPING, WAITING_FINAL_RESULT, COMPLETED, CANCELLED, ERROR, DESTROYED }
    private enum Mode { AUTO, ONLINE, OFFLINE }
    private enum SupportStatus { UNKNOWN, SUPPORTED, UNSUPPORTED, UNAVAILABLE, CHECK_UNAVAILABLE }

    private static Session activeSession;
    private static State state = State.IDLE;
    private static boolean standardServiceAvailable;
    private static boolean onlineUsable;
    private static boolean offlineUsable;
    private static boolean onDeviceServiceAvailable;
    private static boolean trTurkishOnDeviceReady;
    private static SupportStatus onlineSupportStatus = SupportStatus.UNKNOWN;
    private static SupportStatus onDeviceSupportStatus = SupportStatus.UNKNOWN;
    private static boolean supportChecked;
    private static boolean modelDownloadRequested;
    private static boolean forcedModelDownloadRetryUsed;
    private static boolean onlineBiasingSupported;
    private static boolean offlineBiasingSupported;
    private static boolean onlineFormattingSupported;
    private static boolean offlineFormattingSupported;
    private static String selectedWarmupMode = "";
    private static String requestedWarmupMode = "AUTO";
    private static String selectionReason = "";
    private static String networkState = "UNKNOWN";
    private static String networkValidated = "unknown";
    private static String offlineModelStatus = "UNKNOWN";
    private static String supportMessage = "";

    private AndroidSpeechRecognizerBridge() {}

    /**
     * Performs lightweight service/model readiness checks. Online recognition may send audio to
     * the device's configured recognition provider; production UI must disclose that possibility.
     */
    public static String prepareSpeechRecognizer(final String requestedModeValue) {
        final long started = System.currentTimeMillis();
        final Mode requested = parseMode(requestedModeValue);
        requestedWarmupMode = requested.name();
        final Activity activity = unityActivity();
        if (activity == null) {
            selectedWarmupMode = "";
            return supportJson(false, "Unity activity hazır değil", started);
        }

        standardServiceAvailable = SpeechRecognizer.isRecognitionAvailable(activity);
        onlineUsable = standardServiceAvailable;
        onlineSupportStatus = SupportStatus.UNKNOWN;
        onDeviceServiceAvailable = Build.VERSION.SDK_INT >= 31
                && SpeechRecognizer.isOnDeviceRecognitionAvailable(activity);
        trTurkishOnDeviceReady = false;
        onDeviceSupportStatus = SupportStatus.UNKNOWN;
        offlineUsable = Build.VERSION.SDK_INT >= 31 && onDeviceServiceAvailable;
        onlineBiasingSupported = false;
        offlineBiasingSupported = false;
        onlineFormattingSupported = false;
        offlineFormattingSupported = false;
        supportChecked = false;
        supportMessage = "";
        NetworkDiagnostics network = networkDiagnostics(activity);
        networkState = network.state;
        networkValidated = network.validated;
        offlineModelStatus = readModelResult(activity);

        if (Build.VERSION.SDK_INT >= 33) {
            if (standardServiceAvailable) checkApi33OnlineSupport(activity);
            if (onDeviceServiceAvailable) checkApi33OnDeviceSupport(activity, requested);
            else supportChecked = true;
        } else {
            supportChecked = true;
        }

        if (trTurkishOnDeviceReady) {
            offlineModelStatus = "INSTALLED";
            persistModelResult(activity, "INSTALLED", 0, false);
        } else if (onDeviceServiceAvailable) {
            requestOfflineModelDownload(activity, false, "STARTUP_SUPPORT_CHECK");
        } else if (Build.VERSION.SDK_INT < 33) {
            offlineModelStatus = "UNSUPPORTED_API";
            Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                    + " api_level=" + Build.VERSION.SDK_INT
                    + " request_started=false request_method=UNSUPPORTED blocking=false");
        }

        String selected = selectModeNameForTesting(
                requested.name(), network.validatedInternet, onlineUsable,
                Build.VERSION.SDK_INT, onDeviceServiceAvailable, offlineUsable);
        selectedWarmupMode = selected;
        selectionReason = selectionReasonForTesting(requested.name(), onlineUsable,
                Build.VERSION.SDK_INT, onDeviceServiceAvailable, offlineUsable);
        boolean requestedReady = !selected.isEmpty();
        if (requested == Mode.OFFLINE && Build.VERSION.SDK_INT < 31) {
            supportMessage = "Android 12 (API 31) öncesinde çevrimdışı tanıma desteklenmiyor.";
        } else if (requested == Mode.OFFLINE && !offlineUsable) {
            supportMessage = "Türkçe çevrimdışı konuşma modeli hazır değil. Sistem dil paketini indirin.";
        } else if ((requested == Mode.AUTO || requested == Mode.ONLINE) && onlineUsable) {
            // Offline model state must never block an otherwise usable standard recognizer.
            supportMessage = "";
        } else if (!requestedReady && supportMessage.isEmpty()) {
            supportMessage = "Konuşma tanıma servisi cihazda bulunamadı.";
        }
        Log.i(TAG, String.format(Locale.US,
                "[AndroidSTTSupport] requested_mode=%s standard_service_available=%s network_state=%s network_validated=%s "
                        + "online_support_status=%s online_usable=%s "
                        + "on_device_service_available=%s on_device_support_status=%s tr_tr_on_device_ready=%s "
                        + "offline_usable=%s offline_model_status=%s model_download_requested=%s selected_mode=%s "
                        + "selection_reason=%s warmup_success=%s duration_ms=%d",
                requested, standardServiceAvailable, networkState, networkValidated,
                onlineSupportStatus, onlineUsable,
                onDeviceServiceAvailable, onDeviceSupportStatus, trTurkishOnDeviceReady,
                offlineUsable, offlineModelStatus, modelDownloadRequested, selectedWarmupMode,
                selectionReason, requestedReady,
                System.currentTimeMillis() - started));
        return supportJson(requestedReady, supportMessage, started);
    }

    private static void checkApi33OnlineSupport(final Activity activity) {
        final CountDownLatch latch = new CountDownLatch(1);
        final AtomicReference<SpeechRecognizer> temporary = new AtomicReference<>();
        final AtomicBoolean resolved = new AtomicBoolean(false);
        final boolean basicAvailability = standardServiceAvailable;
        activity.runOnUiThread(() -> {
            try {
                SpeechRecognizer recognizer = SpeechRecognizer.createSpeechRecognizer(activity);
                temporary.set(recognizer);
                recognizer.checkRecognitionSupport(recognitionIntent(Mode.ONLINE, false, false),
                        activity.getMainExecutor(), new RecognitionSupportCallback() {
                    @Override public void onSupportResult(RecognitionSupport support) {
                        if (!resolved.compareAndSet(false, true)) return;
                        supportChecked = true;
                        boolean supported = containsLanguage(support.getOnlineLanguages(), LANGUAGE)
                                || containsLanguage(support.getInstalledOnDeviceLanguages(), LANGUAGE);
                        boolean hasAnyLanguageDetail = hasAnyLanguage(support.getOnlineLanguages())
                                || hasAnyLanguage(support.getInstalledOnDeviceLanguages())
                                || hasAnyLanguage(support.getSupportedOnDeviceLanguages())
                                || hasAnyLanguage(support.getPendingOnDeviceLanguages());
                        if (supported) {
                            onlineSupportStatus = SupportStatus.SUPPORTED;
                            onlineUsable = true;
                            checkOptionalExtrasSupport(recognizer, activity, Mode.ONLINE, latch);
                        } else if (hasAnyLanguageDetail) {
                            onlineSupportStatus = SupportStatus.UNSUPPORTED;
                            // A support-query result is advisory for the standard recognizer.
                            // The actual startListening callback remains authoritative.
                            onlineUsable = basicAvailability;
                            latch.countDown();
                        } else {
                            onlineSupportStatus = SupportStatus.UNKNOWN;
                            onlineUsable = basicAvailability;
                            latch.countDown();
                        }
                    }

                    @Override public void onError(int error) {
                        if (!resolved.compareAndSet(false, true)) return;
                        supportChecked = true;
                        onlineSupportStatus = supportStatusForError(error);
                        onlineUsable = basicAvailability;
                        Log.w(TAG, "tr-TR online support status=" + onlineSupportStatus
                                + " error=" + errorName(error) + " online_usable=" + onlineUsable);
                        latch.countDown();
                    }
                });
            } catch (Throwable t) {
                onlineSupportStatus = SupportStatus.UNKNOWN;
                onlineUsable = basicAvailability;
                Log.w(TAG, "tr-TR online support check failed", t);
                latch.countDown();
            }
        });
        try {
            if (!latch.await(SUPPORT_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                resolved.set(true);
                onlineSupportStatus = SupportStatus.UNKNOWN;
                onlineUsable = basicAvailability;
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            resolved.set(true);
            onlineSupportStatus = SupportStatus.UNKNOWN;
            onlineUsable = basicAvailability;
        } finally {
            SpeechRecognizer recognizer = temporary.get();
            if (recognizer != null) activity.runOnUiThread(recognizer::destroy);
        }
    }

    private static void checkApi33OnDeviceSupport(final Activity activity, final Mode requested) {
        final CountDownLatch latch = new CountDownLatch(1);
        final AtomicReference<SpeechRecognizer> temporary = new AtomicReference<>();
        final AtomicBoolean resolved = new AtomicBoolean(false);
        activity.runOnUiThread(() -> {
            try {
                SpeechRecognizer recognizer = SpeechRecognizer.createOnDeviceSpeechRecognizer(activity);
                temporary.set(recognizer);
                Intent intent = recognitionIntent(Mode.OFFLINE, false, false);
                recognizer.checkRecognitionSupport(intent, activity.getMainExecutor(), new RecognitionSupportCallback() {
                    @Override public void onSupportResult(RecognitionSupport support) {
                        if (!resolved.compareAndSet(false, true)) return;
                        trTurkishOnDeviceReady = containsLanguage(support.getInstalledOnDeviceLanguages(), LANGUAGE);
                        boolean downloadable = containsLanguage(support.getSupportedOnDeviceLanguages(), LANGUAGE);
                        boolean pending = containsLanguage(support.getPendingOnDeviceLanguages(), LANGUAGE);
                        boolean hasAnyLanguageDetail = hasAnyLanguage(support.getInstalledOnDeviceLanguages())
                                || hasAnyLanguage(support.getSupportedOnDeviceLanguages())
                                || hasAnyLanguage(support.getPendingOnDeviceLanguages());
                        supportChecked = true;
                        if (trTurkishOnDeviceReady) {
                            offlineUsable = true;
                            onDeviceSupportStatus = SupportStatus.SUPPORTED;
                        } else if (downloadable || pending) {
                            offlineUsable = false;
                            onDeviceSupportStatus = SupportStatus.UNAVAILABLE;
                        } else if (hasAnyLanguageDetail) {
                            offlineUsable = false;
                            onDeviceSupportStatus = SupportStatus.UNSUPPORTED;
                        } else {
                            offlineUsable = onDeviceServiceAvailable;
                            onDeviceSupportStatus = SupportStatus.UNKNOWN;
                        }
                        if (requested == Mode.OFFLINE && !offlineUsable) {
                            supportMessage = pending
                                    ? "Türkçe çevrimdışı model indirmesi sistem tarafından planlandı."
                                    : "Türkçe çevrimdışı konuşma modeli hazır değil. Sistem dil paketini indirin.";
                        }
                        if (trTurkishOnDeviceReady) {
                            checkOptionalExtrasSupport(recognizer, activity, Mode.OFFLINE, latch);
                        } else {
                            latch.countDown();
                        }
                    }

                    @Override public void onError(int error) {
                        if (!resolved.compareAndSet(false, true)) return;
                        supportChecked = true;
                        onDeviceSupportStatus = supportStatusForError(error);
                        if (isCannotCheckSupport(error)) {
                            // Availability + actual startListening is authoritative when the OEM
                            // provider does not implement capability introspection.
                            offlineUsable = onDeviceServiceAvailable;
                            supportMessage = "";
                        } else if (isLanguageError(error)) {
                            offlineUsable = false;
                            if (requested == Mode.OFFLINE) {
                                supportMessage = error == SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED
                                        ? "Türkçe çevrimdışı konuşma bu serviste desteklenmiyor."
                                        : "Türkçe çevrimdışı konuşma modeli hazır değil. Sistem dil paketini indirin.";
                            }
                        } else {
                            offlineUsable = onDeviceServiceAvailable;
                            supportMessage = "";
                        }
                        latch.countDown();
                    }
                });
            } catch (Throwable t) {
                supportChecked = true;
                onDeviceSupportStatus = SupportStatus.UNKNOWN;
                offlineUsable = onDeviceServiceAvailable;
                supportMessage = requested == Mode.OFFLINE
                        ? "Çevrimdışı tanıma desteği kontrol edilemedi: " + message(t) : "";
                latch.countDown();
            }
        });

        try {
            if (!latch.await(SUPPORT_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                resolved.set(true);
                onDeviceSupportStatus = SupportStatus.UNKNOWN;
                offlineUsable = onDeviceServiceAvailable;
                supportMessage = requested == Mode.OFFLINE ? "Çevrimdışı model kontrolü zaman aşımına uğradı." : "";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            resolved.set(true);
            onDeviceSupportStatus = SupportStatus.UNKNOWN;
            offlineUsable = onDeviceServiceAvailable;
            supportMessage = requested == Mode.OFFLINE ? "Çevrimdışı model kontrolü kesildi." : "";
        } finally {
            SpeechRecognizer recognizer = temporary.get();
            if (recognizer != null) activity.runOnUiThread(recognizer::destroy);
        }
    }

    public static void startListening(
            final String sessionId,
            final String requestedModeValue,
            final String unityGameObject,
            final String unityCallbackMethod) {
        final Activity activity = unityActivity();
        if (activity == null) {
            sendImmediateError(sessionId, requestedModeValue, unityGameObject, unityCallbackMethod,
                    SpeechRecognizer.ERROR_CLIENT, "Unity activity hazır değil.");
            return;
        }
        activity.runOnUiThread(() -> startOnMain(activity, sessionId, parseMode(requestedModeValue),
                unityGameObject, unityCallbackMethod));
    }

    private static void startOnMain(Activity activity, String sessionId, Mode requested,
                                    String unityGameObject, String unityCallbackMethod) {
        synchronized (LOCK) {
            if (activeSession != null && !isTerminal(state)) {
                sendImmediateError(sessionId, requested.name(), unityGameObject, unityCallbackMethod,
                        SpeechRecognizer.ERROR_RECOGNIZER_BUSY, "Tanıma servisi meşgul, tekrar deneyin.");
                return;
            }
            if (activity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                sendImmediateError(sessionId, requested.name(), unityGameObject, unityCallbackMethod,
                        SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS, "Mikrofon izni verilmedi.");
                return;
            }
            Mode used = chooseMode(activity, requested);
            if (used == null) {
                String error = requested == Mode.OFFLINE
                        ? (Build.VERSION.SDK_INT < 31
                            ? "Bu cihazda çevrimdışı tanıma desteklenmiyor."
                            : "Türkçe çevrimdışı konuşma modeli hazır değil. Sistem dil paketini indirin.")
                        : "Konuşma tanıma servisi cihazda bulunamadı.";
                sendImmediateError(sessionId, requested.name(), unityGameObject, unityCallbackMethod,
                        SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE, error);
                return;
            }
            activeSession = new Session(sessionId, requested, used, unityGameObject, unityCallbackMethod);
            beginAttempt(activity, activeSession, used);
        }
    }

    private static void beginAttempt(Activity activity, Session session, Mode used) {
        session.used = used;
        session.attempt++;
        session.listeningStartedAt = System.currentTimeMillis();
        session.service = recognizerService(activity, used);
        state = State.STARTING;
        try {
            SpeechRecognizer recognizer = used == Mode.OFFLINE
                    ? SpeechRecognizer.createOnDeviceSpeechRecognizer(activity)
                    : SpeechRecognizer.createSpeechRecognizer(activity);
            session.recognizer = recognizer;
            int attempt = session.attempt;
            recognizer.setRecognitionListener(new Listener(session, attempt));
            boolean biasing = used == Mode.OFFLINE ? offlineBiasingSupported : onlineBiasingSupported;
            boolean formatting = used == Mode.OFFLINE ? offlineFormattingSupported : onlineFormattingSupported;
            Intent intent = recognitionIntent(used, biasing, formatting);
            Log.i(TAG, String.format(Locale.US,
                    "[AndroidSTT] session_id=%s requested_mode=%s used_mode=%s service=%s language=%s partial_results=true biasing_enabled=%s max_results=%d",
                    session.id, session.requested, used, session.service, LANGUAGE, biasing, MAX_RESULTS));
            sendEvent(session, "STATE", null, 0, "", State.STARTING);
            recognizer.startListening(intent);
        } catch (Throwable t) {
            failSession(session, SpeechRecognizer.ERROR_CLIENT, message(t), false);
        }
    }

    public static void stopListening(final String sessionId) {
        Activity activity = unityActivity();
        if (activity == null) return;
        activity.runOnUiThread(() -> {
            synchronized (LOCK) {
                Session session = activeSession;
                if (!matches(session, sessionId) || session.recognizer == null
                        || state == State.STOPPING || state == State.WAITING_FINAL_RESULT || isTerminal(state)) return;
                state = State.STOPPING;
                sendEvent(session, "STATE", null, 0, "", state);
                try {
                    session.recognizer.stopListening();
                    state = State.WAITING_FINAL_RESULT;
                    sendEvent(session, "STATE", null, 0, "", state);
                } catch (Throwable t) {
                    failSession(session, SpeechRecognizer.ERROR_CLIENT, message(t), false);
                }
            }
        });
    }

    public static void cancel(final String sessionId) {
        Activity activity = unityActivity();
        if (activity == null) return;
        activity.runOnUiThread(() -> {
            synchronized (LOCK) {
                Session session = activeSession;
                if (!matches(session, sessionId)) return;
                state = State.CANCELLED;
                try { if (session.recognizer != null) session.recognizer.cancel(); } catch (Throwable ignored) {}
                sendEvent(session, "STATE", null, 0, "", state);
                destroyRecognizer(session);
                activeSession = null;
                state = State.IDLE;
            }
        });
    }

    public static void destroy() {
        Activity activity = unityActivity();
        if (activity == null) return;
        activity.runOnUiThread(() -> {
            synchronized (LOCK) {
                Session session = activeSession;
                if (session != null) {
                    try { if (session.recognizer != null) session.recognizer.cancel(); } catch (Throwable ignored) {}
                    state = State.DESTROYED;
                    sendEvent(session, "STATE", null, 0, "", state);
                    destroyRecognizer(session);
                }
                activeSession = null;
                state = State.DESTROYED;
            }
        });
    }

    private static final class Listener implements RecognitionListener {
        private final Session session;
        private final int attempt;
        Listener(Session session, int attempt) { this.session = session; this.attempt = attempt; }

        private boolean current() {
            synchronized (LOCK) { return activeSession == session && session.attempt == attempt && !isTerminal(state); }
        }
        @Override public void onReadyForSpeech(Bundle params) {
            if (!current()) return;
            markActualRecognitionUsable(session.used);
            state = State.LISTENING;
            sendEvent(session, "STATE", null, 0, "", state);
        }
        @Override public void onBeginningOfSpeech() {
            if (!current()) return;
            session.speechStartedAt = System.currentTimeMillis();
        }
        @Override public void onRmsChanged(float rmsdB) {}
        @Override public void onBufferReceived(byte[] buffer) {}
        @Override public void onEndOfSpeech() {
            if (!current()) return;
            session.speechEndedAt = System.currentTimeMillis();
            state = State.WAITING_FINAL_RESULT;
            sendEvent(session, "STATE", null, 0, "", state);
        }
        @Override public void onError(int error) {
            if (!current()) return;
            updateSupportFromActualError(session.used, error);
            if (session.used == Mode.OFFLINE && error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE) {
                requestOfflineModelDownload(unityActivity(), true, "ACTUAL_ERROR_LANGUAGE_UNAVAILABLE");
            }
            boolean retry = shouldRetryOnDeviceForTesting(session.requested.name(), session.used.name(),
                    session.retriedOnDevice, offlineUsable, error);
            Log.e(TAG, String.format(Locale.US,
                    "[AndroidSTTError] session_id=%s mode=%s error_code=%d error_name=%s android_on_device_retry=%s",
                    session.id, session.used, error, errorName(error), retry));
            if (retry) {
                session.retriedOnDevice = true;
                destroyRecognizer(session);
                beginAttempt(unityActivity(), session, Mode.OFFLINE);
                return;
            }
            failSession(session, error, userError(error), false);
        }
        @Override public void onResults(Bundle results) {
            if (!current() || session.finalSent) return;
            markActualRecognitionUsable(session.used);
            session.finalSent = true;
            session.finalResultAt = System.currentTimeMillis();
            state = State.COMPLETED;
            sendEvent(session, "FINAL", results, 0, "", state);
            destroyRecognizer(session);
            synchronized (LOCK) { if (activeSession == session) activeSession = null; }
        }
        @Override public void onPartialResults(Bundle partialResults) {
            if (!current() || session.finalSent) return;
            markActualRecognitionUsable(session.used);
            if (session.firstPartialAt == 0L) session.firstPartialAt = System.currentTimeMillis();
            sendEvent(session, "PARTIAL", partialResults, 0, "", state);
        }
        @Override public void onEvent(int eventType, Bundle params) {}
    }

    private static Intent recognitionIntent(Mode mode, boolean includeBiasing, boolean includeFormatting) {
        Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
        intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
        intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, LANGUAGE);
        intent.putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true);
        intent.putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, MAX_RESULTS);
        intent.putExtra(RecognizerIntent.EXTRA_PREFER_OFFLINE, mode == Mode.OFFLINE);
        if (Build.VERSION.SDK_INT >= 33 && includeBiasing) {
            intent.putStringArrayListExtra(RecognizerIntent.EXTRA_BIASING_STRINGS, AndroidSpeechBiasCatalog.phrases());
            if (includeFormatting) {
                intent.putExtra(RecognizerIntent.EXTRA_ENABLE_FORMATTING,
                        RecognizerIntent.FORMATTING_OPTIMIZE_LATENCY);
            }
        }
        return intent;
    }

    private static void checkOptionalExtrasSupport(SpeechRecognizer recognizer, Activity activity,
                                                   Mode mode, CountDownLatch latch) {
        if (Build.VERSION.SDK_INT < 33) {
            latch.countDown();
            return;
        }
        try {
            recognizer.checkRecognitionSupport(recognitionIntent(mode, true, false), activity.getMainExecutor(),
                    new RecognitionSupportCallback() {
                @Override public void onSupportResult(RecognitionSupport ignored) {
                    if (mode == Mode.OFFLINE) offlineBiasingSupported = true;
                    else onlineBiasingSupported = true;
                    checkFormattingSupport(recognizer, activity, mode, latch);
                }

                @Override public void onError(int error) {
                    disableOptionalExtras(mode);
                    Log.i(TAG, "Biasing extra status unknown/unsupported for " + mode + ": " + errorName(error));
                    latch.countDown();
                }
            });
        } catch (Throwable t) {
            disableOptionalExtras(mode);
            latch.countDown();
        }
    }

    private static void checkFormattingSupport(SpeechRecognizer recognizer, Activity activity,
                                               Mode mode, CountDownLatch latch) {
        try {
            recognizer.checkRecognitionSupport(recognitionIntent(mode, true, true), activity.getMainExecutor(),
                    new RecognitionSupportCallback() {
                @Override public void onSupportResult(RecognitionSupport ignored) {
                    if (mode == Mode.OFFLINE) offlineFormattingSupported = true;
                    else onlineFormattingSupported = true;
                    latch.countDown();
                }

                @Override public void onError(int error) {
                    if (mode == Mode.OFFLINE) offlineFormattingSupported = false;
                    else onlineFormattingSupported = false;
                    Log.i(TAG, "Formatting extra disabled for " + mode + ": " + errorName(error));
                    latch.countDown();
                }
            });
        } catch (Throwable t) {
            if (mode == Mode.OFFLINE) offlineFormattingSupported = false;
            else onlineFormattingSupported = false;
            latch.countDown();
        }
    }

    private static void disableOptionalExtras(Mode mode) {
        if (mode == Mode.OFFLINE) {
            offlineBiasingSupported = false;
            offlineFormattingSupported = false;
        } else {
            onlineBiasingSupported = false;
            onlineFormattingSupported = false;
        }
    }

    private static Mode chooseMode(Context context, Mode requested) {
        NetworkDiagnostics network = networkDiagnostics(context);
        String selected = selectModeNameForTesting(
                requested.name(), network.validatedInternet, onlineUsable,
                Build.VERSION.SDK_INT, onDeviceServiceAvailable, offlineUsable);
        return selected.isEmpty() ? null : Mode.valueOf(selected);
    }

    static String selectModeNameForTesting(String requestedValue, boolean validatedInternet,
                                           boolean standardAvailable, int apiLevel,
                                           boolean onDeviceAvailable, boolean offlineReadyOrUsable) {
        Mode requested = parseMode(requestedValue);
        if (requested == Mode.ONLINE) return standardAvailable ? Mode.ONLINE.name() : "";
        boolean canUseOffline = apiLevel >= 31 && onDeviceAvailable && offlineReadyOrUsable;
        if (requested == Mode.OFFLINE) return canUseOffline ? Mode.OFFLINE.name() : "";
        // ConnectivityManager is advisory: OEM/network callbacks can transiently report
        // false/unknown even though the configured recognition service is usable.
        if (standardAvailable) return Mode.ONLINE.name();
        return canUseOffline ? Mode.OFFLINE.name() : "";
    }

    static String selectionReasonForTesting(String requestedValue, boolean standardAvailable,
                                            int apiLevel, boolean onDeviceAvailable,
                                            boolean offlineReadyOrUsable) {
        Mode requested = parseMode(requestedValue);
        if ((requested == Mode.AUTO || requested == Mode.ONLINE) && standardAvailable) {
            return "STANDARD_SERVICE_AVAILABLE";
        }
        if (apiLevel >= 31 && onDeviceAvailable && offlineReadyOrUsable) {
            return requested == Mode.OFFLINE ? "OFFLINE_REQUESTED_AND_AVAILABLE" : "ON_DEVICE_AVAILABLE";
        }
        return "NO_RECOGNIZER_AVAILABLE";
    }

    static boolean shouldRetryOnDeviceForTesting(String requestedValue, String usedValue,
                                                  boolean alreadyRetried, boolean turkishReady, int error) {
        return parseMode(requestedValue) == Mode.AUTO && parseMode(usedValue) == Mode.ONLINE
                && !alreadyRetried && turkishReady && isOnlineFailure(error);
    }

    static String userErrorForTesting(int error) {
        return userErrorAtApi(error, 35);
    }

    static boolean onlineUsableAfterSupportErrorForTesting(boolean standardAvailable, int error) {
        // checkRecognitionSupport is advisory; actual recognition callbacks update the cache.
        return standardAvailable;
    }

    static String supportStatusForErrorForTesting(int error) {
        return supportStatusForErrorAtApi(error, 33).name();
    }

    static boolean warmupSuccessForTesting(String requestedMode, boolean validatedInternet,
                                           boolean onlineCanStart, int apiLevel,
                                           boolean onDeviceAvailable, boolean offlineCanStart) {
        return !selectModeNameForTesting(requestedMode, validatedInternet, onlineCanStart,
                apiLevel, onDeviceAvailable, offlineCanStart).isEmpty();
    }

    static boolean optionalExtrasEnabledAfterSupportErrorForTesting(int error) {
        // Any capability-query error, including ERROR_CANNOT_CHECK_SUPPORT, means
        // startListening must fall back to the safe base intent.
        return false;
    }

    private static SupportStatus supportStatusForError(int error) {
        return supportStatusForErrorAtApi(error, Build.VERSION.SDK_INT);
    }

    private static SupportStatus supportStatusForErrorAtApi(int error, int apiLevel) {
        if (isCannotCheckSupportAtApi(error, apiLevel)) return SupportStatus.CHECK_UNAVAILABLE;
        if (apiLevel >= 31 && error == SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED) {
            return SupportStatus.UNSUPPORTED;
        }
        if (apiLevel >= 31 && error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE) {
            return SupportStatus.UNAVAILABLE;
        }
        return SupportStatus.UNKNOWN;
    }

    private static boolean isCannotCheckSupport(int error) {
        return isCannotCheckSupportAtApi(error, Build.VERSION.SDK_INT);
    }

    private static boolean isCannotCheckSupportAtApi(int error, int apiLevel) {
        return apiLevel >= 33 && error == SpeechRecognizer.ERROR_CANNOT_CHECK_SUPPORT;
    }

    private static boolean isLanguageError(int error) {
        return isLanguageErrorAtApi(error, Build.VERSION.SDK_INT);
    }

    private static boolean isLanguageErrorAtApi(int error, int apiLevel) {
        return apiLevel >= 31 && (error == SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED
                || error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE);
    }

    private static void markActualRecognitionUsable(Mode mode) {
        boolean changed;
        if (mode == Mode.OFFLINE) {
            changed = onDeviceSupportStatus != SupportStatus.SUPPORTED || !offlineUsable;
            onDeviceSupportStatus = SupportStatus.SUPPORTED;
            trTurkishOnDeviceReady = true;
            offlineUsable = true;
        } else {
            changed = onlineSupportStatus != SupportStatus.SUPPORTED || !onlineUsable;
            onlineSupportStatus = SupportStatus.SUPPORTED;
            onlineUsable = true;
        }
        if (changed) Log.i(TAG, "Actual recognition callback confirmed usable mode=" + mode);
    }

    private static void updateSupportFromActualError(Mode mode, int error) {
        if (!isLanguageError(error)) return;
        SupportStatus status = supportStatusForError(error);
        if (mode == Mode.OFFLINE) {
            onDeviceSupportStatus = status;
            trTurkishOnDeviceReady = false;
            offlineUsable = false;
        } else {
            onlineSupportStatus = status;
            onlineUsable = false;
        }
        Log.w(TAG, "Actual recognition language error mode=" + mode + " status=" + status
                + " error=" + errorName(error));
    }

    private static void requestOfflineModelDownload(Activity activity, boolean force, String reason) {
        if (activity == null || trTurkishOnDeviceReady) return;
        if (Build.VERSION.SDK_INT < 33) {
            offlineModelStatus = "UNSUPPORTED_API";
            return;
        }
        SharedPreferences prefs = activity.getSharedPreferences(MODEL_PREFS, Context.MODE_PRIVATE);
        long now = System.currentTimeMillis();
        String lastResult = prefs.getString(PREF_LAST_RESULT, "NONE");
        long requestedAt = prefs.getLong(PREF_REQUESTED_AT, 0L);
        boolean forcedRetry = force && !forcedModelDownloadRetryUsed;
        if (!shouldStartModelDownloadForTesting(Build.VERSION.SDK_INT, false, modelDownloadRequested,
                lastResult, requestedAt, now, forcedRetry)) {
            offlineModelStatus = lastResult == null ? "UNKNOWN" : lastResult;
            Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                    + " api_level=" + Build.VERSION.SDK_INT
                    + " request_started=false request_method=" + downloadMethodForTesting(Build.VERSION.SDK_INT)
                    + " blocking=false skip_reason=RETRY_POLICY last_result=" + offlineModelStatus);
            return;
        }
        if (forcedRetry) forcedModelDownloadRetryUsed = true;
        modelDownloadRequested = true;
        offlineModelStatus = "REQUESTED";
        persistModelResult(activity, "REQUESTED", 0, true);
        activity.runOnUiThread(() -> {
            SpeechRecognizer recognizer = null;
            try {
                recognizer = SpeechRecognizer.createOnDeviceSpeechRecognizer(activity);
                Intent intent = recognitionIntent(Mode.OFFLINE, false, false);
                String method = downloadMethodForTesting(Build.VERSION.SDK_INT);
                Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                        + " api_level=" + Build.VERSION.SDK_INT
                        + " request_started=true request_method=" + method
                        + " blocking=false reason=" + reason);
                if (Build.VERSION.SDK_INT >= 34) {
                    Api34ModelDownload.trigger(activity, recognizer, intent);
                } else {
                    recognizer.triggerModelDownload(intent);
                    persistModelResult(activity, "REQUESTED", 0, false);
                    scheduleOfflineSupportRecheck(activity, MODEL_RECHECK_DELAY_MS);
                    recognizer.destroy();
                }
            } catch (Throwable t) {
                if (recognizer != null) {
                    try { recognizer.destroy(); } catch (Throwable ignored) {}
                }
                offlineModelStatus = "FAILED";
                persistModelResult(activity, "FAILED", SpeechRecognizer.ERROR_CLIENT, false);
                Log.e(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                        + " download_status=ERROR error_code=" + SpeechRecognizer.ERROR_CLIENT, t);
            }
        });
    }

    static boolean shouldStartModelDownloadForTesting(int apiLevel, boolean ready,
                                                      boolean requestedThisProcess, String lastResult,
                                                      long requestedAt, long now, boolean forcedRetry) {
        if (apiLevel < 33 || ready) return false;
        if (forcedRetry) return true;
        if (requestedThisProcess) return false;
        if (requestedAt <= 0L) return true;
        long elapsed = Math.max(0L, now - requestedAt);
        long retryAfter = "FAILED".equals(lastResult) ? MODEL_FAILURE_RETRY_MS : MODEL_REQUEST_RETRY_MS;
        return elapsed >= retryAfter;
    }

    static String downloadMethodForTesting(int apiLevel) {
        if (apiLevel >= 34) return "LISTENER_TRIGGER";
        if (apiLevel >= 33) return "LEGACY_TRIGGER";
        return "UNSUPPORTED";
    }

    static boolean shouldRecheckAfterDownloadStatusForTesting(String status) {
        return "SUCCESS".equals(status) || "REQUESTED".equals(status) || "SCHEDULED".equals(status);
    }

    private static String readModelResult(Context context) {
        if (context == null) return "UNKNOWN";
        return context.getSharedPreferences(MODEL_PREFS, Context.MODE_PRIVATE)
                .getString(PREF_LAST_RESULT, "UNKNOWN");
    }

    private static void persistModelResult(Context context, String result, int error, boolean updateRequestedAt) {
        if (context == null) return;
        SharedPreferences.Editor editor = context.getSharedPreferences(MODEL_PREFS, Context.MODE_PRIVATE)
                .edit()
                .putString(PREF_LAST_RESULT, result)
                .putInt(PREF_LAST_ERROR, error)
                .putString(PREF_LANGUAGE_TAG, LANGUAGE);
        if (updateRequestedAt) editor.putLong(PREF_REQUESTED_AT, System.currentTimeMillis());
        editor.apply();
        offlineModelStatus = result;
    }

    private static void scheduleOfflineSupportRecheck(Activity activity, long delayMs) {
        new Handler(Looper.getMainLooper()).postDelayed(() -> recheckOfflineSupport(activity), delayMs);
    }

    private static void recheckOfflineSupport(Activity activity) {
        if (activity == null || Build.VERSION.SDK_INT < 33
                || !SpeechRecognizer.isOnDeviceRecognitionAvailable(activity)) return;
        SpeechRecognizer recognizer = null;
        try {
            recognizer = SpeechRecognizer.createOnDeviceSpeechRecognizer(activity);
            final SpeechRecognizer checkedRecognizer = recognizer;
            recognizer.checkRecognitionSupport(recognitionIntent(Mode.OFFLINE, false, false),
                    activity.getMainExecutor(), new RecognitionSupportCallback() {
                @Override public void onSupportResult(RecognitionSupport support) {
                    boolean installed = containsLanguage(support.getInstalledOnDeviceLanguages(), LANGUAGE);
                    if (installed) {
                        trTurkishOnDeviceReady = true;
                        offlineUsable = true;
                        onDeviceSupportStatus = SupportStatus.SUPPORTED;
                        persistModelResult(activity, "INSTALLED", 0, false);
                        Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                                + " download_status=INSTALLED offline_usable=true");
                    } else {
                        persistModelResult(activity, "PENDING", 0, false);
                        Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                                + " download_status=PENDING offline_usable=" + offlineUsable);
                    }
                    checkedRecognizer.destroy();
                }

                @Override public void onError(int error) {
                    if (isLanguageError(error)) updateSupportFromActualError(Mode.OFFLINE, error);
                    persistModelResult(activity, isCannotCheckSupport(error) ? "CHECK_UNAVAILABLE" : "PENDING",
                            error, false);
                    Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                            + " download_status=" + offlineModelStatus + " error_code=" + error);
                    checkedRecognizer.destroy();
                }
            });
        } catch (Throwable t) {
            if (recognizer != null) {
                try { recognizer.destroy(); } catch (Throwable ignored) {}
            }
            persistModelResult(activity, "FAILED", SpeechRecognizer.ERROR_CLIENT, false);
            Log.w(TAG, "Offline model support recheck failed", t);
        }
    }

    private static NetworkDiagnostics networkDiagnostics(Context context) {
        try {
            ConnectivityManager manager = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
            if (manager == null) return new NetworkDiagnostics("UNKNOWN", "unknown", false);
            Network network = manager.getActiveNetwork();
            if (network == null) return new NetworkDiagnostics("UNAVAILABLE", "false", false);
            NetworkCapabilities capabilities = manager.getNetworkCapabilities(network);
            if (capabilities == null) return new NetworkDiagnostics("AVAILABLE", "unknown", false);
            boolean internet = capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET);
            boolean validated = capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
            return new NetworkDiagnostics("AVAILABLE", Boolean.toString(validated), internet && validated);
        } catch (Throwable t) {
            return new NetworkDiagnostics("UNKNOWN", "unknown", false);
        }
    }

    private static final class NetworkDiagnostics {
        final String state;
        final String validated;
        final boolean validatedInternet;
        NetworkDiagnostics(String state, String validated, boolean validatedInternet) {
            this.state = state;
            this.validated = validated;
            this.validatedInternet = validatedInternet;
        }
    }

    /** Isolates API 34-only listener types from class verification on Android 13/API 33. */
    private static final class Api34ModelDownload {
        static void trigger(Activity activity, SpeechRecognizer recognizer, Intent intent) {
            recognizer.triggerModelDownload(intent, activity.getMainExecutor(), new ModelDownloadListener() {
                @Override public void onProgress(int completedPercent) {
                    offlineModelStatus = "PROGRESS";
                    Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                            + " download_status=PROGRESS progress_percent=" + completedPercent);
                }

                @Override public void onSuccess() {
                    persistModelResult(activity, "SUCCESS", 0, false);
                    Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                            + " download_status=SUCCESS");
                    try { recognizer.destroy(); } catch (Throwable ignored) {}
                    scheduleOfflineSupportRecheck(activity, 250L);
                }

                @Override public void onScheduled() {
                    persistModelResult(activity, "SCHEDULED", 0, false);
                    Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                            + " download_status=SCHEDULED");
                    try { recognizer.destroy(); } catch (Throwable ignored) {}
                    scheduleOfflineSupportRecheck(activity, MODEL_RECHECK_DELAY_MS);
                }

                @Override public void onError(int error) {
                    persistModelResult(activity, "FAILED", error, false);
                    Log.e(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                            + " download_status=ERROR error_code=" + error);
                    try { recognizer.destroy(); } catch (Throwable ignored) {}
                }
            });
        }
    }

    private static String recognizerService(Context context, Mode mode) {
        String configured = "";
        try {
            if (mode == Mode.ONLINE) {
                configured = Settings.Secure.getString(context.getContentResolver(), "voice_recognition_service");
            } else {
                int id = context.getResources().getIdentifier("config_defaultOnDeviceSpeechRecognitionService", "string", "android");
                if (id != 0) configured = context.getResources().getString(id);
            }
        } catch (Throwable ignored) {}
        if (configured != null && !configured.trim().isEmpty()) return configured;
        try {
            Intent query = new Intent("android.speech.RecognitionService");
            List<ResolveInfo> services = context.getPackageManager().queryIntentServices(query, PackageManager.MATCH_DEFAULT_ONLY);
            if (services != null && !services.isEmpty() && services.get(0).serviceInfo != null) {
                return new ComponentName(services.get(0).serviceInfo.packageName, services.get(0).serviceInfo.name).flattenToShortString();
            }
        } catch (Throwable ignored) {}
        return mode == Mode.OFFLINE ? "on-device service component unavailable" : "system default component unavailable";
    }

    private static void sendEvent(Session session, String type, Bundle results, int errorCode,
                                  String errorMessage, State eventState) {
        try {
            JSONObject json = new JSONObject();
            json.put("session_id", session.id);
            json.put("parent_session_id", session.id);
            json.put("event_type", type);
            json.put("recognition_mode_requested", session.requested.name());
            json.put("recognition_mode_used", session.used.name());
            json.put("recognizer_service", session.service);
            json.put("state", eventState.name());
            json.put("partial", "PARTIAL".equals(type));
            json.put("final_result", "FINAL".equals(type));
            json.put("alternatives", alternatives(results));
            json.put("confidence_available", hasConfidence(results));
            json.put("error_code", errorCode);
            json.put("error_name", errorCode == 0 ? "" : errorName(errorCode));
            json.put("error_message", errorMessage == null ? "" : errorMessage);
            json.put("listening_started_at", session.listeningStartedAt);
            json.put("speech_start_at", session.speechStartedAt);
            json.put("speech_end_at", session.speechEndedAt);
            json.put("first_partial_at", session.firstPartialAt);
            json.put("final_result_at", session.finalResultAt);
            long finalLatency = session.finalResultAt > 0 && session.speechEndedAt > 0
                    ? session.finalResultAt - session.speechEndedAt : -1L;
            json.put("finalization_latency_ms", finalLatency);
            json.put("attempt", session.attempt);
            json.put("android_on_device_retry", session.retriedOnDevice);
            unityMessage(session.unityGameObject, session.unityCallbackMethod, json.toString());
        } catch (Throwable t) {
            Log.e(TAG, "Speech event JSON failed", t);
        }
    }

    private static JSONArray alternatives(Bundle bundle) throws Exception {
        JSONArray array = new JSONArray();
        if (bundle == null) return array;
        ArrayList<String> values = bundle.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION);
        float[] confidence = bundle.getFloatArray(SpeechRecognizer.CONFIDENCE_SCORES);
        if (values == null) return array;
        Set<String> unique = new HashSet<>();
        int rank = 0;
        for (int sourceIndex = 0; sourceIndex < values.size() && array.length() < MAX_RESULTS; sourceIndex++) {
            String original = values.get(sourceIndex) == null ? "" : values.get(sourceIndex).trim();
            String key = original.toLowerCase(new Locale("tr", "TR")).replaceAll("\\s+", " ");
            if (key.isEmpty() || !unique.add(key)) continue;
            float score = confidence != null && sourceIndex < confidence.length ? confidence[sourceIndex] : -1f;
            JSONObject item = new JSONObject();
            item.put("text", original);
            item.put("rank", rank++);
            item.put("source_index", sourceIndex);
            item.put("confidence", score);
            item.put("confidence_available", score >= 0f);
            array.put(item);
        }
        return array;
    }

    private static boolean hasConfidence(Bundle bundle) {
        if (bundle == null) return false;
        float[] confidence = bundle.getFloatArray(SpeechRecognizer.CONFIDENCE_SCORES);
        if (confidence == null) return false;
        for (float value : confidence) if (value >= 0f) return true;
        return false;
    }

    private static void failSession(Session session, int code, String message, boolean ignored) {
        if (session == null || session.finalSent) return;
        state = State.ERROR;
        sendEvent(session, "ERROR", null, code, message, state);
        destroyRecognizer(session);
        synchronized (LOCK) { if (activeSession == session) activeSession = null; }
    }

    private static void sendImmediateError(String id, String requested, String target, String callback,
                                           int code, String message) {
        Session session = new Session(id, parseMode(requested), parseMode(requested), target, callback);
        session.service = "";
        sendEvent(session, "ERROR", null, code, message, State.ERROR);
    }

    private static void destroyRecognizer(Session session) {
        SpeechRecognizer recognizer = session.recognizer;
        session.recognizer = null;
        if (recognizer != null) {
            try { recognizer.destroy(); } catch (Throwable t) { Log.w(TAG, "Recognizer destroy failed", t); }
        }
    }

    private static boolean matches(Session session, String id) {
        return session != null && session.id.equals(id == null ? "" : id);
    }
    private static boolean isTerminal(State value) {
        return value == State.COMPLETED || value == State.CANCELLED || value == State.ERROR || value == State.DESTROYED;
    }
    private static boolean isOnlineFailure(int error) {
        return error == SpeechRecognizer.ERROR_NETWORK || error == SpeechRecognizer.ERROR_NETWORK_TIMEOUT
                || error == SpeechRecognizer.ERROR_SERVER
                || (Build.VERSION.SDK_INT >= 31 && error == SpeechRecognizer.ERROR_SERVER_DISCONNECTED);
    }
    private static Mode parseMode(String value) {
        try { return Mode.valueOf((value == null ? "AUTO" : value.trim()).toUpperCase(Locale.ROOT)); }
        catch (Throwable ignored) { return Mode.AUTO; }
    }
    private static boolean containsLanguage(List<String> languages, String wanted) {
        if (languages == null) return false;
        for (String value : languages) {
            if (value != null && (value.equalsIgnoreCase(wanted) || value.toLowerCase(Locale.ROOT).startsWith("tr"))) return true;
        }
        return false;
    }

    private static boolean hasAnyLanguage(List<String> languages) {
        return languages != null && !languages.isEmpty();
    }

    private static String supportJson(boolean ok, String error, long started) {
        try {
            JSONObject json = new JSONObject();
            json.put("ok", ok);
            json.put("initialized", ok);
            json.put("stage", "android_speech_recognizer_ready");
            json.put("durationMs", System.currentTimeMillis() - started);
            json.put("requested_mode", requestedWarmupMode);
            json.put("standard_service_available", standardServiceAvailable);
            json.put("network_state", networkState);
            json.put("network_validated", networkValidated);
            json.put("online_support_status", onlineSupportStatus.name());
            json.put("online_usable", onlineUsable);
            // Backwards-compatible field consumed by the existing Unity warm-up model.
            json.put("online_available", onlineUsable);
            json.put("on_device_service_available", onDeviceServiceAvailable);
            json.put("on_device_support_status", onDeviceSupportStatus.name());
            json.put("tr_tr_on_device_ready", trTurkishOnDeviceReady);
            json.put("offline_usable", offlineUsable);
            json.put("offline_model_status", offlineModelStatus);
            json.put("model_download_requested", modelDownloadRequested);
            json.put("support_checked", supportChecked);
            json.put("selected_mode", selectedWarmupMode);
            json.put("selection_reason", selectionReason);
            json.put("warmup_success", ok);
            json.put("error", error == null ? "" : error);
            return json.toString();
        } catch (Throwable t) {
            return "{\"ok\":false,\"stage\":\"android_speech_support_json_failed\"}";
        }
    }

    private static String userError(int error) {
        return userErrorAtApi(error, Build.VERSION.SDK_INT);
    }

    private static String userErrorAtApi(int error, int apiLevel) {
        switch (error) {
            case SpeechRecognizer.ERROR_AUDIO: return "Mikrofon başka bir uygulama tarafından kullanılıyor veya ses alınamadı.";
            case SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS: return "Mikrofon izni verilmedi.";
            case SpeechRecognizer.ERROR_NETWORK:
            case SpeechRecognizer.ERROR_NETWORK_TIMEOUT: return "Ağ bağlantısı yok veya zaman aşımına uğradı.";
            case SpeechRecognizer.ERROR_NO_MATCH:
            case SpeechRecognizer.ERROR_SPEECH_TIMEOUT: return "Konuşma anlaşılamadı.";
            case SpeechRecognizer.ERROR_RECOGNIZER_BUSY: return "Tanıma servisi meşgul, tekrar deneyin.";
            case SpeechRecognizer.ERROR_TOO_MANY_REQUESTS: return "Çok fazla tanıma isteği gönderildi; biraz bekleyin.";
            default:
                if (apiLevel >= 31 && error == SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED) {
                    return "Türkçe konuşma tanıma bu serviste desteklenmiyor.";
                }
                if (apiLevel >= 31 && error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE) {
                    return "Türkçe konuşma modeli bu serviste hazır değil.";
                }
                if (error == SpeechRecognizer.ERROR_SERVER
                        || (apiLevel >= 31 && error == SpeechRecognizer.ERROR_SERVER_DISCONNECTED)) {
                    return "Konuşma tanıma servisine ulaşılamadı.";
                }
                return "Konuşma tanıma başlatılamadı; tekrar deneyin.";
        }
    }

    private static String errorName(int error) {
        switch (error) {
            case SpeechRecognizer.ERROR_NETWORK_TIMEOUT: return "ERROR_NETWORK_TIMEOUT";
            case SpeechRecognizer.ERROR_NETWORK: return "ERROR_NETWORK";
            case SpeechRecognizer.ERROR_AUDIO: return "ERROR_AUDIO";
            case SpeechRecognizer.ERROR_SERVER: return "ERROR_SERVER";
            case SpeechRecognizer.ERROR_CLIENT: return "ERROR_CLIENT";
            case SpeechRecognizer.ERROR_SPEECH_TIMEOUT: return "ERROR_SPEECH_TIMEOUT";
            case SpeechRecognizer.ERROR_NO_MATCH: return "ERROR_NO_MATCH";
            case SpeechRecognizer.ERROR_RECOGNIZER_BUSY: return "ERROR_RECOGNIZER_BUSY";
            case SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS: return "ERROR_INSUFFICIENT_PERMISSIONS";
            case SpeechRecognizer.ERROR_TOO_MANY_REQUESTS: return "ERROR_TOO_MANY_REQUESTS";
            default:
                if (Build.VERSION.SDK_INT >= 31 && error == SpeechRecognizer.ERROR_SERVER_DISCONNECTED) return "ERROR_SERVER_DISCONNECTED";
                if (Build.VERSION.SDK_INT >= 31 && error == SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED) return "ERROR_LANGUAGE_NOT_SUPPORTED";
                if (Build.VERSION.SDK_INT >= 31 && error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE) return "ERROR_LANGUAGE_UNAVAILABLE";
                if (Build.VERSION.SDK_INT >= 33 && error == SpeechRecognizer.ERROR_CANNOT_CHECK_SUPPORT) return "ERROR_CANNOT_CHECK_SUPPORT";
                return "ERROR_" + error;
        }
    }

    private static Activity unityActivity() {
        try {
            Class<?> cls = Class.forName("com.unity3d.player.UnityPlayer");
            Field field = cls.getField("currentActivity");
            Object value = field.get(null);
            return value instanceof Activity ? (Activity) value : null;
        } catch (Throwable t) {
            Log.e(TAG, "Unity Activity unavailable", t);
            return null;
        }
    }

    private static void unityMessage(final String target, final String callback, final String json) {
        try {
            Class<?> cls = Class.forName("com.unity3d.player.UnityPlayer");
            Method method = cls.getMethod("UnitySendMessage", String.class, String.class, String.class);
            method.invoke(null, target == null ? "" : target, callback == null ? "" : callback, json == null ? "" : json);
        } catch (Throwable t) {
            Log.e(TAG, "UnitySendMessage failed", t);
        }
    }
    private static String message(Throwable t) {
        return t == null ? "" : (t.getMessage() == null ? t.toString() : t.getMessage());
    }

    private static final class Session {
        final String id;
        final Mode requested;
        final String unityGameObject;
        final String unityCallbackMethod;
        Mode used;
        SpeechRecognizer recognizer;
        String service = "";
        int attempt;
        boolean retriedOnDevice;
        boolean finalSent;
        long listeningStartedAt;
        long speechStartedAt;
        long speechEndedAt;
        long firstPartialAt;
        long finalResultAt;

        Session(String id, Mode requested, Mode used, String target, String callback) {
            this.id = id == null ? "" : id;
            this.requested = requested;
            this.used = used;
            this.unityGameObject = target == null ? "" : target;
            this.unityCallbackMethod = callback == null ? "" : callback;
        }
    }
}
