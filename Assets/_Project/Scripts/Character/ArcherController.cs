using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手控制器：在 PlayerControllerBase 共享能力之上，叠加弓箭手专属的远程普通攻击
    /// （单段 ComboDefinition + PlayerBowAttackState + 箭矢生成）。蓄力重击在 Phase 4。
    /// </summary>
    public class ArcherController : PlayerControllerBase
    {
        [Header("Bow Attack")] [SerializeField] private ComboDefinition _combo; // 单段连段表（普通攻击 1 段）
        [SerializeField] private GameObject _arrowPrefab;     // 箭矢预制体（带 Rigidbody + Collider + Arrow）
        [SerializeField] private Transform _arrowSpawnPoint;  // 箭矢生成点（弓弦中点，BowHero 下的 ArrowSpawnPoint）
        [SerializeField] private float _projectileSpeed = 20f; // 箭矢初速度

        private PlayerBowAttackState _bowAttackState;
        private int[] _comboStateHashes;     // 连段各段 Animator 状态名预 hash（Awake 算一次）
        private HealthComponent _health;      // 阵营来源（缓存，避免每次攻击 GetComponent）

        public ComboDefinition Combo => _combo;
        public GameObject ArrowPrefab => _arrowPrefab;
        public Transform ArrowSpawnPoint => _arrowSpawnPoint;
        public float ProjectileSpeed => _projectileSpeed;
        public PlayerBowAttackState BowAttackState => _bowAttackState;
        public HealthComponent Health => _health;

        protected override void Awake()
        {
            base.Awake();
            _health = GetComponent<HealthComponent>();
            _bowAttackState = new PlayerBowAttackState(this);
            BuildComboStateHashes();
        }

        /// <summary>攻击 seam 实现：有缓冲攻击输入则切到弓箭普通攻击态（Phase 3 点按即射；蓄力在 Phase 4）。</summary>
        public override bool TryStartAttack()
        {
            if (AttackBufferCounter > 0f)
            {
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
    }
}
