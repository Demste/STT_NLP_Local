#if UNITY_EDITOR
using NUnit.Framework;

namespace Game.NLP.EditorTests
{
    public sealed class DroneCommandContractTests
    {
        [Test]
        public void PublicSerializerWritesEveryFieldAndJsonNull()
        {
            string json = DroneCommandJsonParser.ToPublicJson(new ValidatedCommandResult
            {
                action = "FIND_OBJECT",
                @object = "SEMI_TRUCK",
                confidence = 0.97d
            });

            string envelope = "{\"public_command\":" + json + "}";
            ValidatedCommandResult parsed = DroneCommandJsonParser.ParseObjectForKey(envelope, "public_command");
            Assert.AreEqual("FIND_OBJECT", parsed.action);
            Assert.AreEqual("SEMI_TRUCK", parsed.@object);
            Assert.AreEqual(0.97d, parsed.confidence, 0.0001d);
            Assert.IsNull(parsed.distance_m);
            Assert.IsNull(parsed.coordinate);
            StringAssert.Contains("\"direction\":null", json);
            StringAssert.Contains("\"coordinate\":null", json);
            Assert.AreEqual(21, CountTopLevelFields(json));
        }

        [Test]
        public void MissingInfoAndCompatibilityFieldsRoundTrip()
        {
            string sparse = "{\"action\":\"MISSING_INFO\",\"direction\":\"LEFT\",\"target_action\":\"MOVE_DIRECTION\",\"missing_field\":\"distance_m\",\"missing_message\":\"Yatay hareket komutu için metre bilgisi eksik.\",\"confidence\":0.96}";
            string envelope = DroneCommandJsonParser.BuildBaselineEnvelope("sola git", sparse, 3);
            string publicJson = DroneCommandJsonParser.RawObjectForKey(envelope, "public_command");
            Assert.AreEqual(publicJson, DroneCommandJsonParser.RawObjectForKey(envelope, "validated_command"));
            Assert.AreEqual(publicJson, DroneCommandJsonParser.RawObjectForKey(envelope, "normalized_command"));
            Assert.AreNotEqual(publicJson, DroneCommandJsonParser.RawObjectForKey(envelope, "internal_validated_command"));
            ValidatedCommandResult parsed = DroneCommandJsonParser.ParseObjectForKey(envelope, "public_command");
            Assert.AreEqual("MISSING_INFO", parsed.action);
            Assert.AreEqual("LEFT", parsed.direction);
            Assert.AreEqual("distance_m", parsed.missing_field);
        }

        [Test]
        public void BaselineEnvelopeCarriesProductionDiagnostics()
        {
            string envelope = DroneCommandJsonParser.BuildBaselineEnvelope(
                "Kartal tırıbul", "tırıbul", "tırı bul", "RESOLVED", true,
                "{\"action\":\"FIND_OBJECT\",\"object\":\"SEMI_TRUCK\",\"confidence\":0.97}", 7);
            StringAssert.Contains("\"raw_input\":\"Kartal tırıbul\"", envelope);
            StringAssert.Contains("\"wake_word_suffix\":\"tırıbul\"", envelope);
            StringAssert.Contains("\"analysis_input\":\"tırı bul\"", envelope);
            StringAssert.Contains("\"repair_applied\":true", envelope);
            StringAssert.Contains("\"resolution_status\":\"RESOLVED\"", envelope);
            StringAssert.Contains("\"action_llm_called\":false", envelope);
            StringAssert.Contains("\"slot_llm_called\":false", envelope);
        }

        [Test]
        public void CoordinateAndTrustedMetadataRoundTrip()
        {
            string json = DroneCommandJsonParser.ToPublicJson(new ValidatedCommandResult
            {
                action = "GO_TO_LOCATION",
                coordinate = new DroneCoordinate { lat = 39.9208d, lon = 32.8541d },
                voice_type = "NORMAL",
                stt_confidence_level = "GOOD",
                confidence = 0.98d
            });
            ValidatedCommandResult parsed = DroneCommandJsonParser.ParseObjectForKey("{\"public_command\":" + json + "}", "public_command");
            Assert.NotNull(parsed.coordinate);
            Assert.AreEqual(39.9208d, parsed.coordinate.lat, 0.000001d);
            Assert.AreEqual(32.8541d, parsed.coordinate.lon, 0.000001d);
            Assert.AreEqual("NORMAL", parsed.voice_type);
            Assert.AreEqual("GOOD", parsed.stt_confidence_level);
        }

        private static int CountTopLevelFields(string json)
        {
            int count = 0;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == ':' && depth == 1) count++;
            }
            return count;
        }
    }
}
#endif
