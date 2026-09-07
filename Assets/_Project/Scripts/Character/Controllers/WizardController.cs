using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法术角色控制器：在共享移动能力之上负责“输入意图”这一段攻击链。
    /// 按下攻击键 -> 锁存准心与缓存请求 -> FSM 允许后进入 PlayerWizardAttackState；
    /// 真正的法术求值与投射物生成分别交给 CastEvaluator 和 SpellCaster，本类不实现组合规则。
    /// 当前陨石与其他投射物一样都是法术数据，不再由角色控制器保存专用重击状态。
    /// </summary>
    public class WizardController : PlayerControllerBase
    {
        private static readonly int JumpStartHash = Animator.StringToHash("JumpStart_MagicWand");
        private static readonly int JumpAirHash = Animator.StringToHash("JumpAir_MagicWand");
        private static readonly int JumpEndHash = Animator.StringToHash("JumpEnd_MagicWand");
        // 当前 Wizard Controller 的待机 State 历史命名仍是 Idle_Bow；必须匹配真实节点名，CrossFade 不存在的名字会静默失败。
        private static readonly int IdleHash = Animator.StringToHash("Idle_Bow");
        private static readonly int RunHash = Animator.StringToHash("Run_MagicWand");

        [Header("Wizard Jump Animation")]
        [Tooltip("JumpStart 过渡到 JumpAir 的 CrossFade 时长（秒）。姿势差较大，建议 0.15~0.22。")]
        [SerializeField, Min(0f)] private float _jumpStartToAirBlendDuration = 0.18f;
        [Tooltip("进入 JumpStart、落地 JumpEnd、自然离地和回到 Idle/Run 的通用 CrossFade 时长（秒）。建议 0.08~0.16。")]
        [SerializeField, Min(0f)] private float _jumpOtherBlendDuration = 0.12f;

        [Header("Wizard Jump VFX")]
        [Tooltip("每次真正起跳时生成在角色脚底的特效；普通跳、Coyote Jump 与二段跳共用。")]
        [SerializeField] private GameObject _jumpVfxPrefab;
        [Tooltip("特效生成点相对 CharacterController 脚底的垂直偏移。地面穿插时可适当调高。")]
        [SerializeField] private float _jumpVfxVerticalOffset = 0.02f;
        [Tooltip("特效实例自动销毁延迟。当前 Jump Prefab 最长 ParticleSystem Duration 为 5 秒。")]
        [SerializeField, Min(0.1f)] private float _jumpVfxLifetime = 5.5f;

        [Header("Wizard Attack (法杖施放)")]
        // ComboDefinition 在这里仅复用“攻击冷却 + 动画 State 名 + 归一化出手进度”，不表示当前法术攻击是多段连招。
        [SerializeField] private ComboDefinition _combo;
        // 历史字段名仍叫 FireballSpawnPoint；语义是所有法术产出的通用世界空间生成 Transform。
        [SerializeField] private Transform _fireballSpawnPoint;
        [Tooltip("空中普攻动画状态名（Animator 节点 JumpAttack_MagicWand）；空 → 0 → 空中攻击退回地面普攻动画")]
        [SerializeField] private string _airAttackStateName = "JumpAttack_MagicWand";

        [Header("Aim")]
        [Tooltip("屏幕中心瞄准的可命中层（排除 Player 层，免瞄到自己）")]
        [SerializeField] private LayerMask _fireballAimMask = ~0;
        [Tooltip("屏幕中心瞄准的射线最大距离；未命中时取相机朝向该远点")]
        [SerializeField] private float _aimMaxDistance = 100f;

        // State 是普通 C# 对象，在 Awake 只创建一次；运行中只切换引用，避免每次攻击 new 状态造成 GC Alloc。
        private PlayerWizardAttackState _wizardAttackState;
        private SpellCaster _spellCaster;             // 同物体上的法术施放器（运行法杖 → 生成投射物）
        private int[] _comboStateHashes;
        private HealthComponent _health;

        // ── 攻击输入 Runtime State ──
        // 这不是 Queue<T>，而是“一个待处理标记 + 剩余有效时间 + 瞄准点”的单槽缓存。
        // 新点击会覆盖旧请求，因此既能宽容冷却期内的提前输入，又不会无限积压连发次数。
        private bool _castQueued;                // true = 当前存在一次尚未被 FSM 消费的施法请求；false = 表示当前没有等待执行的施法请求
        private float _castQueueTimer;           // 这次请求的剩余寿命秒数；归零仍未消费则作废，表示“玩家刚才的攻击意图还能保留多久”
        private Vector3 _clickAimPoint;          // 按下那一刻锁存的准心瞄准点（消除出手时相机/身体已变的方向漂移）
        private bool _hasClickAim;
        private readonly RaycastHit[] _aimHits = new RaycastHit[16]; // 点按锁存瞄准用射线缓冲（预分配，零每帧 GC）

        private int _airAttackStateHash;

        public ComboDefinition Combo => _combo;
        public Transform FireballSpawnPoint => _fireballSpawnPoint;
        public HealthComponent Health => _health;
        public SpellCaster SpellCaster => _spellCaster;
        public Vector3 ClickAimPoint => _clickAimPoint; // 施法态释放时读取：按下那一刻锁存的准心
        public bool HasClickAim => _hasClickAim;

        public float AimMaxDistance => _aimMaxDistance;
        public int AirAttackStateHash => _airAttackStateHash;

        protected override void Awake()
        {
            // override 的 Awake 必须先调用 base.Awake，保证共享组件、Input Actions 和 FSM 已初始化，
            // 子类随后才能安全创建持有 this 引用的专属攻击 State。
            base.Awake();

            // GetComponent 只查询同一 GameObject；这些组件在 Prefab 上与控制器并列挂载。
            _health = GetComponent<HealthComponent>();
            _spellCaster = GetComponent<SpellCaster>();
            _wizardAttackState = new PlayerWizardAttackState(this);
            BuildComboStateHashes();

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

        // 请求寿命覆盖一次射速冷却再加通用 Buffer，使冷却期内提前按下的下一发能等到可用时刻。
        private float CastQueueLifetime =>
            (_combo != null ? _combo.AttackCooldown : 0f) + AttackBufferTime;

        /// <summary>
        /// 每帧攻击输入常驻处理（由基类 Update 调用，始终运行，不被攻击状态打断）：
        /// 上升沿 → 锁存"点按那一刻"的准心瞄准点 + 入队一次施放；入队计时过期 → 作废。
        /// 这样快速连点即便落在动画/射速冷却内也不丢，冷却一过补发，方向用按下瞬间的准心。
        /// </summary>
        protected override void UpdateAttackInput()
        {
            // WasPressedThisFrame 只在按下边沿为 true；持续按住不会每帧重复覆盖请求。
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

        /// <summary>
        /// Wizard 的跳跃动画由 Gameplay FSM 与垂直速度共同驱动，并使用 Inspector 中可调的
        /// CrossFade 时长。Animator Controller 不再保存一套无法在 Runtime 调参的重复时序。
        /// </summary>
        protected override void SyncCharacterAnimatorParameters(bool animIsGrounded)
        {
            bool isAirborneLocomotion = ReferenceEquals(StateMachine.CurrentState, AirborneState);
            bool isGroundedLocomotion = ReferenceEquals(StateMachine.CurrentState, GroundedState) ||
                                        ReferenceEquals(StateMachine.CurrentState, SlidingState);

            if (!isAirborneLocomotion && !isGroundedLocomotion)
                return; // Dash/Attack 自己拥有动画，直到 Gameplay FSM 回到 locomotion 才重新接管。

            AnimatorStateInfo stateInfo = ReadEffectiveAnimatorState();
            JumpAnimationPhase currentPhase = GetJumpAnimationPhase(stateInfo.shortNameHash);

            if (isAirborneLocomotion)
            {
                JumpAnimationPhase targetPhase = JumpAnimationPhaseResolver.ResolveAirbornePhase(
                    currentPhase,
                    stateInfo.normalizedTime,
                    JumpAnimationPhaseResolver.IsFalling(animIsGrounded, VerticalVelocity));

                if (targetPhase == JumpAnimationPhase.JumpAir && currentPhase != targetPhase)
                {
                    float duration = currentPhase == JumpAnimationPhase.JumpStart
                        ? _jumpStartToAirBlendDuration
                        : _jumpOtherBlendDuration;
                    CrossFadeJumpState(JumpAirHash, duration);
                }

                return;
            }

            JumpAnimationPhase groundedTarget = JumpAnimationPhaseResolver.ResolveGroundedPhase(
                currentPhase,
                stateInfo.normalizedTime);

            if (groundedTarget == JumpAnimationPhase.JumpEnd && currentPhase != groundedTarget)
            {
                CrossFadeJumpState(JumpEndHash, _jumpOtherBlendDuration);
            }
            else if (groundedTarget == JumpAnimationPhase.Locomotion &&
                     currentPhase == JumpAnimationPhase.JumpEnd)
            {
                int locomotionHash = MoveInput.sqrMagnitude >= 0.01f ? RunHash : IdleHash;
                CrossFadeJumpState(locomotionHash, _jumpOtherBlendDuration);
            }
        }

        /// <summary>
        /// Wizard 不再依赖 Animator 中写死时长的 jump Trigger Transition。
        /// 每次 Gameplay 真正批准跳跃时，从目标 Clip 的 0 秒重新 CrossFade，二段跳因此会可靠重播 JumpStart。
        /// </summary>
        public override void PlayJumpStartAnimation()
        {
            ClearPendingJumpAnimation();
            Animator.CrossFadeInFixedTime(
                JumpStartHash,
                Mathf.Max(0f, _jumpOtherBlendDuration),
                0,
                0f);
        }

        /// <summary>
        /// 特效使用世界坐标生成且不挂到角色下面：起跳后粒子留在起跳点，避免跟着角色一起飞。
        /// Jump 是离散输入，Instantiate/Destroy 的一次性分配符合项目热路径约束。
        /// </summary>
        protected override void PlayJumpVfx()
        {
            if (_jumpVfxPrefab == null) return;

            Vector3 spawnPosition = JumpVfxPlacement.ResolveFootPosition(
                CharacterController.bounds,
                _jumpVfxVerticalOffset);
            GameObject instance = Instantiate(
                _jumpVfxPrefab,
                spawnPosition,
                _jumpVfxPrefab.transform.rotation);
            Destroy(instance, Mathf.Max(0.1f, _jumpVfxLifetime));
        }

        /// <summary>
        /// CrossFade 期间以目标 State 为“当前语义阶段”，防止每帧重复启动同一次过渡。
        /// </summary>
        private AnimatorStateInfo ReadEffectiveAnimatorState()
        {
            return Animator.IsInTransition(0)
                ? Animator.GetNextAnimatorStateInfo(0)
                : Animator.GetCurrentAnimatorStateInfo(0);
        }

        private static JumpAnimationPhase GetJumpAnimationPhase(int stateHash)
        {
            if (stateHash == JumpStartHash) return JumpAnimationPhase.JumpStart;
            if (stateHash == JumpAirHash) return JumpAnimationPhase.JumpAir;
            if (stateHash == JumpEndHash) return JumpAnimationPhase.JumpEnd;
            return JumpAnimationPhase.Locomotion;
        }

        private void CrossFadeJumpState(int targetHash, float duration)
        {
            Animator.CrossFadeInFixedTime(targetHash, Mathf.Max(0f, duration), 0, 0f);
        }

        /// <summary>
        /// 地面状态的攻击扩展入口。这里只判断“有请求 + 冷却结束”并切状态，尚未计算法术或创建投射物。
        /// 返回 true 告诉 GroundedState 本帧已经发生状态转换，不要继续处理 Jump/离地等低优先级分支。
        /// </summary>
        public override bool TryStartAttack()
        {
            if (_castQueued && AttackCooldownCounter <= 0f)
            {
                FireQueuedCast();
                return true;
            }
            return false;
        }

        /// <summary>空中状态的平行入口；复用同一个单槽请求与锁存瞄准点，只由攻击 State 选择空中动画。</summary>
        public override bool TryStartAirAttack()
        {
            if (_castQueued && AttackCooldownCounter <= 0f)
            {
                FireQueuedCast();
                return true;
            }
            return false;
        }

        /// <summary>原子地消费单槽请求、启动冷却并进入施法 State；真正出手要等待 Animator 进度阈值。</summary>
        private void FireQueuedCast()
        {
            // 清除待处理请求，保证一个请求只执行一次
            _castQueued = false;
            _castQueueTimer = 0f;

            // 冷却在批准攻击时开始，而不是命中时开始；射速因此与投射物飞行时间完全解耦。
            AttackCooldownCounter = _combo != null ? _combo.AttackCooldown : 0f; // 射速冷却（与动画长度解耦）

            // 清理通用攻击缓存
            AttackBufferCounter = 0f;

            // 切换到施法状态
            StateMachine.ChangeState(_wizardAttackState);
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>
        /// 把 Animator State 名预先转换成 int hash。StringToHash 避免运行时反复进行字符串查找，
        /// 之后 CrossFade 直接使用整数；名称必须与 Animator Controller 中的 State 完全一致。
        /// </summary>
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

    }

    /// <summary>把 CharacterController 的世界 Bounds 转换为脚底 VFX 生成点。</summary>
    public static class JumpVfxPlacement
    {
        public static Vector3 ResolveFootPosition(Bounds characterBounds, float verticalOffset)
        {
            Vector3 position = characterBounds.center;
            position.y = characterBounds.min.y + verticalOffset;
            return position;
        }
    }
}
