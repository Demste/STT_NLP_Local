# -*- coding: utf-8 -*-
"""Deterministic action-first Turkish drone-command parser."""

from __future__ import annotations

import re
import time
from typing import Any, Dict, List, Optional, Tuple

from .command_text_repair import build_repair_candidates
from .command_validation import validate_command
from .text_normalization import normalize_turkish_text

OBJECTS = {
    "beton mikseri": "CEMENT_TRUCK", "beton mikser": "CEMENT_TRUCK",
    "cimento kamyon": "CEMENT_TRUCK", "mikser kamyon": "CEMENT_TRUCK",
    "dorseli tir": "SEMI_TRUCK", "cekici tir": "SEMI_TRUCK",
    "kazi makinesi": "EXCAVATOR", "kazi makine": "EXCAVATOR",
    "yukleme makinesi": "LOADER", "yukleme makine": "LOADER",
    "kamyonet": "PICKUP", "pickup": "PICKUP", "pikap": "PICKUP", "pikabi": "PICKUP",
    "otomobil": "CAR", "araba": "CAR", "arac": "CAR",
    "ekskavator": "EXCAVATOR", "kazici": "EXCAVATOR", "kepce": "EXCAVATOR",
    "yukleyici": "LOADER", "loader": "LOADER", "forklift": "FORKLIFT", "kamyon": "TRUCK",
    "tir": "SEMI_TRUCK", "cekici": "SEMI_TRUCK", "vinc": "CRANE", "crane": "CRANE",
}
COLORS = {"kirmizi": "RED", "yesil": "GREEN", "mavi": "BLUE", "beyaz": "WHITE", "siyah": "BLACK"}
SHAPES = {"cizgi": "LINE", "hat": "LINE", "kare": "SQUARE", "ucgen": "TRIANGLE", "arti": "PLUS", "plus": "PLUS"}
NUMBER_WORDS = {
    "sifir": 0, "bir": 1, "iki": 2, "uc": 3, "dort": 4, "bes": 5, "alti": 6,
    "yedi": 7, "sekiz": 8, "dokuz": 9, "on": 10, "yirmi": 20, "otuz": 30,
    "kirk": 40, "elli": 50, "altmis": 60, "yetmis": 70, "seksen": 80, "doksan": 90,
}
NUMBER_WORD_PATTERN = "(?:" + "|".join(sorted(NUMBER_WORDS, key=len, reverse=True)) + "|yuz)"


def build_baseline_hints(analysis: Dict[str, Any], original_text: str = "") -> Dict[str, Any]:
    text = original_text or " ".join(str(token.get("text", "")) for token in analysis.get("tokens", []))
    command, metrics = parse_drone_command_with_diagnostics(text)
    intended_action = command.get("target_action") if command.get("action") == "MISSING_INFO" else command.get("action")
    resolution_status = metrics["resolution_status"]
    final_parser = resolution_status in ("RESOLVED", "MISSING_USER_INFO", "OUT_OF_DOMAIN")
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
        **metrics,
    }


def parse_drone_command(text: str) -> Dict[str, Any]:
    return parse_drone_command_with_diagnostics(text)[0]


def parse_drone_command_with_diagnostics(text: str) -> Tuple[Dict[str, Any], Dict[str, Any]]:
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
    })
    return command, selected_metrics


def _parse_candidate(text: str) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    started = time.perf_counter()
    phrase = _phrase(text)
    phrase = re.sub(r"^kartal\b[\s,;:.-]*", "", phrase).strip()
    if not phrase:
        metrics = _metrics(started, 0.0, 0.0, 0.0)
        metrics.update({"resolution_status": "NEEDS_ACTION_CLASSIFICATION", "detected_object_phrase": ""})
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
    if _has(phrase, "don") and (_turn_direction(phrase) is not None or _quantity(phrase, r"derece") is not None):
        return "TURN", .96
    if _speed_quantity(phrase) is not None or _has_any(phrase, "hizli", "yavas", "hizlan", "hizini", "hizi", "hizla"):
        return "SET_SPEED", .95
    if _vertical_direction(phrase) is not None and (_has_any(phrase, "yuksel", "yukari cik", "asagi in", "alcal") or _quantity(phrase, r"metre|meter|m") is not None):
        return "MOVE_VERTICAL", .97 if _quantity(phrase, r"metre|meter|m") is not None else .94
    if _direction(phrase) is not None and _has_any(phrase, "git", "ilerle", "hareket et"):
        return "MOVE_DIRECTION", .96
    if (_coordinate(phrase) is not None and _has_any(phrase, "git", "konum")) or _has_any(phrase, "noktasina git", "bolgesine git", "konumuna git", "konuma git", "oraya git"):
        return "GO_TO_LOCATION", .98 if _coordinate(phrase) is not None else .92
    if _has_any(phrase, "kalktigin yere in", "baslangic noktasina in", "eve don") or re.fullmatch(r"in[.,]*", phrase):
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


def _mapped(phrase: str, values: Dict[str, str]) -> Optional[str]:
    tokens = _words_phrase(phrase).split()
    for key, mapped in sorted(values.items(), key=lambda item: len(item[0]), reverse=True):
        key_tokens = key.split()
        starts = range(0, len(tokens) - len(key_tokens) + 1)
        if key == "arac":
            starts = (0,)  # "araç bul" is CAR; modified phrases like "yük aracı" stay semantic.
        if any(_alias_at(tokens, start, key_tokens) for start in starts):
            return mapped
    return None


def _alias_at(tokens: List[str], start: int, alias: List[str]) -> bool:
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
    if re.search(r"\bsaga\b", phrase): return "RIGHT"
    if re.search(r"\bsola\b", phrase): return "LEFT"
    return None


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
