# -*- coding: utf-8 -*-
"""Text normalization helpers shared by spaCy analysis and validation."""

from __future__ import annotations

import re
import unicodedata


def repair_common_mojibake(value: str) -> str:
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


def normalize_turkish(value: str) -> str:
    text = repair_common_mojibake(value or "")
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
    return re.sub(r"[^0-9a-z]+", "", text)

