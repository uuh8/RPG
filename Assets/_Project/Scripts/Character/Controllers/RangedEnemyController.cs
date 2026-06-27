using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 远程敌人：保持距离施法。EngageState = KiteState(远追/近退/中间站档输出)；
    /// 施法态在 ArrowSpawnTime 朝玩家(胸口高度)直线发射火球——复用玩家 Fireball 预制，Init 注入敌方阵营 → 只伤玩家、自带爆炸+点燃。
    /// </summary>
    public class RangedEnemyController : EnemyControllerBase
    {
        [Header("远程")]
        [Tooltip("火球生成点(法杖前端等)，必须是本敌人下的子物体")]
        [SerializeField] private Transform _projectileSpawnPoint;
        [Tooltip("瞄准玩家时的目标高度偏移(打向胸口而非脚下)")]
        [SerializeField] private float _aimHeightOffset = 1.0f;

        private HealthComponent _health;
        private EnemyKiteState _kiteState;
        private EnemyRangedAttackState _rangedAttackState;

        public EnemyKiteState KiteState => _kiteState;
        public EnemyRangedAttackState RangedAttackState => _rangedAttackState;
        public override EnemyStateBase EngageState => _kiteState;

        protected override void Awake()
        {
            base.Awake();
            _health = GetComponent<HealthComponent>();
            _kiteState = new EnemyKiteState(this);
            _rangedAttackState = new EnemyRangedAttackState(this);
        }

        /// <summary>朝玩家(胸口高度)直线发射一颗火球，注入敌方阵营。由施法态在 ArrowSpawnTime 调用。</summary>
        public void SpawnFireball()
        {
            if (_definition == null || _definition.ProjectilePrefab == null || _projectileSpawnPoint == null)
            {
                GameLog.Warn("远程敌人 ProjectilePrefab/生成点未配置，无法发射", "Enemy");
                return;
            }

            Transform target = Perception.Target;
            Vector3 aimPoint = target != null
                ? target.position + Vector3.up * _aimHeightOffset
                : _projectileSpawnPoint.position + transform.forward;

            Vector3 dir = aimPoint - _projectileSpawnPoint.position;
            if (dir.sqrMagnitude < 1e-6f) dir = transform.forward;
            dir.Normalize();

            GameObject go = Instantiate(_definition.ProjectilePrefab, _projectileSpawnPoint.position,
                                        Quaternion.LookRotation(dir));
            Fireball fireball = go.GetComponent<Fireball>();
            if (fireball == null)
            {
                GameLog.Warn("ProjectilePrefab 上没有 Fireball 组件", "Enemy");
                return;
            }

            byte team = _health != null ? _health.TeamId : (byte)0;
            int attackerId = gameObject.GetInstanceID();
            AttackDefinition atk = _definition.Attack;
            float dmg = atk != null ? atk.BaseAmount : 0f;
            DamageType type = atk != null ? atk.Type : DamageType.Magical;
            Vector3 velocity = dir * _definition.ProjectileSpeed;
            // 直线飞行：火球关重力
            fireball.Init(team, attackerId, dmg, type, velocity, _cc, useGravity: false);
        }
    }
}
