using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 陨石投射物。从天空斜上方直线砸向引导锁定的落点；命中角色/地面时生成爆炸特效(NovaExplosion_Hit)，
    /// 并对命中的角色结算伤害（伤害与销毁由 ProjectileBase 处理）。直线飞行（生成时 Init 传 useGravity=false）。
    /// </summary>
    public class NovaFireball : ProjectileBase
    {
        [Header("命中表现")]
        [SerializeField] private GameObject _explosionPrefab;   // NovaExplosion_Hit
        [SerializeField] private float _explosionLifetime = 2f;

        protected override void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)
        {
            if (_explosionPrefab != null)
            {
                GameObject fx = Instantiate(_explosionPrefab, hitPoint, Quaternion.identity);
                Destroy(fx, _explosionLifetime);
            }
        }
    }
}
