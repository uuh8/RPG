using UnityEngine;
using UnityEngine.UI;
using Game.Core;
using Game.Combat;

namespace Game.UI
{
    /// <summary>
    /// 玩家屏幕 HUD 蓝条。Inspector 拖入玩家 ManaComponent 和填充 Image。
    /// Image 需要设置为 Type=Filled，Fill Method=Horizontal。
    /// </summary>
    public class PlayerManaBar : MonoBehaviour
    {
        [SerializeField] private Image _fill;
        [SerializeField] private ManaComponent _playerMana;
        [SerializeField] private float _lerpSpeed = 4f;

        private float _targetFill = 1f;
        private float _displayFill = 1f;

        private void Start()
        {
            if (_playerMana == null)
            {
                GameLog.Warn("PlayerManaBar 未指定玩家 ManaComponent，蓝条不会更新", "UI");
            }
            else
            {
                _targetFill = _displayFill = _playerMana.Normalized;
            }

            ApplyFill();
        }

        private void Update()
        {
            if (_playerMana != null)
                _targetFill = _playerMana.Normalized;

            if (Mathf.Approximately(_displayFill, _targetFill))
                return;

            _displayFill = Mathf.MoveTowards(_displayFill, _targetFill, _lerpSpeed * Time.deltaTime);
            ApplyFill();
        }

        private void ApplyFill()
        {
            if (_fill != null)
                _fill.fillAmount = _displayFill;
        }
    }
}
