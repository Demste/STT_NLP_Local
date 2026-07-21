package com.mehmet.sttbridge;

import android.Manifest;
import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ApplicationInfo;
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
    private static final long MODEL_POLL_INTERVAL_MS = 5000L;
    private static final int MODEL_POLL_LIMIT = 60;
    private static final long MODEL_DOWNLOAD_COOLDOWN_MS = 6L * 60L * 60L * 1000L;
    private static final String MODEL_PREFS = "android_stt_model_state";
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
    private static int modelPollCount;
    private static boolean modelPollScheduled;
    private static boolean modelDownloadInFlight;
    private static SpeechRecognizer modelDownloadRecognizer;
    private static long modelDownloadStartedAt;
    private static String modelUnityGameObject = "";
    private static String modelUnityCallbackMethod = "";

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
        offlineModelStatus = readModelState(activity);

        if (Build.VERSION.SDK_INT >= 33) {
            if (standardServiceAvailable) checkApi33OnlineSupport(activity);
            if (onDeviceServiceAvailable) checkApi33OnDeviceSupport(activity, requested);
            else supportChecked = true;
        } else {
            supportChecked = true;
        }

        if (trTurkishOnDeviceReady) {
            offlineModelStatus = "INSTALLED";
        } else if (Build.VERSION.SDK_INT < 33) {
            offlineModelStatus = "UNSUPPORTED_API";
            Log.i(TAG, "[AndroidSTTModelDownload] language=" + LANGUAGE
                    + " api_level=" + Build.VERSION.SDK_INT
                    + " request_started=false request_method=UNSUPPORTED blocking=false");
        }

        String selected = selectModeNameForTesting(
                requested.name(), network.state, network.validated, onlineUsable,
                Build.VERSION.SDK_INT, onDeviceServiceAvailable, offlineUsable);
        selectedWarmupMode = selected;
        selectionReason = selectionReasonForTesting(requested.name(), network.state, network.validated, onlineUsable,
                Build.VERSION.SDK_INT, onDeviceServiceAvailable, offlineUsable);
        boolean requestedReady = !selected.isEmpty();
        if (requested == Mode.OFFLINE && Build.VERSION.SDK_INT < 31) {
            supportMessage = "Android 12 (API 31) öncesinde çevrimdışı tanıma desteklenmiyor.";
        } else if (requested == Mode.OFFLINE && !onDeviceServiceAvailable) {
            supportMessage = "Cihazda çevrimdışı konuşma tanıma servisi bulunamadı.";
        } else if ((requested == Mode.AUTO || requested == Mode.ONLINE) && onlineUsable) {
            // Offline model state must never block an otherwise usable standard recognizer.
            supportMessage = "";
        } else if (!requestedReady && supportMessage.isEmpty()) {
            supportMessage = "Konuşma tanıma servisi cihazda bulunamadı.";
        }
        // Warm-up only reports state. A download starts after explicit Unity UI consent.
        if (Build.VERSION.SDK_INT >= 33
                && ("PENDING".equals(offlineModelStatus) || "REQUESTED".equals(offlineModelStatus))) {
            scheduleModelPoll(activity);
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
                            persistModelState(activity, "INSTALLED");
                            offlineModelStatus = "INSTALLED";
                            offlineUsable = true;
                            onDeviceSupportStatus = SupportStatus.SUPPORTED;
                        } else if (downloadable || pending) {
                            offlineModelStatus = pending ? "PENDING" : "DOWNLOAD_REQUIRED";
                            persistModelState(activity, offlineModelStatus);
                            offlineUsable = false;
                            onDeviceSupportStatus = SupportStatus.UNAVAILABLE;
                        } else if (hasAnyLanguageDetail) {
                            offlineUsable = false;
                            onDeviceSupportStatus = SupportStatus.UNSUPPORTED;
                        } else {
                            offlineUsable = onDeviceServiceAvailable;
                            onDeviceSupportStatus = SupportStatus.UNKNOWN;
                        }
                        // Do not turn advisory language lists into a hard gate. The user may
                        // already have the model installed in the selected recognition service.
                        if (requested == Mode.OFFLINE) supportMessage = "";
                        logModelStatus(activity, "SUPPORT_RESULT", trTurkishOnDeviceReady,
                                pending, downloadable, !trTurkishOnDeviceReady && downloadable);
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
                            // Session callbacks from a real on-device start are authoritative.
                            offlineUsable = onDeviceServiceAvailable;
                            supportMessage = "";
                            offlineModelStatus = "DOWNLOAD_REQUIRED";
                            persistModelState(activity, offlineModelStatus);
                        } else {
                            offlineUsable = onDeviceServiceAvailable;
                            supportMessage = "";
                        }
                        logModelStatus(activity, errorName(error), false, false, false,
                                isLanguageError(error));
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
            refreshRecognizerAvailability(activity);
            Mode used = chooseMode(activity, requested);
            if (used == null) {
                String error = requested == Mode.OFFLINE
                        ? (Build.VERSION.SDK_INT < 31
                            ? "Bu cihazda çevrimdışı tanıma desteklenmiyor."
                            : "Cihazda çevrimdışı konuşma tanıma servisi bulunamadı.")
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
        if (activity == null) {
            failSession(session, SpeechRecognizer.ERROR_CLIENT, "Unity activity hazır değil.", false);
            return;
        }
        session.used = used;
        session.attempt++;
        int generation = ++session.activeRecognizerGeneration;
        session.listeningStartedAt = System.currentTimeMillis();
        session.service = recognizerService(activity, used);
        state = State.STARTING;
        try {
            SpeechRecognizer recognizer = used == Mode.OFFLINE
                    ? SpeechRecognizer.createOnDeviceSpeechRecognizer(activity)
                    : SpeechRecognizer.createSpeechRecognizer(activity);
            session.recognizer = recognizer;
            recognizer.setRecognitionListener(new Listener(session, generation));
            boolean biasing = used == Mode.OFFLINE ? offlineBiasingSupported : onlineBiasingSupported;
            boolean formatting = used == Mode.OFFLINE ? offlineFormattingSupported : onlineFormattingSupported;
            Intent intent = recognitionIntent(used, biasing, formatting);
            Log.i(TAG, String.format(Locale.US,
                    "[AndroidSTT] session_id=%s requested_mode=%s used_mode=%s retry_reason=%s "
                            + "on_device_available=%s retry_count=%d recognizer_recreated=%s language=%s "
                            + "callback=startListening service=%s partial_results=true biasing_enabled=%s max_results=%d",
                    session.id, session.requested, usedModeLogName(used), session.retryReason,
                    onDeviceServiceAvailable, session.retriedOnDevice ? 1 : 0, session.attempt > 1,
                    LANGUAGE, session.service, biasing, MAX_RESULTS));
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
                session.terminalDelivered = true;
                session.activeRecognizerGeneration++;
                try { if (session.recognizer != null) session.recognizer.cancel(); } catch (Throwable ignored) {}
                sendEvent(session, "STATE", null, 0, "", state);
                releaseRecognizer(session, false);
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
                    state = State.DESTROYED;
                    session.terminalDelivered = true;
                    session.activeRecognizerGeneration++;
                    sendEvent(session, "STATE", null, 0, "", state);
                    releaseRecognizer(session, true);
                }
                activeSession = null;
                state = State.DESTROYED;
            }
        });
    }

    private static void scheduleOnDeviceRetry(Activity activity, Session session, int error) {
        final SpeechRecognizer disconnected;
        synchronized (LOCK) {
            if (activeSession != session || session.terminalDelivered || session.retryStarted) return;
            session.retryStarted = true;
            session.retriedOnDevice = true;
            session.retryReason = errorName(error);
            // Invalidate the old callback generation immediately. Destruction and recreation are
            // deferred to the next main-loop turn so they never occur in the provider callback.
            session.activeRecognizerGeneration++;
            disconnected = session.recognizer;
            session.recognizer = null;
            state = State.STARTING;
        }
        mainHandler().post(() -> {
            if (disconnected != null) {
                try { disconnected.setRecognitionListener(null); } catch (Throwable ignored) {}
                // ERROR_SERVER_DISCONNECTED recognizers must not be cancelled before teardown.
                try { disconnected.destroy(); } catch (Throwable t) {
                    Log.w(TAG, "Disconnected recognizer destroy failed", t);
                }
            }
            synchronized (LOCK) {
                if (activeSession != session || session.terminalDelivered) return;
                beginAttempt(activity, session, Mode.OFFLINE);
            }
        });
    }

    private static final class Listener implements RecognitionListener {
        private final Session session;
        private final int generation;
        Listener(Session session, int generation) { this.session = session; this.generation = generation; }

        private boolean current() {
            synchronized (LOCK) {
                return activeSession == session
                        && session.activeRecognizerGeneration == generation
                        && !session.terminalDelivered && !isTerminal(state);
            }
        }
        @Override public void onReadyForSpeech(Bundle params) {
            if (!current()) return;
            markActualRecognitionUsable(session.used);
            state = State.LISTENING;
            logLifecycleCallback(session, "onReadyForSpeech");
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
            NetworkDiagnostics diagnostics = networkDiagnostics(unityActivity());
            if (isNetworkError(error)) session.networkErrorSeen = true;
            if ("UNAVAILABLE".equals(diagnostics.state) || "false".equals(diagnostics.validated)) {
                session.networkKnownUnavailable = true;
            }
            Activity activity = unityActivity();
            boolean onDeviceAvailable = refreshOnDeviceAvailability(activity);
            boolean retry = shouldRetryOnDeviceForTesting(session.requested.name(), session.used.name(),
                    session.retriedOnDevice, onDeviceAvailable,
                    session.networkErrorSeen || session.networkKnownUnavailable, error);
            String loggedRetryReason = retry || (session.used == Mode.ONLINE && isNetworkError(error))
                    ? errorName(error) : session.retryReason;
            Log.e(TAG, String.format(Locale.US,
                    "[AndroidSTT] session_id=%s requested_mode=%s used_mode=%s retry_reason=%s "
                            + "on_device_available=%s retry_count=%d recognizer_recreated=%s language=%s "
                            + "error_code=%d error_name=%s android_on_device_retry=%s",
                    session.id, session.requested, usedModeLogName(session.used), loggedRetryReason,
                    onDeviceAvailable, session.retriedOnDevice ? 1 : 0, session.attempt > 1,
                    LANGUAGE, error, errorName(error), retry));
            if (retry) {
                scheduleOnDeviceRetry(activity, session, error);
                return;
            }
            if (session.used == Mode.OFFLINE && error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE) {
                offlineModelStatus = "DOWNLOAD_REQUIRED";
                persistModelState(activity, offlineModelStatus);
                logModelStatus(activity, errorName(error), false, false, false, true);
            }
            failSession(session, error, sessionError(session, error, onDeviceAvailable), false);
        }
        @Override public void onResults(Bundle results) {
            if (!current() || session.finalSent) return;
            markActualRecognitionUsable(session.used);
            session.finalSent = true;
            session.terminalDelivered = true;
            session.finalResultAt = System.currentTimeMillis();
            state = State.COMPLETED;
            logLifecycleCallback(session, "onResults");
            sendEvent(session, "FINAL", results, 0, "", state);
            resetTerminalSession(session);
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
        intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_PREFERENCE, LANGUAGE);
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
                requested.name(), network.state, network.validated, onlineUsable,
                Build.VERSION.SDK_INT, onDeviceServiceAvailable, offlineUsable);
        if (selected.isEmpty()) return null;
        return "ON_DEVICE".equals(selected) ? Mode.OFFLINE : Mode.valueOf(selected);
    }

    static String selectModeNameForTesting(String requestedValue, boolean validatedInternet,
                                           boolean standardAvailable, int apiLevel,
                                           boolean onDeviceAvailable, boolean offlineReadyOrUsable) {
        return selectModeNameForTesting(requestedValue, "AVAILABLE",
                Boolean.toString(validatedInternet), standardAvailable, apiLevel,
                onDeviceAvailable, offlineReadyOrUsable);
    }

    static String selectModeNameForTesting(String requestedValue, String networkStateValue,
                                           String networkValidatedValue, boolean standardAvailable,
                                           int apiLevel, boolean onDeviceAvailable,
                                           boolean offlineReadyOrUsable) {
        Mode requested = parseMode(requestedValue);
        if (requested == Mode.ONLINE) return standardAvailable ? Mode.ONLINE.name() : "";
        // RecognitionSupport language lists are advisory. Service availability plus the
        // real startListening callbacks are authoritative for an on-device attempt.
        boolean canUseOffline = apiLevel >= 31 && onDeviceAvailable;
        if (requested == Mode.OFFLINE) return canUseOffline ? "ON_DEVICE" : "";
        boolean networkKnownUnavailable = "UNAVAILABLE".equalsIgnoreCase(networkStateValue)
                || "false".equalsIgnoreCase(networkValidatedValue);
        if (networkKnownUnavailable && canUseOffline) return "ON_DEVICE";
        if (standardAvailable) return Mode.ONLINE.name();
        return canUseOffline ? "ON_DEVICE" : "";
    }

    static String selectionReasonForTesting(String requestedValue, boolean standardAvailable,
                                            int apiLevel, boolean onDeviceAvailable,
                                            boolean offlineReadyOrUsable) {
        return selectionReasonForTesting(requestedValue, "UNKNOWN", "unknown", standardAvailable,
                apiLevel, onDeviceAvailable, offlineReadyOrUsable);
    }

    static String selectionReasonForTesting(String requestedValue, String networkStateValue,
                                            String networkValidatedValue, boolean standardAvailable,
                                            int apiLevel, boolean onDeviceAvailable,
                                            boolean offlineReadyOrUsable) {
        Mode requested = parseMode(requestedValue);
        boolean canUseOffline = apiLevel >= 31 && onDeviceAvailable;
        boolean networkKnownUnavailable = "UNAVAILABLE".equalsIgnoreCase(networkStateValue)
                || "false".equalsIgnoreCase(networkValidatedValue);
        if (requested == Mode.AUTO && networkKnownUnavailable && canUseOffline) {
            return "NETWORK_UNAVAILABLE_ON_DEVICE_AVAILABLE";
        }
        if ((requested == Mode.AUTO || requested == Mode.ONLINE) && standardAvailable) {
            return "STANDARD_SERVICE_AVAILABLE";
        }
        if (canUseOffline) {
            return requested == Mode.OFFLINE ? "OFFLINE_REQUESTED_AND_AVAILABLE" : "ON_DEVICE_AVAILABLE";
        }
        return "NO_RECOGNIZER_AVAILABLE";
    }

    static boolean shouldRetryOnDeviceForTesting(String requestedValue, String usedValue,
                                                  boolean alreadyRetried, boolean turkishReady, int error) {
        return shouldRetryOnDeviceForTesting(requestedValue, usedValue, alreadyRetried,
                turkishReady, isNetworkError(error), error);
    }

    static boolean shouldRetryOnDeviceForTesting(String requestedValue, String usedValue,
                                                  boolean alreadyRetried, boolean onDeviceAvailable,
                                                  boolean networkErrorSeen, int error) {
        return parseMode(requestedValue) == Mode.AUTO && parseMode(usedValue) == Mode.ONLINE
                && !alreadyRetried && onDeviceAvailable
                && (isNetworkError(error)
                    || (error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE
                        && networkErrorSeen));
    }

    static String userErrorForTesting(int error) {
        return userErrorAtApi(error, 35);
    }

    static String sessionErrorForTesting(String usedValue, int error, boolean onDeviceAvailable) {
        Mode used = parseMode(usedValue);
        Session session = new Session("test", used, used, "", "");
        return sessionError(session, error, onDeviceAvailable);
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
            Activity activity = unityActivity();
            if (activity != null) {
                offlineModelStatus = "INSTALLED";
                persistModelState(activity, offlineModelStatus);
            }
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
        // A language error belongs only to this recognizer attempt. It must not latch the
        // process into a global "service unavailable" state or block the next request.
        Log.w(TAG, "Actual recognition language error is session-scoped mode=" + mode
                + " status=" + status + " error=" + errorName(error));
    }

    /** Debug-only escape hatch used by the physical-device model provisioning workflow. */
    public static void forceRequestTurkishOnDeviceModel() {
        final Activity activity = unityActivity();
        if (activity == null) return;
        boolean debuggable = (activity.getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) != 0;
        if (!debuggable) {
            Log.w(TAG, "[AndroidSTTModel] force_request_rejected=release_build language=" + LANGUAGE);
            return;
        }
        refreshOnDeviceAvailability(activity);
        beginTurkishOnDeviceModelRequest(activity, true);
    }

    public static String requestTurkishOnDeviceModel(String unityGameObject, String unityCallbackMethod) {
        final Activity activity = unityActivity();
        if (activity == null) {
            return modelActionJson(false, "UNKNOWN", false, "Unity activity hazır değil.");
        }
        setModelUnityCallback(unityGameObject, unityCallbackMethod);
        refreshOnDeviceAvailability(activity);
        String current = readModelState(activity);
        NetworkDiagnostics network = networkDiagnostics(activity);
        if ("INSTALLED".equals(current) || trTurkishOnDeviceReady) {
            offlineModelStatus = "INSTALLED";
            persistModelState(activity, offlineModelStatus);
            sendModelEvent(activity, "installed", true, false, "");
            return modelActionJson(true, offlineModelStatus, network.validatedInternet, "");
        }
        synchronized (LOCK) {
            if (modelDownloadInFlight || "REQUESTED".equals(current) || "PENDING".equals(current)) {
                modelDownloadInFlight = true;
                scheduleModelPoll(activity);
                return modelActionJson(true, current, network.validatedInternet, "");
            }
        }
        if (!network.validatedInternet) {
            return modelActionJson(false, current, false,
                    "Modeli indirmek için Wi-Fi veya mobil veriyi açın.");
        }
        beginTurkishOnDeviceModelRequest(activity, true);
        return modelActionJson(true, "REQUESTED", true, "");
    }

    public static String observeTurkishOnDeviceModel(String unityGameObject, String unityCallbackMethod) {
        final Activity activity = unityActivity();
        if (activity == null) return modelActionJson(false, "UNKNOWN", false, "Unity activity hazır değil.");
        setModelUnityCallback(unityGameObject, unityCallbackMethod);
        String current = readModelState(activity);
        if ("INSTALLED".equals(current) || trTurkishOnDeviceReady) {
            sendModelEvent(activity, "installed", true, false, "");
        } else if ("REQUESTED".equals(current) || "PENDING".equals(current)) {
            synchronized (LOCK) { modelDownloadInFlight = true; }
            scheduleModelPoll(activity);
        }
        return modelActionJson(true, current, networkDiagnostics(activity).validatedInternet, "");
    }

    private static void setModelUnityCallback(String target, String callback) {
        synchronized (LOCK) {
            modelUnityGameObject = target == null ? "" : target;
            modelUnityCallbackMethod = callback == null ? "" : callback;
        }
    }

    private static void beginTurkishOnDeviceModelRequest(Activity activity, boolean userInitiated) {
        if (activity == null || Build.VERSION.SDK_INT < 33 || !onDeviceServiceAvailable) {
            finishModelDownload(activity, false, "ON_DEVICE_SERVICE_UNAVAILABLE");
            return;
        }
        String current = readModelState(activity);
        if ("INSTALLED".equals(current) || trTurkishOnDeviceReady) {
            finishModelDownload(activity, true, "");
            return;
        }
        synchronized (LOCK) {
            if (modelDownloadInFlight || "REQUESTED".equals(current) || "PENDING".equals(current)) {
                modelDownloadInFlight = true;
                scheduleModelPoll(activity);
                return;
            }
        }
        NetworkDiagnostics network = networkDiagnostics(activity);
        if (!network.validatedInternet) return;
        SharedPreferences prefs = activity.getSharedPreferences(MODEL_PREFS, Context.MODE_PRIVATE);
        String prefix = modelPreferencePrefix(activity);
        long lastRequested = prefs.getLong(prefix + ".requested_at", 0L);
        if (!userInitiated && System.currentTimeMillis() - lastRequested < MODEL_DOWNLOAD_COOLDOWN_MS) {
            scheduleModelPoll(activity);
            return;
        }
        synchronized (LOCK) {
            modelDownloadInFlight = true;
            modelDownloadStartedAt = System.currentTimeMillis();
            modelPollCount = 0;
        }
        mainHandler().post(() -> requestModelDownloadOnMain(activity));
    }

    private static void requestModelDownloadOnMain(Activity activity) {
        NetworkDiagnostics network = networkDiagnostics(activity);
        if (!network.validatedInternet || Build.VERSION.SDK_INT < 33) {
            finishModelDownload(activity, false, "NETWORK_NOT_VALIDATED");
            return;
        }
        final SpeechRecognizer recognizer;
        try {
            recognizer = SpeechRecognizer.createOnDeviceSpeechRecognizer(activity);
            recognizer.setRecognitionListener(new ModelRecognitionListener());
        } catch (Throwable t) {
            Log.w(TAG, "[AndroidSTTModel] on-device recognizer creation failed", t);
            finishModelDownload(activity, false, "RECOGNIZER_CREATE_FAILED");
            return;
        }
        Intent intent = recognitionIntent(Mode.OFFLINE, false, false);
        try {
            recognizer.checkRecognitionSupport(intent, activity.getMainExecutor(),
                    new RecognitionSupportCallback() {
                @Override public void onSupportResult(RecognitionSupport support) {
                    boolean installed = containsLanguage(support.getInstalledOnDeviceLanguages(), LANGUAGE);
                    boolean pending = containsLanguage(support.getPendingOnDeviceLanguages(), LANGUAGE);
                    boolean supported = containsLanguage(support.getSupportedOnDeviceLanguages(), LANGUAGE);
                    logModelStatus(activity, "DOWNLOAD_SUPPORT", installed, pending, supported,
                            !installed && supported);
                    if (installed) {
                        offlineModelStatus = "INSTALLED";
                        trTurkishOnDeviceReady = true;
                        offlineUsable = true;
                        persistModelState(activity, offlineModelStatus);
                        destroyTemporaryRecognizer(recognizer);
                        finishModelDownload(activity, true, "");
                    } else if (pending) {
                        offlineModelStatus = "PENDING";
                        persistModelState(activity, offlineModelStatus);
                        destroyTemporaryRecognizer(recognizer);
                        scheduleModelPoll(activity);
                    } else {
                        triggerModelDownload(activity, recognizer, intent);
                    }
                }

                @Override public void onError(int error) {
                    logModelStatus(activity, errorName(error), false, false, false, true);
                    // Samsung/AiAi may not expose authoritative support lists. The actual
                    // API-33 download request remains valid and is verified by later polling.
                    triggerModelDownload(activity, recognizer, intent);
                }
            });
        } catch (Throwable t) {
            Log.w(TAG, "[AndroidSTTModel] support query failed; requesting model", t);
            triggerModelDownload(activity, recognizer, intent);
        }
    }

    private static void triggerModelDownload(Activity activity, SpeechRecognizer recognizer, Intent intent) {
        NetworkDiagnostics network = networkDiagnostics(activity);
        if (!network.validatedInternet) {
            destroyTemporaryRecognizer(recognizer);
            finishModelDownload(activity, false, "NETWORK_NOT_VALIDATED");
            return;
        }
        try {
            recognizer.triggerModelDownload(intent);
            synchronized (LOCK) { modelDownloadRecognizer = recognizer; }
            modelDownloadRequested = true;
            offlineModelStatus = "REQUESTED";
            SharedPreferences prefs = activity.getSharedPreferences(MODEL_PREFS, Context.MODE_PRIVATE);
            String prefix = modelPreferencePrefix(activity);
            prefs.edit().putString(prefix + ".state", offlineModelStatus)
                    .putLong(prefix + ".requested_at", System.currentTimeMillis()).apply();
            Log.i(TAG, "[AndroidSTTModel] event=download_requested language=" + LANGUAGE
                    + " provider=" + onDeviceProvider(activity) + " api=" + Build.VERSION.SDK_INT);
            scheduleModelPoll(activity);
        } catch (Throwable t) {
            offlineModelStatus = "FAILED";
            persistModelState(activity, offlineModelStatus);
            Log.w(TAG, "[AndroidSTTModel] triggerModelDownload failed", t);
            destroyTemporaryRecognizer(recognizer);
            finishModelDownload(activity, false, "TRIGGER_FAILED");
        }
    }

    private static void scheduleModelPoll(Activity activity) {
        synchronized (LOCK) {
            if (Build.VERSION.SDK_INT < 33 || modelPollScheduled) return;
            if (modelPollCount >= MODEL_POLL_LIMIT) {
                finishModelDownload(activity, false, "TIMEOUT");
                return;
            }
            modelPollScheduled = true;
        }
        mainHandler().postDelayed(() -> pollModelSupport(activity), MODEL_POLL_INTERVAL_MS);
    }

    private static void pollModelSupport(Activity activity) {
        synchronized (LOCK) { modelPollScheduled = false; }
        if (Build.VERSION.SDK_INT < 33 || modelPollCount++ >= MODEL_POLL_LIMIT) return;
        final SpeechRecognizer recognizer;
        try {
            recognizer = SpeechRecognizer.createOnDeviceSpeechRecognizer(activity);
            recognizer.setRecognitionListener(new ModelRecognitionListener());
            recognizer.checkRecognitionSupport(recognitionIntent(Mode.OFFLINE, false, false),
                    activity.getMainExecutor(), new RecognitionSupportCallback() {
                @Override public void onSupportResult(RecognitionSupport support) {
                    boolean installed = containsLanguage(support.getInstalledOnDeviceLanguages(), LANGUAGE);
                    boolean pending = containsLanguage(support.getPendingOnDeviceLanguages(), LANGUAGE);
                    boolean supported = containsLanguage(support.getSupportedOnDeviceLanguages(), LANGUAGE);
                    long elapsed = modelDownloadStartedAt > 0L
                            ? System.currentTimeMillis() - modelDownloadStartedAt : 0L;
                    Log.i(TAG, "[AndroidSTTModel] event=status_poll installed=" + installed
                            + " pending=" + pending + " supported=" + supported
                            + " elapsed_ms=" + elapsed);
                    if (installed) {
                        offlineModelStatus = "INSTALLED";
                        trTurkishOnDeviceReady = true;
                        offlineUsable = true;
                        persistModelState(activity, offlineModelStatus);
                    } else {
                        offlineModelStatus = pending ? "PENDING" : "REQUESTED";
                        persistModelState(activity, offlineModelStatus);
                    }
                    logModelStatus(activity, "POLL_" + modelPollCount, installed, pending,
                            supported, !installed && supported);
                    destroyTemporaryRecognizer(recognizer);
                    sendModelEvent(activity, "status_poll", installed, pending, "");
                    if (installed) finishModelDownload(activity, true, "");
                    else scheduleModelPoll(activity);
                }

                @Override public void onError(int error) {
                    logModelStatus(activity, "POLL_" + errorName(error), false, false, false, false);
                    destroyTemporaryRecognizer(recognizer);
                    scheduleModelPoll(activity);
                }
            });
        } catch (Throwable t) {
            Log.w(TAG, "[AndroidSTTModel] poll failed", t);
            scheduleModelPoll(activity);
        }
    }

    private static void finishModelDownload(Activity activity, boolean installed, String reason) {
        SpeechRecognizer retained;
        long duration;
        synchronized (LOCK) {
            retained = modelDownloadRecognizer;
            modelDownloadRecognizer = null;
            modelDownloadInFlight = false;
            modelPollScheduled = false;
            duration = modelDownloadStartedAt > 0L
                    ? System.currentTimeMillis() - modelDownloadStartedAt : 0L;
            modelDownloadStartedAt = 0L;
        }
        destroyTemporaryRecognizer(retained);
        if (installed) {
            offlineModelStatus = "INSTALLED";
            trTurkishOnDeviceReady = true;
            offlineUsable = true;
            if (activity != null) persistModelState(activity, offlineModelStatus);
            Log.i(TAG, "[AndroidSTTModel] event=installed language=" + LANGUAGE
                    + " provider=" + (activity == null ? "" : onDeviceProvider(activity))
                    + " duration_ms=" + duration);
            sendModelEvent(activity, "installed", true, false, "");
        } else {
            offlineModelStatus = "FAILED";
            if (activity != null) persistModelState(activity, offlineModelStatus);
            Log.w(TAG, "[AndroidSTTModel] event=download_failed reason=" + reason);
            sendModelEvent(activity, "download_failed", false, false, reason);
        }
    }

    private static void sendModelEvent(Context context, String event, boolean installed,
                                       boolean pending, String reason) {
        String target;
        String callback;
        synchronized (LOCK) {
            target = modelUnityGameObject;
            callback = modelUnityCallbackMethod;
        }
        if (target.isEmpty() || callback.isEmpty()) return;
        try {
            JSONObject json = new JSONObject();
            json.put("event_name", event);
            json.put("provider", context == null ? "" : onDeviceProvider(context));
            json.put("model_status", offlineModelStatus);
            json.put("installed", installed);
            json.put("pending", pending);
            json.put("reason", reason == null ? "" : reason);
            unityMessage(target, callback, json.toString());
        } catch (Throwable t) {
            Log.w(TAG, "Model event JSON failed", t);
        }
    }

    private static String modelActionJson(boolean ok, String status, boolean network, String error) {
        try {
            JSONObject json = new JSONObject();
            json.put("ok", ok);
            json.put("model_status", status == null ? "UNKNOWN" : status);
            json.put("network_validated", network);
            json.put("error", error == null ? "" : error);
            Activity activity = unityActivity();
            json.put("provider", activity == null ? "" : onDeviceProvider(activity));
            return json.toString();
        } catch (Throwable ignored) {
            return "{\"ok\":false,\"model_status\":\"UNKNOWN\"}";
        }
    }

    private static void destroyTemporaryRecognizer(SpeechRecognizer recognizer) {
        if (recognizer == null) return;
        try { recognizer.setRecognitionListener(null); } catch (Throwable ignored) {}
        try { recognizer.destroy(); } catch (Throwable ignored) {}
    }

    private static String readModelState(Context context) {
        if (context == null) return "UNKNOWN";
        return context.getSharedPreferences(MODEL_PREFS, Context.MODE_PRIVATE)
                .getString(modelPreferencePrefix(context) + ".state", "NOT_REQUESTED");
    }

    private static void persistModelState(Context context, String value) {
        if (context == null) return;
        context.getSharedPreferences(MODEL_PREFS, Context.MODE_PRIVATE).edit()
                .putString(modelPreferencePrefix(context) + ".state", value).apply();
    }

    private static String modelPreferencePrefix(Context context) {
        long version = 0L;
        try {
            version = Build.VERSION.SDK_INT >= 28
                    ? context.getPackageManager().getPackageInfo(context.getPackageName(), 0).getLongVersionCode()
                    : context.getPackageManager().getPackageInfo(context.getPackageName(), 0).versionCode;
        } catch (Throwable ignored) {}
        return (onDeviceProvider(context) + "|" + LANGUAGE + "|" + version).replaceAll("[^A-Za-z0-9_.|-]", "_");
    }

    private static String onDeviceProvider(Context context) {
        String service = recognizerService(context, Mode.OFFLINE);
        int slash = service.indexOf('/');
        return slash > 0 ? service.substring(0, slash) : service;
    }

    private static void logModelStatus(Context context, String status, boolean installed,
                                       boolean pending, boolean supported, boolean downloadRequired) {
        NetworkDiagnostics network = networkDiagnostics(context);
        Log.i(TAG, "[AndroidSTTModel] language=" + LANGUAGE
                + " provider=" + onDeviceProvider(context)
                + " api=" + Build.VERSION.SDK_INT
                + " support_status=" + status
                + " installed=" + installed
                + " pending=" + pending
                + " supported=" + supported
                + " download_required=" + downloadRequired
                + " download_requested=" + modelDownloadRequested
                + " network_validated=" + network.validated);
    }

    private static final class ModelRecognitionListener implements RecognitionListener {
        @Override public void onReadyForSpeech(Bundle params) {}
        @Override public void onBeginningOfSpeech() {}
        @Override public void onRmsChanged(float rmsdB) {}
        @Override public void onBufferReceived(byte[] buffer) {}
        @Override public void onEndOfSpeech() {}
        @Override public void onError(int error) {}
        @Override public void onResults(Bundle results) {}
        @Override public void onPartialResults(Bundle partialResults) {}
        @Override public void onEvent(int eventType, Bundle params) {}
    }

    private static Handler mainHandler() {
        return new Handler(Looper.getMainLooper());
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
        if (session == null || session.finalSent || session.terminalDelivered) return;
        session.finalSent = true;
        session.terminalDelivered = true;
        state = State.ERROR;
        sendEvent(session, "ERROR", null, code, message, state);
        resetTerminalSession(session);
    }

    private static void sendImmediateError(String id, String requested, String target, String callback,
                                           int code, String message) {
        Session session = new Session(id, parseMode(requested), parseMode(requested), target, callback);
        session.service = "";
        sendEvent(session, "ERROR", null, code, message, State.ERROR);
    }

    private static void resetTerminalSession(Session session) {
        session.activeRecognizerGeneration++;
        releaseRecognizer(session, false);
        synchronized (LOCK) {
            if (activeSession == session) activeSession = null;
            state = State.IDLE;
        }
    }

    private static void releaseRecognizer(Session session, boolean cancelFirst) {
        if (session == null) return;
        SpeechRecognizer recognizer = session.recognizer;
        if (recognizer != null) {
            if (cancelFirst) {
                try { recognizer.cancel(); } catch (Throwable t) { Log.w(TAG, "Recognizer cancel failed", t); }
            }
            try { recognizer.setRecognitionListener(null); } catch (Throwable ignored) {}
            try { recognizer.destroy(); } catch (Throwable t) { Log.w(TAG, "Recognizer destroy failed", t); }
        }
        session.recognizer = null;
    }

    private static void refreshRecognizerAvailability(Context context) {
        if (context == null) {
            standardServiceAvailable = false;
            onDeviceServiceAvailable = false;
            onlineUsable = false;
            offlineUsable = false;
            return;
        }
        try {
            standardServiceAvailable = SpeechRecognizer.isRecognitionAvailable(context);
        } catch (Throwable ignored) {
            standardServiceAvailable = false;
        }
        onlineUsable = standardServiceAvailable;
        refreshOnDeviceAvailability(context);
    }

    private static boolean refreshOnDeviceAvailability(Context context) {
        boolean available = false;
        if (context != null && Build.VERSION.SDK_INT >= 31) {
            try {
                available = SpeechRecognizer.isOnDeviceRecognitionAvailable(context);
            } catch (Throwable ignored) {}
        }
        onDeviceServiceAvailable = available;
        offlineUsable = available;
        return available;
    }

    private static String usedModeLogName(Mode mode) {
        return mode == Mode.OFFLINE ? "ON_DEVICE" : mode.name();
    }

    private static void logLifecycleCallback(Session session, String callback) {
        Log.i(TAG, String.format(Locale.US,
                "[AndroidSTT] session_id=%s requested_mode=%s used_mode=%s retry_reason=%s "
                        + "on_device_available=%s retry_count=%d recognizer_recreated=%s "
                        + "language=%s callback=%s",
                session.id, session.requested, usedModeLogName(session.used), session.retryReason,
                onDeviceServiceAvailable, session.retriedOnDevice ? 1 : 0, session.attempt > 1,
                LANGUAGE, callback));
    }

    private static String sessionError(Session session, int error, boolean onDeviceAvailable) {
        if (session.used == Mode.ONLINE && isNetworkError(error) && !onDeviceAvailable) {
            return "İnternet bağlantısı yok ve cihazda çevrimdışı tanıma kullanılamıyor.";
        }
        if (session.used == Mode.OFFLINE && error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE) {
            NetworkDiagnostics network = networkDiagnostics(unityActivity());
            if ("UNAVAILABLE".equals(network.state) || "false".equals(network.validated)) {
                return "Türkçe çevrimdışı modeli hazırlamak için internet bağlantısını geçici olarak açın.";
            }
            return "Türkçe çevrimdışı model seçili konuşma servisinde kullanılamıyor.";
        }
        return userError(error);
    }

    private static boolean matches(Session session, String id) {
        return session != null && session.id.equals(id == null ? "" : id);
    }
    private static boolean isTerminal(State value) {
        return value == State.COMPLETED || value == State.CANCELLED || value == State.ERROR || value == State.DESTROYED;
    }
    private static boolean isNetworkError(int error) {
        return error == SpeechRecognizer.ERROR_NETWORK
                || error == SpeechRecognizer.ERROR_NETWORK_TIMEOUT
                || error == SpeechRecognizer.ERROR_SERVER_DISCONNECTED;
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
            try { Log.e(TAG, "Unity Activity unavailable", t); } catch (Throwable ignored) {}
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
        boolean retryStarted;
        boolean networkErrorSeen;
        boolean networkKnownUnavailable;
        boolean finalSent;
        boolean terminalDelivered;
        int activeRecognizerGeneration;
        String retryReason = "NONE";
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
