using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 生命值管理 + IDamageable 实现。封装自身防御档案，受击时调用纯函数
    /// DamagePipeline.Resolve，扣血后同帧经 EventBus 派发 DamageReceived / Death。
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
            if (!IsAlive) return;

            DamageResult result = DamagePipeline.Resolve(in req, in _defenseProfile);
            _currentHp -= result.Final;
            if (_currentHp < 0f) _currentHp = 0f;

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
