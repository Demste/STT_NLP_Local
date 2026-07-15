# -*- coding: utf-8 -*-
"""Action-specific normalization and sparse semantic validation."""

from __future__ import annotations

import math
from typing import Any, Dict, List

from .schema import ACTION_FIELDS, ACTION_IDS, DEFAULT_COMMAND_SCHEMA, PUBLIC_FIELDS, allowed_fields, load_config

NUMERIC_FIELDS = ("distance_m", "duration_s", "duration_min", "angle_deg", "speed_kmh", "marker_index")
ENUM_FIELDS = {
    "direction": "direction_values", "turn_direction": "turn_direction_values",
    "object": "object_values", "color": "color_values", "shape": "shape_values",
    "observe_mode": "observe_mode_values",
}
MISSING_MESSAGES = {
    ("MOVE_DIRECTION", "direction"): "Yatay hareket komutu için yön bilgisi eksik.",
    ("MOVE_DIRECTION", "distance_m"): "Yatay hareket komutu için metre bilgisi eksik.",
    ("MOVE_VERTICAL", "direction"): "Dikey hareket komutu için yön bilgisi eksik.",
    ("MOVE_VERTICAL", "distance_m"): "Dikey hareket komutu için metre bilgisi eksik.",
    ("SET_SPEED", "speed_kmh"): "Hız komutu için km/h bilgisi eksik.",
    ("TURN", "turn_direction"): "Dönüş komutu için yön bilgisi eksik.",
    ("TURN", "angle_deg"): "Dönüş komutu için açı bilgisi eksik.",
    ("FIND_OBJECT", "object"): "Nesne bulma komutu için hedef nesne bilgisi eksik.",
    ("TRACK_OBJECT", "object"): "Takip komutu için hedef nesne bilgisi eksik.",
    ("HOLD_OVER_OBJECT", "object"): "Nesne üzerinde durma komutu için hedef nesne bilgisi eksik.",
    ("GO_TO_LOCATION", "location_keyword"): "Konuma gitme komutu için hedef konum bilgisi eksik.",
    ("GO_TO_LOCATION", "coordinate"): "Konuma gitme komutu için geçerli koordinat bilgisi eksik.",
    ("FIND_MARKER", "color"): "İşaret bulma komutu için renk bilgisi eksik.",
    ("FIND_MARKER", "shape"): "İşaret bulma komutu için şekil bilgisi eksik.",
}


def validate_command(command: Dict[str, Any], context_json: str = "", diagnostics: Dict[str, List[str]] = None) -> Dict[str, Any]:
    """Build final JSON from an already evidence-backed extractor result.

    The function never copies arbitrary input keys. It selects the action first,
    then normalizes only that action's allowlisted fields.
    """
    config = load_config(context_json)
    errors: List[str] = []
    warnings: List[str] = []
    source = command if isinstance(command, dict) else {}

    intended_action = str(source.get("action") or "UNKNOWN").strip().upper()
    if intended_action not in ACTION_IDS:
        errors.append("action_not_allowed")
        intended_action = "UNKNOWN"

    target_action = str(source.get("target_action") or "").strip().upper()
    if intended_action == "MISSING_INFO" and target_action not in ACTION_FIELDS:
        errors.append("missing_target_action_invalid")
        intended_action = "UNKNOWN"
        target_action = ""

    result: Dict[str, Any] = {"action": intended_action}
    semantic_action = target_action if intended_action == "MISSING_INFO" else intended_action
    permitted = allowed_fields(intended_action, target_action, include_metadata=False)

    for key in source:
        if key not in permitted and key not in ("resolution_source",):
            warnings.append("field_removed:not_allowed_for_action:" + key)

    for field, config_key in ENUM_FIELDS.items():
        if field not in permitted or source.get(field) is None:
            continue
        normalized = str(source[field]).strip().upper()
        if normalized in config.get(config_key, DEFAULT_COMMAND_SCHEMA[config_key]):
            result[field] = normalized
        else:
            warnings.append(field + "_invalid")

    for field in NUMERIC_FIELDS:
        if field not in permitted or source.get(field) is None:
            continue
        value = _number(source.get(field), integer=(field == "marker_index"))
        if value is not None and value >= 0:
            result[field] = value
        else:
            warnings.append(field + "_invalid")

    if "location_keyword" in permitted:
        value = _clean_string(source.get("location_keyword"))
        if value is not None:
            result["location_keyword"] = value
    if "coordinate" in permitted and source.get("coordinate") is not None:
        value = _coordinate(source.get("coordinate"), warnings)
        if value is not None:
            result["coordinate"] = value

    if result.get("duration_s") is not None and result.get("duration_min") is not None:
        result.pop("duration_min", None)
        warnings.append("duration_min_removed_when_duration_s_present")

    confidence = _number(source.get("confidence"))
    confidence = max(0.0, min(1.0, confidence if confidence is not None else 0.0))
    if intended_action == "UNKNOWN":
        confidence = 0.0

    if intended_action not in ("UNKNOWN", "MISSING_INFO"):
        missing = _first_missing(intended_action, result, source)
        if missing:
            result["action"] = "MISSING_INFO"
            result["target_action"] = intended_action
            result["missing_field"] = missing
            result["missing_message"] = MISSING_MESSAGES.get((intended_action, missing), "Komut için zorunlu bilgi eksik.")
            warnings.append("required_field_missing:" + missing)
    elif intended_action == "MISSING_INFO":
        result["target_action"] = target_action
        missing = _first_missing(target_action, result, source)
        missing = _clean_string(source.get("missing_field")) or missing
        if missing:
            result["missing_field"] = missing
            result["missing_message"] = MISSING_MESSAGES.get((target_action, missing), "Komut için zorunlu bilgi eksik.")

    # Voice/STT fields are accepted only from explicit upstream context, never
    # from the command or LLM payload.
    context = _context_values(context_json)
    for field, config_key in (("voice_type", "voice_type_values"), ("stt_confidence_level", "stt_confidence_level_values")):
        raw = context.get(field)
        normalized = str(raw or "").strip().upper()
        if normalized in config[config_key]:
            result[field] = normalized
        elif raw is not None:
            warnings.append(field + "_invalid_upstream_metadata")

    result["confidence"] = confidence
    if diagnostics is not None:
        diagnostics["errors"] = errors
        diagnostics["warnings"] = warnings
    return result


def materialize_public_command(command: Dict[str, Any]) -> Dict[str, Any]:
    """Convert sparse validated data to the fixed public Unity/downstream shape."""
    source = command if isinstance(command, dict) else {}
    result = {field: source.get(field) for field in PUBLIC_FIELDS}
    result["action"] = str(source.get("action") or "UNKNOWN")
    confidence = _number(source.get("confidence"))
    result["confidence"] = max(0.0, min(1.0, confidence if confidence is not None else 0.0))
    return result


def _first_missing(action: str, result: Dict[str, Any], source: Dict[str, Any]) -> str:
    if action == "GO_TO_LOCATION":
        if result.get("location_keyword") is None and result.get("coordinate") is None:
            return "coordinate" if source.get("coordinate") is not None else "location_keyword"
        return ""
    for field in DEFAULT_COMMAND_SCHEMA["required_fields_by_action"].get(action, []):
        if result.get(field) is None:
            return field
    return ""


def _number(value: Any, integer: bool = False):
    if isinstance(value, bool) or value is None:
        return None
    try:
        number = float(str(value).strip().replace(",", "."))
        if not math.isfinite(number):
            return None
        if integer:
            return int(number) if number.is_integer() else None
        return int(number) if number.is_integer() else number
    except Exception:
        return None


def _coordinate(value: Any, warnings: List[str]):
    if not isinstance(value, dict):
        warnings.append("coordinate_invalid")
        return None
    lat, lon = _number(value.get("lat")), _number(value.get("lon"))
    if lat is None or lon is None or not (-90 <= lat <= 90) or not (-180 <= lon <= 180):
        warnings.append("coordinate_invalid")
        return None
    return {"lat": lat, "lon": lon}


def _clean_string(value: Any):
    return value.strip() if isinstance(value, str) and value.strip() else None


def _context_values(context_json: str) -> Dict[str, Any]:
    import json
    try:
        value = json.loads(context_json or "{}")
        return value if isinstance(value, dict) else {}
    except Exception:
        return {}
