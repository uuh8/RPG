using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师控制器：共享移动能力之上叠加远程攻击。攻击 = 运行"法杖"（法术编程系统）：
    /// 按左键 → 求值当前法杖（SpellCaster + Game.Skills.CastEvaluator）→ 生成对应投射物。
    /// 取代旧的"点按火球 / 长按陨石"硬编码攻击。陨石相关字段/状态暂保留为休眠（不再路由），后续作为法术重做。
    /// </summary>
    public class WizardController : PlayerControllerBase
    {
        [Header("Wizard Attack (法杖施放)")]
        [SerializeField] private ComboDefinition _combo;          // 施法动画/时机驱动（1 段：动画状态名 + ArrowSpawnTime 决定何时释放法杖）
        [SerializeField] private Transform _fireballSpawnPoint;   // 投射物生成点（法杖前端）
        [Tooltip("空中普攻动画状态名（Animator 节点 JumpAttack_MagicWand）；空 → 0 → 空中攻击退回地面普攻动画")]
        [SerializeField] private string _airAttackStateName = "JumpAttack_MagicWand";

        [Header("Wizard Heavy (陨石重击 — 暂休眠，后续作为法术重做)")]
        [SerializeField] private MeteorAttackDefinition _meteorData;
        [SerializeField] private GameObject _meteorPrefab;
        [SerializeField] private GameObject _channelRingPrefab;
        [SerializeField] private GameObject _targetIndicatorPrefab;
        [SerializeField] private LayerMask _aimMask = ~0;

        [Header("Aim")]
        [Tooltip("屏幕中心瞄准的可命中层（排除 Player 层，免瞄到自己）")]
        [SerializeField] private LayerMask _fireballAimMask = ~0;
        [Tooltip("屏幕中心瞄准的射线最大距离；未命中时取相机朝向该远点")]
        [SerializeField] private float _aimMaxDistance = 100f;

        private PlayerWizardAttackState _wizardAttackState;
        private PlayerWizardHeavyState _heavyState;   // 休眠：构造但不再进入
        private SpellCaster _spellCaster;             // 同物体上的法术施放器（运行法杖 → 生成投射物）
        private int[] _comboStateHashes;
        private HealthComponent _health;

        // ── 攻击输入（常驻处理：点按锁存准心 + 入队，见 UpdateAttackInput）──
        private bool _castQueued;                // 是否有一次待施放（按下入队；发出/过期出队）
        private float _castQueueTimer;           // 入队存活计时：跨过射速冷却仍能补发，过期作废
        private Vector3 _clickAimPoint;          // 按下那一刻锁存的准心瞄准点（消除出手时相机/身体已变的方向漂移）
        private bool _hasClickAim;
        private readonly RaycastHit[] _aimHits = new RaycastHit[16]; // 点按锁存瞄准用射线缓冲（预分配，零每帧 GC）

        private int _meteorChannelHash;          // 休眠
        private int _meteorReleaseHash;          // 休眠
        private int _airAttackStateHash;

        public ComboDefinition Combo => _combo;
        public Transform FireballSpawnPoint => _fireballSpawnPoint;
        public HealthComponent Health => _health;
        public SpellCaster SpellCaster => _spellCaster;
        public PlayerWizardAttackState WizardAttackState => _wizardAttackState;
        public Vector3 ClickAimPoint => _clickAimPoint; // 施法态释放时读取：按下那一刻锁存的准心
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
            _spellCaster = GetComponent<SpellCaster>();
            _wizardAttackState = new PlayerWizardAttackState(this);
            _heavyState = new PlayerWizardHeavyState(this);
            BuildComboStateHashes();
            BuildMeteorHashes();

            _airAttackStateHash = string.IsNullOrEmpty(_airAttackStateName)
                ? 0 : Animator.StringToHash(_airAttackStateName);
        }

        // 远程角色全程常驻准心：Start 保证初始可见，OnEnable 覆盖重新启用，OnDisable 隐藏。
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

        // 入队存活时长：覆盖一次射速冷却 + 通用攻击缓冲，确保冷却期内/动画播放中按下的下一次施放能可靠补发。
        private float CastQueueLifetime =>
            (_combo != null ? _combo.AttackCooldown : 0f) + AttackBufferTime;

        /// <summary>
        /// 每帧攻击输入常驻处理（由基类 Update 调用，始终运行，不被攻击状态打断）：
        /// 上升沿 → 锁存"点按那一刻"的准心瞄准点 + 入队一次施放；入队计时过期 → 作废。
        /// 这样快速连点即便落在动画/射速冷却内也不丢，冷却一过补发，方向用按下瞬间的准心。
        /// </summary>
        protected override void UpdateAttackInput()
        {
            if (AttackPressedThisFrame)
            {
                _clickAimPoint = ResolveAimTargetPoint(_fireballAimMask, _aimMaxDistance, _aimHits);
                _hasClickAim = true;
                _castQueued = true;
                _castQueueTimer = CastQueueLifetime;
            }

            if (_castQueueTimer > 0f)
            {
                _castQueueTimer -= Time.deltaTime;
                if (_castQueueTimer <= 0f) _castQueued = false; // 过期作废
            }
        }

        /// <summary>地面攻击：有入队施放且射速冷却已过 → 运行法杖。（左键 = 运行当前法杖。）</summary>
        public override bool TryStartAttack()
        {
            if (_castQueued && AttackCooldownCounter <= 0f)
            {
                FireQueuedCast();
                return true;
            }
            return false;
        }

        /// <summary>空中攻击：同样运行法杖（方向用按下锁存的 ClickAimPoint）。</summary>
        public override bool TryStartAirAttack()
        {
            if (_castQueued && AttackCooldownCounter <= 0f)
            {
                FireQueuedCast();
                return true;
            }
            return false;
        }

        /// <summary>出队 + 启动射速冷却 + 切到施法态（其 Enter 据接地决定空中/地面动画，释放点由 SpellCaster 运行法杖）。</summary>
        private void FireQueuedCast()
        {
            _castQueued = false;
            _castQueueTimer = 0f;
            AttackCooldownCounter = _combo != null ? _combo.AttackCooldown : 0f; // 射速冷却（与动画长度解耦）
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

        /// <summary>把连段表各段 AnimationStateName 预 hash 成 int[]。</summary>
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

        // 休眠：陨石动画名预 hash（陨石暂不路由，后续作为法术重做时再启用）。
        private void BuildMeteorHashes()
        {
            if (_meteorData == null) return;
            _meteorChannelHash = string.IsNullOrEmpty(_meteorData.ChannelStateName)
                ? 0 : Animator.StringToHash(_meteorData.ChannelStateName);
            _meteorReleaseHash = string.IsNullOrEmpty(_meteorData.ReleaseStateName)
                ? 0 : Animator.StringToHash(_meteorData.ReleaseStateName);
        }
    }
}
