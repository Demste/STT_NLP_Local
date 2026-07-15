using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Game.NLP
{
    public static class DroneCommandJsonParser
    {
        public static ValidatedCommandResult ParseObjectForKey(string envelopeJson, string key)
        {
            string json = ExtractObjectForKey(envelopeJson, key);
            if (string.IsNullOrWhiteSpace(json)) return null;

            ValidatedCommandResult result = new ValidatedCommandResult
            {
                action = StringValue(json, "action"),
                direction = StringValue(json, "direction"),
                turn_direction = StringValue(json, "turn_direction"),
                distance_m = NumberValue(json, "distance_m"),
                duration_s = NumberValue(json, "duration_s"),
                duration_min = NumberValue(json, "duration_min"),
                angle_deg = NumberValue(json, "angle_deg"),
                speed_kmh = NumberValue(json, "speed_kmh"),
                @object = StringValue(json, "object"),
                target_action = StringValue(json, "target_action"),
                color = StringValue(json, "color"),
                shape = StringValue(json, "shape"),
                marker_index = IntegerValue(json, "marker_index"),
                location_keyword = StringValue(json, "location_keyword"),
                observe_mode = StringValue(json, "observe_mode"),
                voice_type = StringValue(json, "voice_type"),
                stt_confidence_level = StringValue(json, "stt_confidence_level"),
                missing_field = StringValue(json, "missing_field"),
                missing_message = StringValue(json, "missing_message"),
                confidence = NumberValue(json, "confidence") ?? 0d,
            };

            string coordinateJson = ExtractObjectForKey(json, "coordinate");
            double? lat = NumberValue(coordinateJson, "lat");
            double? lon = NumberValue(coordinateJson, "lon");
            if (lat.HasValue && lon.HasValue)
            {
                result.coordinate = new DroneCoordinate { lat = lat.Value, lon = lon.Value };
            }
            return result;
        }

        public static string ToCompactJson(ValidatedCommandResult value)
        {
            if (value == null) return "null";
            List<string> fields = new List<string>(10);
            Add(fields, "action", value.action);
            Add(fields, "direction", value.direction);
            Add(fields, "turn_direction", value.turn_direction);
            Add(fields, "distance_m", value.distance_m);
            Add(fields, "duration_s", value.duration_s);
            Add(fields, "duration_min", value.duration_min);
            Add(fields, "angle_deg", value.angle_deg);
            Add(fields, "speed_kmh", value.speed_kmh);
            Add(fields, "object", value.@object);
            Add(fields, "target_action", value.target_action);
            Add(fields, "color", value.color);
            Add(fields, "shape", value.shape);
            Add(fields, "marker_index", value.marker_index);
            Add(fields, "location_keyword", value.location_keyword);
            if (value.coordinate != null)
            {
                fields.Add("\"coordinate\":{\"lat\":" + Number(value.coordinate.lat) + ",\"lon\":" + Number(value.coordinate.lon) + "}");
            }
            Add(fields, "observe_mode", value.observe_mode);
            Add(fields, "voice_type", value.voice_type);
            Add(fields, "stt_confidence_level", value.stt_confidence_level);
            Add(fields, "missing_field", value.missing_field);
            Add(fields, "missing_message", value.missing_message);
            fields.Add(Field("confidence", value.confidence));
            return "{" + string.Join(",", fields) + "}";
        }

        public static string ToPublicJson(ValidatedCommandResult value)
        {
            ValidatedCommandResult command = value ?? new ValidatedCommandResult { action = "UNKNOWN", confidence = 0d };
            List<string> fields = new List<string>(21)
            {
                Field("action", string.IsNullOrWhiteSpace(command.action) ? "UNKNOWN" : command.action),
                Field("direction", command.direction),
                Field("turn_direction", command.turn_direction),
                Field("distance_m", command.distance_m),
                Field("duration_s", command.duration_s),
                Field("duration_min", command.duration_min),
                Field("angle_deg", command.angle_deg),
                Field("speed_kmh", command.speed_kmh),
                Field("object", command.@object),
                Field("target_action", command.target_action),
                Field("color", command.color),
                Field("shape", command.shape),
                Field("marker_index", command.marker_index),
                Field("location_keyword", command.location_keyword),
                command.coordinate != null
                    ? "\"coordinate\":{\"lat\":" + Number(command.coordinate.lat) + ",\"lon\":" + Number(command.coordinate.lon) + "}"
                    : "\"coordinate\":null",
                Field("observe_mode", command.observe_mode),
                Field("voice_type", command.voice_type),
                Field("stt_confidence_level", command.stt_confidence_level),
                Field("missing_field", command.missing_field),
                Field("missing_message", command.missing_message),
                Field("confidence", command.confidence)
            };
            return "{" + string.Join(",", fields) + "}";
        }

        public static string RawObjectForKey(string envelopeJson, string key) => ExtractObjectForKey(envelopeJson, key);

        public static string BuildBaselineEnvelope(
            string rawInput,
            string wakeWordSuffix,
            string analysisInput,
            string resolutionStatus,
            bool repairApplied,
            string commandJson,
            long durationMs)
        {
            string internalCommand = string.IsNullOrWhiteSpace(commandJson) ? "{\"action\":\"UNKNOWN\",\"confidence\":0}" : commandJson;
            ValidatedCommandResult parsed = ParseObjectForKey("{\"command\":" + internalCommand + "}", "command");
            string publicCommand = ToPublicJson(parsed);
            return "{\"ok\":true,\"contract_version\":\"action_first_v3_20260713\",\"engine\":\"action_first\",\"stage\":\"baseline_resolved\","
                + "\"initialized\":false,\"inference_ran\":false,\"llm_called\":false,\"resolution_source\":\"baseline\","
                + "\"resolution_status\":\"" + Escape(resolutionStatus ?? string.Empty) + "\",\"action_llm_called\":false,\"slot_llm_called\":false,"
                + "\"llm_output_valid\":true,\"balanced_json\":true,"
                + "\"input\":\"" + Escape(analysisInput ?? string.Empty) + "\",\"raw_input\":\"" + Escape(rawInput ?? string.Empty) + "\","
                + "\"wake_word_suffix\":\"" + Escape(wakeWordSuffix ?? string.Empty) + "\",\"analysis_input\":\"" + Escape(analysisInput ?? string.Empty) + "\","
                + "\"repair_applied\":" + (repairApplied ? "true" : "false") + ",\"llm_raw\":\"\","
                + "\"internal_validated_command\":" + internalCommand + ",\"public_command\":" + publicCommand + ","
                + "\"normalized_command\":" + publicCommand + ",\"validated_command\":" + publicCommand + ","
                + "\"errors\":[],\"warnings\":[],\"durationMs\":" + durationMs.ToString(CultureInfo.InvariantCulture)
                + ",\"total_duration_ms\":" + durationMs.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static string BuildBaselineEnvelope(string input, string commandJson, long durationMs) =>
            BuildBaselineEnvelope(input, input, input, "RESOLVED", false, commandJson, durationMs);

        private static string ExtractObjectForKey(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json)) return string.Empty;
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\{");
            if (!match.Success) return string.Empty;
            int start = json.IndexOf('{', match.Index + match.Length - 1);
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return json.Substring(start, i - start + 1);
            }
            return string.Empty;
        }

        private static string StringValue(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*(null|\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\")");
            return !match.Success || match.Groups[1].Value == "null" ? null : Regex.Unescape(match.Groups["v"].Value);
        }

        private static double? NumberValue(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*(?<v>null|-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)");
            if (!match.Success || match.Groups["v"].Value == "null") return null;
            return double.TryParse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : (double?)null;
        }

        private static long? IntegerValue(string json, string key)
        {
            double? value = NumberValue(json, key);
            return value.HasValue ? Convert.ToInt64(value.Value) : (long?)null;
        }

        private static string Field(string key, string value) => "\"" + key + "\":" + (value == null ? "null" : "\"" + Escape(value) + "\"");
        private static string Field(string key, double? value) => "\"" + key + "\":" + (value.HasValue ? Number(value.Value) : "null");
        private static string Field(string key, long? value) => "\"" + key + "\":" + (value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
        private static void Add(List<string> fields, string key, string value) { if (!string.IsNullOrWhiteSpace(value)) fields.Add(Field(key, value)); }
        private static void Add(List<string> fields, string key, double? value) { if (value.HasValue) fields.Add(Field(key, value)); }
        private static void Add(List<string> fields, string key, long? value) { if (value.HasValue) fields.Add(Field(key, value)); }
        private static string Number(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
