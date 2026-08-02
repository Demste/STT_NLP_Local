# -*- coding: utf-8 -*-
"""Deterministic action-first Turkish drone-command parser."""

from __future__ import annotations

import re
import time
from typing import Any, Dict, List, Optional, Tuple

from .command_catalog import (
    COLOR_ALIASES,
    NUMBER_WORDS,
    OBJECT_ALIASES,
    RECOVERY_AUTO_APPLY_GAP,
    RECOVERY_AUTO_APPLY_SCORE,
    RECOVERY_CONFIRM_SCORE,
    RECOVERY_MAX_SUGGESTIONS,
    SHAPE_ALIASES,
)
from .command_recovery import build_recovery_candidates, make_suggestion
from .command_text_repair import build_repair_candidates
from .command_validation import validate_command
from .semantic_routing import build_semantic_frame
from .text_normalization import normalize_turkish_text

OBJECTS = OBJECT_ALIASES
COLORS = COLOR_ALIASES
SHAPES = SHAPE_ALIASES
NUMBER_WORD_PATTERN = "(?:" + "|".join(sorted(NUMBER_WORDS, key=len, reverse=True)) + "|yuz)"


def build_baseline_hints(analysis: Dict[str, Any], original_text: str = "", request_key: str = "") -> Dict[str, Any]:
    text = original_text or " ".join(str(token.get("text", "")) for token in analysis.get("tokens", []))
    command, metrics = parse_drone_command_with_diagnostics(text, request_key=request_key)
    intended_action = command.get("target_action") if command.get("action") == "MISSING_INFO" else command.get("action")
    resolution_status = metrics["resolution_status"]
    final_parser = (
        resolution_status in ("RESOLVED", "MISSING_USER_INFO")
        or (resolution_status == "OUT_OF_DOMAIN" and (
            metrics.get("out_of_domain_final")
            or metrics.get("safety_blocked")
        ))
    )
    return {
        "final_parser": final_parser,
        "resolution_status": resolution_status,
        "domain": "turkish_drone_command",
        "resolution_source": "baseline" if final_parser else "pending_llm",
        "llm_called": False,
        "command_candidate": command,
        "detected_object_phrase": metrics.get("detected_object_phrase", ""),
        "action_candidates": [intended_action] if intended_action and intended_action != "UNKNOWN" else [],
        "direction_candidates": _one(command.get("direction")),
        "turn_direction_candidates": _one(command.get("turn_direction")),
        "distance_candidates": _one(command.get("distance_m")),
        "duration_candidates": _one(command.get("duration_s") if command.get("duration_s") is not None else command.get("duration_min")),
        "angle_candidates": _one(command.get("angle_deg")),
        "speed_candidates": _one(command.get("speed_kmh")),
        "object_candidates": _one(command.get("object")),
        "color_candidates": _one(command.get("color")),
        "shape_candidates": _one(command.get("shape")),
        "marker_index_candidates": _one(command.get("marker_index")),
        "location_candidates": _one(command.get("location_keyword")),
        "coordinate_candidates": _one(command.get("coordinate")),
        "observe_mode_candidates": _one(command.get("observe_mode")),
        "action_evidence": metrics.get("action_evidence", _empty_action_evidence()),
        "evidence_supported_actions": metrics.get("evidence_supported_actions", []),
        **metrics,
    }


def parse_drone_command_with_diagnostics(text: str, request_key: str = "") -> Tuple[Dict[str, Any], Dict[str, Any]]:
    started = time.perf_counter()
    raw = text or ""
    repair = build_repair_candidates(raw)
    parsed: List[Tuple[str, Dict[str, Any], Dict[str, Any]]] = []
    for candidate in repair["repair_candidates"]:
        command, candidate_metrics = _parse_candidate(candidate)
        parsed.append((candidate, command, candidate_metrics))

    selected_text, command, selected_metrics = max(
        parsed,
        key=lambda item: (_resolution_score(item[2]["resolution_status"]), 1 if item[1].get("object") else 0),
    )
    original_text = repair["repair_candidates"][0] if repair["repair_candidates"] else raw
    original_suffix = _strip_wake_word(original_text)
    normalized_suffix = _phrase(original_suffix).strip(".,;:!? ")

    recovery = _resolve_recovery(
        original_suffix,
        command,
        selected_metrics,
        request_key,
    )
    if recovery["decision"] == "auto":
        selected_text = recovery["corrected_suffix"]
        command = recovery["command"]
        selected_metrics = recovery["metrics"]
    elif recovery["decision"] == "confirmation":
        command = {"action": "UNKNOWN", "confidence": 0.0}
        selected_metrics["resolution_status"] = "NEEDS_CONFIRMATION"

    selected_metrics.update({
        "raw_input": raw,
        "wake_word_suffix": original_text,
        "analysis_input": selected_text,
        "repair_applied": selected_text != original_text,
        "repair_candidates": repair["repair_candidates"],
        "selected_repair": selected_text if selected_text != original_text else "",
        "repair_notes": repair["repair_notes"] if selected_text != original_text else [],
        "repair_duration_ms": repair["repair_duration_ms"],
        "baseline_duration_ms": _elapsed_ms(started),
        "original_suffix": original_suffix,
        "normalized_suffix": normalized_suffix,
        "corrected_suffix": recovery.get("corrected_suffix", ""),
        "correction_applied": recovery["decision"] == "auto",
        "correction_confidence": recovery.get("confidence", 0.0),
        "correction_reason": recovery.get("reason", "NONE"),
        "candidate_action": recovery.get("candidate_action", ""),
        "candidate_count": recovery.get("candidate_count", 0),
        "candidate_gap": recovery.get("candidate_gap", 0.0),
        "recovery_resolution_status": recovery.get("resolution_status", "NO_RECOVERY"),
        "suggestions": recovery.get("suggestions", []),
        "parser_input": selected_text,
        "parser_source": "deterministic_recovery" if recovery["decision"] == "auto" else "deterministic_original",
    })
    action_evidence = evaluate_action_evidence(original_suffix, command, selected_metrics)
    selected_metrics["action_evidence"] = action_evidence
    selected_metrics["evidence_supported_actions"] = action_evidence["supported_actions"]
    if (command.get("action") == "UNKNOWN"
            and selected_metrics.get("resolution_status") == "NEEDS_ACTION_CLASSIFICATION"
            and not action_evidence["has_any"]):
        selected_metrics["resolution_status"] = "OUT_OF_DOMAIN"
        selected_metrics["recovery_resolution_status"] = "UNKNOWN"
    return command, selected_metrics


def evaluate_action_evidence(text: str, command: Dict[str, Any], metrics: Dict[str, Any]) -> Dict[str, Any]:
    """Return the single authoritative action-evidence decision for downstream gates.

    The LLM classification is deliberately absent from this function. Only the
    transcript, deterministic command/slots and recovery candidates contribute.
    """
    phrase = _phrase(_strip_wake_word(text or "")).strip(".,;:!? ")
    supported = set()
    reasons: List[str] = []

    intended = command.get("target_action") if command.get("action") == "MISSING_INFO" else command.get("action")
    if intended and intended not in ("UNKNOWN", "MISSING_INFO"):
        supported.add(str(intended))
        reasons.append("deterministic_action:" + str(intended))

    recovery_action = str(metrics.get("candidate_action") or "")
    if metrics.get("candidate_count", 0) > 0 and recovery_action and recovery_action != "UNKNOWN":
        supported.add(recovery_action)
        reasons.append("recovery_candidate:" + recovery_action)
    for suggestion in metrics.get("suggestions") or []:
        action = str(suggestion.get("suggested_action") or "")
        if action and action != "UNKNOWN":
            supported.add(action)
            reasons.append("recovery_suggestion:" + action)

    obj = _mapped(phrase, OBJECTS)
    color = _mapped(phrase, COLORS)
    shape = _mapped(phrase, SHAPES)
    has_move_verb = _has_any(phrase, "git", "gec", "ilerle", "hareket et")
    has_turn_verb = _has_any(phrase, "don", "dondur", "cevir")
    has_find_verb = _has_any(phrase, "bul", "ara", "tespit")
    has_track_verb = _has_any(phrase, "takip et", "izle", "pesinden git")

    if has_move_verb and (_direction(phrase) is not None or _quantity(phrase, r"metre|meter|m") is not None):
        _add_evidence(supported, reasons, "MOVE_DIRECTION", "horizontal_movement_phrase")
    elif has_move_verb and not _has_any(phrase, "nokta", "konum", "bolge", "koordinat"):
        _add_evidence(supported, reasons, "MOVE_DIRECTION", "horizontal_movement_verb")

    if has_turn_verb:
        _add_evidence(supported, reasons, "TURN", "turn_phrase")
    if (_vertical_direction(phrase) is not None
            or _has_any(phrase, "yuksel", "alcal", "yukari cik", "asagi in")):
        _add_evidence(supported, reasons, "MOVE_VERTICAL", "vertical_phrase")
    if _speed_quantity(phrase) is not None or _has_any(phrase, "hiz", "hizlan", "yavasla", "km/h", "kilometre saat"):
        _add_evidence(supported, reasons, "SET_SPEED", "speed_phrase")

    if has_find_verb and obj:
        _add_evidence(supported, reasons, "FIND_OBJECT", "object_find_phrase")
    elif _has_any(phrase, "bul", "ara", "tespit et") and not (color or shape or _has_inflected(phrase, "isaret")):
        _add_evidence(supported, reasons, "FIND_OBJECT", "find_verb")
    if has_track_verb:
        _add_evidence(supported, reasons, "TRACK_OBJECT", "track_phrase")
    if obj and _has_any(phrase, "ustunde", "uzerinde", "ustune", "uzerine") and _has_any(phrase, "dur", "bekle", "hover"):
        _add_evidence(supported, reasons, "HOLD_OVER_OBJECT", "object_hold_phrase")
    if (color or shape or _has_inflected(phrase, "isaret") or _has_any(phrase, "marker")) and has_find_verb:
        _add_evidence(supported, reasons, "FIND_MARKER", "marker_find_phrase")

    if ((_coordinate(phrase) is not None or _has_any(phrase, "nokta", "konum", "bolge", "koordinat", "oraya"))
            and has_move_verb):
        _add_evidence(supported, reasons, "GO_TO_LOCATION", "location_navigation_phrase")
    if _has_any(phrase, "havada dur", "havada bekle", "hover", "havada sabit kal"):
        _add_evidence(supported, reasons, "HOVER", "hover_phrase")
    if _has_any(phrase, "bekle ve izle", "dur ve izle", "oldugun yerde izle", "burada izle"):
        _add_evidence(supported, reasons, "OBSERVE_CURRENT", "observe_phrase")
    if _has_any(phrase, "dur", "bekle", "sabit kal", "hareket etme", "hareketi kes"):
        _add_evidence(supported, reasons, "STOP", "stop_phrase")

    if _has_any(phrase, "havalan", "kalk", "ucusa gec", "ucus yap") or phrase == "uc":
        _add_evidence(supported, reasons, "TAKE_OFF", "takeoff_phrase")
    if _has_any(phrase, "bekleme noktasina in"):
        _add_evidence(supported, reasons, "LAND_AT_WAIT_POINT", "wait_point_landing_phrase")
    if _has_any(phrase, "oldugun yere in", "bulundugun yere in", "mevcut konuma in", "buraya in"):
        _add_evidence(supported, reasons, "LAND_CURRENT", "current_location_landing_phrase")
    if (_has_any(phrase, "eve don", "geri don", "baslangic noktasina don", "baslangic noktasina in")
            or re.fullmatch(r"in[.,]*", phrase)):
        _add_evidence(supported, reasons, "LAND_HOME", "home_landing_phrase")

    ordered = sorted(supported)
    return {
        "has_any": bool(ordered),
        "supported_actions": ordered,
        "reasons": reasons,
        "source_text": phrase,
    }


def _add_evidence(supported: set, reasons: List[str], action: str, reason: str) -> None:
    supported.add(action)
    reasons.append(reason + ":" + action)


def _empty_action_evidence() -> Dict[str, Any]:
    return {"has_any": False, "supported_actions": [], "reasons": [], "source_text": ""}


def _resolve_recovery(
    original_suffix: str,
    original_command: Dict[str, Any],
    original_metrics: Dict[str, Any],
    request_key: str,
) -> Dict[str, Any]:
    original_status = original_metrics.get("resolution_status", "NEEDS_ACTION_CLASSIFICATION")
    if original_status == "RESOLVED":
        return _recovery_result("none", "RESOLVED_ORIGINAL")
    if original_status == "OUT_OF_DOMAIN":
        return _recovery_result("none", "UNKNOWN", reason="LOW_CONFIDENCE")

    candidates = build_recovery_candidates(original_suffix, request_key)
    # Missing user data may only use an explicit known/token repair; fuzzy
    # matching cannot fabricate the absent required slot.
    if original_status in ("MISSING_USER_INFO", "NEEDS_SLOT_CLASSIFICATION"):
        candidates = [candidate for candidate in candidates if candidate.get("reason") in (
            "KNOWN_STT_VARIANT", "TOKEN_REPAIR", "NUMBER_UNIT_REPAIR",
        )]

    evaluated = []
    for candidate in candidates:
        corrected = str(candidate.get("corrected_suffix") or "")
        parsed_command, parsed_metrics = _parse_candidate(corrected)
        parsed_status = parsed_metrics.get("resolution_status")
        if parsed_status not in ("RESOLVED", "MISSING_USER_INFO"):
            continue
        action = parsed_command.get("target_action") if parsed_command.get("action") == "MISSING_INFO" else parsed_command.get("action")
        item = dict(candidate)
        item.update({"command": parsed_command, "metrics": parsed_metrics, "candidate_action": action or "UNKNOWN"})
        evaluated.append(item)

    evaluated.sort(key=lambda item: float(item.get("confidence") or 0.0), reverse=True)
    viable = [item for item in evaluated if float(item.get("confidence") or 0.0) >= RECOVERY_CONFIRM_SCORE and not item.get("critical_conflict")]
    if not viable:
        reason = (
            "VALIDATION_FAILED" if candidates and not evaluated
            else "CRITICAL_SLOT_CONFLICT" if any(item.get("critical_conflict") for item in evaluated)
            else "LOW_CONFIDENCE"
        )
        resolution = "UNKNOWN" if original_command.get("action") == "UNKNOWN" else "MISSING_INFO"
        return _recovery_result("none", resolution, reason=reason)

    best = viable[0]
    second_score = float(viable[1].get("confidence") or 0.0) if len(viable) > 1 else 0.0
    gap = round(float(best["confidence"]) - second_score, 4)
    if (float(best["confidence"]) >= RECOVERY_AUTO_APPLY_SCORE
            and gap >= RECOVERY_AUTO_APPLY_GAP
            and not best.get("critical_conflict")):
        return _recovery_result(
            "auto",
            "RESOLVED_AUTO_CORRECTION" if best["command"].get("action") != "MISSING_INFO" else "MISSING_INFO",
            best=best,
            candidate_count=len(viable),
            candidate_gap=gap,
        )

    unique_actions = []
    for item in viable:
        if item["candidate_action"] not in unique_actions:
            unique_actions.append(item["candidate_action"])
    suggestion_items = []
    for action in unique_actions[:RECOVERY_MAX_SUGGESTIONS]:
        item = next(candidate for candidate in viable if candidate["candidate_action"] == action)
        suggestion_items.append(make_suggestion(item, request_key, multiple=len(unique_actions) > 1))
    return _recovery_result(
        "confirmation",
        "NEEDS_CONFIRMATION",
        best=best,
        candidate_count=len(viable),
        candidate_gap=gap,
        suggestions=suggestion_items,
        reason="MULTIPLE_CLOSE_CANDIDATES" if len(unique_actions) > 1 else best.get("reason", "FUZZY_SINGLE_CANDIDATE"),
    )


def _recovery_result(
    decision: str,
    resolution_status: str,
    best: Dict[str, Any] = None,
    candidate_count: int = 0,
    candidate_gap: float = 0.0,
    suggestions: List[Dict[str, Any]] = None,
    reason: str = "NONE",
) -> Dict[str, Any]:
    item = best or {}
    return {
        "decision": decision,
        "resolution_status": resolution_status,
        "corrected_suffix": str(item.get("corrected_suffix") or ""),
        "confidence": float(item.get("confidence") or 0.0),
        "reason": reason if reason != "NONE" else str(item.get("reason") or "NONE"),
        "candidate_action": str(item.get("candidate_action") or ""),
        "candidate_count": candidate_count,
        "candidate_gap": candidate_gap,
        "suggestions": suggestions or [],
        "command": item.get("command"),
        "metrics": item.get("metrics"),
    }


def _parse_candidate(text: str) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    started = time.perf_counter()
    phrase = _phrase(text)
    phrase = re.sub(r"^kartal\b[\s,;:.-]*", "", phrase).strip()
    if not phrase:
        metrics = _metrics(started, 0.0, 0.0, 0.0)
        metrics.update({"resolution_status": "NEEDS_ACTION_CLASSIFICATION", "detected_object_phrase": ""})
        return {"action": "UNKNOWN", "confidence": 0.0}, metrics

    semantic_frame = build_semantic_frame(None, {"action_candidates": []}, phrase)
    if semantic_frame.get("safety_blocked"):
        metrics = _metrics(started, 0.0, 0.0, 0.0)
        metrics.update({
            "resolution_status": "OUT_OF_DOMAIN",
            "detected_object_phrase": "",
            "protected_spans": semantic_frame.get("protected_spans", []),
            "safety_blocked": True,
            "safety_reasons": semantic_frame.get("safety_reasons", []),
            "explicit_out_of_domain": False,
            "out_of_domain_final": False,
        })
        return {"action": "UNKNOWN", "confidence": 0.0}, metrics

    action_started = time.perf_counter()
    action, confidence = resolve_action(phrase)
    action_ms = _elapsed_ms(action_started)

    slots_started = time.perf_counter()
    slots = extract_slots(action, phrase)
    detected_object_phrase = _extract_object_phrase(action, phrase) if action in ("FIND_OBJECT", "TRACK_OBJECT", "HOLD_OVER_OBJECT") else ""
    slots_ms = _elapsed_ms(slots_started)

    validation_started = time.perf_counter()
    candidate = {"action": action, **slots, "confidence": confidence}
    command = validate_command(candidate)
    validation_ms = _elapsed_ms(validation_started)
    metrics = _metrics(started, action_ms, slots_ms, validation_ms)
    if action == "UNKNOWN":
        resolution_status = "OUT_OF_DOMAIN" if _obviously_out_of_domain(phrase) else "NEEDS_ACTION_CLASSIFICATION"
    elif command.get("action") != "MISSING_INFO":
        resolution_status = "RESOLVED"
    elif detected_object_phrase:
        resolution_status = "NEEDS_SLOT_CLASSIFICATION"
    else:
        resolution_status = "MISSING_USER_INFO"
    metrics["resolution_status"] = resolution_status
    metrics["detected_object_phrase"] = detected_object_phrase
    metrics["protected_spans"] = semantic_frame.get("protected_spans", [])
    metrics["safety_blocked"] = False
    metrics["safety_reasons"] = []
    metrics["explicit_out_of_domain"] = _explicitly_out_of_domain(phrase)
    metrics["out_of_domain_final"] = _obviously_out_of_domain(phrase)
    return command, metrics


def resolve_action(phrase: str) -> Tuple[str, float]:
    """Resolve intent only; no parameter object is built in this phase."""
    if _explicitly_out_of_domain(phrase):
        return "UNKNOWN", 0.0
    obj = _mapped(phrase, OBJECTS)
    color = _mapped(phrase, COLORS)
    shape = _mapped(phrase, SHAPES)

    if "bekleme nokta" in phrase and _has(phrase, "in"):
        return "LAND_AT_WAIT_POINT", .98
    if _has_any(phrase, "oldugun yere in", "bulundugun yere in", "mevcut konuma in", "buraya in"):
        return "LAND_CURRENT", .98
    if _has_any(phrase, "ustunde", "uzerinde", "ustune", "uzerine") and _has_any(phrase, "dur", "bekle", "hover", "sabit kal"):
        return "HOLD_OVER_OBJECT", .97
    if _has_any(phrase, "bekle ve izle", "dur ve izle", "oldugun yerde", "mevcut konumda", "bulundugun nokta", "burada") and _has_any(phrase, "izle", "gozlem", "gozetle"):
        return "OBSERVE_CURRENT", .96
    if (shape or _has_inflected(phrase, "isaret") or _has_any(phrase, "marker") or (color and not obj)) and _has_any(phrase, "bul", "ara", "tespit"):
        return "FIND_MARKER", .96
    if _has_any(phrase, "takip et", "pesinden git", "izle"):
        return "TRACK_OBJECT", .97 if obj else .92
    if _has_any(phrase, "bul", "ara", "tespit et", "tespit"):
        return "FIND_OBJECT", .97 if obj else .88
    if _has_any(phrase, "havada dur", "havada bekle", "hover", "havada sabit kal"):
        return "HOVER", .98
    if _compound_turn_and_move(phrase):
        return "UNKNOWN", 0.0
    if _has_turn_context(phrase) and (_turn_direction(phrase) is not None or _quantity(phrase, r"derece") is not None):
        return "TURN", .96
    if _speed_quantity(phrase) is not None or _has_any(phrase, "hizli", "yavas", "hizlan", "hizini", "hizi", "hizla"):
        return "SET_SPEED", .95
    if _vertical_direction(phrase) is not None and (_has_any(phrase, "yuksel", "yukari cik", "yukari cek", "asagi in", "alcal") or _quantity(phrase, r"metre|meter|m") is not None):
        return "MOVE_VERTICAL", .97 if _quantity(phrase, r"metre|meter|m") is not None else .94
    if _direction(phrase) is not None and _has_any(phrase, "git", "gec", "ilerle", "hareket et"):
        return "MOVE_DIRECTION", .96
    if (_coordinate(phrase) is not None and _has_any(phrase, "git", "konum")) or _has_any(phrase, "noktasina git", "bolgesine git", "konumuna git", "konuma git", "oraya git"):
        return "GO_TO_LOCATION", .98 if _coordinate(phrase) is not None else .92
    if _has_any(
        phrase,
        "kalktigin yere in", "baslangic noktasina in", "baslangic noktasina don",
        "eve don", "geri don",
    ) or re.fullmatch(r"in[.,]*", phrase):
        return "LAND_HOME", .98
    if _has(phrase, "havalan") or _has(phrase, "kalk") or phrase == "uc" or _has_any(phrase, "ucusa gec", "ucus yap"):
        return "TAKE_OFF", .98
    if _has_any(phrase, "dur", "bekle", "sabit kal", "hareket etme", "hareketi kes"):
        return "STOP", .96
    return "UNKNOWN", 0.0


def extract_slots(action: str, phrase: str) -> Dict[str, Any]:
    """Dispatch to an extractor that can emit only fields for one action."""
    extractors = {
        "MOVE_DIRECTION": extract_move_direction,
        "MOVE_VERTICAL": extract_move_vertical,
        "SET_SPEED": extract_speed,
        "TURN": extract_turn,
        "FIND_OBJECT": extract_object,
        "TRACK_OBJECT": extract_object,
        "HOLD_OVER_OBJECT": extract_object,
        "OBSERVE_CURRENT": extract_observation,
        "GO_TO_LOCATION": extract_location,
        "HOVER": extract_hover,
        "FIND_MARKER": extract_marker,
    }
    extractor = extractors.get(action)
    return extractor(phrase) if extractor else {}


def extract_move_direction(phrase: str) -> Dict[str, Any]:
    return _present(direction=_direction(phrase), distance_m=_quantity(phrase, r"metre|meter|m"))


def extract_move_vertical(phrase: str) -> Dict[str, Any]:
    return _present(direction=_vertical_direction(phrase), distance_m=_quantity(phrase, r"metre|meter|m"))


def extract_speed(phrase: str) -> Dict[str, Any]:
    return _present(speed_kmh=_speed_quantity(phrase))


def _speed_quantity(phrase: str):
    value = _quantity(phrase, r"km\s*/?\s*h|kilometre\s*(?:/|bolu\s+saat|saatte|saat|saate)|km\s*saat")
    if value is not None:
        return value
    match = re.search(r"\bsaatte\s+(?P<n>-?\d+(?:[.,]\d+)?)\s+kilometre\b", phrase)
    if match:
        return _compact_number(float(match.group("n").replace(",", ".")))
    if _has_any(phrase, "hizini", "hizi", "hizla", "hizli"):
        match = re.search(r"(?P<n>-?\d+(?:[.,]\d+)?)\s+kilometre\b", phrase)
        if match:
            return _compact_number(float(match.group("n").replace(",", ".")))
    return None


def extract_turn(phrase: str) -> Dict[str, Any]:
    return _present(turn_direction=_turn_direction(phrase), angle_deg=_quantity(phrase, r"derece"))


def extract_object(phrase: str) -> Dict[str, Any]:
    return _present(**{"object": _mapped(phrase, OBJECTS)})


def extract_observation(phrase: str) -> Dict[str, Any]:
    mode = "HOLD_AND_WATCH" if _has_any(phrase, "bekle ve izle", "dur ve izle") else "MONITOR" if _has_any(phrase, "gozlem", "gozetle") else "WATCH"
    return _present(observe_mode=mode, duration_s=_quantity(phrase, r"saniye|sn"), duration_min=_quantity(phrase, r"dakika|dk"))


def extract_hover(phrase: str) -> Dict[str, Any]:
    return _present(duration_s=_quantity(phrase, r"saniye|sn"), duration_min=_quantity(phrase, r"dakika|dk"))


def extract_location(phrase: str) -> Dict[str, Any]:
    coordinate = _coordinate(phrase)
    return {"coordinate": coordinate} if coordinate is not None else _present(location_keyword=_location(phrase))


def extract_marker(phrase: str) -> Dict[str, Any]:
    return _present(color=_mapped(phrase, COLORS), shape=_mapped(phrase, SHAPES), marker_index=_marker_index(phrase))


def _phrase(value: str) -> str:
    return normalize_turkish_text(value)


def _strip_wake_word(value: str) -> str:
    return re.sub(r"^\s*kartal\b[\s,;:.-]*", "", value or "", flags=re.IGNORECASE).strip()


def _mapped(phrase: str, values: Dict[str, str]) -> Optional[str]:
    tokens = _words_phrase(phrase).split()
    for key, mapped in sorted(values.items(), key=lambda item: len(item[0]), reverse=True):
        key_tokens = key.split()
        starts = range(0, len(tokens) - len(key_tokens) + 1)
        if key == "arac":
            starts = (0,) if len(tokens) >= len(key_tokens) else ()  # Modified phrases stay semantic.
        if any(_alias_at(tokens, start, key_tokens) for start in starts):
            return mapped
    return None


def _alias_at(tokens: List[str], start: int, alias: List[str]) -> bool:
    if start < 0 or start + len(alias) > len(tokens):
        return False
    for offset, root in enumerate(alias):
        token = tokens[start + offset]
        if offset < len(alias) - 1:
            if token != root:
                return False
        elif not _inflected_token(token, root):
            return False
    return True


def _inflected_token(token: str, root: str) -> bool:
    if not token.startswith(root):
        return False
    suffix = token[len(root):]
    return suffix in {
        "", "i", "u", "a", "e", "yi", "yu", "ni", "nu", "in", "un", "ini", "unu", "nin", "nun",
        "dan", "den", "tan", "ten", "da", "de", "ta", "te", "lar", "ler",
        "lari", "leri", "larla", "lerle",
    }


def _quantity(phrase: str, unit_pattern: str):
    number = r"-?\d+(?:[.,]\d+)?"
    for pattern in (r"(?P<n>" + number + r")\s*(?:" + unit_pattern + r")\b", r"(?:" + unit_pattern + r")\s*(?P<n>" + number + r")\b"):
        match = re.search(pattern, phrase)
        if match:
            return _compact_number(float(match.group("n").replace(",", ".")))
    word_patterns = (
        r"(?P<w>(?:" + NUMBER_WORD_PATTERN + r")(?:\s+(?:" + NUMBER_WORD_PATTERN + r")){0,2})\s+(?:" + unit_pattern + r")\b",
        r"(?:" + unit_pattern + r")\s+(?P<w>(?:" + NUMBER_WORD_PATTERN + r")(?:\s+(?:" + NUMBER_WORD_PATTERN + r")){0,2})\b",
    )
    for pattern in word_patterns:
        match = re.search(pattern, phrase)
        if match:
            return _word_number(match.group("w").split())
    return None


def _word_number(words):
    total = 0
    for word in words:
        if word == "yuz":
            total = max(1, total) * 100
        elif word in NUMBER_WORDS:
            total += NUMBER_WORDS[word]
    return total if total or "sifir" in words else None


def _coordinate(phrase: str):
    patterns = [
        r"(?<!\d)(-?\d{1,2}(?:\.\d+)?)\s*,\s*(-?\d{1,3}(?:\.\d+)?)(?!\d)",
        r"(?<!\d)(-?\d{1,2},\d+)\s+(-?\d{1,3},\d+)(?!\d)",
    ]
    for pattern in patterns:
        match = re.search(pattern, phrase)
        if match:
            lat, lon = (float(value.replace(",", ".")) for value in match.groups())
            if -90 <= lat <= 90 and -180 <= lon <= 180:
                return {"lat": lat, "lon": lon}
    return None


def _location(phrase: str):
    if _has_any(phrase, "konuma git", "oraya git"):
        return None
    match = re.search(r"(.+?)\s+(?:noktasina|bolgesine|konumuna)\s+git", phrase)
    if not match:
        return None
    value = re.sub(r"^kartal\s+", "", match.group(1)).strip()
    suffix = " noktası" if "noktasina" in phrase else " bölgesi" if "bolgesine" in phrase else " konumu"
    return value.upper() + suffix if len(value) == 1 else value + suffix


def _marker_index(phrase: str):
    match = re.search(r"(?:^|\s)(\d+)\s*(?:numarali|olan|$)", phrase) or re.search(r"(?:isareti|arti)\s+(\d+)\b", phrase)
    return int(match.group(1)) if match else None


def _direction(phrase: str):
    if re.search(r"\b(?:sola|sol\s+tarafa|sol\s+yon(?:e|une)?)\b", phrase): return "LEFT"
    if re.search(r"\b(?:saga|sag\s+tarafa|sag\s+yon(?:e|une)?)\b", phrase): return "RIGHT"
    if re.search(r"\b(?:geriye|geri)\b", phrase): return "BACKWARD"
    if re.search(r"\b(?:ileriye|ileri)\b", phrase): return "FORWARD"
    return None


def _vertical_direction(phrase: str):
    if _has_any(phrase, "asagi", "alcal"): return "DOWN"
    if _has_any(phrase, "yukari", "yuksel"): return "UP"
    return None


def _turn_direction(phrase: str):
    if "saat yonunun ters" in phrase: return "COUNTER_CLOCKWISE"
    if "saat yonunde" in phrase: return "CLOCKWISE"
    if re.search(r"\b(?:saga|sag(?:\s+(?:tarafa|yon(?:e|une)?))?)\b", phrase): return "RIGHT"
    if re.search(r"\b(?:sola|sol(?:\s+(?:tarafa|yon(?:e|une)?))?)\b", phrase): return "LEFT"
    return None


def _has_turn_context(phrase: str) -> bool:
    return _has_any(phrase, "don", "cevir", "yonel", "donus yap")


def _compound_turn_and_move(phrase: str) -> bool:
    words = _words_phrase(phrase)
    turn = r"(?:don|cevir|yonel|donus\s+yap)"
    move = r"(?:git|ilerle|hareket\s+et)"
    return bool(re.search(r"\b" + turn + r"\b.*\bve\b.*\b" + move + r"\b", words)
                or re.search(r"\b" + move + r"\b.*\bve\b.*\b" + turn + r"\b", words))


def _has(phrase: str, root: str) -> bool:
    return root in _words_phrase(phrase).split()


def _has_inflected(phrase: str, root: str) -> bool:
    return any(_inflected_token(token, root) for token in _words_phrase(phrase).split())


def _has_any(phrase: str, *parts: str) -> bool:
    padded = " " + _words_phrase(phrase) + " "
    return any((" " + part + " ") in padded for part in parts)


def _extract_object_phrase(action: str, phrase: str) -> str:
    patterns = {
        "FIND_OBJECT": r"^(?P<object>.+?)\s+(?:bul|ara|tespit(?: et)?)$",
        "TRACK_OBJECT": r"^(?P<object>.+?)\s+(?:takip et|izle|pesinden git)$",
        "HOLD_OVER_OBJECT": r"^(?P<object>.+?)\s+(?:ustunde|uzerinde|ustune|uzerine)\s+(?:dur|bekle|hover|sabit kal)$",
    }
    match = re.match(patterns[action], _words_phrase(phrase))
    if not match:
        return ""
    value = match.group("object").strip()
    tokens = value.split()
    return "" if len(tokens) == 1 and any(_inflected_token(tokens[0], root) for root in ("nesne", "hedef")) else value


def _resolution_score(status: str) -> int:
    return {
        "RESOLVED": 5,
        "MISSING_USER_INFO": 4,
        "OUT_OF_DOMAIN": 3,
        "NEEDS_SLOT_CLASSIFICATION": 2,
        "NEEDS_ACTION_CLASSIFICATION": 1,
    }.get(status, 0)


def _words_phrase(value: str) -> str:
    return re.sub(r"[^0-9a-z]+", " ", value).strip()


def _present(**values):
    return {key: value for key, value in values.items() if value is not None}


def _compact_number(value: float):
    return int(value) if value.is_integer() else value


def _obviously_out_of_domain(phrase: str) -> bool:
    return _explicitly_out_of_domain(phrase) or _has_any(phrase, "hava cok", "bugun hava", "merhaba", "nasilsin", "gunaydin", "iyi aksamlar")


def _explicitly_out_of_domain(phrase: str) -> bool:
    return _has_any(
        phrase,
        "bana bir siir yaz", "siir yaz", "durum nedir", "kabul et",
        "bulutlari izle", "bulutu izle", "tirnak isaretini bul", "tirnak isareti bul",
        "fotograf cek", "resim cek",
    )


def _one(value):
    return [] if value is None else [value]


def _elapsed_ms(started: float) -> float:
    return round((time.perf_counter() - started) * 1000.0, 3)


def _metrics(started: float, action_ms: float, slots_ms: float, validation_ms: float) -> Dict[str, float]:
    return {
        "baseline_duration_ms": _elapsed_ms(started),
        "action_resolution_duration_ms": action_ms,
        "slot_extraction_duration_ms": slots_ms,
        "validation_duration_ms": validation_ms,
    }
