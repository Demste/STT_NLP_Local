# -*- coding: utf-8 -*-
"""Chaquopy entrypoint for real spaCy Turkish command analysis.

The public parse_command function is kept for Android/Unity compatibility.
It now returns linguistic analysis plus non-final baseline hints; semantic
command interpretation is owned by the offline LLM bridge and validator.
"""

from __future__ import annotations

import json
import time
import traceback
from typing import Any, Dict, Optional

from nlp_pipeline.baseline_rules import build_baseline_hints, parse_drone_command
from nlp_pipeline.command_validation import materialize_public_command, validate_command
from nlp_pipeline.spacy_analysis import build_analysis, build_compact_evidence
from nlp_pipeline.text_normalization import normalize_turkish, repair_common_mojibake

MODEL_NAME = "tr_core_news_md"
CONTRACT_VERSION = "action_first_v3_20260713"

_nlp = None
_load_error: Optional[Dict[str, Any]] = None


def parse_command(text: str, context_json: str = "") -> str:
    """Analyze Turkish text with the vendored spaCy model and return JSON."""
    wake_word_suffix = text if text is not None else ""
    context = _context_dict(context_json)
    raw_text = context.get("raw_transcript") if isinstance(context.get("raw_transcript"), str) else wake_word_suffix
    analysis_text = _repair_common_mojibake(wake_word_suffix).strip()

    # Deterministic action/slot extraction is deliberately before spaCy model
    # loading. Clear commands therefore pay neither spaCy nor LLM inference cost.
    baseline_hints = build_baseline_hints({}, analysis_text)
    analysis_text = baseline_hints.get("analysis_input") or analysis_text
    if baseline_hints["final_parser"]:
        internal_command = baseline_hints["command_candidate"]
        public_command = materialize_public_command(internal_command)
        return _json_dumps({
            "ok": True,
            "contract_version": CONTRACT_VERSION,
            "engine": "action_first_baseline",
            "model": None,
            "spacy_version": None,
            "input": analysis_text,
            "raw_input": raw_text,
            "wake_word_suffix": baseline_hints.get("wake_word_suffix", wake_word_suffix),
            "analysis_input": analysis_text,
            "repair_applied": baseline_hints.get("repair_applied", False),
            "repair_candidates": baseline_hints.get("repair_candidates", []),
            "selected_repair": baseline_hints.get("selected_repair", ""),
            "repair_notes": baseline_hints.get("repair_notes", []),
            "repair_duration_ms": baseline_hints.get("repair_duration_ms", 0.0),
            "resolution_status": baseline_hints.get("resolution_status", "RESOLVED"),
            "context_json": context_json or "",
            "root": None,
            "tokens": [],
            "entities": [],
            "analysis": {"tokens": [], "entities": [], "root": None, "action_candidates": [], "noun_candidates": [], "object_phrase_candidates": [], "spatial_phrase_candidates": []},
            "compact_evidence": {"root_lemma": "", "action_candidates": [], "noun_candidates": [], "object_phrase_candidates": [], "spatial_phrase_candidates": []},
            "baseline_hints": baseline_hints,
            "drone_command": internal_command,
            "internal_validated_command": internal_command,
            "public_command": public_command,
            "normalized_command": public_command,
            "validated_command": public_command,
            "spacy_duration_ms": 0.0,
            "baseline_duration_ms": baseline_hints["baseline_duration_ms"],
            "action_resolution_duration_ms": baseline_hints["action_resolution_duration_ms"],
            "slot_extraction_duration_ms": baseline_hints["slot_extraction_duration_ms"],
            "validation_duration_ms": baseline_hints["validation_duration_ms"],
            "total_duration_ms": baseline_hints["baseline_duration_ms"],
            "resolution_source": "baseline",
            "llm_called": False,
            "semantic_parser": {
                "engine": "action_first",
                "status": "resolved_by_baseline",
                "message": "Deterministic result is final.",
            },
        })

    try:
        nlp = _get_nlp()
    except ThrowableCompat as exc:
        return _json_dumps(_error_with_baseline(dict(exc.payload), raw_text, wake_word_suffix, analysis_text, baseline_hints))
    except Exception as exc:
        payload = _error_payload(raw_text, analysis_text, "model_load_failed", exc)
        return _json_dumps(_error_with_baseline(payload, raw_text, wake_word_suffix, analysis_text, baseline_hints))

    try:
        spacy_started = time.perf_counter()
        doc = nlp(analysis_text)
        analysis = build_analysis(doc)
        compact_evidence = build_compact_evidence(analysis)
        spacy_duration_ms = round((time.perf_counter() - spacy_started) * 1000.0, 3)

        payload = {
            "ok": True,
            "contract_version": CONTRACT_VERSION,
            "engine": "real_spacy",
            "model": MODEL_NAME,
            "spacy_version": _spacy_version(),
            "input": analysis_text,
            "raw_input": raw_text,
            "wake_word_suffix": baseline_hints.get("wake_word_suffix", wake_word_suffix),
            "analysis_input": analysis_text,
            "repair_applied": baseline_hints.get("repair_applied", False),
            "repair_candidates": baseline_hints.get("repair_candidates", []),
            "selected_repair": baseline_hints.get("selected_repair", ""),
            "repair_notes": baseline_hints.get("repair_notes", []),
            "repair_duration_ms": baseline_hints.get("repair_duration_ms", 0.0),
            "resolution_status": baseline_hints.get("resolution_status", "NEEDS_ACTION_CLASSIFICATION"),
            "context_json": context_json or "",
            "root": analysis["root"],
            "tokens": analysis["tokens"],
            "entities": analysis["entities"],
            "analysis": analysis,
            "compact_evidence": compact_evidence,
            "baseline_hints": baseline_hints,
            "drone_command": baseline_hints["command_candidate"],
            "internal_validated_command": baseline_hints["command_candidate"],
            "public_command": materialize_public_command(baseline_hints["command_candidate"]),
            "spacy_duration_ms": spacy_duration_ms,
            "baseline_duration_ms": baseline_hints["baseline_duration_ms"],
            "action_resolution_duration_ms": baseline_hints["action_resolution_duration_ms"],
            "slot_extraction_duration_ms": baseline_hints["slot_extraction_duration_ms"],
            "validation_duration_ms": baseline_hints["validation_duration_ms"],
            "total_duration_ms": round(baseline_hints["baseline_duration_ms"] + spacy_duration_ms, 3),
            "resolution_source": baseline_hints["resolution_source"],
            "llm_called": False,
            "semantic_parser": {
                "engine": "action_first",
                "status": baseline_hints["resolution_status"],
                "message": "Deterministic result is final." if baseline_hints["final_parser"] else "Run the required bounded LLM classifier.",
            },
        }
        payload["normalized_command"] = payload["public_command"]
        payload["validated_command"] = payload["public_command"]
        return _json_dumps(payload)
    except Exception as exc:
        payload = _error_payload(raw_text, analysis_text, "parse_failed", exc)
        return _json_dumps(_error_with_baseline(payload, raw_text, wake_word_suffix, analysis_text, baseline_hints))


def validate_llm_command(command_json: str, context_json: str = "") -> str:
    try:
        command = json.loads(command_json or "{}")
    except Exception as exc:
        diagnostics = {}
        internal = validate_command({}, context_json, diagnostics)
        public = materialize_public_command(internal)
        return _json_dumps({
            "ok": False,
            "stage": "llm_json_parse_failed",
            "error": str(exc),
            "internal_validated_command": internal,
            "public_command": public,
            "validated_command": public,
            "errors": diagnostics.get("errors", []),
            "warnings": diagnostics.get("warnings", []),
        })

    action = str(command.get("action") or "UNKNOWN").strip().upper() if isinstance(command, dict) else "UNKNOWN"
    context = _context_dict(context_json)
    original = context.get("original_command") or context.get("input") or context.get("normalized_command") or ""
    evidence = parse_drone_command(original) if isinstance(original, str) and original.strip() else {"action": "UNKNOWN", "confidence": 0.0}
    evidence_action = evidence.get("target_action") if evidence.get("action") == "MISSING_INFO" else evidence.get("action")
    if action == evidence_action:
        candidate = dict(evidence)
    else:
        # The LLM classifies intent only. Its confidence and slot fields are
        # deliberately ignored when deterministic evidence is unavailable.
        candidate = {"action": action, "confidence": 0.0 if action == "UNKNOWN" else 0.76}
    diagnostics = {}
    validated = validate_command(candidate, context_json, diagnostics)
    public = materialize_public_command(validated)
    removed = [key for key in command if key != "action"] if isinstance(command, dict) else []
    diagnostics.setdefault("warnings", []).extend("llm_field_removed:not_classification_field:" + key for key in removed)
    return _json_dumps({
        "ok": True,
        "stage": "validated",
        "internal_validated_command": validated,
        "public_command": public,
        "normalized_command": public,
        "validated_command": public,
        "errors": diagnostics.get("errors", []),
        "warnings": diagnostics.get("warnings", []),
    })


def _context_dict(context_json: str) -> Dict[str, Any]:
    try:
        value = json.loads(context_json or "{}")
        return value if isinstance(value, dict) else {}
    except Exception:
        return {}


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


def _error_with_baseline(
    payload: Dict[str, Any],
    raw_text: str,
    wake_word_suffix: str,
    analysis_text: str,
    baseline_hints: Dict[str, Any],
) -> Dict[str, Any]:
    internal = baseline_hints.get("command_candidate") or {"action": "UNKNOWN", "confidence": 0.0}
    public = materialize_public_command(internal)
    payload.update({
        "input": analysis_text,
        "raw_input": raw_text,
        "wake_word_suffix": wake_word_suffix,
        "analysis_input": analysis_text,
        "repair_applied": baseline_hints.get("repair_applied", False),
        "repair_candidates": baseline_hints.get("repair_candidates", []),
        "selected_repair": baseline_hints.get("selected_repair", ""),
        "repair_notes": baseline_hints.get("repair_notes", []),
        "repair_duration_ms": baseline_hints.get("repair_duration_ms", 0.0),
        "baseline_hints": baseline_hints,
        "drone_command": internal,
        "internal_validated_command": internal,
        "public_command": public,
        "normalized_command": public,
        "validated_command": public,
        "resolution_status": baseline_hints.get("resolution_status", "NEEDS_ACTION_CLASSIFICATION"),
        "resolution_source": "failure",
        "llm_called": False,
        "errors": [str(payload.get("stage") or "python_failure") + ":" + str(payload.get("error") or "unknown")],
        "warnings": [],
    })
    return payload


def _json_dumps(payload: Dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))


class ThrowableCompat(Exception):
    def __init__(self, payload: Dict[str, Any]):
        super().__init__(payload.get("error", "spaCy load failed"))
        self.payload = payload
