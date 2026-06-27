using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.STT
{
    public sealed class HoldToTalkButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
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

        public void OnPointerExit(PointerEventData eventData)
        {
            // Mobile kullanıcı parmağı butondan kaydırırsa kayıt takılı kalmasın.
            if (_isHolding && eventData.pointerPress == gameObject)
            {
                EndHold();
            }
        }

        private void EndHold()
        {
            if (!_isHolding) return;
            _isHolding = false;
            controller?.EndHoldRecordingAndTranscribeSelected();
        }
    }
}
