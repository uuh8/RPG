using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 恢复型法力资源。用于限制法术释放频率，不表示弹药数量。
    /// 挂在施法者身上，由 SpellCaster 扣费，由 UI 读取显示。
    /// </summary>
    public class ManaComponent : MonoBehaviour
    {
        [SerializeField, Min(1f)] private float _maxMana = 100f;
        [SerializeField, Min(0f)] private float _manaRegenPerSecond = 20f;
        [SerializeField] private bool _startFull = true;

        private float _currentMana;

        public float CurrentMana => _currentMana;
        public float MaxMana => _maxMana;
        public float ManaRegenPerSecond => _manaRegenPerSecond;
        public float Normalized => _maxMana > 0f ? Mathf.Clamp01(_currentMana / _maxMana) : 0f;

        private void Awake()
        {
            _maxMana = Mathf.Max(1f, _maxMana);
            _manaRegenPerSecond = Mathf.Max(0f, _manaRegenPerSecond);
            _currentMana = _startFull ? _maxMana : 0f;
        }

        private void Update()
        {
            if (_currentMana >= _maxMana || _manaRegenPerSecond <= 0f)
                return;

            _currentMana = Mathf.Min(_maxMana, _currentMana + _manaRegenPerSecond * Time.deltaTime);
        }

        public bool CanSpend(float amount)
        {
            float cost = Mathf.Max(0f, amount);
            return _currentMana >= cost;
        }

        public bool Spend(float amount)
        {
            float cost = Mathf.Max(0f, amount);
            if (_currentMana < cost)
                return false;

            _currentMana -= cost;
            return true;
        }
    }
}
