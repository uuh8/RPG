using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行火球。投射物基类 + 命中表现：命中点生成爆炸特效(Fireball_Explosion)，
    /// 并给被命中的角色附加持续燃烧(BurnStatus + OnFire)。直线飞行（生成时 Init 传 useGravity=false）。
    /// </summary>
    public class Fireball : ProjectileBase
    {
        [Header("命中表现")]
        [SerializeField] private GameObject _explosionPrefab;   // Fireball_Explosion
        [SerializeField] private float _explosionLifetime = 2f;

        [Header("燃烧 (DoT)")]
        [SerializeField] private GameObject _onFirePrefab;      // OnFire
        [SerializeField] private float _burnDamagePerTick = 3f;
        [SerializeField] private float _burnInterval = 0.5f;
        [SerializeField] private float _burnDuration = 3f;

        protected override void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)
        {
            // 命中任何东西都放爆炸
            if (_explosionPrefab != null)
            {
                GameObject fx = Instantiate(_explosionPrefab, hitPoint, Quaternion.identity);
                Destroy(fx, _explosionLifetime);
            }

            // 仅对结算了伤害的角色附加燃烧
            if (damaged)
                ApplyBurn(collision.collider);
        }

        /// <summary>在被命中目标承载 HealthComponent 的根物体上获取/新增 BurnStatus 并施加燃烧。</summary>
        private void ApplyBurn(Collider hitCollider)
        {
            HealthComponent health = hitCollider.GetComponentInParent<HealthComponent>();
            if (health == null) return;

            BurnStatus burn = health.GetComponent<BurnStatus>();
            if (burn == null) burn = health.gameObject.AddComponent<BurnStatus>();
            burn.Apply(_attackerId, _attackerTeam, _burnDamagePerTick, _type,
                       _burnInterval, _burnDuration, _onFirePrefab);
        }
    }
}
