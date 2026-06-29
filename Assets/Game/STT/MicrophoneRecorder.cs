using System;
using UnityEngine;

namespace Game.STT
{
    public sealed class MicrophoneRecorder
    {
        private readonly int _sampleRate;
        private readonly int _maxSeconds;
        private AudioClip _recordingClip;
        private string _device;
        private bool _isRecording;

        public bool IsRecording => _isRecording;
        public int LastRecordedSamples { get; private set; }
        public int LastFrequency { get; private set; }
        public int LastChannels { get; private set; }
        public float LastRecordedDurationSeconds { get; private set; }

        public MicrophoneRecorder(int sampleRate, int maxSeconds)
        {
            _sampleRate = sampleRate;
            _maxSeconds = Mathf.Max(1, maxSeconds);
        }

        public void Start()
        {
            if (_isRecording) return;

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                throw new InvalidOperationException("Android microphone bulunamadi. RECORD_AUDIO izni verilmis mi kontrol et.");
            }

            _device = Microphone.devices[0];
            _recordingClip = Microphone.Start(_device, false, _maxSeconds, _sampleRate);
            _isRecording = true;
            LastRecordedSamples = 0;
            LastFrequency = _recordingClip != null ? _recordingClip.frequency : _sampleRate;
            LastChannels = _recordingClip != null ? _recordingClip.channels : 0;
            LastRecordedDurationSeconds = 0f;
        }

        public AudioClip StopAndGetClip()
        {
            if (!_isRecording || _recordingClip == null)
            {
                throw new InvalidOperationException("Aktif kayit yok.");
            }

            int recordedSamples = Microphone.GetPosition(_device);
            int channels = _recordingClip.channels;
            int frequency = _recordingClip.frequency;
            Microphone.End(_device);
            _isRecording = false;

            if (recordedSamples <= 0)
            {
                throw new InvalidOperationException("Kayit cok kisa veya mikrofon veri uretmedi.");
            }

            float[] samples = new float[recordedSamples * channels];
            _recordingClip.GetData(samples, 0);

            AudioClip trimmed = AudioClip.Create(
                "stt_recording_trimmed",
                recordedSamples,
                channels,
                frequency,
                false
            );
            trimmed.SetData(samples, 0);

            LastRecordedSamples = recordedSamples;
            LastFrequency = frequency;
            LastChannels = channels;
            LastRecordedDurationSeconds = frequency > 0 ? (float)recordedSamples / frequency : 0f;
            return trimmed;
        }
    }
}
