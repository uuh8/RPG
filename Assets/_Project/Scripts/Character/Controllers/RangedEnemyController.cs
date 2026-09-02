using UnityEngine;
using Game.Combat;
using Game.Core;
using Game.Skills;
using UnityEngine.Serialization;

namespace Game.Character
{
    /// <summary>
    /// 远程敌人：保持距离施法。EngageState = KiteState(远追/近退/中间站档输出)；
    /// 施法态在 ArrowSpawnTime 从 Authoring 配置的 WandLoadout 中选择一套程序，
    /// 再交给 SpellCaster 复用玩家与 Boss 相同的解释、生成、Payload 和伤害链路。
    /// </summary>
    [RequireComponent(typeof(SpellCaster))]
    public class RangedEnemyController : EnemyControllerBase
    {
        [Header("远程施法")]
        [FormerlySerializedAs("_projectileSpawnPoint")]
        [Tooltip("法术生成点（法杖前端等），必须是本敌人下的子物体")]
        [SerializeField] private Transform _castOrigin;
        [Tooltip("开发者离线配置的法术程序。普通敌人可填 1 套，精英敌人可填 2~3 套。")]
        [SerializeField] private WandLoadout[] _spellPrograms;
        [Tooltip("瞄准玩家时的目标高度偏移(打向胸口而非脚下)")]
        [SerializeField] private float _aimHeightOffset = 1.0f;

        private HealthComponent _health;
        private SpellCaster _spellCaster;
        private WandLoadout[] _validPrograms;
        private int _lastProgramIndex = -1;
        private EnemyKiteState _kiteState;
        private EnemyRangedAttackState _rangedAttackState;

        public EnemyKiteState KiteState => _kiteState;
        public EnemyRangedAttackState RangedAttackState => _rangedAttackState;
        public override EnemyStateBase EngageState => _kiteState;

        protected override void Awake()
        {
            base.Awake();
            _health = GetComponent<HealthComponent>();
            _spellCaster = GetComponent<SpellCaster>();
            BuildValidProgramCache();
            _kiteState = new EnemyKiteState(this);
            _rangedAttackState = new EnemyRangedAttackState(this);
        }

        /// <summary>
        /// 在真正的动画释放帧选择并提交一套法术程序。返回值只表示有效 Program 已提交；
        /// 它不把 EmitCommand 数量误当成最终成功 Instantiate 的对象数量。
        /// 普通敌人只有一个候选时稳定选择该配置，精英敌人有多个候选时均匀随机且不连续重复。
        /// </summary>
        public bool CastSelectedProgram()
        {
            if (_validPrograms == null || _validPrograms.Length == 0)
            {
                GameLog.Warn("远程敌人没有可执行的法术程序，已跳过本次释放", "Enemy");
                return false;
            }

            if (_spellCaster == null || _castOrigin == null)
            {
                GameLog.Warn("远程敌人缺少 SpellCaster 或法术生成点，已跳过本次释放", "Enemy");
                return false;
            }

            int selectedIndex = EnemySpellProgramSelector.SelectNextIndex(
                _validPrograms.Length,
                _lastProgramIndex,
                Random.value);
            if (selectedIndex < 0)
            {
                return false;
            }

            _lastProgramIndex = selectedIndex;
            // 测试或异常初始化顺序下感知器可能尚未建立；此时仍可沿自身 forward 做确定性降级施放。
            Transform target = Perception != null ? Perception.Target : null;
            Vector3 aimPoint = target != null
                ? target.position + Vector3.up * _aimHeightOffset
                : _castOrigin.position + transform.forward;

            byte team = _health != null ? _health.TeamId : (byte)0;
            int attackerId = _health != null ? _health.Id : gameObject.GetInstanceID();
            _spellCaster.CastProgram(
                _validPrograms[selectedIndex],
                _castOrigin.position,
                aimPoint,
                team,
                attackerId,
                _cc,
                SpellManaPolicy.IgnoreMana);
            return true;
        }

        /// <summary>
        /// Authoring 数组只在初始化时过滤一次，攻击 Hot Path 直接读取紧凑缓存，
        /// 避免每次施法扫描配置或创建临时集合。
        /// </summary>
        private void BuildValidProgramCache()
        {
            int sourceCount = _spellPrograms != null ? _spellPrograms.Length : 0;
            int validCount = 0;
            for (int i = 0; i < sourceCount; i++)
            {
                WandLoadout program = _spellPrograms[i];
                if (program != null && program.Spells != null && program.Spells.Length > 0)
                {
                    validCount++;
                }
            }

            _validPrograms = new WandLoadout[validCount];
            int writeIndex = 0;
            for (int i = 0; i < sourceCount; i++)
            {
                WandLoadout program = _spellPrograms[i];
                if (program == null || program.Spells == null || program.Spells.Length == 0)
                {
                    continue;
                }

                _validPrograms[writeIndex++] = program;
            }
        }
    }
}
