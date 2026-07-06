using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    [DisallowMultipleComponent]
    public class StatusController : MonoBehaviour
    {
        private const int StatusCount = 4;
        private static readonly string[] DefaultDisplayNames = { "Burning", "Wet", "Poisoned", "Sticky" };

        [SerializeField] private StatusDatabase _database;
        [SerializeField] private float _wetExtinguishBurningPerSecond = 35f;

        private readonly StatusInstance[] _instances = new StatusInstance[StatusCount];
        private readonly StatusDefinition[] _definitions = new StatusDefinition[StatusCount];
        private readonly GameObject[] _vfxInstances = new GameObject[StatusCount];
        private readonly int[] _lastPublishedPercent = new int[StatusCount];
        private readonly bool[] _lastPublishedActive = new bool[StatusCount];
        private IDamageable _damageable;
        private int _targetId;

        public float MoveSpeedMultiplier { get; private set; } = 1f;

        private void Awake()
        {
            _damageable = GetComponent<IDamageable>();
            _targetId = gameObject.GetInstanceID();
            CacheDefinitions();
            RecalculateMoveSpeedMultiplier();
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        public void ApplyStatus(StatusKind kind, float amount, int sourceId, byte sourceTeam)
        {
            if (amount <= 0f)
                return;

            int index = ToIndex(kind);
            StatusInstance instance = _instances[index];
            instance.Active = true;
            instance.Intensity = Mathf.Clamp(instance.Intensity + amount, 0f, 100f);
            instance.SourceId = sourceId;
            instance.SourceTeam = sourceTeam;

            StatusDefinition definition = GetDefinition(kind);
            if (definition != null && definition.DealsDamage && instance.TickTimer <= 0f)
                instance.TickTimer = Mathf.Max(0.05f, definition.DamageInterval);

            _instances[index] = instance;
            EnsureVfx(kind);
            RecalculateMoveSpeedMultiplier();
            PublishStatusChanged(kind);
        }

        public bool HasStatus(StatusKind kind)
        {
            return _instances[ToIndex(kind)].Active;
        }

        public float GetIntensity(StatusKind kind)
        {
            return _instances[ToIndex(kind)].Intensity;
        }

        public bool TryGetIntensity(StatusKind kind, out float intensity)
        {
            StatusInstance instance = _instances[ToIndex(kind)];
            intensity = instance.Intensity;
            return instance.Active;
        }

        public void TickForTests(float deltaTime)
        {
            Tick(deltaTime);
        }

        public void SetDefinitionsForTests(params StatusDefinition[] definitions)
        {
            for (int i = 0; i < _definitions.Length; i++)
                _definitions[i] = null;

            if (definitions == null)
                return;

            for (int i = 0; i < definitions.Length; i++)
            {
                StatusDefinition definition = definitions[i];
                if (definition != null)
                    _definitions[ToIndex(definition.Kind)] = definition;
            }

            RecalculateMoveSpeedMultiplier();
        }

        private void Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
                return;

            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                if (!instance.Active)
                    continue;

                StatusKind kind = (StatusKind)i;
                StatusDefinition definition = GetDefinition(kind);
                float decay = definition != null ? definition.NaturalDecayPerSecond : 0f;
                if (kind == StatusKind.Burning && HasStatus(StatusKind.Wet))
                    decay += _wetExtinguishBurningPerSecond;

                instance.Intensity = Mathf.Max(0f, instance.Intensity - decay * deltaTime);

                if (definition != null && definition.DealsDamage && _damageable != null && _damageable.IsAlive)
                {
                    instance.TickTimer -= deltaTime;
                    if (instance.TickTimer <= 0f)
                    {
                        instance.TickTimer += Mathf.Max(0.05f, definition.DamageInterval);
                        float amount = definition.BaseDamagePerTick * (instance.Intensity / 100f);
                        if (amount > 0f)
                        {
                            var request = new DamageRequest(
                                instance.SourceId,
                                instance.SourceTeam,
                                amount,
                                definition.DamageType,
                                transform.position,
                                Vector3.up,
                                definition.TriggerHitReaction);
                            _damageable.ReceiveHit(in request);
                        }
                    }
                }

                if (instance.Intensity <= 0f)
                {
                    instance.Active = false;
                    instance.TickTimer = 0f;
                    DestroyVfx(kind);
                }

                _instances[i] = instance;
                PublishStatusChangedIfNeeded(kind);
            }

            RecalculateMoveSpeedMultiplier();
        }

        private void CacheDefinitions()
        {
            for (int i = 0; i < StatusCount; i++)
            {
                StatusKind kind = (StatusKind)i;
                if (_database != null && _database.TryGet(kind, out StatusDefinition definition))
                    _definitions[i] = definition;
            }
        }

        private StatusDefinition GetDefinition(StatusKind kind)
        {
            return _definitions[ToIndex(kind)];
        }

        private void EnsureVfx(StatusKind kind)
        {
            int index = ToIndex(kind);
            if (_vfxInstances[index] != null)
                return;

            StatusDefinition definition = _definitions[index];
            if (definition == null || definition.VfxPrefab == null)
                return;

            _vfxInstances[index] = Instantiate(definition.VfxPrefab, transform.position, transform.rotation, transform);
        }

        private void DestroyVfx(StatusKind kind)
        {
            int index = ToIndex(kind);
            if (_vfxInstances[index] == null)
                return;

            Destroy(_vfxInstances[index]);
            _vfxInstances[index] = null;
        }

        private void RecalculateMoveSpeedMultiplier()
        {
            float multiplier = 1f;

            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                if (!instance.Active)
                    continue;

                StatusDefinition definition = _definitions[i];
                if (definition == null || !definition.AffectsMoveSpeed)
                    continue;

                float slowRatio = definition.MaxMoveSpeedSlowRatio * (instance.Intensity / 100f);
                multiplier *= Mathf.Clamp01(1f - slowRatio);
            }

            MoveSpeedMultiplier = multiplier;
        }

        private void PublishStatusChanged(StatusKind kind)
        {
            int index = ToIndex(kind);
            StatusDefinition definition = GetDefinition(kind);
            StatusInstance instance = _instances[index];
            int roundedPercent = Mathf.RoundToInt(instance.Intensity);
            _lastPublishedPercent[index] = roundedPercent;
            _lastPublishedActive[index] = instance.Active;

            EventBus<StatusChangedEvent>.Publish(new StatusChangedEvent
            {
                TargetId = _targetId,
                Kind = kind,
                Intensity = instance.Intensity,
                IsActive = instance.Active,
                Icon = definition != null ? definition.Icon : null,
                DisplayName = definition != null && !string.IsNullOrEmpty(definition.DisplayName)
                    ? definition.DisplayName
                    : DefaultDisplayNames[index],
            });
        }

        private void PublishStatusChangedIfNeeded(StatusKind kind)
        {
            int index = ToIndex(kind);
            StatusInstance instance = _instances[index];
            int roundedPercent = Mathf.RoundToInt(instance.Intensity);
            if (_lastPublishedActive[index] == instance.Active && _lastPublishedPercent[index] == roundedPercent)
                return;

            PublishStatusChanged(kind);
        }

        private static int ToIndex(StatusKind kind)
        {
            return (int)kind;
        }
    }
}
