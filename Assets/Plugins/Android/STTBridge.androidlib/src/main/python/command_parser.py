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

from nlp_pipeline.baseline_rules import build_baseline_hints
from nlp_pipeline.command_recovery import build_semantic_recovery_suggestions
from nlp_pipeline.command_validation import materialize_public_command, validate_candidate_record
from nlp_pipeline.semantic_routing import (
    build_protected_spans,
    build_semantic_frame,
    enrich_action_evidence,
    resolve_semantic_candidate,
)
from nlp_pipeline.spacy_analysis import build_analysis, build_compact_evidence
from nlp_pipeline.text_normalization import repair_common_mojibake

MODEL_NAME = "tr_core_news_md"
CONTRACT_VERSION = "action_first_v3_20260713"

_nlp = None
_load_error: Optional[Dict[str, Any]] = None


def warm_up() -> str:
    """Import and cache the vendored spaCy model without running inference."""
    started = time.perf_counter()
    reused = _nlp is not None
    try:
        _get_nlp()
        return _json_dumps({
            "ok": True,
            "stage": "python_spacy_warmed",
            "python_preinitialized": True,
            "spacy_model_reused": reused,
            "model": MODEL_NAME,
            "durationMs": round((time.perf_counter() - started) * 1000.0, 3),
        })
    except Exception as exc:
        payload = _error_payload("", "", "python_spacy_warm_up_failed", exc)
        payload["durationMs"] = round((time.perf_counter() - started) * 1000.0, 3)
        return _json_dumps(payload)


def parse_command(text: str, context_json: str = "") -> str:
    """Analyze Turkish text with the vendored spaCy model and return JSON."""
    wake_word_suffix = text if text is not None else ""
    context = _context_dict(context_json)
    raw_text = context.get("raw_transcript") if isinstance(context.get("raw_transcript"), str) else wake_word_suffix
    analysis_text = _repair_common_mojibake(wake_word_suffix).strip()

    # Deterministic action/slot extraction is deliberately before spaCy model
    # loading. Clear commands therefore pay neither spaCy nor LLM inference cost.
    recovery_request_key = context.get("recovery_request_key") if isinstance(context.get("recovery_request_key"), str) else ""
    baseline_hints = build_baseline_hints({}, analysis_text, request_key=recovery_request_key)
    analysis_text = baseline_hints.get("analysis_input") or analysis_text
    protected_spans = build_protected_spans(analysis_text)
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
            "protected_spans": protected_spans,
            "semantic_frame": None,
            "candidate_record": {
                "raw_text": raw_text,
                "normalized_text": baseline_hints.get("normalized_suffix", ""),
                "repaired_text": analysis_text,
                "semantic_frame": None,
                "command": internal_command,
                "evidence": baseline_hints.get("action_evidence", {}),
                "source": "deterministic",
            },
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
        semantic_frame_text = baseline_hints.get("original_suffix") or analysis_text
        semantic_frame = build_semantic_frame(doc, analysis, semantic_frame_text, baseline_hints)
        semantic_record = resolve_semantic_candidate(semantic_frame)
        semantic_diagnostics: Dict[str, Any] = {}
        semantic_command = validate_candidate_record(
            semantic_record,
            context_json,
            semantic_diagnostics,
        )
        unresolved_object_span = _first_unresolved_object_span(semantic_frame)
        semantic_needs_object_slot = (
            semantic_record.get("status") == "candidate"
            and semantic_command.get("action") == "MISSING_INFO"
            and semantic_command.get("target_action") in ("FIND_OBJECT", "TRACK_OBJECT", "HOLD_OVER_OBJECT")
            and semantic_command.get("missing_field") == "object"
            and unresolved_object_span is not None
        )
        semantic_final = (
            semantic_record.get("status") == "candidate"
            and semantic_command.get("action") != "UNKNOWN"
            and not semantic_needs_object_slot
        )
        baseline_hints["action_evidence"] = enrich_action_evidence(
            baseline_hints.get("action_evidence"),
            semantic_frame,
        )
        baseline_hints["evidence_supported_actions"] = baseline_hints["action_evidence"]["supported_actions"]
        baseline_hints["protected_spans"] = semantic_frame.get("protected_spans", protected_spans)
        baseline_hints["semantic_frame"] = semantic_frame
        baseline_hints["candidate_record"] = semantic_record
        baseline_hints["semantic_validation_errors"] = semantic_diagnostics.get("errors", [])
        baseline_hints["semantic_validation_warnings"] = semantic_diagnostics.get("warnings", [])

        if semantic_needs_object_slot:
            intended = semantic_command.get("target_action")
            baseline_hints["command_candidate"] = semantic_command
            baseline_hints["action_candidates"] = [intended]
            baseline_hints["final_parser"] = False
            baseline_hints["resolution_status"] = "NEEDS_SLOT_CLASSIFICATION"
            baseline_hints["resolution_source"] = "pending_llm"
            baseline_hints["detected_object_phrase"] = unresolved_object_span.get("text", "")
            baseline_hints["suggestions"] = []
            baseline_hints["candidate_count"] = 0
            baseline_hints["recovery_resolution_status"] = "NO_RECOVERY"
            semantic_record["status"] = "needs_slot_classification"
            semantic_record["command"] = semantic_command
        elif semantic_final:
            intended = semantic_command.get("target_action") if semantic_command.get("action") == "MISSING_INFO" else semantic_command.get("action")
            baseline_hints["command_candidate"] = semantic_command
            baseline_hints["action_candidates"] = [intended] if intended else []
            baseline_hints["final_parser"] = True
            baseline_hints["resolution_status"] = (
                "MISSING_USER_INFO" if semantic_command.get("action") == "MISSING_INFO" else "RESOLVED"
            )
            baseline_hints["resolution_source"] = "spacy"
            semantic_record["command"] = semantic_command
        else:
            suggestions = build_semantic_recovery_suggestions(
                analysis_text,
                recovery_request_key,
                semantic_frame,
                baseline_hints.get("suggestions") or [],
            )
            baseline_hints["suggestions"] = suggestions
            baseline_hints["candidate_count"] = len(suggestions)
            if suggestions:
                baseline_hints["recovery_resolution_status"] = "NEEDS_CONFIRMATION"

        spacy_duration_ms = round((time.perf_counter() - spacy_started) * 1000.0, 3)

        internal_command = semantic_command if semantic_final else baseline_hints["command_candidate"]
        public_command = materialize_public_command(internal_command)
        resolution_status = baseline_hints["resolution_status"]
        resolution_source = baseline_hints["resolution_source"]

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
            "resolution_status": resolution_status,
            "request_id": context.get("request_id", ""),
            "context_json": context_json or "",
            "root": analysis["root"],
            "tokens": analysis["tokens"],
            "entities": analysis["entities"],
            "analysis": analysis,
            "compact_evidence": compact_evidence,
            "protected_spans": semantic_frame.get("protected_spans", protected_spans),
            "semantic_frame": semantic_frame,
            "candidate_record": semantic_record,
            "baseline_hints": baseline_hints,
            "drone_command": internal_command,
            "internal_validated_command": internal_command,
            "public_command": public_command,
            "spacy_duration_ms": spacy_duration_ms,
            "baseline_duration_ms": baseline_hints["baseline_duration_ms"],
            "action_resolution_duration_ms": baseline_hints["action_resolution_duration_ms"],
            "slot_extraction_duration_ms": baseline_hints["slot_extraction_duration_ms"],
            "validation_duration_ms": baseline_hints["validation_duration_ms"],
            "total_duration_ms": round(baseline_hints["baseline_duration_ms"] + spacy_duration_ms, 3),
            "resolution_source": resolution_source,
            "llm_called": False,
            "semantic_parser": {
                "engine": "action_first",
                "status": resolution_status,
                "message": "Semantic result is final." if semantic_final else "Run the required bounded LLM semantic parser.",
            },
        }
        payload["normalized_command"] = payload["public_command"]
        payload["validated_command"] = payload["public_command"]
        return _json_dumps(payload)
    except Exception as exc:
        payload = _error_payload(raw_text, analysis_text, "parse_failed", exc)
        return _json_dumps(_error_with_baseline(payload, raw_text, wake_word_suffix, analysis_text, baseline_hints))


def _first_unresolved_object_span(frame: Dict[str, Any]) -> Optional[Dict[str, Any]]:
    for span in frame.get("object_spans") or []:
        if not isinstance(span, dict):
            continue
        if span.get("type") != "OBJECT" or span.get("field") != "object":
            continue
        if span.get("value") not in (None, "") or span.get("canonical_value") not in (None, ""):
            continue
        if span.get("source") != "spacy_predicate_object_phrase":
            continue
        if str(span.get("text") or "").strip():
            return span
    return None


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
