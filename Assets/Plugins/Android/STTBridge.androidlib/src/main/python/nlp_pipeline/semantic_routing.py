# -*- coding: utf-8 -*-
"""Bounded Turkish semantic frames built on the shared command ontology."""

from __future__ import annotations

import re
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

from .command_catalog import COLOR_ALIASES, NUMBER_WORDS, OBJECT_ALIASES, SHAPE_ALIASES
from .text_normalization import normalize_turkish_text


_TOKEN_RE = re.compile(r"[-+]?\d+(?:[.,]\d+)?|[A-Za-zÇĞİIÖŞÜçğıöşü]+|/")
_NUMBER_UNITS = {
    "metre": ("METER", "distance_m"),
    "meter": ("METER", "distance_m"),
    "m": ("METER", "distance_m"),
    "saniye": ("SECOND", "duration_s"),
    "sn": ("SECOND", "duration_s"),
    "dakika": ("MINUTE", "duration_min"),
    "dk": ("MINUTE", "duration_min"),
    "derece": ("DEGREE", "angle_deg"),
}
_DIRECTION_ALIASES = {
    "LEFT": ("sol", "sola", "sol tarafa", "sol yana", "sol cenaha", "sol kanada"),
    "RIGHT": ("sag", "saga", "sag tarafa", "sag yana", "sag cenaha", "sag kanada"),
    "FORWARD": ("ileri", "ileriye", "on tarafa"),
    "BACKWARD": ("geri", "geriye", "arka tarafa"),
    "UP": ("yukari", "yukariya"),
    "DOWN": ("asagi", "asagiya"),
}
_PREDICATE_PHRASES = {
    "MOVE_DIRECTION": (
        "git", "gec", "ilerle", "hareket et", "yol al", "yol kat et", "mesafe al",
    ),
    "MOVE_VERTICAL": (
        "yuksel", "yukari cik", "yukari cek", "alcal", "asagi in",
        "irtifani artir", "irtifa artir", "irtifani yukselt", "irtifa yukselt",
        "irtifani azalt", "irtifa azalt", "irtifani dusur", "irtifa dusur",
    ),
    "TURN": (
        "don", "cevir", "rota kir", "rotani kir", "yonunu kir", "yonel",
    ),
    "FIND_OBJECT": ("bul", "ara", "tespit et"),
    "TRACK_OBJECT": (
        "takip et", "izle", "pesinden git", "goz hapsinde tut",
        "gozden kacirma", "pesini birakma",
    ),
    "HOLD_OVER_OBJECT": ("uzerinde dur", "ustunde dur", "uzerinde bekle", "ustunde bekle"),
    "OBSERVE_CURRENT": (
        "gozlem yap", "gozetle", "bekle ve izle", "dur ve izle", "nobet tut",
    ),
    "GO_TO_LOCATION": ("noktasina git", "bolgesine git", "konumuna git"),
    "HOVER": ("havada dur", "havada bekle", "hover yap"),
    "STOP": ("dur", "hareketi kes", "sabit kal"),
    "TAKE_OFF": ("havalan", "kalk", "ucusa gec"),
    "LAND_HOME": (
        "eve don", "geri don", "baslangic noktasina don",
        "baslangic noktasina geri cekil", "kalktigin yere in",
    ),
    "LAND_CURRENT": ("oldugun yere in", "bulundugun yere in", "mevcut konuma in"),
    "LAND_AT_WAIT_POINT": ("bekleme noktasina in",),
}
_AMBIGUOUS_PREDICATES = {
    "kivril": ("TURN", "MOVE_DIRECTION"),
    "kivrilmak": ("TURN", "MOVE_DIRECTION"),
    "ilgilen": ("TRACK_OBJECT", "FIND_OBJECT"),
}
_IDIOMATIC_NEGATIONS = ("gozden kacirma", "pesini birakma")
_CONTEXT_PRONOUNS = ("onu", "ona", "oraya", "aynisini", "bunu", "buna")


def build_protected_spans(text: str) -> List[Dict[str, Any]]:
    raw = _strip_wake_word(text or "")
    tokens = _tokens(raw)
    spans: List[Dict[str, Any]] = []

    for value, aliases in _DIRECTION_ALIASES.items():
        match = _first_alias(tokens, aliases)
        if match:
            _append_span(spans, "DIRECTION", raw, match, value=value)

    for aliases, span_type in (
        (OBJECT_ALIASES, "OBJECT"),
        (COLOR_ALIASES, "COLOR"),
        (SHAPE_ALIASES, "SHAPE"),
    ):
        for alias, value in sorted(aliases.items(), key=lambda item: len(item[0]), reverse=True):
            match = _first_object_alias(tokens, alias) if span_type == "OBJECT" else _first_alias(tokens, (alias,))
            if match:
                _append_span(spans, span_type, raw, match, value=value)
                break

    spans.extend(_quantity_spans(raw, tokens))
    marker = _marker_index_span(raw, tokens)
    if marker:
        spans.append(marker)
    coordinate = _coordinate_span(raw)
    if coordinate:
        spans.append(coordinate)

    normalized = normalize_turkish_text(raw)
    for phrase in _IDIOMATIC_NEGATIONS:
        match = _first_alias(tokens, (phrase,))
        if match:
            _append_span(spans, "IDIOMATIC_NEGATION", raw, match, value=phrase)

    if _has_explicit_negation(normalized):
        match = _negative_span(tokens)
        if match:
            _append_span(spans, "NEGATION", raw, match, value="NEGATED")

    for index, token in enumerate(tokens):
        if token["normalized"] in ("ve", "ile", "sonra", "ardindan"):
            _append_span(spans, "CONJUNCTION", raw, (index, index + 1), value=token["normalized"])

    for pronoun in _CONTEXT_PRONOUNS:
        match = _first_alias(tokens, (pronoun,))
        if match:
            _append_span(spans, "CONTEXT_REFERENCE", raw, match, value=pronoun)

    for keyword, value in (
        ("burada", "CURRENT"),
        ("bulundugun yer", "CURRENT"),
        ("oldugun yer", "CURRENT"),
        ("baslangic noktasi", "HOME"),
        ("kalktigin yer", "HOME"),
        ("ev", "HOME"),
        ("bekleme noktasi", "WAIT_POINT"),
    ):
        match = _first_alias(tokens, (keyword,))
        if match:
            _append_span(spans, "LOCATION", raw, match, value=value)

    return _dedupe_spans(spans)


def build_semantic_frame(
    doc,
    analysis: Dict[str, Any],
    text: str,
    baseline_hints: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    raw = _strip_wake_word(text or "")
    normalized = normalize_turkish_text(raw)
    protected = build_protected_spans(raw)
    predicate_lemmas = _predicate_lemmas(analysis)
    predicate_actions, predicate_evidence = _candidate_actions(raw, normalized, predicate_lemmas)
    if _has_altitude_semantics(normalized):
        predicate_actions.append("MOVE_VERTICAL")
        predicate_evidence["MOVE_VERTICAL"] = _semantic_predicate_evidence(raw, normalized, "MOVE_VERTICAL")
        direction = "UP" if _contains_any(normalized, ("artir", "yukselt")) else "DOWN"
        protected.append({
            "type": "DIRECTION",
            "text": predicate_evidence["MOVE_VERTICAL"],
            "normalized": normalize_turkish_text(predicate_evidence["MOVE_VERTICAL"]),
            "start": -1,
            "end": -1,
            "value": direction,
            "source": "semantic_predicate",
        })
    if _has_route_turn_semantics(normalized):
        predicate_actions.append("TURN")
        predicate_evidence["TURN"] = _semantic_predicate_evidence(raw, normalized, "TURN")
    baseline_actions = list((baseline_hints or {}).get("action_candidates") or [])
    candidate_actions = _unique(baseline_actions + predicate_actions)

    raw_object = _unresolved_object_span(raw, analysis, baseline_hints, candidate_actions, protected)
    if raw_object:
        protected.append(raw_object)

    directions = _spans(protected, "DIRECTION")
    quantities = _spans(protected, "QUANTITY")
    objects = _spans(protected, "OBJECT")
    locations = _spans(protected, "LOCATION")
    idiomatic_negation = bool(_spans(protected, "IDIOMATIC_NEGATION"))
    negated = bool(_spans(protected, "NEGATION")) and not idiomatic_negation
    conjunct_count = 1 + len(_spans(protected, "CONJUNCTION"))
    conflicting_direction = len({item.get("value") for item in directions if item.get("value")}) > 1
    predicate_family_count = len({
        action for action in candidate_actions
        if action not in ("UNKNOWN", "MISSING_INFO")
    })
    multi_action = conflicting_direction or (
        conjunct_count > 1
        and predicate_family_count > 1
        and not _single_predicate_construction(normalized)
    )
    context_spans = _spans(protected, "CONTEXT_REFERENCE")
    context_reference = bool(context_spans)
    context_blocked = any(item.get("value") != "oraya" for item in context_spans)

    return {
        "raw_text": raw,
        "normalized_text": normalized,
        "predicate_lemmas": predicate_lemmas,
        "predicate_evidence": predicate_evidence,
        "negated": negated,
        "idiomatic_negation": idiomatic_negation,
        "conjunct_count": conjunct_count,
        "multi_action": multi_action,
        "conflicting_direction": conflicting_direction,
        "context_reference": context_reference,
        "object_spans": objects,
        "direction_spans": directions,
        "quantity_spans": quantities,
        "location_spans": locations,
        "protected_spans": protected,
        "candidate_actions": candidate_actions,
        "safety_blocked": negated or multi_action or context_blocked,
        "safety_reasons": _safety_reasons(negated, multi_action, context_blocked, conflicting_direction),
    }


def resolve_semantic_candidate(frame: Dict[str, Any]) -> Dict[str, Any]:
    raw = str(frame.get("raw_text") or "")
    normalized = str(frame.get("normalized_text") or "")
    candidate_actions = list(frame.get("candidate_actions") or [])
    if frame.get("safety_blocked"):
        return _record(raw, normalized, {"action": "UNKNOWN", "confidence": 0.0}, {}, "blocked", frame)

    action = ""
    if _contains_any(normalized, _IDIOMATIC_NEGATIONS) and _slot_value(frame, "object_spans"):
        action = "TRACK_OBJECT"
    elif _contains_any(normalized, ("goz hapsinde tut", "pesinden ayrilma")) and _slot_value(frame, "object_spans"):
        action = "TRACK_OBJECT"
    elif _contains_any(normalized, ("burada nobet tut", "mevcut konumda nobet tut")):
        action = "OBSERVE_CURRENT"
    elif _contains_any(normalized, ("baslangic noktasina geri cekil", "kalktigin yere geri cekil")):
        action = "LAND_HOME"
    elif _has_altitude_semantics(normalized):
        action = "MOVE_VERTICAL"
    elif _has_route_turn_semantics(normalized):
        action = "TURN"
    elif _has_horizontal_semantics(normalized, frame):
        action = "MOVE_DIRECTION"
    elif len(candidate_actions) == 1 and _strong_predicate(normalized, candidate_actions[0]):
        action = candidate_actions[0]

    if not action:
        return _record(raw, normalized, {"action": "UNKNOWN", "confidence": 0.0}, {}, "ambiguous", frame)

    evidence = {"action": _action_evidence(raw, normalized, action)}
    command: Dict[str, Any] = {"action": action, "confidence": 0.84}
    _copy_semantic_slots(command, evidence, frame, action, normalized)
    return _record(raw, normalized, command, evidence, "candidate", frame)


def enrich_action_evidence(
    evidence: Optional[Dict[str, Any]],
    frame: Dict[str, Any],
) -> Dict[str, Any]:
    result = dict(evidence or {})
    supported = list(result.get("supported_actions") or [])
    reasons = list(result.get("reasons") or [])
    for action in frame.get("candidate_actions") or []:
        if action not in supported:
            supported.append(action)
            reasons.append("spacy_semantic_candidate:" + action)
    result["supported_actions"] = supported
    result["has_any"] = bool(supported)
    result["reasons"] = reasons
    result["source_text"] = result.get("source_text") or frame.get("normalized_text") or ""
    return result


def _copy_semantic_slots(
    command: Dict[str, Any],
    evidence: Dict[str, Any],
    frame: Dict[str, Any],
    action: str,
    normalized: str,
) -> None:
    direction_span = _first(frame.get("direction_spans"))
    object_span = _first(frame.get("object_spans"))
    if action == "MOVE_VERTICAL":
        direction = "UP" if _contains_any(normalized, ("artir", "yukselt", "yuksel", "yukari")) else "DOWN"
        command["direction"] = direction
        evidence["direction"] = _span_evidence(direction_span) or _semantic_word_evidence(frame, normalized, direction)
        quantity = _quantity_for(frame, "METER")
        if quantity:
            command["distance_m"] = quantity["value"]
            evidence["distance_m"] = quantity["text"]
    elif action == "MOVE_DIRECTION":
        if direction_span:
            command["direction"] = direction_span["value"]
            evidence["direction"] = direction_span["text"]
        quantity = _quantity_for(frame, "METER")
        if quantity:
            command["distance_m"] = quantity["value"]
            evidence["distance_m"] = quantity["text"]
    elif action == "TURN":
        if direction_span:
            command["turn_direction"] = direction_span["value"]
            evidence["turn_direction"] = direction_span["text"]
        quantity = _quantity_for(frame, "DEGREE")
        if quantity:
            command["angle_deg"] = quantity["value"]
            evidence["angle_deg"] = quantity["text"]
    elif action in ("FIND_OBJECT", "TRACK_OBJECT", "HOLD_OVER_OBJECT"):
        if object_span and object_span.get("value"):
            command["object"] = object_span["value"]
            evidence["object"] = object_span["text"]
    elif action == "OBSERVE_CURRENT":
        command["observe_mode"] = "WATCH"
        evidence["observe_mode"] = _action_evidence(frame.get("raw_text", ""), normalized, action)


def _candidate_actions(raw: str, normalized: str, lemmas: Sequence[str]) -> Tuple[List[str], Dict[str, str]]:
    actions: List[str] = []
    evidence: Dict[str, str] = {}
    for action, phrases in _PREDICATE_PHRASES.items():
        phrase = _first_present(normalized, phrases)
        lemma_match = next((lemma for lemma in lemmas if _lemma_supports(lemma, phrases)), "")
        if phrase or lemma_match:
            actions.append(action)
            evidence[action] = _original_phrase(raw, phrase or lemma_match)
    for phrase, values in _AMBIGUOUS_PREDICATES.items():
        if phrase in normalized or any(lemma.startswith(phrase) for lemma in lemmas):
            for action in values:
                actions.append(action)
                evidence.setdefault(action, _original_phrase(raw, phrase))
    return _unique(actions), evidence


def _predicate_lemmas(analysis: Dict[str, Any]) -> List[str]:
    result = []
    for item in analysis.get("action_candidates") or []:
        value = normalize_turkish_text(str(item.get("lemma") or item.get("text") or ""))
        if value and value not in result:
            result.append(value)
    return result


def _quantity_spans(raw: str, tokens: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    result: List[Dict[str, Any]] = []
    for index, token in enumerate(tokens):
        unit = _NUMBER_UNITS.get(token["normalized"])
        if unit is None:
            continue
        number_start, value = _number_before(tokens, index)
        if value is None:
            number_start, value = _number_after(tokens, index)
            end = number_start + 1 if value is not None else index + 1
            start = index
        else:
            start = number_start
            end = index + 1
        if value is None:
            continue
        span = _span("QUANTITY", raw, tokens, start, end, value=value)
        span["unit"] = unit[0]
        span["slot"] = unit[1]
        result.append(span)

    normalized = normalize_turkish_text(raw)
    speed = re.search(r"(?P<n>-?\d+(?:[.,]\d+)?)\s*(?:km\s*/?\s*h|kilometre\s+saat)", normalized)
    if speed:
        result.append({
            "type": "QUANTITY",
            "text": speed.group(0),
            "normalized": speed.group(0),
            "start": -1,
            "end": -1,
            "value": _compact_number(float(speed.group("n").replace(",", "."))),
            "unit": "KMH",
            "slot": "speed_kmh",
        })
    word_speed = re.search(
        r"(?P<w>(?:sifir|bir|iki|uc|dort|bes|alti|yedi|sekiz|dokuz|on|yirmi|otuz|kirk|elli|altmis|yetmis|seksen|doksan|yuz)"
        r"(?:\s+(?:sifir|bir|iki|uc|dort|bes|alti|yedi|sekiz|dokuz|on|yirmi|otuz|kirk|elli|altmis|yetmis|seksen|doksan|yuz)){0,2})"
        r"\s+(?:km\s*/?\s*h|kilometre\s+saat)",
        normalized,
    )
    if word_speed:
        value = _word_number(word_speed.group("w").split())
        if value is not None:
            result.append({
                "type": "QUANTITY",
                "text": word_speed.group(0),
                "normalized": word_speed.group(0),
                "start": -1,
                "end": -1,
                "value": value,
                "unit": "KMH",
                "slot": "speed_kmh",
            })
    return result


def _marker_index_span(raw: str, tokens: List[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
    normalized = [item["normalized"] for item in tokens]
    for index, token in enumerate(normalized):
        value = _numeric_token(token)
        if value is None or value < 0 or int(value) != value:
            continue
        window = normalized[max(0, index - 2):min(len(tokens), index + 3)]
        if any(part.startswith(("numara", "isaret", "marker")) for part in window):
            return _span("MARKER_INDEX", raw, tokens, index, index + 1, value=int(value))
    return None


def _number_before(tokens: List[Dict[str, Any]], unit_index: int) -> Tuple[int, Optional[float]]:
    if unit_index <= 0:
        return unit_index, None
    token = tokens[unit_index - 1]["normalized"]
    numeric = _numeric_token(token)
    if numeric is not None:
        return unit_index - 1, numeric
    start = unit_index - 1
    while start > 0 and tokens[start - 1]["normalized"] in NUMBER_WORDS:
        start -= 1
    return start, _word_number([item["normalized"] for item in tokens[start:unit_index]])


def _number_after(tokens: List[Dict[str, Any]], unit_index: int) -> Tuple[int, Optional[float]]:
    if unit_index + 1 >= len(tokens):
        return unit_index, None
    token = tokens[unit_index + 1]["normalized"]
    numeric = _numeric_token(token)
    return (unit_index + 1, numeric)


def _numeric_token(value: str) -> Optional[float]:
    if not re.fullmatch(r"-?\d+(?:[.,]\d+)?", value):
        return None
    return _compact_number(float(value.replace(",", ".")))


def _word_number(words: Sequence[str]) -> Optional[int]:
    if not words or any(word not in NUMBER_WORDS and word != "yuz" for word in words):
        return None
    total = 0
    for word in words:
        if word == "yuz":
            total = max(1, total) * 100
        else:
            total += NUMBER_WORDS[word]
    return total


def _coordinate_span(raw: str) -> Optional[Dict[str, Any]]:
    match = re.search(r"(?<!\d)(-?\d{1,2}(?:[.,]\d+)?)\s*[,;]\s*(-?\d{1,3}(?:[.,]\d+)?)(?!\d)", raw)
    if not match:
        return None
    lat, lon = (float(value.replace(",", ".")) for value in match.groups())
    if not (-90 <= lat <= 90 and -180 <= lon <= 180):
        return None
    return {
        "type": "COORDINATE",
        "text": match.group(0),
        "normalized": normalize_turkish_text(match.group(0)),
        "start": match.start(),
        "end": match.end(),
        "value": {"lat": lat, "lon": lon},
    }


def _has_explicit_negation(normalized: str) -> bool:
    if re.search(r"\bizleme\s+(?:nokta|bolge|konum)", normalized):
        return False
    if _contains_any(normalized, _IDIOMATIC_NEGATIONS):
        remaining = normalized
        for phrase in _IDIOMATIC_NEGATIONS:
            remaining = remaining.replace(phrase, "")
    else:
        remaining = normalized
    return bool(re.search(r"\b(?:takip|git|don|bul|izle)\s+etme\b", remaining)
                or re.search(r"\b(?:takipetme|gitme|donme|bulma|izleme)\b", remaining))


def _negative_span(tokens: List[Dict[str, Any]]) -> Optional[Tuple[int, int]]:
    for index, token in enumerate(tokens):
        value = token["normalized"]
        if value == "etme" and index > 0:
            return index - 1, index + 1
        if value.endswith(("me", "ma")):
            return index, index + 1
    return None


def _has_horizontal_semantics(normalized: str, frame: Dict[str, Any]) -> bool:
    return bool(frame.get("direction_spans") and _quantity_for(frame, "METER")
                and _contains_any(normalized, ("yol al", "yol kat et", "mesafe al", "ilerle", "hareket et")))


def _has_altitude_semantics(normalized: str) -> bool:
    return "irtifa" in normalized and _contains_any(normalized, ("artir", "yukselt", "azalt", "dusur"))


def _has_route_turn_semantics(normalized: str) -> bool:
    return bool(re.search(r"\b(?:rota\w*|yonunu)\b.*\bkir\b", normalized)) and "kivril" not in normalized


def _strong_predicate(normalized: str, action: str) -> bool:
    return bool(_first_present(normalized, _PREDICATE_PHRASES.get(action, ())))


def _single_predicate_construction(normalized: str) -> bool:
    return _contains_any(normalized, ("bekle ve izle", "dur ve izle"))


def _safety_reasons(negated: bool, multi: bool, context: bool, conflict: bool) -> List[str]:
    reasons = []
    if negated:
        reasons.append("explicit_negation")
    if multi:
        reasons.append("multiple_actions")
    if conflict:
        reasons.append("conflicting_direction")
    if context:
        reasons.append("unresolved_context_reference")
    return reasons


def _record(raw: str, normalized: str, command: Dict[str, Any], evidence: Dict[str, Any],
            status: str, frame: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "raw_text": raw,
        "normalized_text": normalized,
        "repaired_text": raw,
        "semantic_frame": frame,
        "command": command,
        "evidence": evidence,
        "source": "spacy",
        "status": status,
    }


def _action_evidence(raw: str, normalized: str, action: str) -> str:
    phrase = _first_present(normalized, _PREDICATE_PHRASES.get(action, ()))
    return _original_phrase(raw, phrase) if phrase else _semantic_predicate_evidence(raw, normalized, action)


def _semantic_predicate_evidence(raw: str, normalized: str, action: str) -> str:
    candidates = {
        "MOVE_VERTICAL": ("artir", "yukselt", "azalt", "dusur"),
        "TURN": ("kir", "cevir", "don"),
    }.get(action, ())
    for candidate in candidates:
        value = _original_phrase(raw, candidate)
        if value:
            return value
    return ""


def _semantic_word_evidence(frame: Dict[str, Any], normalized: str, direction: str) -> str:
    words = ("artır", "yükselt") if direction == "UP" else ("azalt", "düşür")
    raw = str(frame.get("raw_text") or "")
    for word in words:
        match = re.search(re.escape(word), raw, flags=re.IGNORECASE)
        if match:
            return match.group(0)
    return _original_phrase(raw, "artir" if direction == "UP" else "azalt")


def _slot_value(frame: Dict[str, Any], key: str):
    item = _first(frame.get(key))
    return item.get("value") if item else None


def _quantity_for(frame: Dict[str, Any], unit: str) -> Optional[Dict[str, Any]]:
    return next((item for item in frame.get("quantity_spans") or [] if item.get("unit") == unit), None)


def _spans(values: Sequence[Dict[str, Any]], span_type: str) -> List[Dict[str, Any]]:
    return [item for item in values or [] if item.get("type") == span_type]


def _span_evidence(item: Optional[Dict[str, Any]]) -> str:
    return str(item.get("text") or "") if item else ""


def _tokens(raw: str) -> List[Dict[str, Any]]:
    return [{
        "text": match.group(0),
        "normalized": normalize_turkish_text(match.group(0)),
        "start": match.start(),
        "end": match.end(),
    } for match in _TOKEN_RE.finditer(raw)]


def _first_alias(tokens: List[Dict[str, Any]], aliases: Iterable[str]) -> Optional[Tuple[int, int]]:
    normalized = [item["normalized"] for item in tokens]
    for alias in sorted(aliases, key=len, reverse=True):
        parts = normalize_turkish_text(alias).split()
        for start in range(0, len(tokens) - len(parts) + 1):
            if _matches_tokens(normalized[start:start + len(parts)], parts):
                return start, start + len(parts)
    return None


def _first_object_alias(tokens: List[Dict[str, Any]], alias: str) -> Optional[Tuple[int, int]]:
    """Match ontology objects without letting a generic token steal a longer phrase."""
    normalized = [item["normalized"] for item in tokens]
    parts = normalize_turkish_text(alias).split()
    for start in range(0, len(tokens) - len(parts) + 1):
        if parts == ["arac"] and start > 0:
            # "yük aracını" is a compound phrase requiring semantic slot
            # classification, not deterministic CAR evidence.
            continue
        if _matches_tokens(normalized[start:start + len(parts)], parts):
            return start, start + len(parts)
    return None


def _matches_tokens(values: Sequence[str], alias: Sequence[str]) -> bool:
    if len(values) != len(alias):
        return False
    for index, (value, root) in enumerate(zip(values, alias)):
        if value == root:
            continue
        if index == len(alias) - 1 and _has_nominal_suffix(value, root):
            continue
        return False
    return True


def _has_nominal_suffix(value: str, root: str) -> bool:
    if not value.startswith(root):
        return False
    suffix = value[len(root):]
    return suffix in {
        "i", "u", "a", "e", "yi", "yu", "ya", "ye",
        "in", "un", "nin", "nun", "ini", "unu", "inin", "unun",
        "da", "de", "dan", "den", "la", "le", "yla", "yle",
        "lar", "ler", "lari", "leri",
    }


def _unresolved_object_span(
    raw: str,
    analysis: Dict[str, Any],
    baseline_hints: Optional[Dict[str, Any]],
    candidate_actions: Sequence[str],
    protected: Sequence[Dict[str, Any]],
) -> Optional[Dict[str, Any]]:
    """Keep a spoken non-canonical noun phrase as bounded slot evidence.

    The baseline phrase is predicate-anchored; the spaCy candidate supplies the
    POS/dependency-side noun evidence. Neither source alone is treated as an
    unrestricted substring classifier.
    """
    hints = baseline_hints or {}
    object_actions = [value for value in candidate_actions if value in (
        "FIND_OBJECT", "TRACK_OBJECT", "HOLD_OVER_OBJECT",
    )]
    if len(object_actions) != 1 or _spans(protected, "OBJECT"):
        return None
    if hints.get("resolution_status") != "NEEDS_SLOT_CLASSIFICATION":
        return None
    detected = normalize_turkish_text(str(hints.get("detected_object_phrase") or ""))
    if not detected:
        return None

    candidates = []
    for item in analysis.get("object_phrase_candidates") or []:
        if not isinstance(item, dict):
            continue
        normalized = normalize_turkish_text(str(item.get("normalized") or item.get("text") or ""))
        if normalized and (normalized in detected or detected in normalized):
            candidates.append(item)
    linguistic_basis = "spacy_object_phrase_candidate"
    if not candidates:
        # Some Turkish model outputs tag an inflected target such as
        # "vasıtayı" outside NOUN/PROPN/PRON, so object_phrase_candidates can
        # be empty even though the fixed predicate and its bounded argument are
        # explicit. Retain the predicate-anchored baseline phrase only when at
        # least one real spaCy token overlaps it; this is not a free substring
        # fallback.
        action = object_actions[0]
        if not _strong_predicate(normalize_turkish_text(raw), action):
            return None
        for item in analysis.get("tokens") or []:
            if not isinstance(item, dict):
                continue
            normalized = normalize_turkish_text(str(
                item.get("normalized_text") or item.get("text") or ""
            ))
            pos = str(item.get("pos") or "").upper()
            if normalized and normalized in detected and pos not in ("VERB", "AUX", "PUNCT", "SPACE"):
                candidates.append(item)
        if not candidates:
            return None
        linguistic_basis = "spacy_token_plus_predicate_anchor"

    tokens = _tokens(raw)
    bounds = _first_alias(tokens, (detected,))
    if not bounds:
        return None
    span = _span("OBJECT", raw, tokens, bounds[0], bounds[1])
    strongest = max(candidates, key=lambda item: len(str(item.get("text") or "")))
    span.update({
        "field": "object",
        "canonical_value": None,
        "source": "spacy_predicate_object_phrase",
        "linguistic_basis": linguistic_basis,
        "head_i": strongest.get("head_i", -1),
        "head_text": strongest.get("head_text") or strongest.get("text", ""),
    })
    return span


def _append_span(spans: List[Dict[str, Any]], span_type: str, raw: str,
                 bounds: Tuple[int, int], value: Any = None) -> None:
    tokens = _tokens(raw)
    spans.append(_span(span_type, raw, tokens, bounds[0], bounds[1], value=value))


def _span(span_type: str, raw: str, tokens: List[Dict[str, Any]],
          start: int, end: int, value: Any = None) -> Dict[str, Any]:
    raw_start = tokens[start]["start"]
    raw_end = tokens[end - 1]["end"]
    text = raw[raw_start:raw_end]
    result = {
        "type": span_type,
        "text": text,
        "normalized": normalize_turkish_text(text),
        "start": raw_start,
        "end": raw_end,
    }
    if value is not None:
        result["value"] = value
    return result


def _dedupe_spans(spans: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    result = []
    seen = set()
    for item in spans:
        key = (item.get("type"), item.get("start"), item.get("end"), str(item.get("value")))
        if key not in seen:
            seen.add(key)
            result.append(item)
    return result


def _first(values):
    return values[0] if values else None


def _first_present(normalized: str, phrases: Iterable[str]) -> str:
    padded = " " + normalized + " "
    for phrase in sorted(phrases, key=len, reverse=True):
        candidate = normalize_turkish_text(phrase)
        if " " + candidate + " " in padded:
            return candidate
    return ""


def _contains_any(normalized: str, phrases: Iterable[str]) -> bool:
    return bool(_first_present(normalized, phrases))


def _lemma_supports(lemma: str, phrases: Iterable[str]) -> bool:
    # A bare lemma may support a bare predicate, but it must not inherit the
    # meaning of a qualified multi-token phrase. For example, "çek" in
    # "fotoğraf çek" is not evidence for the MOVE_VERTICAL phrase "yukarı çek".
    return any(
        len(tokens) == 1 and lemma == tokens[0]
        for tokens in (normalize_turkish_text(phrase).split() for phrase in phrases)
    )


def _original_phrase(raw: str, normalized_phrase: str) -> str:
    if not raw or not normalized_phrase:
        return ""
    tokens = _tokens(raw)
    bounds = _first_alias(tokens, (normalized_phrase,))
    if not bounds:
        return ""
    return _span("EVIDENCE", raw, tokens, bounds[0], bounds[1])["text"]


def _strip_wake_word(value: str) -> str:
    return re.sub(r"^\s*kartal\b[\s,;:.-]*", "", value or "", flags=re.IGNORECASE).strip()


def _unique(values: Iterable[str]) -> List[str]:
    result = []
    for value in values:
        if value and value not in result:
            result.append(value)
    return result


def _compact_number(value: float):
    return int(value) if value.is_integer() else value
