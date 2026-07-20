package com.mehmet.sttbridge;

import android.speech.SpeechRecognizer;

import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public final class AndroidSpeechRecognizerBridgeTest {
    @Test public void onlineAlwaysUsesStandardRecognizer() {
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "ONLINE", false, true, 33, true, true));
    }

    @Test public void offlineUsesOnlyReadyOnDeviceRecognizer() {
        assertEquals("OFFLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "OFFLINE", true, true, 33, true, true));
        assertEquals("", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "OFFLINE", true, true, 33, true, false));
    }

    @Test public void offlineBeforeApi31IsUnsupported() {
        assertEquals("", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "OFFLINE", false, true, 30, true, true));
    }

    @Test public void autoUsesOnlineWithValidatedInternet() {
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "AUTO", true, true, 33, true, true));
    }

    @Test public void autoUsesOnlineWithoutValidatedInternetWhenStandardServiceExists() {
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "AUTO", false, true, 33, true, true));
    }

    @Test public void autoUsesOnlineWhenNetworkUnknownAndOfflineIsMissing() {
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "AUTO", false, true, 33, true, false));
        assertEquals("STANDARD_SERVICE_AVAILABLE", AndroidSpeechRecognizerBridge.selectionReasonForTesting(
                "AUTO", true, 33, true, false));
    }

    @Test public void autoNetworkFailureRetriesOnDeviceOnlyOnce() {
        assertTrue(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "AUTO", "ONLINE", false, true, SpeechRecognizer.ERROR_NETWORK));
        assertFalse(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "AUTO", "ONLINE", true, true, SpeechRecognizer.ERROR_NETWORK));
        assertFalse(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "ONLINE", "ONLINE", false, true, SpeechRecognizer.ERROR_NETWORK));
        assertFalse(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "AUTO", "ONLINE", false, false, SpeechRecognizer.ERROR_NETWORK));
    }

    @Test public void modelDownloadApiBranchAndProcessSpamPolicyAreCorrect() {
        long now = 1_000_000_000L;
        assertEquals("LEGACY_TRIGGER", AndroidSpeechRecognizerBridge.downloadMethodForTesting(33));
        assertEquals("LISTENER_TRIGGER", AndroidSpeechRecognizerBridge.downloadMethodForTesting(34));
        assertEquals("UNSUPPORTED", AndroidSpeechRecognizerBridge.downloadMethodForTesting(32));
        assertTrue(AndroidSpeechRecognizerBridge.shouldStartModelDownloadForTesting(
                33, false, false, "NONE", 0L, now, false));
        assertFalse(AndroidSpeechRecognizerBridge.shouldStartModelDownloadForTesting(
                33, false, true, "REQUESTED", now, now, false));
        assertFalse(AndroidSpeechRecognizerBridge.shouldStartModelDownloadForTesting(
                32, false, false, "NONE", 0L, now, false));
        assertFalse(AndroidSpeechRecognizerBridge.shouldStartModelDownloadForTesting(
                33, true, false, "NONE", 0L, now, false));
    }

    @Test public void persistedDownloadRetryPolicyUsesCooldownAndForcedRetry() {
        long now = 2_000_000_000L;
        assertFalse(AndroidSpeechRecognizerBridge.shouldStartModelDownloadForTesting(
                33, false, false, "REQUESTED", now - 1000L, now, false));
        assertFalse(AndroidSpeechRecognizerBridge.shouldStartModelDownloadForTesting(
                33, false, false, "FAILED", now - 1000L, now, false));
        assertTrue(AndroidSpeechRecognizerBridge.shouldStartModelDownloadForTesting(
                33, false, false, "FAILED", now - 7L * 60L * 60L * 1000L, now, false));
        assertTrue(AndroidSpeechRecognizerBridge.shouldStartModelDownloadForTesting(
                33, false, true, "REQUESTED", now, now, true));
    }

    @Test public void downloadSuccessAndPendingStatesRequestSupportRecheck() {
        assertTrue(AndroidSpeechRecognizerBridge.shouldRecheckAfterDownloadStatusForTesting("SUCCESS"));
        assertTrue(AndroidSpeechRecognizerBridge.shouldRecheckAfterDownloadStatusForTesting("REQUESTED"));
        assertTrue(AndroidSpeechRecognizerBridge.shouldRecheckAfterDownloadStatusForTesting("SCHEDULED"));
        assertFalse(AndroidSpeechRecognizerBridge.shouldRecheckAfterDownloadStatusForTesting("FAILED"));
    }

    @Test public void networkAndBusyErrorsHaveUserSafeMessages() {
        assertEquals("Ağ bağlantısı yok veya zaman aşımına uğradı.",
                AndroidSpeechRecognizerBridge.userErrorForTesting(SpeechRecognizer.ERROR_NETWORK));
        assertEquals("Tanıma servisi meşgul, tekrar deneyin.",
                AndroidSpeechRecognizerBridge.userErrorForTesting(SpeechRecognizer.ERROR_RECOGNIZER_BUSY));
    }

    @Test public void cannotCheckSupportKeepsExistingOnlineServiceUsable() {
        assertEquals("CHECK_UNAVAILABLE", AndroidSpeechRecognizerBridge.supportStatusForErrorForTesting(
                SpeechRecognizer.ERROR_CANNOT_CHECK_SUPPORT));
        assertTrue(AndroidSpeechRecognizerBridge.onlineUsableAfterSupportErrorForTesting(
                true, SpeechRecognizer.ERROR_CANNOT_CHECK_SUPPORT));
    }

    @Test public void cannotCheckSupportAllowsAutoAndOnlineWarmup() {
        assertTrue(AndroidSpeechRecognizerBridge.warmupSuccessForTesting(
                "AUTO", true, true, 33, true, false));
        assertTrue(AndroidSpeechRecognizerBridge.warmupSuccessForTesting(
                "ONLINE", true, true, 33, true, false));
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "AUTO", true, true, 33, true, false));
    }

    @Test public void cannotCheckFormattingDisablesOnlyOptionalExtras() {
        assertFalse(AndroidSpeechRecognizerBridge.optionalExtrasEnabledAfterSupportErrorForTesting(
                SpeechRecognizer.ERROR_CANNOT_CHECK_SUPPORT));
        assertTrue(AndroidSpeechRecognizerBridge.onlineUsableAfterSupportErrorForTesting(
                true, SpeechRecognizer.ERROR_CANNOT_CHECK_SUPPORT));
    }

    @Test public void offlineModelMissingDoesNotBlockOnlineButBlocksExplicitOffline() {
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "AUTO", true, true, 33, true, false));
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "ONLINE", true, true, 33, true, false));
        assertEquals("", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "OFFLINE", true, true, 33, true, false));
    }

    @Test public void languageErrorsRemainAuthoritativeAndDistinct() {
        assertEquals("UNSUPPORTED", AndroidSpeechRecognizerBridge.supportStatusForErrorForTesting(
                SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED));
        assertEquals("UNAVAILABLE", AndroidSpeechRecognizerBridge.supportStatusForErrorForTesting(
                SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE));
        assertEquals("Türkçe konuşma tanıma bu serviste desteklenmiyor.",
                AndroidSpeechRecognizerBridge.userErrorForTesting(SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED));
        assertEquals("Türkçe konuşma modeli bu serviste hazır değil.",
                AndroidSpeechRecognizerBridge.userErrorForTesting(SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE));
    }

    @Test public void missingRecognitionServiceFailsWarmup() {
        assertFalse(AndroidSpeechRecognizerBridge.warmupSuccessForTesting(
                "AUTO", true, false, 33, false, false));
        assertFalse(AndroidSpeechRecognizerBridge.warmupSuccessForTesting(
                "ONLINE", true, false, 33, false, false));
    }
}
