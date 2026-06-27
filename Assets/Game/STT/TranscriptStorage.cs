using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Game.STT
{
    public sealed class TranscriptStorage
    {
        public string RootDirectory { get; }
        public string LogCsvPath { get; }

        public TranscriptStorage()
        {
            RootDirectory = Path.Combine(Application.persistentDataPath, "STTTests");
            Directory.CreateDirectory(RootDirectory);
            LogCsvPath = Path.Combine(RootDirectory, "result_log.csv");
            EnsureCsvHeader();
        }

        public string BuildAudioPath(string recordingId)
        {
            return Path.Combine(RootDirectory, $"audio_{recordingId}.wav");
        }

        public string BuildTranscriptPath(string recordingId, STTProvider provider)
        {
            return Path.Combine(RootDirectory, $"transcript_{recordingId}_{provider.ToSafeFileName()}.txt");
        }

        public void SaveTranscript(string path, STTResult result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RootDirectory);
            string text =
                $"provider: {result.provider}\n" +
                $"language: {result.language}\n" +
                $"durationMs: {result.durationMs}\n" +
                $"audioFile: {result.audioFile}\n" +
                $"error: {result.error}\n\n" +
                $"text:\n{result.text}\n";
            File.WriteAllText(path, text, Encoding.UTF8);
        }

        public void AppendCsv(string timestamp, string recordingId, string audioPath, string transcriptPath, STTResult result)
        {
            EnsureCsvHeader();
            string line = string.Join(",", new[]
            {
                Escape(timestamp),
                Escape(recordingId),
                Escape(result.provider),
                Escape(audioPath),
                Escape(transcriptPath),
                Escape(result.durationMs.ToString()),
                Escape(result.text),
                Escape(result.error)
            });
            File.AppendAllText(LogCsvPath, line + Environment.NewLine, Encoding.UTF8);
        }

        private void EnsureCsvHeader()
        {
            if (File.Exists(LogCsvPath)) return;
            File.WriteAllText(LogCsvPath, "timestamp,recordingId,provider,audioFile,transcriptFile,durationMs,text,error" + Environment.NewLine, Encoding.UTF8);
        }

        private static string Escape(string value)
        {
            value ??= string.Empty;
            value = value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
            return $"\"{value}\"";
        }
    }
}
