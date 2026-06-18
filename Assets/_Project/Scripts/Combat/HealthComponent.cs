using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 生命值管理 + IDamageable 实现。封装自身防御档案，受击时调用纯函数
    /// 实现了一个挂在"能挨打的 GameObject"上的 MonoBehaviour，它是 IDamageable 的具体实现
    /// 负责：持有血量/阵营/防御档案 → 受击时调 DamagePipeline 算账 → 扣血 → 同帧发受击事件 → 血量归零再发死亡事件。
    /// </summary>
    public class HealthComponent : MonoBehaviour, IDamageable
    {
        [SerializeField] private float _maxHp = 100f;
        [SerializeField] private byte _teamId = 0;
        [SerializeField] private DefenseProfile _defenseProfile;

        private float _currentHp;
        private int _id;

        public byte TeamId => _teamId;
        public bool IsAlive => _currentHp > 0f;
        public float CurrentHp => _currentHp;
        public float MaxHp => _maxHp;

        private void Awake()
        {
            _currentHp = _maxHp;
            _id = gameObject.GetInstanceID();
        }

        public void ReceiveHit(in DamageRequest req)
        {
            if (!IsAlive) return;   // 死亡时不再受击

            // 算出最终伤害
            DamageResult result = DamagePipeline.Resolve(in req, in _defenseProfile);
            _currentHp -= result.Final;
            if (_currentHp < 0f) _currentHp = 0f;   // 扣血钳制到 ≥0

            // 同帧 Publish 受击事件
            EventBus<DamageReceivedEvent>.Publish(new DamageReceivedEvent
            {
                TargetId     = _id,
                AttackerId   = req.AttackerId,
                Amount       = result.Final,
                Type         = result.Type,
                HitPoint     = req.HitPoint,
                HitDirection = req.HitDirection,
                RemainingHp  = _currentHp,
            });

            // 若血量 ≤0，同帧 Publish 死亡事件
            if (_currentHp <= 0f)
            {
                EventBus<DeathEvent>.Publish(new DeathEvent
                {
                    TargetId   = _id,
                    AttackerId = req.AttackerId,
                    Position   = transform.position,
                });
            }
        }
    }
}
