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
                throw new InvalidOperationException("Android microphone bulunamadı. RECORD_AUDIO izni verilmiş mi kontrol et.");
            }

            _device = Microphone.devices[0];
            _recordingClip = Microphone.Start(_device, false, _maxSeconds, _sampleRate);
            _isRecording = true;
        }

        public AudioClip StopAndGetClip()
        {
            if (!_isRecording || _recordingClip == null)
            {
                throw new InvalidOperationException("Aktif kayıt yok.");
            }

            int position = Microphone.GetPosition(_device);
            Microphone.End(_device);
            _isRecording = false;

            if (position <= 0)
            {
                throw new InvalidOperationException("Kayıt çok kısa veya mikrofon veri üretmedi.");
            }

            int channels = _recordingClip.channels;
            float[] samples = new float[position * channels];
            _recordingClip.GetData(samples, 0);

            AudioClip trimmed = AudioClip.Create(
                "stt_recording_trimmed",
                position,
                channels,
                _recordingClip.frequency,
                false
            );
            trimmed.SetData(samples, 0);
            return trimmed;
        }
    }
}
