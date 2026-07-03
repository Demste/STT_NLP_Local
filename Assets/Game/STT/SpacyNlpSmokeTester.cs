using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.STT
{
    public sealed class SpacyNlpSmokeTester : MonoBehaviour
    {
        [SerializeField] private bool runOnStart = true;

        private async void Start()
        {
            if (!runOnStart)
                return;

            await RunSmokeTestsAsync(GetDestroyToken());
        }

        private async Task RunSmokeTestsAsync(CancellationToken cancellationToken)
        {
            Debug.Log("[spaCy NLP] IsAvailable: " + SpacyNlpAndroidBridgeClient.IsAvailable);
            Debug.Log("[spaCy NLP] Smoke test started.");

            string result1 = await SpacyNlpAndroidBridgeClient.AnalyzeAsync(
                "Dün ben de gittim.",
                "",
                cancellationToken
            );

            Debug.Log("[spaCy NLP] Result 1: " + result1);

            string result2 = await SpacyNlpAndroidBridgeClient.AnalyzeAsync(
                "Kırmızı artı işaretinin yanına git.",
                "",
                cancellationToken
            );

            Debug.Log("[spaCy NLP] Result 2: " + result2);
        }

        private CancellationToken GetDestroyToken()
        {
#if UNITY_2022_2_OR_NEWER
            return destroyCancellationToken;
#else
            return CancellationToken.None;
#endif
        }
    }
}