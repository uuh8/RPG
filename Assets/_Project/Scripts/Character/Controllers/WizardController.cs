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
        private float _attackHeldTime;         // 攻击键按住累计时长（tap/hold 路由）
        private bool _attackTracking;          // 是否正在跟踪一次有效输入（仅由"本帧上升沿"开启）

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

        /// <summary>
        /// 攻击输入路由（边沿门控）：仅"本帧刚按下"才开始跟踪一次输入，避免把上一发重击残留的"按住"误判为新长按。
        /// 跟踪中：按住过 TapThreshold → 陨石引导态；未达阈值松手(含亚帧点按) → 火球普攻。
        /// </summary>
        public override bool TryStartAttack()
        {
            // 仅上升沿开启跟踪：从上个动作残留下来的按住没有新边沿，不会启动新蓄力
            if (AttackPressedThisFrame)
            {
                _attackTracking = true;
                _attackHeldTime = 0f;
            }
            if (!_attackTracking)
                return false; // 没有正在跟踪的有效输入

            // 跟踪中且仍按住：累计时长，过阈值进引导
            if (IsAttackHeld)
            {
                _attackHeldTime += Time.deltaTime;
                if (_meteorData != null && _attackHeldTime >= _meteorData.TapThreshold)
                {
                    EndAttackTracking();
                    StateMachine.ChangeState(_heavyState);
                    return true;
                }
                return false; // 仍在 tap 窗口内，按住等待
            }

            // 跟踪中已松手且未达阈值 → 火球普攻点射（含亚帧点按：上升沿+同帧已松开）
            EndAttackTracking();
            if (AttackCooldownCounter > 0f)
                return false; // 射速冷却中：本次点按作废，不进攻击态（重击/蓄力不受此冷却限制）
            AttackCooldownCounter = _combo != null ? _combo.AttackCooldown : 0f; // 启动射速冷却（与动画长度解耦）
            StateMachine.ChangeState(_wizardAttackState);
            return true;
        }

        /// <summary>
        /// 空中攻击：仅支持点按火球普攻（陨石重击是地面落点技能，空中无意义，故不做长按蓄力）。
        /// 用本帧上升沿触发，避免把跳跃前残留的"按住/缓冲"误判为空中起手；同样受射速冷却限制。
        /// </summary>
        public override bool TryStartAirAttack()
        {
            if (!AttackPressedThisFrame)
                return false; // 空中必须本帧新按下才施放，不吃旧缓冲
            if (AttackCooldownCounter > 0f)
                return false; // 射速冷却中：本次点按作废
            AttackCooldownCounter = _combo != null ? _combo.AttackCooldown : 0f; // 启动射速冷却（与地面普攻共用）
            AttackBufferCounter = 0f;
            StateMachine.ChangeState(_wizardAttackState); // 复用普攻态，其 Enter 据是否接地决定播空中/地面动画
            return true;
        }

        /// <summary>结束一次输入跟踪：清跟踪标记、累计时长与攻击缓冲，防止下一发误触发。</summary>
        private void EndAttackTracking()
        {
            _attackTracking = false;
            _attackHeldTime = 0f;
            AttackBufferCounter = 0f;
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
