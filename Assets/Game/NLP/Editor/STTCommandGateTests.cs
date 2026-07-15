#if UNITY_EDITOR
using Game.STT;
using NUnit.Framework;

namespace Game.NLP.EditorTests
{
    public sealed class STTCommandGateTests
    {
        [TestCase("Kartal tırı bul", "tırı bul")]
        [TestCase("Kartal tırıbul", "tırıbul")]
        [TestCase("KARTAL, kamyonubul!", "kamyonubul!")]
        [TestCase("Kartal ağır vasıtayı bul", "ağır vasıtayı bul")]
        [TestCase("Kartal bana bir şiir yaz", "bana bir şiir yaz")]
        public void ExplicitWakeWordRoutesNonEmptySuffixToPipeline(string input, string expectedSuffix)
        {
            Assert.IsTrue(STTTestController.TryExtractCommandSuffixForTesting(input, out string suffix));
            Assert.AreEqual(expectedSuffix, suffix);
        }

        [TestCase("")]
        [TestCase("Kartal")]
        [TestCase("Bugün hava güzel")]
        [TestCase("tırıbul")]
        public void MissingWakeOrEmptySuffixIsRejected(string input)
        {
            Assert.IsFalse(STTTestController.TryExtractCommandSuffixForTesting(input, out string suffix));
            Assert.AreEqual(string.Empty, suffix);
        }

        [TestCase("tırıbul")]
        [TestCase("kamyonubul")]
        [TestCase("arabayıizle")]
        [TestCase("tırıtakipet")]
        [TestCase("tırınüstündebekle")]
        public void FusedRegressionSuffixesPassGate(string suffix)
        {
            Assert.IsTrue(STTTestController.IsPotentialCommandSuffix(suffix));
        }
    }
}
#endif
