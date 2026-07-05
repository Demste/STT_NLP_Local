# -*- coding: utf-8 -*-
"""spaCy analysis extraction for Turkish speech commands."""

from __future__ import annotations

from typing import Any, Dict, List, Optional

from .text_normalization import normalize_turkish


def build_analysis(doc) -> Dict[str, Any]:
    tokens = [_token_to_dict(token) for token in doc]
    entities = [_entity_to_dict(ent) for ent in doc.ents]
    root = _root_to_dict(find_root(doc))

    return {
        "tokens": tokens,
        "entities": entities,
        "root": root,
        "action_candidates": _action_candidates(doc),
        "noun_candidates": _noun_candidates(doc),
        "target_candidates": _target_candidates(doc),
        "spatial_phrase_candidates": _spatial_phrase_candidates(doc),
    }


def find_root(doc):
    for token in doc:
        if token.dep_.upper() == "ROOT":
            return token

    for token in doc:
        if token.head == token:
            return token

    return doc[0] if len(doc) else None


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
        "normalized_text": normalize_turkish(token.text),
        "normalized_lemma": normalize_turkish(token.lemma_),
    }


def _entity_to_dict(ent) -> Dict[str, Any]:
    return {
        "text": ent.text,
        "label": ent.label_,
        "start": ent.start,
        "end": ent.end,
    }


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
        "normalized_text": normalize_turkish(token.text),
        "normalized_lemma": normalize_turkish(token.lemma_),
    }


def _action_candidates(doc) -> List[Dict[str, Any]]:
    candidates = []
    root = find_root(doc)
    for token in doc:
        if token.is_space or token.is_punct:
            continue
        if token.pos_ in ("VERB", "AUX") or token == root:
            candidates.append(_candidate(token, "verb_or_root"))
    return candidates


def _noun_candidates(doc) -> List[Dict[str, Any]]:
    candidates = []
    for token in doc:
        if token.is_space or token.is_punct:
            continue
        if token.pos_ in ("NOUN", "PROPN", "PRON"):
            candidates.append(_candidate(token, "noun_like"))
    return candidates


def _target_candidates(doc) -> List[Dict[str, Any]]:
    candidates: List[Dict[str, Any]] = []
    seen = set()

    for token in doc:
        if token.is_space or token.is_punct:
            continue
        if token.pos_ not in ("NOUN", "PROPN", "PRON"):
            continue

        start = token.i
        end = token.i + 1
        while start > 0:
            previous = doc[start - 1]
            if previous.is_space or previous.is_punct:
                break
            if previous.pos_ in ("ADJ", "NOUN", "PROPN", "NUM", "DET"):
                start -= 1
                continue
            break

        text = doc[start:end].text
        lemma_text = " ".join(part.lemma_ or part.text for part in doc[start:end])
        key = (start, end, text)
        if key in seen:
            continue
        seen.add(key)
        candidates.append({
            "text": text,
            "lemma_text": lemma_text,
            "start": start,
            "end": end,
            "head_i": token.i,
            "head_text": token.text,
            "normalized": normalize_turkish(text),
            "source": "open_vocabulary_noun_phrase",
        })

    return candidates


def _spatial_phrase_candidates(doc) -> List[Dict[str, Any]]:
    spatial_roots = (
        "alt",
        "arka",
        "cevre",
        "etraf",
        "ic",
        "on",
        "sag",
        "sol",
        "ust",
        "uzer",
        "yan",
    )
    candidates = []
    for token in doc:
        if token.is_space or token.is_punct:
            continue
        norm_text = normalize_turkish(token.text)
        norm_lemma = normalize_turkish(token.lemma_)
        if any(norm_text.startswith(root) or norm_lemma.startswith(root) for root in spatial_roots):
            candidates.append(_candidate(token, "spatial_root"))
    return candidates


def _candidate(token, source: str) -> Dict[str, Any]:
    return {
        "i": token.i,
        "text": token.text,
        "lemma": token.lemma_,
        "pos": token.pos_,
        "dep": token.dep_,
        "normalized_text": normalize_turkish(token.text),
        "normalized_lemma": normalize_turkish(token.lemma_),
        "source": source,
    }
