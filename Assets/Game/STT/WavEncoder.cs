using System;
using System.IO;
using UnityEngine;

namespace Game.STT
{
    public static class WavEncoder
    {
        public static byte[] FromAudioClip(AudioClip clip, bool forceMono = true)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));

            float[] input = new float[clip.samples * clip.channels];
            clip.GetData(input, 0);

            int outputChannels = forceMono ? 1 : clip.channels;
            int outputSampleCount = forceMono ? clip.samples : input.Length;
            const int bytesPerSample = 2;
            int dataSize = outputSampleCount * bytesPerSample;
            int fileSize = 44 + dataSize;

            using MemoryStream stream = new MemoryStream(fileSize);
            using BinaryWriter writer = new BinaryWriter(stream);

            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(fileSize - 8);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)outputChannels);
            writer.Write(clip.frequency);
            writer.Write(clip.frequency * outputChannels * bytesPerSample);
            writer.Write((short)(outputChannels * bytesPerSample));
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            if (forceMono)
            {
                for (int frame = 0; frame < clip.samples; frame++)
                {
                    float mixed = 0f;
                    for (int ch = 0; ch < clip.channels; ch++)
                    {
                        mixed += input[frame * clip.channels + ch];
                    }
                    mixed /= Mathf.Max(1, clip.channels);
                    WriteSample(writer, mixed);
                }
            }
            else
            {
                for (int i = 0; i < input.Length; i++)
                {
                    WriteSample(writer, input[i]);
                }
            }

            writer.Flush();
            return stream.ToArray();
        }

        public static void SaveToFile(AudioClip clip, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            File.WriteAllBytes(path, FromAudioClip(clip, forceMono: true));
        }

        private static void WriteSample(BinaryWriter writer, float sample)
        {
            float clamped = Mathf.Clamp(sample, -1f, 1f);
            short value = (short)(clamped * short.MaxValue);
            writer.Write(value);
        }
    }
}
