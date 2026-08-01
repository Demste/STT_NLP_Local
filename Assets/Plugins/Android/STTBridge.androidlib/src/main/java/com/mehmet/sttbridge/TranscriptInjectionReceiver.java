package com.mehmet.sttbridge;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import java.lang.reflect.Method;

/** ADB-shell-only adapter for the existing Unity production transcript test entry. */
public final class TranscriptInjectionReceiver extends BroadcastReceiver {
    private static final String TAG = "TranscriptInjection";

    @Override
    public void onReceive(Context context, Intent intent) {
        String transcript = intent != null ? intent.getStringExtra("transcript") : null;
        if (transcript == null || transcript.trim().isEmpty()) {
            Log.w(TAG, "Ignored empty transcript injection.");
            return;
        }
        try {
            Class<?> unityPlayer = Class.forName("com.unity3d.player.UnityPlayer");
            Method sendMessage = unityPlayer.getMethod(
                    "UnitySendMessage", String.class, String.class, String.class);
            sendMessage.invoke(null, "Panel_Background", "ProcessTranscriptFromExternal", transcript);
            Log.i(TAG, "Forwarded transcript to production pipeline chars=" + transcript.length());
        } catch (Throwable t) {
            Log.e(TAG, "Unity transcript forwarding failed.", t);
        }
    }
}
