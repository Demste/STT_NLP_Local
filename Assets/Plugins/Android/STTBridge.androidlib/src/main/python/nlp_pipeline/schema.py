# -*- coding: utf-8 -*-
"""Sparse tagged-union contract for Turkish drone commands."""

from __future__ import annotations

import copy
import json
from typing import Any, Dict, List, Set


ACTION_IDS = [
    "TAKE_OFF", "LAND_HOME", "LAND_CURRENT", "LAND_AT_WAIT_POINT",
    "MOVE_DIRECTION", "MOVE_VERTICAL", "SET_SPEED", "TURN", "FIND_OBJECT",
    "TRACK_OBJECT", "HOLD_OVER_OBJECT", "OBSERVE_CURRENT", "GO_TO_LOCATION",
    "HOVER", "FIND_MARKER", "STOP", "MISSING_INFO", "UNKNOWN",
]

CLASSIFIABLE_ACTION_IDS = [value for value in ACTION_IDS if value != "MISSING_INFO"]
DEFAULT_ALLOWED_ACTIONS: List[Dict[str, Any]] = [{"id": value} for value in ACTION_IDS]

# This is the canonical Python contract. Each action is serialized only with the
# fields listed here; absent values and unrelated fields never enter final JSON.
ACTION_FIELDS: Dict[str, Set[str]] = {
    "TAKE_OFF": set(),
    "LAND_HOME": set(),
    "LAND_CURRENT": set(),
    "LAND_AT_WAIT_POINT": set(),
    "MOVE_DIRECTION": {"direction", "distance_m"},
    "MOVE_VERTICAL": {"direction", "distance_m"},
    "SET_SPEED": {"speed_kmh"},
    "TURN": {"turn_direction", "angle_deg"},
    "FIND_OBJECT": {"object"},
    "TRACK_OBJECT": {"object"},
    "HOLD_OVER_OBJECT": {"object"},
    "OBSERVE_CURRENT": {"observe_mode", "duration_s", "duration_min"},
    "GO_TO_LOCATION": {"location_keyword", "coordinate"},
    "HOVER": {"duration_s", "duration_min"},
    "FIND_MARKER": {"color", "shape", "marker_index"},
    "STOP": set(),
    "UNKNOWN": set(),
}

REQUIRED_FIELDS = {
    "MOVE_DIRECTION": ["direction", "distance_m"],
    "MOVE_VERTICAL": ["direction", "distance_m"],
    "SET_SPEED": ["speed_kmh"],
    "TURN": ["turn_direction", "angle_deg"],
    "FIND_OBJECT": ["object"],
    "TRACK_OBJECT": ["object"],
    "HOLD_OVER_OBJECT": ["object"],
    "GO_TO_LOCATION": ["location_keyword|coordinate"],
    "FIND_MARKER": ["color", "shape"],
}

UPSTREAM_METADATA_FIELDS = {"voice_type", "stt_confidence_level"}
BASE_FIELDS = {"action", "confidence"}
MISSING_FIELDS = {"target_action", "missing_field", "missing_message"}
PUBLIC_FIELDS = [
    "action", "direction", "turn_direction", "distance_m", "duration_s", "duration_min",
    "angle_deg", "speed_kmh", "object", "target_action", "color", "shape", "marker_index",
    "location_keyword", "coordinate", "observe_mode", "voice_type", "stt_confidence_level",
    "missing_field", "missing_message", "confidence",
]

DEFAULT_COMMAND_SCHEMA: Dict[str, Any] = {
    "version": 3,
    "allowed_actions": DEFAULT_ALLOWED_ACTIONS,
    "action_fields": {key: sorted(value) for key, value in ACTION_FIELDS.items()},
    "direction_values": ["LEFT", "RIGHT", "FORWARD", "BACKWARD", "UP", "DOWN"],
    "turn_direction_values": ["CLOCKWISE", "COUNTER_CLOCKWISE", "LEFT", "RIGHT"],
    "object_values": ["TRUCK", "PICKUP", "CAR", "EXCAVATOR", "LOADER", "FORKLIFT", "CEMENT_TRUCK", "SEMI_TRUCK", "CRANE"],
    "color_values": ["RED", "GREEN", "BLUE", "WHITE", "BLACK"],
    "shape_values": ["LINE", "SQUARE", "TRIANGLE", "PLUS"],
    "observe_mode_values": ["WATCH", "MONITOR", "HOLD_AND_WATCH"],
    "voice_type_values": ["NORMAL", "WHISTLED", "ENCRYPTED"],
    "stt_confidence_level_values": ["GOOD", "MEDIUM", "BAD"],
    "required_fields_by_action": REQUIRED_FIELDS,
    "unknown_action": "UNKNOWN",
    "missing_action": "MISSING_INFO",
}


def allowed_fields(action: str, target_action: str = "", include_metadata: bool = True) -> Set[str]:
    normalized = str(action or "UNKNOWN").upper()
    if normalized == "MISSING_INFO":
        fields = BASE_FIELDS | MISSING_FIELDS | ACTION_FIELDS.get(str(target_action or "").upper(), set())
    else:
        fields = BASE_FIELDS | ACTION_FIELDS.get(normalized, set())
    if include_metadata:
        fields |= UPSTREAM_METADATA_FIELDS
    return fields


def load_config(context_json: str) -> Dict[str, Any]:
    config = copy.deepcopy(DEFAULT_COMMAND_SCHEMA)
    if context_json:
        try:
            parsed = json.loads(context_json)
            if isinstance(parsed, dict):
                # Runtime settings and trusted upstream metadata may be overridden;
                # an older schema cannot weaken this tagged-union contract.
                for key in ("llm", "voice_type", "stt_confidence_level"):
                    if key in parsed:
                        config[key] = copy.deepcopy(parsed[key])
        except Exception:
            pass
    return config


def allowed_action_ids(config: Dict[str, Any]) -> List[str]:
    values = []
    for action in config.get("allowed_actions", DEFAULT_ALLOWED_ACTIONS):
        value = action.get("id") if isinstance(action, dict) else action
        value = str(value or "").upper()
        if value in ACTION_IDS and value not in values:
            values.append(value)
    for required in ("MISSING_INFO", "UNKNOWN"):
        if required not in values:
            values.append(required)
    return values
