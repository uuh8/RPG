using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 陨石投射物。从天空斜上方直线砸向引导锁定的落点；命中角色/地面时：
    ///   ① 生成爆炸特效(NovaExplosion_Hit)——其上的 AreaDamage 组件做范围伤害(一次性)；
    ///   ② 生成地面火场(FireField)——其上的 AreaDamage 组件做持续范围伤害(周期跳伤)。
    /// 直击伤害由 ProjectileBase 处理：砸中角色 → 直击 + 爆炸 AOE 都吃；砸中地面 → 无直击，仅 AOE。
    /// 爆炸/火场的半径与伤害配在各自预制体的 AreaDamage 上(自包含、可复用)；本脚本只负责生成 + 注入阵营。
    /// 直线飞行（生成时 Init 传 useGravity=false）。
    /// </summary>
    public class NovaFireball : ProjectileBase
    {
        [Header("爆炸 (一次性 AOE，伤害配在预制体的 AreaDamage 上)")]
        [SerializeField] private GameObject _explosionPrefab;   // NovaExplosion_Hit（建议挂 AreaDamage，TickInterval=0）
        [SerializeField] private float _explosionLifetime = 2f;
        // 爆炸特效旋转(欧拉角)。多数竖直朝向的特效填 (90,0,0) 可平铺到地面；若仍歪改这里。
        [SerializeField] private Vector3 _explosionRotationEuler = new Vector3(90f, 0f, 0f);

        [Header("火场 (持续 AOE，伤害/间隔配在预制体的 AreaDamage 上)")]
        [SerializeField] private GameObject _fireFieldPrefab;   // FireField（建议挂 AreaDamage，TickInterval>0）
        [Tooltip("火场存活时长(秒)：本物体存活多久 = 火场烧多久")]
        [SerializeField] private float _fireFieldLifetime = 5f;
        [SerializeField] private Vector3 _fireFieldRotationEuler = Vector3.zero;

        protected override void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)
        {
            SpawnEffect(_explosionPrefab, hitPoint, _explosionRotationEuler, _explosionLifetime);
            SpawnEffect(_fireFieldPrefab, hitPoint, _fireFieldRotationEuler, _fireFieldLifetime);
        }

        /// <summary>生成一个特效实例，若其上有 AreaDamage 则注入本陨石的阵营，按 lifetime 延时销毁。</summary>
        private void SpawnEffect(GameObject prefab, Vector3 position, Vector3 rotationEuler, float lifetime)
        {
            if (prefab == null) return;
            GameObject go = Instantiate(prefab, position, Quaternion.Euler(rotationEuler));
            // 注入阵营/攻击者，使 AOE 跳过施法者一方、只伤敌人（须在该物体 Start 之前，同帧 Instantiate 后调用即满足）。
            // 用 GetComponentsInChildren：特效预制体常把 AreaDamage 挂在子物体上，只查根会漏掉 → 漏注入则阵营默认 0 会误伤。
            AreaDamage[] areas = go.GetComponentsInChildren<AreaDamage>(true);
            for (int i = 0; i < areas.Length; i++)
                areas[i].Init(_attackerId, _attackerTeam);
            Destroy(go, lifetime);
        }
    }
}
