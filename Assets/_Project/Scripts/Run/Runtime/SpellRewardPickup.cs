using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 把 Unity Trigger 转换为“向本局法术背包授予奖励”的一次性 Scene Adapter。
    /// 奖励规则属于 RunSpellSession；本组件只判断玩家是否进入以及拾取物何时隐藏。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class SpellRewardPickup : MonoBehaviour
    {
        [SerializeField] private RunSpellSession _session;
        [SerializeField] private SpellDefinition _rewardSpell;
        [SerializeField] private LayerMask _playerLayers;
        [SerializeField] private GameObject _visualRoot;

        private Collider _trigger;
        private bool _consumed;

        private void Awake()
        {
            _trigger = GetComponent<Collider>();
            if (_trigger != null && !_trigger.isTrigger)
            {
                GameLog.Warn("SpellRewardPickup 的 Collider 应启用 Is Trigger。", "Run");
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_consumed || other == null)
            {
                return;
            }

            int enteringLayerBit = 1 << other.gameObject.layer;
            if ((_playerLayers.value & enteringLayerBit) == 0)
            {
                return;
            }

            if (_session == null || _rewardSpell == null)
            {
                GameLog.Warn("SpellRewardPickup 缺少 RunSpellSession 或 Reward Spell。", "Run");
                return;
            }

            // 即使玩家已拥有该法术，也消耗这个世界拾取物；否则 Trigger 会在角色多个 Collider 下反复触发。
            bool granted = _session.TryGrantSpell(_rewardSpell);
            if (!granted)
            {
                GameLog.Info($"法术奖励 {_rewardSpell.DisplayName} 已存在，本次不重复加入。", "Run");
            }

            _consumed = true;
            if (_trigger != null)
            {
                _trigger.enabled = false;
            }

            if (_visualRoot != null)
            {
                _visualRoot.SetActive(false);
            }
            else
            {
                gameObject.SetActive(false);
            }
        }
    }
}
