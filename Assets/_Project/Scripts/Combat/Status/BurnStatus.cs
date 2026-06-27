using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 持续燃烧状态（DoT）。火球命中后由 Fireball 附加到目标身上：
    /// 每隔 _interval 秒对目标结算一次 _damagePerTick 点伤害，持续 duration 秒后自动移除；
    /// 期间在目标身上挂一个 OnFire 特效（结束时销毁）。重复命中刷新持续时间（不叠加第二份特效）。
    /// 伤害仍走 IDamageable.ReceiveHit 标准路径，故与火球直击/近战/箭矢复用同一套结算与事件。
    /// </summary>
    public class BurnStatus : MonoBehaviour
    {
        private IDamageable _target;     // 同物体上的可受击目标（缓存）

        // 燃烧快照（Apply 注入；攻击者可能已销毁，故按值保存）
        private int _attackerId;
        private byte _attackerTeam;
        private float _damagePerTick;
        private DamageType _type;
        private float _interval;

        private float _remaining;        // 剩余燃烧时间
        private float _tickTimer;        // 距下一次结算的计时
        private GameObject _vfx;         // 挂在目标身上的 OnFire 特效实例
        private bool _active;

        private void Awake()
        {
            _target = GetComponent<IDamageable>();
        }

        /// <summary>
        /// 施加/刷新燃烧。首次调用生成 OnFire 特效并开始计时；重复调用刷新持续时间与参数（不再生成第二份特效）。
        /// </summary>
        public void Apply(int attackerId, byte attackerTeam, float damagePerTick, DamageType type,
                          float interval, float duration, GameObject onFirePrefab)
        {
            _attackerId    = attackerId;
            _attackerTeam  = attackerTeam;
            _damagePerTick = damagePerTick;
            _type          = type;
            _interval      = Mathf.Max(0.05f, interval); // 防 0/负间隔导致每帧结算
            _remaining     = duration;

            if (!_active)
            {
                _active = true;
                _tickTimer = _interval; // 首跳延迟一个 interval（直击伤害已在命中帧结算）
                if (onFirePrefab != null && _vfx == null)
                    _vfx = Object.Instantiate(onFirePrefab, transform.position, transform.rotation, transform);
            }
        }

        private void Update()
        {
            if (!_active) return;

            // 目标已死亡：停止燃烧并清理（ReceiveHit 对死者本就早退，这里负责收尾特效与自毁）
            if (_target == null || !_target.IsAlive) { Stop(); return; }

            float dt = Time.deltaTime;
            _remaining -= dt;
            _tickTimer -= dt;

            if (_tickTimer <= 0f)
            {
                _tickTimer += _interval;
                var req = new DamageRequest(_attackerId, _attackerTeam, _damagePerTick,
                                            _type, transform.position, Vector3.up,
                                            triggerHitReaction: false); // DoT：不触发受击动作/硬直，仅扣血+闪红
                _target.ReceiveHit(in req);
            }

            if (_remaining <= 0f) Stop();
        }

        private void Stop()
        {
            _active = false;
            if (_vfx != null) Destroy(_vfx);
            Destroy(this); // 移除自身组件（燃烧结束）
        }
    }
}
