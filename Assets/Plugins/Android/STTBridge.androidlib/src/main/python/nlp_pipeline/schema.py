# -*- coding: utf-8 -*-
"""Schema defaults for speech-command semantic validation."""

from __future__ import annotations

import json
from typing import Any, Dict, List


DEFAULT_ALLOWED_ACTIONS: List[Dict[str, Any]] = [
    {
        "id": "move_above",
        "description": "Move above or onto the target.",
        "examples_tr": ["üstüne çık", "üzerine git", "üstünde dur"],
    },
    {
        "id": "move_to",
        "description": "Move to the target.",
        "examples_tr": ["yanına git", "oraya git", "hedefe git"],
    },
    {
        "id": "approach",
        "description": "Approach the target.",
        "examples_tr": ["yaklaş", "yanına yaklaş"],
    },
    {
        "id": "follow",
        "description": "Follow the target.",
        "examples_tr": ["takip et", "arkasından git"],
    },
    {
        "id": "stop",
        "description": "Stop or hover in place.",
        "examples_tr": ["dur", "bekle"],
    },
    {
        "id": "descend",
        "description": "Move downward.",
        "examples_tr": ["alçal", "aşağı in"],
    },
    {
        "id": "ascend",
        "description": "Move upward.",
        "examples_tr": ["yüksel", "yukarı çık"],
    },
    {
        "id": "unknown",
        "description": "No allowed action fits the utterance.",
        "examples_tr": [],
    },
]

DEFAULT_COMMAND_SCHEMA: Dict[str, Any] = {
    "version": 1,
    "allowed_actions": DEFAULT_ALLOWED_ACTIONS,
    "required_fields": ["action", "target", "confidence"],
    "target_policy": "open_vocabulary",
    "unknown_action": "unknown",
}


def load_config(context_json: str) -> Dict[str, Any]:
    if not context_json:
        return dict(DEFAULT_COMMAND_SCHEMA)

    try:
        parsed = json.loads(context_json)
    except Exception:
        return dict(DEFAULT_COMMAND_SCHEMA)

    if not isinstance(parsed, dict):
        return dict(DEFAULT_COMMAND_SCHEMA)

    config = dict(DEFAULT_COMMAND_SCHEMA)
    config.update(parsed)
    if not config.get("allowed_actions"):
        config["allowed_actions"] = DEFAULT_ALLOWED_ACTIONS
    if "unknown_action" not in config:
        config["unknown_action"] = "unknown"
    if "target_policy" not in config:
        config["target_policy"] = "open_vocabulary"
    return config


def allowed_action_ids(config: Dict[str, Any]) -> List[str]:
    values = []
    for action in config.get("allowed_actions", []):
        if isinstance(action, dict) and action.get("id"):
            values.append(str(action["id"]))
        elif isinstance(action, str):
            values.append(action)
    if "unknown" not in values:
        values.append("unknown")
    return values

