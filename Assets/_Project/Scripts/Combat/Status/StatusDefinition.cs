using UnityEngine;

namespace Game.Combat
{
    [CreateAssetMenu(menuName = "Game/Combat/Status Definition", fileName = "StatusDefinition")]
    public class StatusDefinition : ScriptableObject
    {
        [Header("Common")]
        public StatusKind Kind;
        public string DisplayName = "";
        public Sprite Icon;
        [Min(0f)] public float NaturalDecayPerSecond = 8f;
        public GameObject VfxPrefab;

        [Header("Damage Over Time")]
        public bool DealsDamage;
        [Min(0.05f)] public float DamageInterval = 1f;
        [Min(0f)] public float BaseDamagePerTick = 0f;
        public DamageType DamageType = DamageType.Magical;
        public bool TriggerHitReaction = false;

        [Header("Wet Cleanse")]
        [Tooltip("启用后，该状态会消耗 Wet Cleanse 的有限预算；Burning/Wet 应保持关闭。")]
        public bool WetCleanseable;
        [Min(0f)] public float WetCleanseMultiplier = 1f;

        [Header("Movement Modifier")]
        public bool AffectsMoveSpeed;
        [Range(0f, 0.95f)] public float MaxMoveSpeedSlowRatio = 0f;
    }
}
