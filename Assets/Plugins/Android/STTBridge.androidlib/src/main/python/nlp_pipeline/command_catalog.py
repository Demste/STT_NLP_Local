# -*- coding: utf-8 -*-
"""Central command vocabulary shared by deterministic parsing and STT recovery."""

from __future__ import annotations


OBJECT_ALIASES = {
    "beton mikseri": "CEMENT_TRUCK", "beton mikser": "CEMENT_TRUCK",
    "cimento kamyon": "CEMENT_TRUCK", "mikser kamyon": "CEMENT_TRUCK",
    "dorseli tir": "SEMI_TRUCK", "cekici tir": "SEMI_TRUCK",
    "kazi makinesi": "EXCAVATOR", "kazi makine": "EXCAVATOR",
    "yukleme makinesi": "LOADER", "yukleme makine": "LOADER",
    "kamyonet": "PICKUP", "pickup": "PICKUP", "pikap": "PICKUP", "pikabi": "PICKUP",
    "otomobil": "CAR", "araba": "CAR", "arac": "CAR",
    "ekskavator": "EXCAVATOR", "kazici": "EXCAVATOR", "kepce": "EXCAVATOR",
    "yukleyici": "LOADER", "loader": "LOADER", "forklift": "FORKLIFT", "kamyon": "TRUCK",
    # Existing contract and tests define unqualified "tır" as SEMI_TRUCK.
    "tir": "SEMI_TRUCK", "cekici": "SEMI_TRUCK", "vinc": "CRANE", "crane": "CRANE",
}

COLOR_ALIASES = {
    "kirmizi": "RED", "yesil": "GREEN", "mavi": "BLUE", "beyaz": "WHITE", "siyah": "BLACK",
}

SHAPE_ALIASES = {
    "cizgi": "LINE", "hat": "LINE", "kare": "SQUARE", "ucgen": "TRIANGLE",
    "arti": "PLUS", "plus": "PLUS",
}

NUMBER_WORDS = {
    "sifir": 0, "bir": 1, "iki": 2, "uc": 3, "dort": 4, "bes": 5, "alti": 6,
    "yedi": 7, "sekiz": 8, "dokuz": 9, "on": 10, "yirmi": 20, "otuz": 30,
    "kirk": 40, "elli": 50, "altmis": 60, "yetmis": 70, "seksen": 80, "doksan": 90,
}

# The catalog intentionally contains action phrases rather than general Turkish
# words. Slot-bearing commands keep their number/object/color evidence in the
# original parser; recovery never invents those values.
ACTION_PHRASES = {
    "TAKE_OFF": ("havalan", "kalk", "uç"),
    "LAND_HOME": ("in", "eve dön", "geri dön", "kalktığın yere in", "başlangıç noktasına dön"),
    "LAND_CURRENT": ("bulunduğun yere in", "olduğun yere in", "mevcut konuma in"),
    "LAND_AT_WAIT_POINT": ("bekleme noktasına in",),
    "MOVE_DIRECTION": (
        "sola git", "sağa git", "ileri git", "geri git", "sola geç", "sağa geç",
        "sola ilerle", "sağa ilerle", "ileri ilerle", "geri ilerle",
    ),
    "MOVE_VERTICAL": ("yüksel", "yukarı çık", "yukarı çek", "alçal", "aşağı in"),
    "SET_SPEED": ("hızla git", "hızını yap", "kilometre saat hızla git", "km/h hızla git"),
    "TURN": ("sağa dön", "sola dön", "saat yönünde dön", "saat yönünün tersine dön"),
    "FIND_OBJECT": ("bul", "ara", "tespit et"),
    "TRACK_OBJECT": ("takip et", "izle", "peşinden git"),
    "HOLD_OVER_OBJECT": ("üzerinde dur", "üstünde bekle", "üzerinde hover yap"),
    "OBSERVE_CURRENT": ("olduğun yerde dur ve izle", "mevcut konumda gözlem yap", "burada bekle ve izle"),
    "GO_TO_LOCATION": ("noktasına git", "konumuna git"),
    "HOVER": ("havada dur", "havada bekle", "hover yap"),
    "FIND_MARKER": ("çizgiyi bul", "kareyi bul", "üçgeni bul", "artı işaretini bul"),
    "STOP": ("dur", "bekle", "sabit kal", "hareket etme"),
}

# Exact, allowlisted Whisper errors. Keys are normalized by command_recovery;
# values are deliberately narrow corrections, not general spell checking.
KNOWN_STT_VARIANTS = {
    "ev edin": "eve dön",
    "eve din": "eve dön",
    "ev don": "eve dön",
    "eve don": "eve dön",
    "alco": "alçal",
    "alcal": "alçal",
    "sol gec": "sola geç",
}

KNOWN_TOKEN_REPAIRS = (
    ("ikim etre", "iki metre", "NUMBER_UNIT_REPAIR", 0.96),
)

# These variants intentionally remain suggestions because the observed suffix
# does not contain enough verb evidence to choose between the close actions.
AMBIGUOUS_STT_VARIANTS = {
    "saga de": (
        ("sağa git", "MOVE_DIRECTION", 0.81),
        ("sağa dön", "TURN", 0.78),
    ),
}

RECOVERY_AUTO_APPLY_SCORE = 0.90
RECOVERY_CONFIRM_SCORE = 0.72
RECOVERY_AUTO_APPLY_GAP = 0.10
RECOVERY_MAX_SUGGESTIONS = 3

CORRECTION_REASONS = (
    "NONE", "EXACT_ALIAS", "KNOWN_STT_VARIANT", "TOKEN_REPAIR", "NUMBER_UNIT_REPAIR",
    "FUZZY_SINGLE_CANDIDATE", "MULTIPLE_CLOSE_CANDIDATES", "LOW_CONFIDENCE",
    "CRITICAL_SLOT_CONFLICT", "VALIDATION_FAILED",
)
