# -*- coding: utf-8 -*-
"""Chaquopy entrypoint for real spaCy Turkish command parsing."""

from __future__ import annotations

import json
import re
import traceback
import unicodedata
from typing import Any, Dict, Iterable, List, Optional, Tuple

MODEL_NAME = "tr_core_news_md"

_nlp = None
_load_error: Optional[Dict[str, Any]] = None


def parse_command(text: str, context_json: str = "") -> str:
    """Analyze Turkish text with the vendored spaCy model and return JSON."""
    raw_text = text if text is not None else ""
    analysis_text = _repair_common_mojibake(raw_text).strip()

    try:
        nlp = _get_nlp()
    except ThrowableCompat as exc:
        payload = dict(exc.payload)
        payload["input"] = analysis_text
        payload["raw_input"] = raw_text
        return _json_dumps(payload)
    except Exception as exc:
        return _json_dumps(_error_payload(raw_text, analysis_text, "model_load_failed", exc))

    try:
        doc = nlp(analysis_text)
        tokens = [_token_to_dict(token) for token in doc]
        entities = [_entity_to_dict(ent) for ent in doc.ents]
        root = _root_to_dict(_find_root(doc))
        drone_command = _parse_drone_command(doc)

        payload = {
            "ok": True,
            "engine": "real_spacy",
            "model": MODEL_NAME,
            "spacy_version": _spacy_version(),
            "input": analysis_text,
            "raw_input": raw_text,
            "context_json": context_json or "",
            "root": root,
            "tokens": tokens,
            "entities": entities,
            "drone_command": drone_command,
        }
        return _json_dumps(payload)
    except Exception as exc:
        return _json_dumps(_error_payload(raw_text, analysis_text, "parse_failed", exc))


def _get_nlp():
    global _nlp, _load_error

    if _nlp is not None:
        return _nlp

    if _load_error is not None:
        raise ThrowableCompat(_load_error)

    try:
        import spacy
        import tr_core_news_md

        _nlp = tr_core_news_md.load()
        return _nlp
    except Exception as exc:
        _load_error = _error_payload("", "", "model_load_failed", exc)
        raise ThrowableCompat(_load_error)


def _spacy_version() -> Optional[str]:
    try:
        import spacy

        return spacy.__version__
    except Exception:
        return None


def _token_to_dict(token) -> Dict[str, Any]:
    return {
        "i": token.i,
        "text": token.text,
        "lemma": token.lemma_,
        "pos": token.pos_,
        "tag": token.tag_,
        "dep": token.dep_,
        "morph": str(token.morph),
        "head": token.head.text if token.head is not None else None,
        "head_i": token.head.i if token.head is not None else None,
        "is_alpha": token.is_alpha,
        "is_stop": token.is_stop,
    }


def _entity_to_dict(ent) -> Dict[str, Any]:
    return {
        "text": ent.text,
        "label": ent.label_,
        "start": ent.start,
        "end": ent.end,
    }


def _find_root(doc):
    for token in doc:
        if token.dep_.upper() == "ROOT":
            return token

    for token in doc:
        if token.head == token:
            return token

    return doc[0] if len(doc) else None


def _root_to_dict(token) -> Optional[Dict[str, Any]]:
    if token is None:
        return None

    return {
        "i": token.i,
        "text": token.text,
        "lemma": token.lemma_,
        "pos": token.pos_,
        "tag": token.tag_,
        "dep": token.dep_,
        "morph": str(token.morph),
    }


def _parse_drone_command(doc) -> Dict[str, Any]:
    token_views = [_token_view(token) for token in doc if not token.is_space and not token.is_punct]
    action = _extract_action(token_views, _find_root(doc))
    target = _extract_target(token_views)
    color = _extract_color(token_views)
    spatial_relation = _extract_spatial_relation(token_views)

    if not action and not target:
        return {
            "action": "unknown",
            "target": None,
            "confidence": 0.0,
            "reason": "not_a_drone_command",
        }

    confidence = 0.0
    reasons = []

    if action:
        confidence += 0.35
        reasons.append("action_verb")

    if target:
        confidence += 0.35
        reasons.append("target_object")

    if color:
        confidence += 0.1
        reasons.append("color_modifier")

    if spatial_relation:
        confidence += 0.1
        reasons.append("spatial_relation")

    if action and not target:
        confidence = min(confidence, 0.45)

    return {
        "action": action or "unknown",
        "target": target["value"] if target else None,
        "target_text": target["text"] if target else None,
        "color": color["value"] if color else None,
        "color_text": color["text"] if color else None,
        "spatial_relation": spatial_relation["value"] if spatial_relation else None,
        "spatial_relation_text": spatial_relation["text"] if spatial_relation else None,
        "confidence": round(min(confidence, 0.95), 2),
        "reason": ",".join(reasons) if reasons else "weak_partial_match",
    }


def _token_view(token) -> Dict[str, Any]:
    return {
        "text": token.text,
        "lemma": token.lemma_,
        "norm_text": _normalize_turkish(token.text),
        "norm_lemma": _normalize_turkish(token.lemma_),
        "pos": token.pos_,
        "i": token.i,
    }


def _extract_action(token_views: List[Dict[str, Any]], root) -> Optional[str]:
    action_roots = [
        ("git", "go_to"),
        ("yaklas", "approach"),
        ("dur", "stop_or_hover"),
        ("bekle", "wait"),
        ("in", "descend_or_land"),
        ("yuksel", "ascend"),
        ("alcal", "descend"),
        ("gec", "pass"),
        ("takip", "follow"),
    ]

    candidates: List[Dict[str, Any]] = []

    if root is not None:
        candidates.append(_token_view(root))

    candidates.extend(token_views)

    for candidate in candidates:
        values = (candidate["norm_lemma"], candidate["norm_text"])
        for root_text, action in action_roots:
            if any(_matches_root(value, root_text) for value in values):
                return action

    return None


def _extract_color(token_views: List[Dict[str, Any]]) -> Optional[Dict[str, str]]:
    colors = [
        ("kirmizi", "red"),
        ("sari", "yellow"),
        ("siyah", "black"),
        ("beyaz", "white"),
        ("mavi", "blue"),
        ("yesil", "green"),
        ("turuncu", "orange"),
    ]

    return _first_token_match(token_views, colors)


def _extract_target(token_views: List[Dict[str, Any]]) -> Optional[Dict[str, str]]:
    plus = _find_plus_sign_span(token_views)
    if plus:
        return plus

    multi_word_targets = [
        (("cimento", "kamyon"), "cement_truck"),
        (("kule", "vinc"), "tower_crane"),
    ]
    for words, value in multi_word_targets:
        match = _find_ordered_span(token_views, words)
        if match:
            return {"value": value, "text": match}

    targets = [
        ("arti", "plus_sign"),
        ("daire", "circle"),
        ("kare", "square"),
        ("forklift", "forklift"),
        ("ekskavator", "excavator"),
        ("yukleyici", "loader"),
        ("kamyon", "truck"),
        ("vinc", "crane"),
    ]

    return _first_token_match(token_views, targets)


def _extract_spatial_relation(token_views: List[Dict[str, Any]]) -> Optional[Dict[str, str]]:
    spatial_roots = [
        ("yan", "side_or_near"),
        ("ust", "above_or_on"),
        ("uzer", "above_or_on"),
        ("alt", "below"),
        ("on", "in_front_of"),
        ("arka", "behind"),
        ("sag", "right_of"),
        ("sol", "left_of"),
        ("ic", "inside"),
        ("etraf", "around"),
        ("cevre", "around"),
    ]

    return _first_token_match(token_views, spatial_roots)


def _find_plus_sign_span(token_views: List[Dict[str, Any]]) -> Optional[Dict[str, str]]:
    for index, token in enumerate(token_views):
        if not _matches_root(token["norm_text"], "arti") and not _matches_root(token["norm_lemma"], "arti"):
            continue

        for later in token_views[index + 1:index + 3]:
            if _matches_root(later["norm_text"], "isaret") or _matches_root(later["norm_lemma"], "isaret"):
                return {"value": "plus_sign", "text": token["text"] + " " + later["text"]}

        return {"value": "plus_sign", "text": token["text"]}

    return None


def _find_ordered_span(token_views: List[Dict[str, Any]], words: Iterable[str]) -> Optional[str]:
    expected = list(words)

    for start in range(0, len(token_views)):
        collected = []
        cursor = start

        for word in expected:
            while cursor < len(token_views):
                token = token_views[cursor]
                cursor += 1

                if _matches_root(token["norm_text"], word) or _matches_root(token["norm_lemma"], word):
                    collected.append(token["text"])
                    break
            else:
                break

        if len(collected) == len(expected):
            return " ".join(collected)

    return None


def _first_token_match(token_views: List[Dict[str, Any]], pairs: Iterable[Tuple[str, str]]) -> Optional[Dict[str, str]]:
    for token in token_views:
        values = (token["norm_text"], token["norm_lemma"])
        for root_text, mapped in pairs:
            if any(_matches_root(value, root_text) for value in values):
                return {"value": mapped, "text": token["text"]}

    return None


def _matches_root(value: str, root: str) -> bool:
    if not value:
        return False

    return value == root or value.startswith(root)


def _normalize_turkish(value: str) -> str:
    text = _repair_common_mojibake(value or "")
    text = unicodedata.normalize("NFKC", text).casefold()
    replacements = {
        "\u0131": "i",
        "\u015f": "s",
        "\u011f": "g",
        "\u00fc": "u",
        "\u00f6": "o",
        "\u00e7": "c",
        "\u00e2": "a",
        "\u00ee": "i",
        "\u00fb": "u",
    }
    for source, target in replacements.items():
        text = text.replace(source, target)

    decomposed = unicodedata.normalize("NFD", text)
    text = "".join(ch for ch in decomposed if unicodedata.category(ch) != "Mn")
    text = re.sub(r"[^0-9a-z]+", "", text)
    return text


def _repair_common_mojibake(value: str) -> str:
    if not value or not any(marker in value for marker in ("\u00c3", "\u00c4", "\u00c5")):
        return value

    for encoding in ("latin1", "cp1252"):
        try:
            repaired = value.encode(encoding).decode("utf-8")
            if repaired:
                return repaired
        except Exception:
            continue

    return value


def _error_payload(raw_text: str, analysis_text: str, stage: str, exc: Exception) -> Dict[str, Any]:
    return {
        "ok": False,
        "engine": "real_spacy",
        "model": MODEL_NAME,
        "spacy_version": _spacy_version(),
        "input": analysis_text,
        "raw_input": raw_text,
        "stage": stage,
        "error": str(exc),
        "exception_type": exc.__class__.__name__,
        "traceback": traceback.format_exc(),
    }


def _json_dumps(payload: Dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))


class ThrowableCompat(Exception):
    def __init__(self, payload: Dict[str, Any]):
        super().__init__(payload.get("error", "spaCy load failed"))
        self.payload = payload
