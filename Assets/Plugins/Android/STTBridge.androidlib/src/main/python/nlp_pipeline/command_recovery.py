# -*- coding: utf-8 -*-
"""Conservative, catalog-bounded STT command candidate generation and scoring."""

from __future__ import annotations

import hashlib
import re
from difflib import SequenceMatcher
from typing import Any, Dict, List

from .command_catalog import (
    ACTION_PHRASES,
    AMBIGUOUS_STT_VARIANTS,
    COLOR_ALIASES,
    KNOWN_STT_VARIANTS,
    KNOWN_TOKEN_REPAIRS,
    OBJECT_ALIASES,
    SHAPE_ALIASES,
)
from .text_normalization import normalize_turkish_text


_DIRECTIONS = {
    "sol": "LEFT", "sola": "LEFT", "sag": "RIGHT", "saga": "RIGHT",
    "ileri": "FORWARD", "ileriye": "FORWARD", "geri": "BACKWARD", "geriye": "BACKWARD",
    "yukari": "UP", "yuksel": "UP", "asagi": "DOWN", "alcal": "DOWN",
}
_VERB_FAMILIES = {
    "git": "GO", "gec": "GO", "ilerle": "GO",
    "don": "TURN", "cevir": "TURN",
    "bul": "FIND", "ara": "FIND", "tespit": "FIND",
    "takip": "TRACK", "izle": "TRACK",
    "yuksel": "UP", "cik": "UP", "alcal": "DOWN", "in": "DOWN_OR_LAND",
}


def build_recovery_candidates(value: str, request_key: str = "") -> List[Dict[str, Any]]:
    normalized = _suffix(value)
    if not normalized:
        return []

    known = _known_candidates(normalized)
    if known:
        return known

    candidates: List[Dict[str, Any]] = []
    for action, phrases in ACTION_PHRASES.items():
        for phrase in phrases:
            canonical = normalize_turkish_text(phrase)
            conflict = _critical_conflict(normalized, canonical)
            score = _score(normalized, canonical)
            if conflict:
                score = min(score, 0.40)
            candidates.append({
                "corrected_suffix": phrase,
                "candidate_action": action,
                "confidence": round(score, 4),
                "reason": "CRITICAL_SLOT_CONFLICT" if conflict else "FUZZY_SINGLE_CANDIDATE",
                "critical_conflict": conflict,
                "request_key": request_key or "",
            })
    return _dedupe(candidates)


def make_suggestion(candidate: Dict[str, Any], request_key: str, multiple: bool) -> Dict[str, Any]:
    command = str(candidate.get("corrected_suffix") or "")
    action = str(candidate.get("candidate_action") or "UNKNOWN")
    identity = (request_key or "no-request") + "\x1f" + action + "\x1f" + command
    suggestion_id = hashlib.sha256(identity.encode("utf-8")).hexdigest()[:24]
    return {
        "suggestion_id": suggestion_id,
        "message": "\"{}\" komutunu mu vermek istediniz?".format(command),
        "suggested_command": command,
        "suggested_action": action,
        "confidence": float(candidate.get("confidence") or 0.0),
        "reason": "MULTIPLE_CLOSE_CANDIDATES" if multiple else str(candidate.get("reason") or "FUZZY_SINGLE_CANDIDATE"),
    }


def _known_candidates(normalized: str) -> List[Dict[str, Any]]:
    if normalized in AMBIGUOUS_STT_VARIANTS:
        return [{
            "corrected_suffix": command,
            "candidate_action": action,
            "confidence": confidence,
            "reason": "MULTIPLE_CLOSE_CANDIDATES",
            "critical_conflict": False,
        } for command, action, confidence in AMBIGUOUS_STT_VARIANTS[normalized]]

    if normalized in KNOWN_STT_VARIANTS:
        corrected = KNOWN_STT_VARIANTS[normalized]
        return [{
            "corrected_suffix": corrected,
            "candidate_action": "",
            "confidence": 0.94,
            "reason": "KNOWN_STT_VARIANT",
            "critical_conflict": False,
        }]

    repaired = normalized
    reason = ""
    confidence = 0.0
    for source, target, candidate_reason, candidate_confidence in KNOWN_TOKEN_REPAIRS:
        if source in repaired:
            repaired = repaired.replace(source, target)
            reason = candidate_reason
            confidence = max(confidence, candidate_confidence)
    if repaired != normalized:
        return [{
            "corrected_suffix": repaired,
            "candidate_action": "",
            "confidence": confidence,
            "reason": reason or "TOKEN_REPAIR",
            "critical_conflict": False,
        }]
    return []


def _score(source: str, target: str) -> float:
    if source == target:
        return 1.0
    source_tokens = source.split()
    target_tokens = target.split()
    token_score = _token_similarity(source_tokens, target_tokens)
    char_score = SequenceMatcher(None, source, target).ratio()
    order_score = SequenceMatcher(None, source_tokens, target_tokens).ratio()
    verb_score = 1.0 if _verb_family(source_tokens) == _verb_family(target_tokens) and _verb_family(source_tokens) else 0.0
    return min(1.0, 0.60 * char_score + 0.25 * token_score + 0.10 * order_score + 0.05 * verb_score)


def _token_similarity(source_tokens: List[str], target_tokens: List[str]) -> float:
    if not source_tokens or not target_tokens:
        return 0.0

    def directional(left: List[str], right: List[str]) -> float:
        return sum(max(SequenceMatcher(None, token, other).ratio() for other in right) for token in left) / len(left)

    return (directional(source_tokens, target_tokens) + directional(target_tokens, source_tokens)) / 2.0


def _critical_conflict(source: str, target: str) -> bool:
    source_numbers = re.findall(r"-?\d+(?:[.,]\d+)?", source)
    target_numbers = re.findall(r"-?\d+(?:[.,]\d+)?", target)
    if source_numbers and source_numbers != target_numbers:
        return True

    source_direction, target_direction = _slot(source.split(), _DIRECTIONS), _slot(target.split(), _DIRECTIONS)
    if source_direction and target_direction and source_direction != target_direction:
        return True

    source_verb, target_verb = _verb_family(source.split()), _verb_family(target.split())
    if source_verb and target_verb and source_verb != target_verb:
        return True

    for aliases in (OBJECT_ALIASES, COLOR_ALIASES, SHAPE_ALIASES):
        source_slot, target_slot = _phrase_slot(source, aliases), _phrase_slot(target, aliases)
        if source_slot and target_slot and source_slot != target_slot:
            return True
    return False


def _verb_family(tokens: List[str]) -> str:
    return _slot(tokens, _VERB_FAMILIES)


def _slot(tokens: List[str], aliases: Dict[str, str]) -> str:
    for token in tokens:
        for alias, value in aliases.items():
            if token == alias or token.startswith(alias):
                return value
    return ""


def _phrase_slot(value: str, aliases: Dict[str, str]) -> str:
    padded = " " + value + " "
    for alias, mapped in sorted(aliases.items(), key=lambda item: len(item[0]), reverse=True):
        if " " + alias + " " in padded:
            return mapped
    return ""


def _suffix(value: str) -> str:
    normalized = normalize_turkish_text(value or "")
    normalized = re.sub(r"^kartal\b[\s,;:.-]*", "", normalized).strip()
    return normalized.strip(".,;:!? ")


def _dedupe(candidates: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    best: Dict[str, Dict[str, Any]] = {}
    for candidate in candidates:
        key = str(candidate["candidate_action"]) + "\x1f" + str(candidate["corrected_suffix"])
        if key not in best or candidate["confidence"] > best[key]["confidence"]:
            best[key] = candidate
    return sorted(best.values(), key=lambda item: item["confidence"], reverse=True)
