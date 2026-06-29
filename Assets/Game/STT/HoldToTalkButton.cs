using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.STT
{
    public sealed class HoldToTalkButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        [SerializeField] private STTTestController controller;
        private bool _isHolding;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_isHolding) return;
            _isHolding = true;
            controller?.BeginHoldRecording();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            EndHold();
        }

        private void OnDisable()
        {
            EndHold();
        }

        private void EndHold()
        {
            if (!_isHolding) return;
            _isHolding = false;
            controller?.EndHoldRecordingAndTranscribeSelected();
        }
    }
}