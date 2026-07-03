package com.mehmet.sttbridge;

import android.content.Context;
import android.util.Log;

import com.chaquo.python.PyObject;
import com.chaquo.python.Python;
import com.chaquo.python.android.AndroidPlatform;

import org.json.JSONObject;

import java.io.PrintWriter;
import java.io.StringWriter;

public final class SpacyNlpBridge {
    private static final String TAG = "SpacyNlpBridge";
    private static final String MODULE_NAME = "command_parser";

    private SpacyNlpBridge() {}

    public static String analyzeText(Context context, String text) {
        return analyzeText(context, text, "");
    }

    public static String analyzeText(Context context, String text, String contextJson) {
        try {
            if (context == null) {
                throw new IllegalArgumentException("Context is null.");
            }

            Context appContext = context.getApplicationContext();

            if (!Python.isStarted()) {
                Python.start(new AndroidPlatform(appContext));
            }

            Python python = Python.getInstance();
            PyObject module = python.getModule(MODULE_NAME);
            PyObject result = module.callAttr(
                    "parse_command",
                    text != null ? text : "",
                    contextJson != null ? contextJson : ""
            );

            return result != null ? result.toString() : buildErrorJson("python_returned_null", null);
        } catch (Throwable t) {
            Log.e(TAG, "spaCy NLP bridge failed.", t);
            return buildErrorJson("java_bridge_failed", t);
        }
    }

    private static String buildErrorJson(String stage, Throwable throwable) {
        try {
            JSONObject object = new JSONObject();
            object.put("ok", false);
            object.put("engine", "real_spacy");
            object.put("model", "tr_core_news_md");
            object.put("stage", stage);

            if (throwable != null) {
                object.put("error", throwable.getMessage() != null ? throwable.getMessage() : throwable.toString());
                object.put("exception_type", throwable.getClass().getName());
                object.put("traceback", stackTraceToString(throwable));
            } else {
                object.put("error", stage);
                object.put("exception_type", JSONObject.NULL);
                object.put("traceback", JSONObject.NULL);
            }

            return object.toString();
        } catch (Throwable jsonError) {
            return "{\"ok\":false,\"engine\":\"real_spacy\",\"model\":\"tr_core_news_md\",\"stage\":"
                    + JSONObject.quote(stage)
                    + ",\"error\":"
                    + JSONObject.quote(jsonError.getMessage() != null ? jsonError.getMessage() : jsonError.toString())
                    + "}";
        }
    }

    private static String stackTraceToString(Throwable throwable) {
        StringWriter writer = new StringWriter();
        throwable.printStackTrace(new PrintWriter(writer));
        return writer.toString();
    }
}
