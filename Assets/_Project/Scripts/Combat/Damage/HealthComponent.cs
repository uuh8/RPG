using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 挂在“能受伤 GameObject”上的生命值组件，也是 IDamageable 的具体实现。
    /// 它持有目标自己的血量、阵营和防御配置，并作为统一 Damage Funnel：调用 DamagePipeline 纯计算，
    /// 再修改 Runtime HP，同帧发布受击事件；HP 归零时继续发布死亡事件。
    /// </summary>
    public class HealthComponent : MonoBehaviour, IDamageable
    {
        [SerializeField] private float _maxHp = 100f;
        [SerializeField] private byte _teamId = 0;
        [SerializeField] private DefenseProfile _defenseProfile;

        // Inspector 字段提供初始配置；当前生命值、实例 Id 和无敌开关是场景实例各自拥有的 Runtime State。
        private float _currentHp;
        private int _id;
        private bool _isInvulnerable;

        public byte TeamId => _teamId;
        public bool IsAlive => _currentHp > 0f;
        public float CurrentHp => _currentHp;
        public float MaxHp => _maxHp;
        public int Id => _id;   // = gameObject.GetInstanceID()，与 DamageReceivedEvent.TargetId 同源，供血条/表现层按 id 过滤
        public bool IsInvulnerable => _isInvulnerable;

        private void Awake()
        {
            // Awake 对每个组件实例调用一次，因此每个角色都会获得独立的初始 HP。
            _currentHp = _maxHp;
            // GetInstanceID 返回本次 Unity 运行期间对象的实例标识，供事件消费者筛选目标；它不是跨存档的永久 Id。
            _id = gameObject.GetInstanceID();
        }

        /// <summary>统一伤害入口：入口过滤 → 纯计算 → 扣血 → 同步发布表现/死亡所需的事实事件。</summary>
        public void ReceiveHit(in DamageRequest req)
        {
            // Invulnerability 是 Damage Funnel 的入口 Gate：既不扣血，也不发布“0 伤害”事件。
            // 这样 HUD、受击闪白和死亡逻辑不会把 Phase Transition 期间的命中误认成有效伤害。
            if (!IsAlive || _isInvulnerable) return;

            // DamagePipeline 只算结果，不直接碰 MonoBehaviour 状态；HealthComponent 才拥有并修改 _currentHp。
            DamageResult result = DamagePipeline.Resolve(in req, in _defenseProfile);
            _currentHp -= result.Final;
            if (_currentHp < 0f) _currentHp = 0f;   // 扣血钳制到 ≥0

            // EventBus.Publish 是同步调用：当前 ReceiveHit 返回前，已订阅的 HUD、受击表现等消费者就会收到事件。
            // 事件只携带值数据，不把 HealthComponent 或攻击者对象直接暴露给表现模块。
            EventBus<DamageReceivedEvent>.Publish(new DamageReceivedEvent
            {
                TargetId     = _id,
                AttackerId   = req.AttackerId,
                Amount       = result.Final,
                Type         = result.Type,
                HitPoint     = req.HitPoint,
                HitDirection = req.HitDirection,
                RemainingHp  = _currentHp,
                TriggerHitReaction = req.TriggerHitReaction,
            });

            // 受击事件总是先于死亡事件，消费者可以先显示本次伤害和 0 HP，再进入死亡表现。
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

        /// <summary>
        /// P8 当前只有 Phase Transition 一个无敌 Owner，因此 bool 足够且易于在 State.Exit 兜底释放。
        /// 将来若 Shield/Buff 也能同时申请无敌，应升级为 Token 或引用计数，避免一个 Owner 提前关闭另一个。
        /// </summary>
        public void SetInvulnerable(bool value)
        {
            _isInvulnerable = value;
        }
    }
}
