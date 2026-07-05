# -*- coding: utf-8 -*-
"""Non-final rule hints for command parsing.

These rules are intentionally small. They provide evidence and a fallback debug
signal, but the final semantic parser is expected to be an offline LLM followed
by validation.
"""

from __future__ import annotations

from typing import Any, Dict, Iterable, List, Optional, Tuple


def build_baseline_hints(analysis: Dict[str, Any]) -> Dict[str, Any]:
    tokens = analysis.get("tokens", [])
    token_views = [_token_view(token) for token in tokens if not _is_ignored(token)]
    action = _extract_action(token_views, analysis.get("root"))
    target = _extract_open_target(analysis)
    spatial_relation = _extract_spatial_relation(token_views)

    confidence = 0.0
    reasons = []
    if action:
        confidence += 0.3
        reasons.append("action_hint")
    if target:
        confidence += 0.35
        reasons.append("open_target_candidate")
    if spatial_relation:
        confidence += 0.15
        reasons.append("spatial_relation_hint")

    return {
        "action_hint": action or "unknown",
        "target_hint": (target.get("lemma_text") or target.get("text")) if target else None,
        "spatial_relation_hint": spatial_relation["value"] if spatial_relation else None,
        "spatial_relation_text": spatial_relation["text"] if spatial_relation else None,
        "confidence": round(min(confidence, 0.8), 2),
        "reason": ",".join(reasons) if reasons else "no_rule_hint",
        "final_parser": False,
    }


def legacy_drone_command_from_hints(hints: Dict[str, Any]) -> Dict[str, Any]:
    """Compatibility shape for older Unity experiments.

    The value is deprecated and must not be treated as the final semantic parse.
    """
    return {
        "action": hints.get("action_hint") or "unknown",
        "target": hints.get("target_hint"),
        "target_text": hints.get("target_hint"),
        "color": None,
        "color_text": None,
        "spatial_relation": hints.get("spatial_relation_hint"),
        "spatial_relation_text": hints.get("spatial_relation_text"),
        "confidence": hints.get("confidence", 0.0),
        "reason": hints.get("reason", "deprecated_baseline_hint"),
        "deprecated": True,
        "final_parser": False,
    }


def _token_view(token: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "text": token.get("text", ""),
        "lemma": token.get("lemma", ""),
        "norm_text": token.get("normalized_text") or token.get("text", ""),
        "norm_lemma": token.get("normalized_lemma") or token.get("lemma", ""),
        "pos": token.get("pos", ""),
        "i": token.get("i", -1),
    }


def _is_ignored(token: Dict[str, Any]) -> bool:
    dep = str(token.get("dep", "")).lower()
    pos = str(token.get("pos", "")).upper()
    text = str(token.get("text", ""))
    return dep == "punct" or pos == "PUNCT" or not text.strip()


def _extract_action(token_views: List[Dict[str, Any]], root: Optional[Dict[str, Any]]) -> Optional[str]:
    action_roots = [
        ("ust", "move_above"),
        ("uzer", "move_above"),
        ("cik", "ascend"),
        ("git", "move_to"),
        ("yaklas", "approach"),
        ("dur", "stop"),
        ("bekle", "stop"),
        ("in", "descend"),
        ("yuksel", "ascend"),
        ("alcal", "descend"),
        ("takip", "follow"),
    ]

    candidates: List[Dict[str, Any]] = []
    if root:
        candidates.append({
            "norm_text": root.get("text", ""),
            "norm_lemma": root.get("lemma", ""),
        })
    candidates.extend(token_views)

    for candidate in candidates:
        values = (candidate.get("norm_lemma", ""), candidate.get("norm_text", ""))
        for root_text, action in action_roots:
            if any(_matches_root(value, root_text) for value in values):
                return action
    return None


def _extract_open_target(analysis: Dict[str, Any]) -> Optional[Dict[str, Any]]:
    candidates = analysis.get("target_candidates") or analysis.get("noun_candidates") or []
    if not candidates:
        return None
    return candidates[0]


def _extract_spatial_relation(token_views: List[Dict[str, Any]]) -> Optional[Dict[str, str]]:
    spatial_roots = [
        ("yan", "near_or_side"),
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


def _first_token_match(token_views: List[Dict[str, Any]], pairs: Iterable[Tuple[str, str]]) -> Optional[Dict[str, str]]:
    for token in token_views:
        values = (token.get("norm_text", ""), token.get("norm_lemma", ""))
        for root_text, mapped in pairs:
            if any(_matches_root(value, root_text) for value in values):
                return {"value": mapped, "text": token.get("text", "")}
    return None


def _matches_root(value: str, root: str) -> bool:
    return bool(value) and (value == root or value.startswith(root))
