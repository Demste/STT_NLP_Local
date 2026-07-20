# -*- coding: utf-8 -*-
import json
import re
import unittest
from pathlib import Path

from nlp_pipeline.baseline_rules import build_baseline_hints, parse_drone_command
from nlp_pipeline.baseline_rules import parse_drone_command_with_diagnostics
from nlp_pipeline.command_validation import materialize_public_command, validate_command
from nlp_pipeline.schema import ACTION_IDS, DEFAULT_COMMAND_SCHEMA, PUBLIC_FIELDS, allowed_fields
from nlp_pipeline.text_normalization import normalize_turkish_key, normalize_turkish_text
from command_parser import parse_command, validate_llm_command
import command_parser as command_parser_module


class DroneCommandTests(unittest.TestCase):
    CASES = {
        "Kartal havalan": {"action": "TAKE_OFF", "confidence": .98},
        "Kartal kalk": {"action": "TAKE_OFF", "confidence": .98},
        "Kartal uç": {"action": "TAKE_OFF", "confidence": .98},
        "Kartal in": {"action": "LAND_HOME", "confidence": .98},
        "KARTAL, İN!": {"action": "LAND_HOME", "confidence": .98},
        "eve dön": {"action": "LAND_HOME", "confidence": .98},
        "Kartal olduğun yere in": {"action": "LAND_CURRENT", "confidence": .98},
        "Bekleme noktasına in": {"action": "LAND_AT_WAIT_POINT", "confidence": .98},
        "Kartal 15 metre sola git": {"action": "MOVE_DIRECTION", "direction": "LEFT", "distance_m": 15, "confidence": .96},
        "Kartal sola git": {"action": "MISSING_INFO", "direction": "LEFT", "target_action": "MOVE_DIRECTION", "missing_field": "distance_m", "missing_message": "Yatay hareket komutu için metre bilgisi eksik.", "confidence": .96},
        "metre 20 sol tarafa git": {"action": "MOVE_DIRECTION", "direction": "LEFT", "distance_m": 20, "confidence": .96},
        "Kartal 8 metre yüksel": {"action": "MOVE_VERTICAL", "direction": "UP", "distance_m": 8, "confidence": .97},
        "3 metre yüksel.": {"action": "MOVE_VERTICAL", "direction": "UP", "distance_m": 3, "confidence": .97},
        "Kartal yüksel": {"action": "MISSING_INFO", "direction": "UP", "target_action": "MOVE_VERTICAL", "missing_field": "distance_m", "missing_message": "Dikey hareket komutu için metre bilgisi eksik.", "confidence": .94},
        "Kartal 10 metre aşağı in": {"action": "MOVE_VERTICAL", "direction": "DOWN", "distance_m": 10, "confidence": .97},
        "Kartal 30 km/h hızla git": {"action": "SET_SPEED", "speed_kmh": 30, "confidence": .95},
        "kilometre saat 30 hızla git": {"action": "SET_SPEED", "speed_kmh": 30, "confidence": .95},
        "Kartal saatte 20 kilometre hızla git": {"action": "SET_SPEED", "speed_kmh": 20, "confidence": .95},
        "Kartal hızlı git": {"action": "MISSING_INFO", "target_action": "SET_SPEED", "missing_field": "speed_kmh", "missing_message": "Hız komutu için km/h bilgisi eksik.", "confidence": .95},
        "Kartal 90 derece sağa dön": {"action": "TURN", "turn_direction": "RIGHT", "angle_deg": 90, "confidence": .96},
        "Kartal sağa dön": {"action": "MISSING_INFO", "turn_direction": "RIGHT", "target_action": "TURN", "missing_field": "angle_deg", "missing_message": "Dönüş komutu için açı bilgisi eksik.", "confidence": .96},
        "Tırı bul": {"action": "FIND_OBJECT", "object": "SEMI_TRUCK", "confidence": .97},
        "TIRI, BUL!": {"action": "FIND_OBJECT", "object": "SEMI_TRUCK", "confidence": .97},
        "Tırı izle": {"action": "TRACK_OBJECT", "object": "SEMI_TRUCK", "confidence": .97},
        "Takip et": {"action": "MISSING_INFO", "target_action": "TRACK_OBJECT", "missing_field": "object", "missing_message": "Takip komutu için hedef nesne bilgisi eksik.", "confidence": .92},
        "Forklift üzerinde dur": {"action": "HOLD_OVER_OBJECT", "object": "FORKLIFT", "confidence": .97},
        "Olduğun yerde dur ve izle": {"action": "OBSERVE_CURRENT", "observe_mode": "HOLD_AND_WATCH", "confidence": .96},
        "30 saniye burada bekle ve izle": {"action": "OBSERVE_CURRENT", "observe_mode": "HOLD_AND_WATCH", "duration_s": 30, "confidence": .96},
        "A noktasına git": {"action": "GO_TO_LOCATION", "location_keyword": "A noktası", "confidence": .92},
        "39.9208, 32.8541 konumuna git": {"action": "GO_TO_LOCATION", "coordinate": {"lat": 39.9208, "lon": 32.8541}, "confidence": .98},
        "Konuma git": {"action": "MISSING_INFO", "target_action": "GO_TO_LOCATION", "missing_field": "location_keyword", "missing_message": "Konuma gitme komutu için hedef konum bilgisi eksik.", "confidence": .92},
        "Havada bekle": {"action": "HOVER", "confidence": .98},
        "2 dakika hover yap": {"action": "HOVER", "duration_min": 2, "confidence": .98},
        "Kırmızı kareyi bul": {"action": "FIND_MARKER", "color": "RED", "shape": "SQUARE", "confidence": .96},
        "Kırmızı artı işareti 3 olanı bul": {"action": "FIND_MARKER", "color": "RED", "shape": "PLUS", "marker_index": 3, "confidence": .96},
        "Kırmızı işareti bul": {"action": "MISSING_INFO", "color": "RED", "target_action": "FIND_MARKER", "missing_field": "shape", "missing_message": "İşaret bulma komutu için şekil bilgisi eksik.", "confidence": .96},
        "Kartal dur": {"action": "STOP", "confidence": .96},
        "Bugün hava çok güzel": {"action": "UNKNOWN", "confidence": 0.0},
    }

    def test_action_first_examples_and_sparse_contract(self):
        for text, expected in self.CASES.items():
            with self.subTest(text=text):
                result = parse_drone_command(text)
                self.assertEqual(expected, result)
                self.assertIn(result["action"], ACTION_IDS)
                self.assertNotIn(None, result.values())
                self.assertTrue(set(result).issubset(allowed_fields(result["action"], result.get("target_action", ""))))

    def test_hallucinated_fields_are_removed_for_land_home(self):
        diagnostics = {}
        result = validate_command({
            "action": "LAND_HOME", "direction": "LEFT", "distance_m": 8,
            "object": "TRUCK", "color": "GREEN", "coordinate": {"lat": 36, "lon": 36},
            "confidence": .8,
        }, diagnostics=diagnostics)
        self.assertEqual({"action": "LAND_HOME", "confidence": .8}, result)
        self.assertTrue(any(value.startswith("field_removed:not_allowed_for_action") for value in diagnostics["warnings"]))

    def test_turn_angle_and_direction_are_required(self):
        missing_angle = validate_command({"action": "TURN", "turn_direction": "RIGHT", "confidence": .9})
        self.assertEqual("MISSING_INFO", missing_angle["action"])
        self.assertEqual("angle_deg", missing_angle["missing_field"])
        result = validate_command({"action": "TURN", "angle_deg": 90, "confidence": .9})
        self.assertEqual("MISSING_INFO", result["action"])
        self.assertEqual("TURN", result["target_action"])
        self.assertEqual("turn_direction", result["missing_field"])
        self.assertEqual("Dönüş komutu için yön bilgisi eksik.", result["missing_message"])
        self.assertEqual(.9, result["confidence"])

    def test_every_required_action_has_a_missing_info_variant(self):
        incomplete = {
            "MOVE_DIRECTION": {"direction": "LEFT"},
            "MOVE_VERTICAL": {"direction": "UP"},
            "SET_SPEED": {},
            "TURN": {},
            "FIND_OBJECT": {},
            "TRACK_OBJECT": {},
            "HOLD_OVER_OBJECT": {},
            "GO_TO_LOCATION": {},
            "FIND_MARKER": {"color": "RED"},
        }
        for action, fields in incomplete.items():
            with self.subTest(action=action):
                result = validate_command({"action": action, "confidence": .9, **fields})
                self.assertEqual("MISSING_INFO", result["action"])
                self.assertEqual(action, result["target_action"])
                self.assertTrue(result["missing_field"])
                self.assertTrue(result["missing_message"])

    def test_voice_metadata_is_upstream_only(self):
        result = validate_command({"action": "TAKE_OFF", "voice_type": "HOT", "stt_confidence_level": "GOOD", "confidence": .9})
        self.assertEqual({"action": "TAKE_OFF", "confidence": .9}, result)
        trusted = validate_command({"action": "TAKE_OFF", "confidence": .9}, json.dumps({"voice_type": "NORMAL", "stt_confidence_level": "GOOD"}))
        self.assertEqual("NORMAL", trusted["voice_type"])
        self.assertEqual("GOOD", trusted["stt_confidence_level"])

    def test_baseline_diagnostics_mark_llm_skipped(self):
        hints = build_baseline_hints({}, "8 metre yüksel")
        self.assertTrue(hints["final_parser"])
        self.assertFalse(hints["llm_called"])
        self.assertEqual("baseline", hints["resolution_source"])
        for field in ("baseline_duration_ms", "action_resolution_duration_ms", "slot_extraction_duration_ms", "validation_duration_ms"):
            self.assertGreaterEqual(hints[field], 0)

    def test_clear_command_skips_spacy_model_and_llm(self):
        payload = json.loads(parse_command("8 metre yüksel"))
        self.assertEqual("action_first_baseline", payload["engine"])
        self.assertEqual("action_first_v3_20260713", payload["contract_version"])
        self.assertEqual(0.0, payload["spacy_duration_ms"])
        self.assertFalse(payload["llm_called"])
        self.assertEqual({"action": "MOVE_VERTICAL", "direction": "UP", "distance_m": 8.0, "confidence": .97}, payload["drone_command"])
        unknown = json.loads(parse_command("bugün hava çok güzel"))
        self.assertFalse(unknown["llm_called"])
        self.assertEqual({"action": "UNKNOWN", "confidence": 0.0}, unknown["drone_command"])

    def test_explicit_turn_phrases_are_deterministic_and_skip_spacy_and_llm(self):
        cases = {
            "sağ dön": ("RIGHT", None),
            "sağa dön": ("RIGHT", None),
            "sağ tarafa dön": ("RIGHT", None),
            "sol dön": ("LEFT", None),
            "sola dön": ("LEFT", None),
            "sol tarafa dön": ("LEFT", None),
            "90 derece sağa dön": ("RIGHT", 90),
            "45 derece sola dön": ("LEFT", 45),
            "sağ yönüne dön": ("RIGHT", None),
            "sol yönüne dön": ("LEFT", None),
            "Sağ dön.": ("RIGHT", None),
            "SAĞA DÖN!": ("RIGHT", None),
            "Kartal sağ dön.": ("RIGHT", None),
        }
        for text, (direction, angle) in cases.items():
            with self.subTest(text=text):
                payload = json.loads(parse_command(text))
                command = payload["drone_command"]
                self.assertEqual("RESOLVED" if angle is not None else "MISSING_USER_INFO", payload["resolution_status"])
                self.assertEqual("baseline", payload["resolution_source"])
                self.assertEqual(0.0, payload["spacy_duration_ms"])
                self.assertFalse(payload["llm_called"])
                self.assertEqual("TURN" if angle is not None else "MISSING_INFO", command["action"])
                if angle is None:
                    self.assertEqual("TURN", command["target_action"])
                    self.assertEqual("angle_deg", command["missing_field"])
                self.assertEqual(direction, command["turn_direction"])
                if angle is None:
                    self.assertNotIn("angle_deg", command)
                else:
                    self.assertEqual(angle, command["angle_deg"])

    def test_turn_words_do_not_steal_move_object_or_compound_commands(self):
        movement_cases = {
            "sağa git": "RIGHT",
            "sağa ilerle": "RIGHT",
            "sağa hareket et": "RIGHT",
            "sola git": "LEFT",
        }
        for text, direction in movement_cases.items():
            with self.subTest(text=text):
                command = parse_drone_command(text)
                self.assertEqual("MISSING_INFO", command["action"])
                self.assertEqual("MOVE_DIRECTION", command["target_action"])
                self.assertEqual(direction, command["direction"])

        self.assertEqual("FIND_OBJECT", parse_drone_command("sağdaki arabayı bul")["action"])
        marker = parse_drone_command("sol taraftaki işareti bul")
        self.assertEqual("MISSING_INFO", marker["action"])
        self.assertEqual("FIND_MARKER", marker["target_action"])

        compound, diagnostics = parse_drone_command_with_diagnostics("sağa dön ve ilerle")
        self.assertEqual("UNKNOWN", compound["action"])
        self.assertEqual("NEEDS_ACTION_CLASSIFICATION", diagnostics["resolution_status"])

    def test_spacy_failure_stays_explicit_and_keeps_fixed_public_contract(self):
        previous_nlp = command_parser_module._nlp
        previous_error = command_parser_module._load_error
        try:
            command_parser_module._nlp = None
            command_parser_module._load_error = {
                "ok": False, "engine": "real_spacy", "stage": "model_load_failed",
                "error": "forced_test_failure",
            }
            payload = json.loads(parse_command("ağır vasıtayı bul", json.dumps({"raw_transcript": "Kartal ağır vasıtayı bul"}, ensure_ascii=False)))
        finally:
            command_parser_module._nlp = previous_nlp
            command_parser_module._load_error = previous_error
        self.assertFalse(payload["ok"])
        self.assertEqual("model_load_failed", payload["stage"])
        self.assertEqual("Kartal ağır vasıtayı bul", payload["raw_input"])
        self.assertEqual("ağır vasıtayı bul", payload["wake_word_suffix"])
        self.assertEqual("NEEDS_SLOT_CLASSIFICATION", payload["resolution_status"])
        self.assertEqual(PUBLIC_FIELDS, list(payload["public_command"]))
        self.assertEqual("MISSING_INFO", payload["public_command"]["action"])
        self.assertTrue(payload["errors"])

    def test_python_llm_validator_uses_original_command_evidence(self):
        malicious = json.dumps({"action": "LAND_HOME", "direction": "LEFT", "distance_m": 8, "object": "TRUCK"})
        payload = json.loads(validate_llm_command(malicious, json.dumps({"original_command": "in"})))
        self.assertEqual({"action": "LAND_HOME", "confidence": .98}, payload["internal_validated_command"])
        self.assertEqual(set(PUBLIC_FIELDS), set(payload["public_command"]))
        self.assertIsNone(payload["public_command"]["object"])
        self.assertIn("llm_field_removed:not_classification_field:direction", payload["warnings"])

    def test_fused_commands_are_repaired_before_resolution(self):
        expected = {
            "Kartal tırıbul": ("Kartal tırı bul", "FIND_OBJECT", "SEMI_TRUCK"),
            "Kartal kamyonubul": ("Kartal kamyonu bul", "FIND_OBJECT", "TRUCK"),
            "Kartal arabayıizle": ("Kartal arabayı izle", "TRACK_OBJECT", "CAR"),
            "Kartal tırıtakipet": ("Kartal tırı takip et", "TRACK_OBJECT", "SEMI_TRUCK"),
            "Kartal tırınüstündebekle": ("Kartal tirin üstünde bekle", "HOLD_OVER_OBJECT", "SEMI_TRUCK"),
        }
        for text, (analysis_input, action, object_value) in expected.items():
            with self.subTest(text=text):
                command, diagnostics = parse_drone_command_with_diagnostics(text)
                self.assertTrue(diagnostics["repair_applied"])
                self.assertEqual(analysis_input, diagnostics["analysis_input"])
                self.assertEqual("RESOLVED", diagnostics["resolution_status"])
                self.assertEqual(action, command["action"])
                self.assertEqual(object_value, command["object"])

    def test_resolution_status_separates_slot_missing_and_domain_cases(self):
        cases = {
            "Kartal ağır vasıtayı bul": "NEEDS_SLOT_CLASSIFICATION",
            "Kartal kazı makinesini bul": "RESOLVED",
            "Kartal yük aracını takip et": "NEEDS_SLOT_CLASSIFICATION",
            "Kartal bul": "MISSING_USER_INFO",
            "Kartal takip et": "MISSING_USER_INFO",
            "Kartal bugün hava çok güzel": "OUT_OF_DOMAIN",
            "Kartal kabul et": "OUT_OF_DOMAIN",
        }
        for text, expected in cases.items():
            with self.subTest(text=text):
                _, diagnostics = parse_drone_command_with_diagnostics(text)
                self.assertEqual(expected, diagnostics["resolution_status"])

    def test_false_positive_prefixes_do_not_create_object_or_action(self):
        for text in ("Kartal tırnak işaretini bul", "Kartal bulutları izle", "Kartal durum nedir", "Kartal kabul et"):
            with self.subTest(text=text):
                command, _ = parse_drone_command_with_diagnostics(text)
                self.assertNotIn("object", command)
        self.assertEqual("UNKNOWN", parse_drone_command("Kartal durum nedir")["action"])
        self.assertEqual("UNKNOWN", parse_drone_command("Kartal kabul et")["action"])

    def test_text_and_key_normalization_have_distinct_contracts(self):
        self.assertEqual("tiri bul", normalize_turkish_text("  Tırı   Bul  "))
        self.assertEqual("betonmikseri", normalize_turkish_key("Beton Mikseri"))

    def test_python_java_and_unity_contracts_do_not_drift(self):
        java = (Path(__file__).parents[2] / "main/java/com/mehmet/sttbridge/llm/LlmCommandSchema.java").read_text(encoding="utf-8")
        public_java = (Path(__file__).parents[2] / "main/java/com/mehmet/sttbridge/llm/LlmPublicCommandSerializer.java").read_text(encoding="utf-8")
        unity = (Path(__file__).parents[6] / "Game/NLP/Runtime/Models/ValidatedCommandResult.cs").read_text(encoding="utf-8")

        def values(source, name):
            block = re.search(r"\b" + name + r"\s*=\s*\{(?P<body>.*?)\};", source, re.S).group("body")
            return re.findall(r'"([A-Za-z_]+)"', block)

        contracts = (
            (ACTION_IDS, "ACTIONS", "Actions"),
            (DEFAULT_COMMAND_SCHEMA["direction_values"], "DIRECTIONS", "Directions"),
            (DEFAULT_COMMAND_SCHEMA["turn_direction_values"], "TURN_DIRECTIONS", "TurnDirections"),
            (DEFAULT_COMMAND_SCHEMA["object_values"], "OBJECTS", "Objects"),
            (DEFAULT_COMMAND_SCHEMA["color_values"], "COLORS", "Colors"),
            (DEFAULT_COMMAND_SCHEMA["shape_values"], "SHAPES", "Shapes"),
            (DEFAULT_COMMAND_SCHEMA["observe_mode_values"], "OBSERVE_MODES", "ObserveModes"),
            (DEFAULT_COMMAND_SCHEMA["voice_type_values"], "VOICE_TYPES", "VoiceTypes"),
            (DEFAULT_COMMAND_SCHEMA["stt_confidence_level_values"], "STT_CONFIDENCE_LEVELS", "SttConfidenceLevels"),
        )
        for expected, java_name, unity_name in contracts:
            with self.subTest(contract=java_name):
                self.assertEqual(expected, values(java, java_name))
                self.assertEqual(expected, values(unity, unity_name))
        self.assertEqual(PUBLIC_FIELDS, values(public_java, "PUBLIC_FIELDS"))
        self.assertEqual(PUBLIC_FIELDS, values(unity, "PublicFields"))

    def test_all_binding_alias_variants_are_deterministic(self):
        object_aliases = {
            "TRUCK": ("kamyon", "kamyonu", "yük kamyonu"),
            "CEMENT_TRUCK": ("beton mikseri", "mikser kamyonu", "çimento kamyonu"),
            "SEMI_TRUCK": ("tır", "tırı", "tırın", "çekici", "dorseli tır"),
            "PICKUP": ("pikap", "pikabı", "pickup"),
            "CAR": ("araba", "arabayı", "otomobil", "araç"),
            "EXCAVATOR": ("ekskavatör", "kazıcı", "kepçe", "kazı makinesi"),
            "LOADER": ("loader", "yükleyici", "yükleme makinesi"),
            "FORKLIFT": ("forklift", "forklifti", "forkliftin"),
            "CRANE": ("vinç", "vinci", "crane"),
        }
        for expected, aliases in object_aliases.items():
            for alias in aliases:
                with self.subTest(alias=alias):
                    command = parse_drone_command("Kartal " + alias + " bul")
                    self.assertEqual("FIND_OBJECT", command["action"])
                    self.assertEqual(expected, command["object"])

        for alias, expected in (("kırmızı", "RED"), ("yeşil", "GREEN"), ("mavi", "BLUE"), ("beyaz", "WHITE"), ("siyah", "BLACK")):
            with self.subTest(color=alias):
                self.assertEqual(expected, parse_drone_command("Kartal " + alias + " kareyi bul")["color"])
        for alias, expected in (("çizgi", "LINE"), ("hat", "LINE"), ("kare", "SQUARE"), ("üçgen", "TRIANGLE"), ("artı", "PLUS"), ("plus", "PLUS")):
            with self.subTest(shape=alias):
                self.assertEqual(expected, parse_drone_command("Kartal kırmızı " + alias + " işaretini bul")["shape"])

    def test_fixed_public_shape_and_unknown_metadata_policy(self):
        sparse = validate_command(
            {"action": "UNKNOWN", "confidence": .8},
            json.dumps({"voice_type": "NORMAL", "stt_confidence_level": "GOOD"}),
        )
        public = materialize_public_command(sparse)
        self.assertEqual(PUBLIC_FIELDS, list(public))
        self.assertEqual("UNKNOWN", public["action"])
        self.assertEqual(0.0, public["confidence"])
        self.assertEqual("NORMAL", public["voice_type"])
        self.assertEqual("GOOD", public["stt_confidence_level"])
        self.assertTrue(set(sparse).issubset(allowed_fields("UNKNOWN")))
        for field in PUBLIC_FIELDS:
            if field not in ("action", "confidence", "voice_type", "stt_confidence_level"):
                self.assertIsNone(public[field], field)

    def test_command_recovery_known_variants_and_number_unit_repair(self):
        cases = {
            "ev edin": ("eve dön", "LAND_HOME", "KNOWN_STT_VARIANT"),
            "ev edin.": ("eve dön", "LAND_HOME", "KNOWN_STT_VARIANT"),
            "eve din": ("eve dön", "LAND_HOME", "KNOWN_STT_VARIANT"),
            "ev dön": ("eve dön", "LAND_HOME", "KNOWN_STT_VARIANT"),
            "ev edın": ("eve dön", "LAND_HOME", "KNOWN_STT_VARIANT"),
            "alço": ("alçal", "MISSING_INFO", "KNOWN_STT_VARIANT"),
            "alcal": ("alçal", "MISSING_INFO", "KNOWN_STT_VARIANT"),
            "ikim etre sağa git": ("iki metre saga git", "MOVE_DIRECTION", "NUMBER_UNIT_REPAIR"),
        }
        for text, (corrected, action, reason) in cases.items():
            with self.subTest(text=text):
                command, diagnostics = parse_drone_command_with_diagnostics(text, request_key="known-variant")
                self.assertEqual(action, command["action"])
                self.assertTrue(diagnostics["correction_applied"])
                self.assertEqual(corrected, diagnostics["corrected_suffix"])
                self.assertEqual(reason, diagnostics["correction_reason"])
                self.assertGreaterEqual(diagnostics["correction_confidence"], .90)
                self.assertEqual("deterministic_recovery", diagnostics["parser_source"])

        movement, _ = parse_drone_command_with_diagnostics("ikim etre sağa git")
        self.assertEqual("RIGHT", movement["direction"])
        self.assertEqual(2, movement["distance_m"])
        down, _ = parse_drone_command_with_diagnostics("alço")
        self.assertEqual("DOWN", down["direction"])
        self.assertEqual("distance_m", down["missing_field"])

    def test_recovery_preserves_valid_originals_and_skips_llm(self):
        cases = {
            "eve dön": {"action": "LAND_HOME"},
            "20 metre sağa git": {"action": "MOVE_DIRECTION", "direction": "RIGHT", "distance_m": 20},
            "15 metre yüksel": {"action": "MOVE_VERTICAL", "direction": "UP", "distance_m": 15},
            "30 derece sağa dön": {"action": "TURN", "turn_direction": "RIGHT", "angle_deg": 30},
        }
        for text, expected in cases.items():
            with self.subTest(text=text):
                payload = json.loads(parse_command(text))
                self.assertFalse(payload["llm_called"])
                self.assertFalse(payload["baseline_hints"]["correction_applied"])
                self.assertEqual("RESOLVED_ORIGINAL", payload["baseline_hints"]["recovery_resolution_status"])
                for field, value in expected.items():
                    self.assertEqual(value, payload["drone_command"].get(field), field)

    def test_extended_catalog_aliases_remain_deterministic(self):
        cases = {
            "geri dön": {"action": "LAND_HOME"},
            "başlangıç noktasına dön": {"action": "LAND_HOME"},
            "sağa geç": {"action": "MISSING_INFO", "target_action": "MOVE_DIRECTION", "direction": "RIGHT", "missing_field": "distance_m"},
            "yukarı çek": {"action": "MISSING_INFO", "target_action": "MOVE_VERTICAL", "direction": "UP", "missing_field": "distance_m"},
        }
        for text, expected in cases.items():
            with self.subTest(text=text):
                payload = json.loads(parse_command(text))
                self.assertFalse(payload["llm_called"])
                for field, value in expected.items():
                    self.assertEqual(value, payload["drone_command"].get(field), field)

    def test_recovery_does_not_cross_critical_slots_or_damage_coordinates(self):
        negatives = ("eve git", "sağa dön", "sağa git", "aşağı bak", "bugün hava çok güzel")
        for text in negatives:
            with self.subTest(text=text):
                _, diagnostics = parse_drone_command_with_diagnostics(text)
                self.assertFalse(diagnostics["correction_applied"])

        left, left_diagnostics = parse_drone_command_with_diagnostics("sola gti")
        self.assertNotEqual("RIGHT", left.get("direction"))
        self.assertFalse(left_diagnostics["correction_applied"])
        up, up_diagnostics = parse_drone_command_with_diagnostics("yukarı bk")
        self.assertNotEqual("DOWN", up.get("direction"))
        self.assertFalse(up_diagnostics["correction_applied"])

        coordinate, diagnostics = parse_drone_command_with_diagnostics("39.9208, 32.8541 konumuna git")
        self.assertEqual({"lat": 39.9208, "lon": 32.8541}, coordinate["coordinate"])
        self.assertEqual("39.9208, 32.8541 konumuna git", diagnostics["normalized_suffix"])

    def test_auto_apply_adversarial_pairs_never_cross_actions_or_slots(self):
        cases = {
            "sağa git": ("MOVE_DIRECTION", {"direction": "RIGHT"}),
            "sağa dön": ("TURN", {"turn_direction": "RIGHT"}),
            "sola git": ("MOVE_DIRECTION", {"direction": "LEFT"}),
            "sola dön": ("TURN", {"turn_direction": "LEFT"}),
            "dur": ("STOP", {}),
            "uç": ("TAKE_OFF", {}),
            "in": ("LAND_HOME", {}),
            "bin": ("UNKNOWN", {}),
            "bul": ("FIND_OBJECT", {}),
            "izle": ("TRACK_OBJECT", {}),
            "gizle": ("UNKNOWN", {}),
            "ileri git": ("MOVE_DIRECTION", {"direction": "FORWARD"}),
            "geri git": ("MOVE_DIRECTION", {"direction": "BACKWARD"}),
            "yukarı çık": ("MOVE_VERTICAL", {"direction": "UP"}),
            "aşağı in": ("MOVE_VERTICAL", {"direction": "DOWN"}),
        }
        for text, (intended_action, slots) in cases.items():
            with self.subTest(text=text):
                command, diagnostics = parse_drone_command_with_diagnostics(text)
                action = command.get("target_action") if command.get("action") == "MISSING_INFO" else command.get("action")
                self.assertEqual(intended_action, action)
                self.assertFalse(diagnostics["correction_applied"])
                for field, expected in slots.items():
                    self.assertEqual(expected, command.get(field), field)

        # Explicit slot evidence may result in no recovery or confirmation, but
        # never in an opposite auto-applied slot.
        for text, forbidden_field, forbidden_value in (
            ("sağa gti", "direction", "LEFT"),
            ("sola gti", "direction", "RIGHT"),
            ("yukarı bk", "direction", "DOWN"),
            ("aşağı bk", "direction", "UP"),
        ):
            with self.subTest(text=text):
                command, diagnostics = parse_drone_command_with_diagnostics(text)
                self.assertFalse(diagnostics["correction_applied"])
                self.assertNotEqual(forbidden_value, command.get(forbidden_field))

    def test_known_recovery_inputs_are_not_stopped_as_out_of_domain(self):
        cases = {
            "ev edin": ("LAND_HOME", "RESOLVED_AUTO_CORRECTION"),
            "alço": ("MOVE_VERTICAL", "MISSING_INFO"),
            "ikim etre sağa git": ("MOVE_DIRECTION", "RESOLVED_AUTO_CORRECTION"),
        }
        for text, (expected_action, expected_recovery_status) in cases.items():
            with self.subTest(text=text):
                command, diagnostics = parse_drone_command_with_diagnostics(text)
                action = command.get("target_action") if command.get("action") == "MISSING_INFO" else command.get("action")
                self.assertEqual(expected_action, action)
                self.assertTrue(diagnostics["correction_applied"])
                self.assertNotEqual("OUT_OF_DOMAIN", diagnostics["resolution_status"])
                self.assertEqual(expected_recovery_status, diagnostics["recovery_resolution_status"])

    def test_action_evidence_gate_rejects_hallucinated_missing_info(self):
        payload = json.loads(parse_command("sadın"))
        hints = payload["baseline_hints"]
        self.assertEqual("UNKNOWN", payload["public_command"]["action"])
        self.assertEqual("OUT_OF_DOMAIN", payload["resolution_status"])
        self.assertFalse(payload["llm_called"])
        self.assertFalse(hints["action_evidence"]["has_any"])
        self.assertEqual([], hints["evidence_supported_actions"])
        self.assertIsNone(payload["public_command"]["target_action"])
        self.assertIsNone(payload["public_command"]["missing_field"])
        self.assertLessEqual(payload["public_command"]["confidence"], 0.35)

        rejected = json.loads(validate_llm_command(
            json.dumps({"action": "MOVE_DIRECTION"}),
            json.dumps({"original_command": "sadın"}),
        ))
        self.assertEqual("UNKNOWN", rejected["public_command"]["action"])
        self.assertIsNone(rejected["public_command"]["target_action"])
        self.assertIsNone(rejected["public_command"]["missing_field"])

        compound, diagnostics = parse_drone_command_with_diagnostics("sağa dön ve ilerle")
        self.assertEqual("UNKNOWN", compound["action"])
        self.assertTrue(diagnostics["action_evidence"]["has_any"])
        self.assertIn("TURN", diagnostics["evidence_supported_actions"])
        self.assertIn("MOVE_DIRECTION", diagnostics["evidence_supported_actions"])

    def test_multiple_close_candidates_require_confirmation_and_never_emit_command(self):
        payload = json.loads(parse_command(
            "sağa de",
            json.dumps({"recovery_request_key": "request-a"}),
        ))
        hints = payload["baseline_hints"]
        self.assertEqual("NEEDS_CONFIRMATION", payload["resolution_status"])
        self.assertEqual("NEEDS_CONFIRMATION", hints["recovery_resolution_status"])
        self.assertFalse(hints["correction_applied"])
        self.assertGreaterEqual(len(hints["suggestions"]), 2)
        self.assertEqual("UNKNOWN", payload["drone_command"]["action"])
        self.assertFalse(payload["llm_called"])
        self.assertEqual(len(hints["suggestions"]), len({item["suggestion_id"] for item in hints["suggestions"]}))

    def test_confirmed_suggestion_is_reparsed_and_validated(self):
        payload = json.loads(parse_command(
            "bekl",
            json.dumps({"recovery_request_key": "request-confirm"}),
        ))
        suggestions = payload["baseline_hints"]["suggestions"]
        self.assertEqual(1, len(suggestions))
        confirmed = json.loads(parse_command(suggestions[0]["suggested_command"]))
        self.assertEqual("STOP", confirmed["public_command"]["action"])
        self.assertFalse(confirmed["llm_called"])

    def test_recovery_metadata_stays_out_of_public_command(self):
        payload = json.loads(parse_command("ev edin"))
        self.assertEqual(PUBLIC_FIELDS, list(payload["public_command"]))
        self.assertNotIn("corrected_suffix", payload["public_command"])
        self.assertNotIn("suggestions", payload["public_command"])
        self.assertTrue(payload["baseline_hints"]["correction_applied"])

    def test_binding_contract_sentence_matrix(self):
        cases = [
            # TAKE_OFF / landing
            ("Kartal havalan", "RESOLVED", {"action": "TAKE_OFF"}),
            ("Kartal kalk", "RESOLVED", {"action": "TAKE_OFF"}),
            ("Kartal uç", "RESOLVED", {"action": "TAKE_OFF"}),
            ("Kartal in", "RESOLVED", {"action": "LAND_HOME"}),
            ("Kartal kalktığın yere in", "RESOLVED", {"action": "LAND_HOME"}),
            ("Kartal eve dön", "RESOLVED", {"action": "LAND_HOME"}),
            ("Kartal bulunduğun yere in", "RESOLVED", {"action": "LAND_CURRENT"}),
            ("Kartal olduğun yere in", "RESOLVED", {"action": "LAND_CURRENT"}),
            ("Kartal mevcut konuma in", "RESOLVED", {"action": "LAND_CURRENT"}),
            ("Kartal bekleme noktasına in", "RESOLVED", {"action": "LAND_AT_WAIT_POINT"}),
            ("Bekleme noktasına in", "RESOLVED", {"action": "LAND_AT_WAIT_POINT"}),
            # Horizontal / vertical movement
            ("Kartal 150 metre sola git", "RESOLVED", {"action": "MOVE_DIRECTION", "direction": "LEFT", "distance_m": 150}),
            ("Kartal 50 metre ileri git", "RESOLVED", {"action": "MOVE_DIRECTION", "direction": "FORWARD", "distance_m": 50}),
            ("Kartal 30 metre geri git", "RESOLVED", {"action": "MOVE_DIRECTION", "direction": "BACKWARD", "distance_m": 30}),
            ("Kartal 25 metre sağa git", "RESOLVED", {"action": "MOVE_DIRECTION", "direction": "RIGHT", "distance_m": 25}),
            ("Kartal sola git", "MISSING_USER_INFO", {"action": "MISSING_INFO", "direction": "LEFT", "target_action": "MOVE_DIRECTION", "missing_field": "distance_m"}),
            ("Kartal 15 metre yüksel", "RESOLVED", {"action": "MOVE_VERTICAL", "direction": "UP", "distance_m": 15}),
            ("Kartal 10 metre yukarı çık", "RESOLVED", {"action": "MOVE_VERTICAL", "direction": "UP", "distance_m": 10}),
            ("Kartal 8 metre aşağı in", "RESOLVED", {"action": "MOVE_VERTICAL", "direction": "DOWN", "distance_m": 8}),
            ("Kartal 12 metre alçal", "RESOLVED", {"action": "MOVE_VERTICAL", "direction": "DOWN", "distance_m": 12}),
            ("Kartal yüksel", "MISSING_USER_INFO", {"action": "MISSING_INFO", "direction": "UP", "target_action": "MOVE_VERTICAL", "missing_field": "distance_m"}),
            # Speed / turn
            ("Kartal 15 kilometre saat hızla git", "RESOLVED", {"action": "SET_SPEED", "speed_kmh": 15}),
            ("Kartal hızını 20 kilometre yap", "RESOLVED", {"action": "SET_SPEED", "speed_kmh": 20}),
            ("Kartal 30 km/h hızla git", "RESOLVED", {"action": "SET_SPEED", "speed_kmh": 30}),
            ("Kartal saatte 20 kilometre hızla git", "RESOLVED", {"action": "SET_SPEED", "speed_kmh": 20}),
            ("Kartal 20 kilometre bölü saat hızla git", "RESOLVED", {"action": "SET_SPEED", "speed_kmh": 20}),
            ("Kartal hızlı git", "MISSING_USER_INFO", {"action": "MISSING_INFO", "target_action": "SET_SPEED", "missing_field": "speed_kmh"}),
            ("Kartal saat yönünde 90 derece dön", "RESOLVED", {"action": "TURN", "turn_direction": "CLOCKWISE", "angle_deg": 90}),
            ("Kartal saat yönünün tersine 45 derece dön", "RESOLVED", {"action": "TURN", "turn_direction": "COUNTER_CLOCKWISE", "angle_deg": 45}),
            ("Kartal 30 derece sağa dön", "RESOLVED", {"action": "TURN", "turn_direction": "RIGHT", "angle_deg": 30}),
            ("Kartal 60 derece sola dön", "RESOLVED", {"action": "TURN", "turn_direction": "LEFT", "angle_deg": 60}),
            ("Kartal sağa dön", "MISSING_USER_INFO", {"action": "MISSING_INFO", "turn_direction": "RIGHT", "target_action": "TURN", "missing_field": "angle_deg"}),
            # Objects
            ("Kartal tırı bul", "RESOLVED", {"action": "FIND_OBJECT", "object": "SEMI_TRUCK"}),
            ("Kartal kamyonu bul", "RESOLVED", {"action": "FIND_OBJECT", "object": "TRUCK"}),
            ("Kartal forklifti bul", "RESOLVED", {"action": "FIND_OBJECT", "object": "FORKLIFT"}),
            ("Kartal ekskavatörü tespit et", "RESOLVED", {"action": "FIND_OBJECT", "object": "EXCAVATOR"}),
            ("Kartal vinç ara", "RESOLVED", {"action": "FIND_OBJECT", "object": "CRANE"}),
            ("Kartal tırıbul", "RESOLVED", {"action": "FIND_OBJECT", "object": "SEMI_TRUCK"}),
            ("Kartal kamyonubul", "RESOLVED", {"action": "FIND_OBJECT", "object": "TRUCK"}),
            ("Kartal ağır vasıtayı bul", "NEEDS_SLOT_CLASSIFICATION", {"action": "MISSING_INFO", "target_action": "FIND_OBJECT"}),
            ("Kartal kazı makinesini bul", "RESOLVED", {"action": "FIND_OBJECT", "object": "EXCAVATOR"}),
            ("Kartal bul", "MISSING_USER_INFO", {"action": "MISSING_INFO", "target_action": "FIND_OBJECT", "missing_field": "object"}),
            ("Kartal nesneyi bul", "MISSING_USER_INFO", {"action": "MISSING_INFO", "target_action": "FIND_OBJECT", "missing_field": "object"}),
            ("Kartal kamyonu takip et", "RESOLVED", {"action": "TRACK_OBJECT", "object": "TRUCK"}),
            ("Kartal tırı izle", "RESOLVED", {"action": "TRACK_OBJECT", "object": "SEMI_TRUCK"}),
            ("Kartal forkliftin peşinden git", "RESOLVED", {"action": "TRACK_OBJECT", "object": "FORKLIFT"}),
            ("Kartal arabayı takip et", "RESOLVED", {"action": "TRACK_OBJECT", "object": "CAR"}),
            ("Kartal yük aracını takip et", "NEEDS_SLOT_CLASSIFICATION", {"action": "MISSING_INFO", "target_action": "TRACK_OBJECT"}),
            ("Kartal takip et", "MISSING_USER_INFO", {"action": "MISSING_INFO", "target_action": "TRACK_OBJECT", "missing_field": "object"}),
            ("Kartal izle", "MISSING_USER_INFO", {"action": "MISSING_INFO", "target_action": "TRACK_OBJECT", "missing_field": "object"}),
            ("Kartal forklift üzerinde dur", "RESOLVED", {"action": "HOLD_OVER_OBJECT", "object": "FORKLIFT"}),
            ("Kartal tır üstünde bekle", "RESOLVED", {"action": "HOLD_OVER_OBJECT", "object": "SEMI_TRUCK"}),
            ("Kartal kamyon üzerinde hover yap", "RESOLVED", {"action": "HOLD_OVER_OBJECT", "object": "TRUCK"}),
            ("Kartal tırın üstünde bekle", "RESOLVED", {"action": "HOLD_OVER_OBJECT", "object": "SEMI_TRUCK"}),
            ("Kartal kazı aracının üzerinde dur", "NEEDS_SLOT_CLASSIFICATION", {"action": "MISSING_INFO", "target_action": "HOLD_OVER_OBJECT"}),
            # Observe / location / hover
            ("Kartal olduğun yerde dur ve izle", "RESOLVED", {"action": "OBSERVE_CURRENT", "observe_mode": "HOLD_AND_WATCH"}),
            ("Kartal mevcut konumda gözlem yap", "RESOLVED", {"action": "OBSERVE_CURRENT", "observe_mode": "MONITOR"}),
            ("Kartal burada bekle ve izle", "RESOLVED", {"action": "OBSERVE_CURRENT", "observe_mode": "HOLD_AND_WATCH"}),
            ("Kartal burada 20 saniye izle", "RESOLVED", {"action": "OBSERVE_CURRENT", "observe_mode": "WATCH", "duration_s": 20}),
            ("Kartal burada 2 dakika bekle ve izle", "RESOLVED", {"action": "OBSERVE_CURRENT", "observe_mode": "HOLD_AND_WATCH", "duration_min": 2}),
            ("Kartal A noktasına git", "RESOLVED", {"action": "GO_TO_LOCATION", "location_keyword": "A noktası"}),
            ("Kartal İzleme noktasına git", "RESOLVED", {"action": "GO_TO_LOCATION", "location_keyword": "izleme noktası"}),
            ("Kartal 39.9208, 32.8541 konumuna git", "RESOLVED", {"action": "GO_TO_LOCATION", "coordinate": {"lat": 39.9208, "lon": 32.8541}}),
            ("Kartal konuma git", "MISSING_USER_INFO", {"action": "MISSING_INFO", "target_action": "GO_TO_LOCATION", "missing_field": "location_keyword"}),
            ("Kartal oraya git", "MISSING_USER_INFO", {"action": "MISSING_INFO", "target_action": "GO_TO_LOCATION", "missing_field": "location_keyword"}),
            ("Kartal havada dur", "RESOLVED", {"action": "HOVER"}),
            ("Kartal havada bekle", "RESOLVED", {"action": "HOVER"}),
            ("Kartal hover yap", "RESOLVED", {"action": "HOVER"}),
            ("Kartal 20 saniye havada bekle", "RESOLVED", {"action": "HOVER", "duration_s": 20}),
            ("Kartal 2 dakika hover yap", "RESOLVED", {"action": "HOVER", "duration_min": 2}),
            # Markers
            ("Kartal kırmızı çizgiyi bul", "RESOLVED", {"action": "FIND_MARKER", "color": "RED", "shape": "LINE"}),
            ("Kartal yeşil kareyi bul", "RESOLVED", {"action": "FIND_MARKER", "color": "GREEN", "shape": "SQUARE"}),
            ("Kartal mavi üçgeni bul", "RESOLVED", {"action": "FIND_MARKER", "color": "BLUE", "shape": "TRIANGLE"}),
            ("Kartal beyaz artı işaretini bul", "RESOLVED", {"action": "FIND_MARKER", "color": "WHITE", "shape": "PLUS"}),
            ("Kartal siyah plus işaretini bul", "RESOLVED", {"action": "FIND_MARKER", "color": "BLACK", "shape": "PLUS"}),
            ("Kartal kırmızı artı işareti 3 olanı bul", "RESOLVED", {"action": "FIND_MARKER", "color": "RED", "shape": "PLUS", "marker_index": 3}),
            ("Kartal 3 numaralı kırmızı artı işaretini bul", "RESOLVED", {"action": "FIND_MARKER", "color": "RED", "shape": "PLUS", "marker_index": 3}),
            ("Kartal kırmızı işareti bul", "MISSING_USER_INFO", {"action": "MISSING_INFO", "color": "RED", "target_action": "FIND_MARKER", "missing_field": "shape"}),
            ("Kartal kareyi bul", "MISSING_USER_INFO", {"action": "MISSING_INFO", "shape": "SQUARE", "target_action": "FIND_MARKER", "missing_field": "color"}),
            ("Kartal artı işaretini bul", "MISSING_USER_INFO", {"action": "MISSING_INFO", "shape": "PLUS", "target_action": "FIND_MARKER", "missing_field": "color"}),
            # Stop / unknown
            ("Kartal dur", "RESOLVED", {"action": "STOP"}),
            ("Kartal bekle", "RESOLVED", {"action": "STOP"}),
            ("Kartal sabit kal", "RESOLVED", {"action": "STOP"}),
            ("Kartal hareket etme", "RESOLVED", {"action": "STOP"}),
            ("Kartal hareketi kes", "RESOLVED", {"action": "STOP"}),
            ("Bugün hava çok güzel", "OUT_OF_DOMAIN", {"action": "UNKNOWN", "confidence": 0.0}),
            ("Kartal bugün hava çok güzel", "OUT_OF_DOMAIN", {"action": "UNKNOWN", "confidence": 0.0}),
            ("Kartal nasılsın", "OUT_OF_DOMAIN", {"action": "UNKNOWN", "confidence": 0.0}),
            ("Kartal bana bir şiir yaz", "OUT_OF_DOMAIN", {"action": "UNKNOWN", "confidence": 0.0}),
            ("Kartal durum nedir", "OUT_OF_DOMAIN", {"action": "UNKNOWN", "confidence": 0.0}),
            ("Kartal bulutları izle", "OUT_OF_DOMAIN", {"action": "UNKNOWN", "confidence": 0.0}),
            ("Kartal tırnak işaretini bul", "OUT_OF_DOMAIN", {"action": "UNKNOWN", "confidence": 0.0}),
            ("Kartal kabul et", "OUT_OF_DOMAIN", {"action": "UNKNOWN", "confidence": 0.0}),
        ]
        self.assertEqual(93, len(cases))
        for text, expected_status, expected in cases:
            with self.subTest(text=text):
                internal, diagnostics = parse_drone_command_with_diagnostics(text)
                self.assertEqual(expected_status, diagnostics["resolution_status"])
                for field, value in expected.items():
                    self.assertEqual(value, internal.get(field), field)
                public = materialize_public_command(internal)
                self.assertEqual(PUBLIC_FIELDS, list(public))
                self.assertTrue(0.0 <= public["confidence"] <= 1.0)
                self.assertNotIn(None, internal.values())


if __name__ == "__main__":
    unittest.main()
