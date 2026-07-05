# -*- coding: utf-8 -*-
"""Chaquopy entrypoint for real spaCy Turkish command analysis.

The public parse_command function is kept for Android/Unity compatibility.
It now returns linguistic analysis plus non-final baseline hints; semantic
command interpretation is owned by the offline LLM bridge and validator.
"""

from __future__ import annotations

import json
import traceback
from typing import Any, Dict, Optional

from nlp_pipeline.baseline_rules import build_baseline_hints, legacy_drone_command_from_hints
from nlp_pipeline.command_validation import validate_command
from nlp_pipeline.spacy_analysis import build_analysis
from nlp_pipeline.text_normalization import normalize_turkish, repair_common_mojibake

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
        analysis = build_analysis(doc)
        baseline_hints = build_baseline_hints(analysis)

        payload = {
            "ok": True,
            "engine": "real_spacy",
            "model": MODEL_NAME,
            "spacy_version": _spacy_version(),
            "input": analysis_text,
            "raw_input": raw_text,
            "context_json": context_json or "",
            "root": analysis["root"],
            "tokens": analysis["tokens"],
            "entities": analysis["entities"],
            "analysis": analysis,
            "baseline_hints": baseline_hints,
            "drone_command": legacy_drone_command_from_hints(baseline_hints),
            "semantic_parser": {
                "engine": "offline_llm",
                "status": "not_run_by_spacy_bridge",
                "message": "Use OfflineLlmBridge for final semantic command parsing.",
            },
        }
        return _json_dumps(payload)
    except Exception as exc:
        return _json_dumps(_error_payload(raw_text, analysis_text, "parse_failed", exc))


def validate_llm_command(command_json: str, context_json: str = "") -> str:
    try:
        command = json.loads(command_json or "{}")
    except Exception as exc:
        return _json_dumps({
            "ok": False,
            "stage": "llm_json_parse_failed",
            "error": str(exc),
            "validated_command": validate_command({}, context_json),
        })

    return _json_dumps({
        "ok": True,
        "stage": "validated",
        "validated_command": validate_command(command, context_json),
    })


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


def _normalize_turkish(value: str) -> str:
    return normalize_turkish(value)


def _repair_common_mojibake(value: str) -> str:
    return repair_common_mojibake(value)


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
