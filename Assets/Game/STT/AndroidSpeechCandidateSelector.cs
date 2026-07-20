using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.NLP;

namespace Game.STT
{
    public sealed class AndroidSpeechCandidateSelection
    {
        public string recognizerPrimaryTranscript = string.Empty;
        public AndroidSpeechAlternative[] recognizerAlternatives = Array.Empty<AndroidSpeechAlternative>();
        public string selectedTranscript = string.Empty;
        public float selectedAndroidConfidence = -1f;
        public bool selectedAndroidConfidenceAvailable;
        public int selectedCandidateIndex = -1;
        public string candidateSelectionReason = "NO_WAKE_WORD_CANDIDATE";
        public string wakeWordSuffix = string.Empty;
        public SpeechCommandCandidateProbe probe;
        public long evaluationDurationMs;
    }

    public static class AndroidSpeechCandidateSelector
    {
        public static async Task<AndroidSpeechCandidateSelection> SelectAsync(
            AndroidSpeechAlternative[] alternatives,
            Func<string, Task<SpeechCommandCandidateProbe>> evaluateSuffixAsync,
            CancellationToken cancellationToken = default)
        {
            Stopwatch timer = Stopwatch.StartNew();
            AndroidSpeechAlternative[] source = alternatives ?? Array.Empty<AndroidSpeechAlternative>();
            AndroidSpeechCandidateSelection result = new AndroidSpeechCandidateSelection
            {
                recognizerAlternatives = source,
                recognizerPrimaryTranscript = source.Length > 0 ? source[0]?.text ?? string.Empty : string.Empty,
            };

            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            Candidate best = null;
            int evaluated = 0;
            for (int i = 0; i < source.Length && evaluated < 5; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AndroidSpeechAlternative alternative = source[i];
                string original = alternative?.text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(original) || !unique.Add(Normalize(original))) continue;
                evaluated++;
                if (!STTTestController.TryExtractCommandSuffixForTesting(original, out string suffix)) continue;

                SpeechCommandCandidateProbe probe = evaluateSuffixAsync != null
                    ? await evaluateSuffixAsync(suffix)
                    : null;
                cancellationToken.ThrowIfCancellationRequested();
                Candidate candidate = new Candidate(i, original, suffix, alternative, probe);
                if (best == null || Compare(candidate, best) > 0) best = candidate;
            }

            if (best != null)
            {
                result.selectedTranscript = best.transcript;
                result.selectedCandidateIndex = best.index;
                result.selectedAndroidConfidence = best.alternative?.confidence ?? -1f;
                result.selectedAndroidConfidenceAvailable = best.alternative != null
                    && best.alternative.confidence_available && best.alternative.confidence >= 0f;
                result.candidateSelectionReason = best.probe?.reason ?? "WAKE_WORD_ONLY";
                result.wakeWordSuffix = best.suffix;
                result.probe = best.probe;
            }
            result.evaluationDurationMs = timer.ElapsedMilliseconds;
            return result;
        }

        private static int Compare(Candidate left, Candidate right)
        {
            int value = (left.probe?.safetyRank ?? 0).CompareTo(right.probe?.safetyRank ?? 0);
            if (value != 0) return value;
            value = (left.probe?.evidenceStrength ?? 0).CompareTo(right.probe?.evidenceStrength ?? 0);
            if (value != 0) return value;
            value = (left.probe?.recoveryConfidence ?? 0d).CompareTo(right.probe?.recoveryConfidence ?? 0d);
            if (value != 0) return value;
            float leftConfidence = left.alternative != null && left.alternative.confidence_available
                ? left.alternative.confidence : -1f;
            float rightConfidence = right.alternative != null && right.alternative.confidence_available
                ? right.alternative.confidence : -1f;
            value = leftConfidence.CompareTo(rightConfidence);
            return value != 0 ? value : right.index.CompareTo(left.index);
        }

        private static string Normalize(string value)
        {
            string lower = (value ?? string.Empty).Normalize(NormalizationForm.FormC)
                .ToLower(new CultureInfo("tr-TR"));
            StringBuilder builder = new StringBuilder(lower.Length);
            bool space = false;
            foreach (char c in lower)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!space) builder.Append(' ');
                    space = true;
                }
                else if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                    space = false;
                }
            }
            return builder.ToString().Trim();
        }

        private sealed class Candidate
        {
            public readonly int index;
            public readonly string transcript;
            public readonly string suffix;
            public readonly AndroidSpeechAlternative alternative;
            public readonly SpeechCommandCandidateProbe probe;
            public Candidate(int index, string transcript, string suffix, AndroidSpeechAlternative alternative,
                SpeechCommandCandidateProbe probe)
            {
                this.index = index;
                this.transcript = transcript;
                this.suffix = suffix;
                this.alternative = alternative;
                this.probe = probe;
            }
        }
    }
}
