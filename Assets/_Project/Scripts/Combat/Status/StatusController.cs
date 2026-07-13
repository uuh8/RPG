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
        [SerializeField] private ElementReactionProfile _reactionProfile;

        private readonly StatusInstance[] _instances = new StatusInstance[StatusCount];
        private readonly StatusDefinition[] _definitions = new StatusDefinition[StatusCount];
        private readonly GameObject[] _vfxInstances = new GameObject[StatusCount];
        private readonly int[] _lastPublishedPercent = new int[StatusCount];
        private readonly bool[] _lastPublishedActive = new bool[StatusCount];
        private IDamageable _damageable;
        private int _targetId;
        private ElementReactionRuntime _reactionRuntime;
        private AreaReactionDamageResolver _areaReactionDamageResolver;

        public float MoveSpeedMultiplier { get; private set; } = 1f;

        private void Awake()
        {
            _damageable = GetComponent<IDamageable>();
            _targetId = gameObject.GetInstanceID();
            CacheDefinitions();
            _areaReactionDamageResolver = new AreaReactionDamageResolver();
            RebuildReactionRuntime();
            RecalculateMoveSpeedMultiplier();
        }

        private void OnEnable()
        {
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
            CancelReactions();
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
            if (_reactionRuntime != null)
            {
                _reactionRuntime.MarkDirty(new StatusSource(sourceId, sourceTeam));
                ElementStateSnapshot snapshot = BuildReactionSnapshot();
                ElementReactionFrame startFrame = _reactionRuntime.Tick(in snapshot, 0f);
                ApplyReactionFrame(in startFrame);
            }
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
            RebuildReactionRuntime();
        }

        public void SetReactionProfileForTests(ElementReactionProfile profile)
        {
            // EditMode 中 AddComponent 后不保证已经走过 PlayMode 的 Awake；测试入口补齐同一身份缓存。
            if (_targetId == 0)
                _targetId = gameObject.GetInstanceID();
            _reactionProfile = profile;
            RebuildReactionRuntime();
        }

        private void Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
                return;

            StatusMask suppressed = StatusMask.None;
            if (_reactionRuntime != null)
            {
                ElementStateSnapshot snapshot = BuildReactionSnapshot();
                ElementReactionFrame reactionFrame = _reactionRuntime.Tick(in snapshot, deltaTime);
                suppressed = reactionFrame.SuppressNaturalDecay;
                ApplyReactionFrame(in reactionFrame);
            }

            ApplyNaturalDecay(deltaTime, suppressed);
            TickDamageOverTime(deltaTime);
            FinalizeStatusesAndPublish();
            RecalculateMoveSpeedMultiplier();
        }

        private void ApplyNaturalDecay(float deltaTime, StatusMask suppressed)
        {
            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                if (!instance.Active || IsSuppressed((StatusKind)i, suppressed))
                    continue;

                StatusKind kind = (StatusKind)i;
                StatusDefinition definition = GetDefinition(kind);
                float decay = definition != null ? definition.NaturalDecayPerSecond : 0f;
                instance.Intensity = Mathf.Max(0f, instance.Intensity - decay * deltaTime);
                _instances[i] = instance;
            }
        }

        private void TickDamageOverTime(float deltaTime)
        {
            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                if (!instance.Active)
                    continue;

                StatusKind kind = (StatusKind)i;
                StatusDefinition definition = GetDefinition(kind);
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

                _instances[i] = instance;
            }
        }

        private void FinalizeStatusesAndPublish()
        {
            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                StatusKind kind = (StatusKind)i;
                if (instance.Intensity <= 0f)
                {
                    instance.Active = false;
                    instance.TickTimer = 0f;
                    DestroyVfx(kind);
                }

                _instances[i] = instance;
                PublishStatusChangedIfNeeded(kind);
            }
        }

        private void ApplyReactionFrame(in ElementReactionFrame frame)
        {
            AddIntensityDelta(StatusKind.Burning, frame.FireDelta);
            AddIntensityDelta(StatusKind.Wet, frame.WaterDelta);
            AddIntensityDelta(StatusKind.Poisoned, frame.PoisonDelta);
            AddIntensityDelta(StatusKind.Sticky, frame.GooDelta);

            if (frame.SignalCount > 0)
                PublishReactionSignal(in frame.Signal0);
            if (frame.SignalCount > 1)
                PublishReactionSignal(in frame.Signal1);
            if (frame.SignalCount > 2)
                PublishReactionSignal(in frame.Signal2);

            if (frame.HasAreaDamage)
            {
                // 正常由 Awake 预创建；EditMode 测试/工具若先调用公开 API，再做一次低频兜底。
                if (_areaReactionDamageResolver == null)
                    _areaReactionDamageResolver = new AreaReactionDamageResolver();
                _areaReactionDamageResolver.Resolve(in frame.AreaDamage, transform.position);
            }
        }

        private ElementStateSnapshot BuildReactionSnapshot()
        {
            StatusInstance fire = _instances[ToIndex(StatusKind.Burning)];
            StatusInstance water = _instances[ToIndex(StatusKind.Wet)];
            StatusInstance poison = _instances[ToIndex(StatusKind.Poisoned)];
            StatusInstance goo = _instances[ToIndex(StatusKind.Sticky)];
            return new ElementStateSnapshot(
                fire.Intensity, water.Intensity, poison.Intensity, goo.Intensity,
                ToSource(in fire), ToSource(in water), ToSource(in poison), ToSource(in goo));
        }

        private void RebuildReactionRuntime()
        {
            CancelReactions();
            if (_reactionProfile == null)
            {
                _reactionRuntime = null;
                return;
            }

            float poisonMultiplier = GetWetCleanseMultiplier(StatusKind.Poisoned);
            float gooMultiplier = GetWetCleanseMultiplier(StatusKind.Sticky);
            ElementReactionTuningSnapshot tuning = _reactionProfile.CreateSnapshot(poisonMultiplier, gooMultiplier);
            _reactionRuntime = new ElementReactionRuntime(in tuning);
        }

        private void CancelReactions()
        {
            if (_reactionRuntime == null)
                return;

            ElementReactionFrame frame = _reactionRuntime.CancelAll();
            ApplyReactionFrame(in frame);
        }

        private void OnDeath(DeathEvent e)
        {
            if (e.TargetId == _targetId)
                CancelReactions();
        }

        private void AddIntensityDelta(StatusKind kind, float delta)
        {
            if (Mathf.Abs(delta) <= 0f)
                return;

            int index = ToIndex(kind);
            StatusInstance instance = _instances[index];
            instance.Intensity = Mathf.Clamp(instance.Intensity + delta, 0f, 100f);
            instance.Active = instance.Intensity > 0f;
            _instances[index] = instance;
        }

        private float GetWetCleanseMultiplier(StatusKind kind)
        {
            StatusDefinition definition = GetDefinition(kind);
            return definition != null && definition.WetCleanseable
                ? Mathf.Max(0f, definition.WetCleanseMultiplier)
                : 0f;
        }

        private void PublishReactionSignal(in ReactionSignal signal)
        {
            // 正常 PlayMode 由 Awake 缓存；EditMode 工具/测试可能在 Awake 前直接调用公开 API。
            if (_targetId == 0)
                _targetId = gameObject.GetInstanceID();

            EventBus<ElementReactionEvent>.Publish(new ElementReactionEvent
            {
                TargetId = _targetId,
                Reaction = signal.Reaction,
                Phase = signal.Phase,
                NormalizedStrength = signal.NormalizedStrength,
                ExpectedDuration = signal.ExpectedDuration,
            });
        }

        private static StatusSource ToSource(in StatusInstance instance)
        {
            return instance.Active
                ? new StatusSource(instance.SourceId, instance.SourceTeam)
                : default;
        }

        private static bool IsSuppressed(StatusKind kind, StatusMask mask)
        {
            StatusMask bit = (StatusMask)(1 << (int)kind);
            return (mask & bit) != 0;
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
