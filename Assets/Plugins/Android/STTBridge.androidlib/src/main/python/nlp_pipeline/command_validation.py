# -*- coding: utf-8 -*-
"""Final command validation gate for LLM command output."""

from __future__ import annotations

from typing import Any, Dict, List

from .schema import allowed_action_ids, load_config


def validate_command(command: Dict[str, Any], context_json: str = "") -> Dict[str, Any]:
    config = load_config(context_json)
    allowed = allowed_action_ids(config)
    unknown_action = str(config.get("unknown_action", "unknown"))

    if not isinstance(command, dict):
        command = {}

    action = str(command.get("action") or unknown_action)
    errors: List[str] = []
    warnings: List[str] = []

    if action not in allowed:
        errors.append("action_not_allowed")
        action = unknown_action if unknown_action in allowed else "unknown"

    target = command.get("target")
    if target is not None and not isinstance(target, str):
        warnings.append("target_coerced_to_string")
        target = str(target)

    try:
        confidence = float(command.get("confidence", 0.0))
    except Exception:
        confidence = 0.0
        warnings.append("confidence_parse_failed")
    confidence = max(0.0, min(1.0, confidence))

    if action == unknown_action:
        confidence = min(confidence, 0.35)

    requires_clarification = bool(command.get("requires_clarification")) or action == unknown_action

    validated = {
        "ok": len(errors) == 0 and not requires_clarification,
        "action": action,
        "target": target,
        "target_policy": "open_vocabulary",
        "confidence": round(confidence, 3),
        "parameters": command.get("parameters") if isinstance(command.get("parameters"), dict) else {},
        "requires_clarification": requires_clarification,
        "reason": command.get("reason") or ("unknown_action" if action == unknown_action else "validated"),
        "errors": errors,
        "warnings": warnings,
        "source": command.get("source") or "validator",
    }
    return validated
