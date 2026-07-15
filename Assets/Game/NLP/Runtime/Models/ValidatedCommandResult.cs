using System;

namespace Game.NLP
{
    public static class DroneCommandContract
    {
        public static readonly string[] PublicFields =
        {
            "action", "direction", "turn_direction", "distance_m", "duration_s", "duration_min",
            "angle_deg", "speed_kmh", "object", "target_action", "color", "shape", "marker_index",
            "location_keyword", "coordinate", "observe_mode", "voice_type", "stt_confidence_level",
            "missing_field", "missing_message", "confidence"
        };

        public static readonly string[] Actions =
        {
            "TAKE_OFF", "LAND_HOME", "LAND_CURRENT", "LAND_AT_WAIT_POINT", "MOVE_DIRECTION",
            "MOVE_VERTICAL", "SET_SPEED", "TURN", "FIND_OBJECT", "TRACK_OBJECT", "HOLD_OVER_OBJECT",
            "OBSERVE_CURRENT", "GO_TO_LOCATION", "HOVER", "FIND_MARKER", "STOP", "MISSING_INFO", "UNKNOWN"
        };

        public static readonly string[] Directions = { "LEFT", "RIGHT", "FORWARD", "BACKWARD", "UP", "DOWN" };
        public static readonly string[] TurnDirections = { "CLOCKWISE", "COUNTER_CLOCKWISE", "LEFT", "RIGHT" };
        public static readonly string[] Objects = { "TRUCK", "PICKUP", "CAR", "EXCAVATOR", "LOADER", "FORKLIFT", "CEMENT_TRUCK", "SEMI_TRUCK", "CRANE" };
        public static readonly string[] Colors = { "RED", "GREEN", "BLUE", "WHITE", "BLACK" };
        public static readonly string[] Shapes = { "LINE", "SQUARE", "TRIANGLE", "PLUS" };
        public static readonly string[] ObserveModes = { "WATCH", "MONITOR", "HOLD_AND_WATCH" };
        public static readonly string[] VoiceTypes = { "NORMAL", "WHISTLED", "ENCRYPTED" };
        public static readonly string[] SttConfidenceLevels = { "GOOD", "MEDIUM", "BAD" };
    }

    [Serializable]
    public sealed class ValidatedCommandResult
    {
        public string action;
        public string direction;
        public string turn_direction;
        public double? distance_m;
        public double? duration_s;
        public double? duration_min;
        public double? angle_deg;
        public double? speed_kmh;
        public string @object;
        public string target_action;
        public string color;
        public string shape;
        public long? marker_index;
        public string location_keyword;
        public DroneCoordinate coordinate;
        public string observe_mode;
        public string voice_type;
        public string stt_confidence_level;
        public string missing_field;
        public string missing_message;
        public double confidence;
    }

    [Serializable]
    public sealed class DroneCoordinate
    {
        public double lat;
        public double lon;
    }
}
