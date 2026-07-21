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

    @Test public void offlineUsesAvailableOnDeviceRecognizerEvenWhenSupportListIsStale() {
        assertEquals("ON_DEVICE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "OFFLINE", true, true, 33, true, true));
        assertEquals("ON_DEVICE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
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

    @Test public void autoUsesOnDeviceWhenNetworkIsKnownUnavailable() {
        assertEquals("ON_DEVICE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "AUTO", false, true, 33, true, true));
        assertEquals("NETWORK_UNAVAILABLE_ON_DEVICE_AVAILABLE",
                AndroidSpeechRecognizerBridge.selectionReasonForTesting(
                        "AUTO", "UNAVAILABLE", "false", true, 33, true, true));
    }

    @Test public void autoUsesOnlineWhenNetworkUnknown() {
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "AUTO", "UNKNOWN", "unknown", true, 33, true, false));
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
        assertTrue(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "AUTO", "ONLINE", false, true, SpeechRecognizer.ERROR_NETWORK_TIMEOUT));
        assertTrue(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "AUTO", "ONLINE", false, true, SpeechRecognizer.ERROR_SERVER_DISCONNECTED));
        assertFalse(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "AUTO", "ONLINE", false, true, SpeechRecognizer.ERROR_SERVER));
    }

    @Test public void autoLanguageUnavailableRetriesOnlyAfterNetworkErrorWasSeen() {
        assertTrue(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "AUTO", "ONLINE", false, true, true,
                SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE));
        assertFalse(AndroidSpeechRecognizerBridge.shouldRetryOnDeviceForTesting(
                "AUTO", "ONLINE", false, true, false,
                SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE));
    }

    @Test public void networkAndBusyErrorsHaveUserSafeMessages() {
        assertEquals("İnternet bağlantısı yok ve cihazda çevrimdışı tanıma kullanılamıyor.",
                AndroidSpeechRecognizerBridge.sessionErrorForTesting(
                        "ONLINE", SpeechRecognizer.ERROR_NETWORK, false));
        assertEquals("Türkçe çevrimdışı model seçili konuşma servisinde kullanılamıyor.",
                AndroidSpeechRecognizerBridge.sessionErrorForTesting(
                        "OFFLINE", SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE, true));
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

    @Test public void recognitionSupportModelListDoesNotBlockRealOnDeviceStart() {
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "AUTO", true, true, 33, true, false));
        assertEquals("ONLINE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
                "ONLINE", true, true, 33, true, false));
        assertEquals("ON_DEVICE", AndroidSpeechRecognizerBridge.selectModeNameForTesting(
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
