using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师控制器：共享移动能力之上叠加远程攻击。攻击输入路由（仿 ArcherController 决策 A：代码计时 + 阈值门控）：
    /// 点按左键 → 火球普攻（PlayerWizardAttackState）；长按左键过 TapThreshold → 陨石重击引导（PlayerWizardHeavyState）。
    /// 发射物 Fireball / 陨石 NovaFireball 均派生自 ProjectileBase。
    /// </summary>
    public class WizardController : PlayerControllerBase
    {
        [Header("Wizard Attack (火球普攻)")]
        [SerializeField] private ComboDefinition _combo;          // 单段连段表（普通攻击 1 段）
        [SerializeField] private GameObject _fireballPrefab;      // 火球预制体（带 Rigidbody + Collider + Fireball）
        [SerializeField] private Transform _fireballSpawnPoint;   // 火球生成点（法杖前端）
        [SerializeField] private float _projectileSpeed = 20f;    // 火球初速度
        [Tooltip("空中普攻动画状态名（Animator 节点 JumpAttack_MagicWand）；空 → 0 → 空中攻击退回地面普攻动画")]
        [SerializeField] private string _airAttackStateName = "JumpAttack_MagicWand"; // 空中火球普攻动画状态名（数据驱动）

        [Header("Wizard Heavy (陨石重击)")]
        [SerializeField] private MeteorAttackDefinition _meteorData;
        [SerializeField] private GameObject _meteorPrefab;            // NovaFireball
        [SerializeField] private GameObject _channelRingPrefab;       // NovaFireball_Skill_Start（脚下光圈）
        [SerializeField] private GameObject _targetIndicatorPrefab;   // NovaFireball_Pre_Field（落点圆框）
        [SerializeField] private LayerMask _aimMask = ~0;            // 引导落点射线命中层（建议只勾地面层）

        [Header("Aim")]
        [Tooltip("普通火球屏幕中心瞄准的可命中层（排除 Player 层，免瞄到自己；与陨石的地面层 _aimMask 区别：这里要能瞄到敌人/环境）")]
        [SerializeField] private LayerMask _fireballAimMask = ~0;
        [Tooltip("普通火球屏幕中心瞄准的射线最大距离；未命中时取相机朝向该远点")]
        [SerializeField] private float _aimMaxDistance = 100f;

        private PlayerWizardAttackState _wizardAttackState;
        private PlayerWizardHeavyState _heavyState;
        private int[] _comboStateHashes;
        private HealthComponent _health;       // 阵营来源（缓存）

        // ── 攻击输入（常驻处理：点按锁存准心 + tap/hold 判定 + 点按入队，见 UpdateAttackInput）──
        private float _attackHeldTime;         // 当前这次按下已持续时长（按下清零、按住累加、松开归零）——tap/hold 判定
        private bool _attackPressActive;       // 当前是否有一次"按下"在进行（上升沿置真，松开/被消费置假）
        private bool _fireballQueued;          // 是否有一发待发的点按火球（按下入队；发出/转陨石/过期时出队）
        private float _fireballQueueTimer;     // 入队存活计时：跨过射速冷却仍能补发，过期作废避免迟发
        private Vector3 _clickAimPoint;        // 按下那一刻锁存的准心瞄准点：消除"出手时相机/身体已变"的方向漂移
        private bool _hasClickAim;             // 是否已锁存过有效的点按瞄准点
        private readonly RaycastHit[] _aimHits = new RaycastHit[16]; // 点按锁存瞄准用射线缓冲（预分配，零每帧 GC）

        // 陨石引导/施法动画状态名预 hash（可空 → 0 → 不 CrossFade）
        private int _meteorChannelHash;
        private int _meteorReleaseHash;
        private int _airAttackStateHash;       // 空中普攻动画状态名预 hash（可空 → 0 → 退回地面普攻动画）

        public ComboDefinition Combo => _combo;
        public GameObject FireballPrefab => _fireballPrefab;
        public Transform FireballSpawnPoint => _fireballSpawnPoint;
        public float ProjectileSpeed => _projectileSpeed;
        public HealthComponent Health => _health;
        public PlayerWizardAttackState WizardAttackState => _wizardAttackState;
        public Vector3 ClickAimPoint => _clickAimPoint; // 普攻态发射时读取：按下那一刻锁存的准心瞄准点
        public bool HasClickAim => _hasClickAim;

        public MeteorAttackDefinition MeteorData => _meteorData;
        public GameObject MeteorPrefab => _meteorPrefab;
        public GameObject ChannelRingPrefab => _channelRingPrefab;
        public GameObject TargetIndicatorPrefab => _targetIndicatorPrefab;
        public LayerMask AimMask => _aimMask;
        public LayerMask FireballAimMask => _fireballAimMask;
        public float AimMaxDistance => _aimMaxDistance;
        public PlayerWizardHeavyState HeavyState => _heavyState;
        public int MeteorChannelHash => _meteorChannelHash;
        public int MeteorReleaseHash => _meteorReleaseHash;
        public int AirAttackStateHash => _airAttackStateHash;

        protected override void Awake()
        {
            base.Awake();   // 基类：组件/输入/状态机/共享四态/Dash hash

            _health = GetComponent<HealthComponent>();
            _wizardAttackState = new PlayerWizardAttackState(this);
            _heavyState = new PlayerWizardHeavyState(this);
            BuildComboStateHashes();
            BuildMeteorHashes();

            // 空中普攻动画名预 hash（数据驱动；空 → 0 → 空中攻击态退回地面普攻动画，不告警——空中攻击为可选润色）
            _airAttackStateHash = string.IsNullOrEmpty(_airAttackStateName)
                ? 0 : Animator.StringToHash(_airAttackStateName);
        }

        // 远程角色全程常驻准心：Start 保证初始可见（越过 OnEnable 与 CrosshairUI 订阅的先后竞态），
        // OnEnable 覆盖运行时重新启用（如复活），OnDisable（含死亡禁用控制器）隐藏。
        protected override void Start()
        {
            base.Start();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = false });
        }

        // 点按入队的存活时长：覆盖一次射速冷却 + 通用攻击缓冲，确保冷却期内/攻击播放中按下的下一发能可靠补发而非被丢弃。
        private float FireballQueueLifetime =>
            (_combo != null ? _combo.AttackCooldown : 0f) + AttackBufferTime;

        /// <summary>
        /// 每帧攻击输入常驻处理（由基类 Update 调用，始终运行，不被攻击状态打断轮询）：
        /// ① 上升沿 → 锁存"点按那一刻"的准心瞄准点（消除出手时相机/身体已转动导致的方向漂移）+ 入队一发火球 + 重置按住计时；
        /// ② 按住 → 累加计时（供 tap/hold 判定）；松开 → 结束本次按下；
        /// ③ 入队计时过期 → 作废，避免迟发。
        /// 这样快速连点的第二下即便落在攻击动画/射速冷却内，也会被可靠记账并在冷却一过补发，而不是被吞掉。
        /// </summary>
        protected override void UpdateAttackInput()
        {
            if (AttackPressedThisFrame)
            {
                _clickAimPoint = ResolveAimTargetPoint(_fireballAimMask, _aimMaxDistance, _aimHits);
                _hasClickAim = true;
                _attackPressActive = true;
                _attackHeldTime = 0f;
                _fireballQueued = true;
                _fireballQueueTimer = FireballQueueLifetime;
            }

            if (_attackPressActive)
            {
                if (IsAttackHeld) _attackHeldTime += Time.deltaTime;
                else _attackPressActive = false; // 松手：本次按下结束（是否补发火球由队列决定）
            }

            if (_fireballQueueTimer > 0f)
            {
                _fireballQueueTimer -= Time.deltaTime;
                if (_fireballQueueTimer <= 0f) _fireballQueued = false; // 过期作废
            }
        }

        /// <summary>
        /// 地面攻击路由：长按过阈值 → 陨石引导；否则有入队点按且冷却已过 → 火球普攻。
        /// 点按在冷却内不丢弃（已入队），冷却一过自动补发——解决"快速连点第二发被吞 → 误去狂按 → 触发陨石"。
        /// </summary>
        public override bool TryStartAttack()
        {
            // 长按 → 陨石引导（仅地面）：当前按住且持续超过阈值。消费本次按下并取消待发火球。
            if (_attackPressActive && IsAttackHeld
                && _meteorData != null && _attackHeldTime >= _meteorData.TapThreshold)
            {
                _attackPressActive = false;
                _fireballQueued = false;
                _fireballQueueTimer = 0f;
                StateMachine.ChangeState(_heavyState);
                return true;
            }

            // "未判定的长按"：仍按住且未到阈值时先等，以区分点按/长按（无陨石配置则按下即发，不等松手）。
            bool undecidedHold = _meteorData != null && _attackPressActive && IsAttackHeld
                                 && _attackHeldTime < _meteorData.TapThreshold;
            if (_fireballQueued && AttackCooldownCounter <= 0f && !undecidedHold)
            {
                FireQueuedFireball();
                return true;
            }

            return false;
        }

        /// <summary>
        /// 空中攻击：仅支持点按火球（陨石是地面落点技能）。有入队点按 + 冷却过 → 直接发，不做长按判定。
        /// 方向同样用按下锁存的 ClickAimPoint。
        /// </summary>
        public override bool TryStartAirAttack()
        {
            if (_fireballQueued && AttackCooldownCounter <= 0f)
            {
                FireQueuedFireball();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 发射一发已入队的点按火球：出队 + 消费本次按下（一次按下只触发一个动作，防同一长按落地再触发陨石）+
        /// 启动射速冷却 + 切到普攻态（其 Enter 据是否接地决定空中/地面动画，发射方向用 ClickAimPoint）。
        /// </summary>
        private void FireQueuedFireball()
        {
            _fireballQueued = false;
            _fireballQueueTimer = 0f;
            _attackPressActive = false;
            AttackCooldownCounter = _combo != null ? _combo.AttackCooldown : 0f; // 启动射速冷却（与动画长度解耦）
            AttackBufferCounter = 0f;
            StateMachine.ChangeState(_wizardAttackState);
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>把连段表各段 AnimationStateName 预 hash 成 int[]（仿 Warrior/Archer）。</summary>
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
                    GameLog.Warn($"法师连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }

        /// <summary>预 hash 陨石引导/施法动画名。二者可空（引导/施法动画为可选润色）：空 → 0 → 重击态据此跳过 CrossFade，不告警。</summary>
        private void BuildMeteorHashes()
        {
            if (_meteorData == null)
            {
                GameLog.Warn("法师 MeteorAttackDefinition 未配置，陨石重击不可用", "Combat");
                return;
            }
            _meteorChannelHash = string.IsNullOrEmpty(_meteorData.ChannelStateName)
                ? 0 : Animator.StringToHash(_meteorData.ChannelStateName);
            _meteorReleaseHash = string.IsNullOrEmpty(_meteorData.ReleaseStateName)
                ? 0 : Animator.StringToHash(_meteorData.ReleaseStateName);
        }
    }
}
