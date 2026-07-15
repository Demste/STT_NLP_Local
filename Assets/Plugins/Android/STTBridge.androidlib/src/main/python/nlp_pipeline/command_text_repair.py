# -*- coding: utf-8 -*-
"""Conservative candidate generation for fused Turkish voice commands."""

from __future__ import annotations

import re
import time
from typing import Any, Dict, List

from .text_normalization import normalize_turkish_key, normalize_turkish_text, repair_common_mojibake


VERB_SUFFIXES = (
    ("takipet", "takip et"),
    ("tespitet", "tespit et"),
    ("hareketet", "hareket et"),
    ("sabitkal", "sabit kal"),
    ("bekle", "bekle"),
    ("bul", "bul"),
    ("ara", "ara"),
    ("izle", "izle"),
    ("git", "git"),
    ("don", "dön"),
    ("dur", "dur"),
    ("in", "in"),
)

# Candidate generation is intentionally limited to domain-bearing stems. The
# deterministic parser still decides whether a repaired candidate is stronger.
SAFE_STEM_ROOTS = (
    "tir", "kamyon", "kamyonet", "araba", "otomobil", "vinc", "forklift",
    "ekskavator", "kepce", "yukleyici", "loader", "cekici", "mikser",
    "isaret", "kare", "ucgen", "cizgi", "arti", "ileri", "geri", "sag",
    "sol", "yukari", "asagi", "ev", "nokta",
)


def build_repair_candidates(value: str) -> Dict[str, Any]:
    started = time.perf_counter()
    original = repair_common_mojibake(value or "").strip()
    candidates: List[str] = [original]
    notes: List[str] = []
    if not original:
        return _result(candidates, notes, started)

    tokens = original.split()
    for index, token in enumerate(tokens):
        normalized = normalize_turkish_key(token)
        for suffix, replacement in VERB_SUFFIXES:
            if not normalized.endswith(suffix) or normalized == suffix:
                continue
            stem_len = len(normalized) - len(suffix)
            stem = normalized[:stem_len]
            if not _safe_stem(stem):
                continue
            # Turkish normalization used here is length-preserving for the
            # command alphabet, so this keeps the user's original noun spelling.
            raw_stem = token[:len(token) - len(suffix)]
            if not raw_stem:
                continue
            repaired_tokens = list(tokens)
            repaired_tokens[index:index + 1] = [raw_stem, replacement]
            repaired = " ".join(repaired_tokens)
            if repaired not in candidates:
                candidates.append(repaired)
                notes.append("split_fused_command_suffix:" + replacement.replace(" ", "_"))
            break

    # Handle the common fully fused spatial form without general suffix stripping.
    normalized_all = normalize_turkish_key(original)
    spatial = re.fullmatch(r"(?P<wake>kartal)?(?P<object>tir(?:in)?|kamyon(?:un)?|araba(?:nin)?)(?:in)?ustunde(?P<verb>bekle|dur)", normalized_all)
    if spatial:
        object_text = spatial.group("object")
        prefix = "Kartal " if spatial.group("wake") else ""
        repaired = prefix + object_text + " üstünde " + spatial.group("verb")
        if repaired not in candidates:
            candidates.append(repaired)
            notes.append("split_fused_spatial_command:ustunde_" + spatial.group("verb"))

    return _result(candidates, notes, started)


def _safe_stem(value: str) -> bool:
    return len(value) >= 3 and any(value.startswith(root) for root in SAFE_STEM_ROOTS)


def _result(candidates: List[str], notes: List[str], started: float) -> Dict[str, Any]:
    return {
        "repair_candidates": candidates,
        "repair_notes": notes,
        "repair_duration_ms": round((time.perf_counter() - started) * 1000.0, 3),
    }
