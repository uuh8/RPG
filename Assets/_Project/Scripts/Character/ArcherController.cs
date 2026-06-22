using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手控制器：在 PlayerControllerBase 共享能力之上，叠加远程普通攻击（点按，PlayerBowAttackState）
    /// 与蓄力重击（按住，PlayerChargeAttackState）。攻击输入路由（决策 A：代码计时 + 阈值门控）：
    /// 点按→普攻；按住过 TapThreshold→蓄力，tap 不经拉弓。
    /// </summary>
    public class ArcherController : PlayerControllerBase
    {
        [Header("Bow Attack")] [SerializeField] private ComboDefinition _combo; // 单段连段表（普通攻击 1 段）
        [SerializeField] private GameObject _arrowPrefab;     // 箭矢预制体（带 Rigidbody + Collider + Arrow）
        [SerializeField] private Transform _arrowSpawnPoint;  // 箭矢生成点（弓弦中点）
        [SerializeField] private float _projectileSpeed = 20f; // 普通攻击箭矢初速度

        [Header("Charge Attack")] [SerializeField] private ChargeAttackDefinition _chargeData; // 蓄力重击数据 SO

        private PlayerBowAttackState _bowAttackState;
        private PlayerChargeAttackState _chargeAttackState;
        private int[] _comboStateHashes;
        private HealthComponent _health;       // 阵营来源（缓存）
        private float _attackHeldTime;         // 攻击键按住累计时长（tap/hold 路由）

        // 蓄力动画状态名预 hash（Awake 算一次）；满弓态由 Animator 过渡驱动，代码不 hash
        private int _chargeDrawHash;
        private int _chargeLooseHash;

        public ComboDefinition Combo => _combo;
        public GameObject ArrowPrefab => _arrowPrefab;
        public Transform ArrowSpawnPoint => _arrowSpawnPoint;
        public float ProjectileSpeed => _projectileSpeed;
        public HealthComponent Health => _health;
        public PlayerBowAttackState BowAttackState => _bowAttackState;

        public ChargeAttackDefinition ChargeData => _chargeData;
        public PlayerChargeAttackState ChargeAttackState => _chargeAttackState;
        public int ChargeDrawHash => _chargeDrawHash;
        public int ChargeLooseHash => _chargeLooseHash;

        protected override void Awake()
        {
            base.Awake();
            _health = GetComponent<HealthComponent>();
            _bowAttackState = new PlayerBowAttackState(this);
            _chargeAttackState = new PlayerChargeAttackState(this);
            BuildComboStateHashes();
            BuildChargeHashes();
        }

        /// <summary>
        /// 攻击输入路由（决策 A：代码计时 + 阈值门控，tap 不经拉弓）：
        /// 按住累计时长，过 TapThreshold → 蓄力态；未达阈值松手（或子帧点按）→ 普通攻击点射。
        /// </summary>
        public override bool TryStartAttack()
        {
            // 按住且有蓄力数据：累计时长，过阈值进蓄力
            if (_chargeData != null && IsAttackHeld)
            {
                _attackHeldTime += Time.deltaTime;
                if (_attackHeldTime >= _chargeData.TapThreshold)
                {
                    _attackHeldTime = 0f;
                    AttackBufferCounter = 0f;
                    StateMachine.ChangeState(_chargeAttackState);
                    return true;
                }
                return false; // 仍在 tap 窗口内，按住等待
            }

            // 已松手（或无蓄力数据）：曾有按下 → 普通攻击点射
            bool hadPress = _attackHeldTime > 0f || AttackBufferCounter > 0f;
            _attackHeldTime = 0f;
            if (hadPress)
            {
                AttackBufferCounter = 0f;
                StateMachine.ChangeState(_bowAttackState);
                return true;
            }
            return false;
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>把连段表各段 AnimationStateName 预 hash 成 int[]（仿 WarriorController，平行实现）。</summary>
        private void BuildComboStateHashes()
        {
            int count = _combo != null ? _combo.SegmentCount : 0;
            _comboStateHashes = new int[count];
            for (int i = 0; i < count; i++)
            {
                AttackDefinition seg = _combo.Segments[i];
                string stateName = seg != null ? seg.AnimationStateName : null;
                if (string.IsNullOrEmpty(stateName))
                {
                    _comboStateHashes[i] = 0;
                    GameLog.Warn($"弓箭手连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }

        /// <summary>预 hash 蓄力拉弓/放箭两段状态名（满弓态由 Animator 过渡驱动，代码不 hash）。</summary>
        private void BuildChargeHashes()
        {
            if (_chargeData == null)
            {
                GameLog.Warn("弓箭手 ChargeAttackDefinition 未配置，蓄力重击不可用", "Combat");
                return;
            }
            _chargeDrawHash = HashState(_chargeData.DrawStateName);
            _chargeLooseHash = HashState(_chargeData.LooseStateName);
        }

        private static int HashState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName))
            {
                GameLog.Warn("蓄力动画状态名为空，CrossFade 将无法切换动画", "Combat");
                return 0;
            }
            return Animator.StringToHash(stateName);
        }
    }
}
