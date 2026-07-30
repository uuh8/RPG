using Game.Combat;
using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// P8 法术 Boss 的 Scene Runtime Owner。
    /// Awake 一次性缓存组件、状态和 Program Runtime 数组；Update 不查找 Scene、不 new、不使用 LINQ。
    /// Entry 后只接受第一次 Target，之后没有 Detect/Lose Radius，因此 Player 拉远也不会脱战。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(HealthComponent))]
    [RequireComponent(typeof(StatusController))]
    [RequireComponent(typeof(SpellCaster))]
    public sealed class WizardBossController : MonoBehaviour
    {
        private const int TeleportOverlapCapacity = 32;
        private const float FailedTeleportRetryDelay = 1f;
        private const float GoldenRatioConjugate = 0.61803399f;

        [SerializeField] private BossDefinition _definition;
        [SerializeField] private Transform _castOrigin;
        [SerializeField] private Vector3 _aimOffset = new Vector3(0f, 1f, 0f);

        private CharacterController _characterController;
        private Animator _animator;
        private HealthComponent _health;
        private StatusController _status;
        private SpellCaster _spellCaster;
        private Collider _casterCollider;

        private BossStateMachine _stateMachine;
        private BossInactiveState _inactiveState;
        private BossApproachState _approachState;
        private BossDecisionState _decisionState;
        private BossCastState _castState;
        private BossPhaseTransitionState _phaseTransitionState;
        private BossTeleportState _teleportState;
        private BossProgramRuntimeState _programRuntime;
        private BossTeleportRuntimeState _teleportRuntime;
        private BossTeleportSampler _teleportSampler;

        private Transform _target;
        private CharacterController _targetCharacterController;
        private StatusController _targetStatus;
        private HealthComponent _targetHealth;
        private bool _isEncounterActive;
        private bool _isActionLocked;
        private BossPhase _currentPhase = BossPhase.Phase1;
        private BossPhase _pendingPhase = BossPhase.Phase1;
        private float _globalCastIntervalRemaining;
        private int _selectedProgramIndex = -1;
        private Vector3 _lockedAimPoint;
        private int _castTriggerHash;
        private int _phaseTransitionTriggerHash;
        private int _teleportFallbackProgramIndex = -1;
        private int _actionDecisionSequence;
        private int _teleportSampleSequence;
        private Vector3 _teleportSourcePosition;
        private Vector3 _teleportDestination;

        private static readonly int SpeedHash =
            Animator.StringToHash("speed");

        public BossDefinition Definition => _definition;
        public HealthComponent Health => _health;
        public Transform Target => _target;
        public bool HasTarget => _target != null;
        public bool IsEncounterActive => _isEncounterActive;
        public bool IsActionLocked => _isActionLocked;
        public BossPhase CurrentPhase => _currentPhase;
        public BossPhase PendingPhase => _pendingPhase;
        public BossRuntimeStateKind CurrentStateKind =>
            _stateMachine != null
                ? _stateMachine.CurrentKind
                : BossRuntimeStateKind.None;
        public Vector3 LockedAimPoint => _lockedAimPoint;
        public int SelectedProgramIndex => _selectedProgramIndex;
        public float GlobalCastIntervalRemaining =>
            _globalCastIntervalRemaining;
        public float DecisionInterval =>
            _definition != null
                ? Mathf.Max(0.05f, _definition.DecisionInterval)
                : 0.2f;
        public float CastTelegraphDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.CastTelegraphDuration)
                : 0f;
        public float CastRecoveryDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.CastRecoveryDuration)
                : 0f;
        public float PhaseTransitionDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.PhaseTransitionDuration)
                : 0f;
        public float TeleportTelegraphDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.TeleportTelegraphDuration)
                : 0f;
        public float TeleportRecoveryDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.TeleportRecoveryDuration)
                : 0f;
        public float TeleportCooldownRemaining =>
            _teleportRuntime != null
                ? _teleportRuntime.CooldownRemaining
                : 0f;
        internal int TeleportFallbackProgramIndex =>
            _teleportFallbackProgramIndex;

        public bool IsWithinStopDistance =>
            HasTarget &&
            HorizontalDistanceToTarget <=
            (_definition != null
                ? Mathf.Max(0f, _definition.StopDistance)
                : 0f);

        private float HorizontalDistanceToTarget
        {
            get
            {
                if (_target == null)
                {
                    return float.PositiveInfinity;
                }

                Vector3 delta = _target.position - transform.position;
                delta.y = 0f;
                return delta.magnitude;
            }
        }

        private void Awake()
        {
            _characterController = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>();
            _health = GetComponent<HealthComponent>();
            _status = GetComponent<StatusController>();
            _spellCaster = GetComponent<SpellCaster>();
            _casterCollider = GetComponent<Collider>();
            if (_castOrigin == null)
            {
                _castOrigin = transform;
            }

            _stateMachine = new BossStateMachine();
            _inactiveState = new BossInactiveState(this);
            _approachState = new BossApproachState(this);
            _decisionState = new BossDecisionState(this);
            _castState = new BossCastState(this);
            _phaseTransitionState =
                new BossPhaseTransitionState(this);
            _teleportState = new BossTeleportState(this);

            int programCount =
                _definition != null &&
                _definition.Programs != null
                    ? _definition.Programs.Length
                    : 0;
            int historyCapacity =
                _definition != null
                    ? Mathf.Max(0, _definition.RecentHistoryCapacity)
                    : 0;
            _programRuntime = new BossProgramRuntimeState(
                programCount,
                historyCapacity);
            _teleportRuntime = new BossTeleportRuntimeState();

            int candidateCount =
                _definition != null
                    ? Mathf.Max(1, _definition.TeleportCandidateCount)
                    : 1;
            var candidateBuffer = new Vector3[candidateCount];
            var overlapBuffer =
                new Collider[TeleportOverlapCapacity];
            _teleportSampler = new BossTeleportSampler(
                new UnityBossTeleportWorldQuery(overlapBuffer),
                candidateBuffer);

            _castTriggerHash = HashOptionalTrigger(
                _definition != null
                    ? _definition.CastAnimatorTrigger
                    : null);
            _phaseTransitionTriggerHash = HashOptionalTrigger(
                _definition != null
                    ? _definition.PhaseTransitionAnimatorTrigger
                    : null);
            _stateMachine.ChangeState(_inactiveState);
        }

        private void OnEnable()
        {
            EventBus<BossEncounterStartedEvent>.Subscribe(
                OnBossEncounterStarted);
            if (_stateMachine != null &&
                _stateMachine.CurrentKind == BossRuntimeStateKind.None)
            {
                RouteAfterAction();
            }
        }

        private void OnDisable()
        {
            EventBus<BossEncounterStartedEvent>.Unsubscribe(
                OnBossEncounterStarted);
            _stateMachine?.ChangeState(null);
            _health?.SetInvulnerable(false);
            _isActionLocked = false;
        }

        private void Update()
        {
            if (Time.timeScale == 0f ||
                !_isEncounterActive ||
                _health == null ||
                !_health.IsAlive)
            {
                return;
            }

            float deltaTime = Time.deltaTime;
            _programRuntime.TickCooldowns(deltaTime);
            _teleportRuntime.Tick(deltaTime);
            _globalCastIntervalRemaining = Mathf.Max(
                0f,
                _globalCastIntervalRemaining - deltaTime);
            RefreshPendingPhase();

            if (_pendingPhase > _currentPhase &&
                !_isActionLocked &&
                CurrentStateKind !=
                BossRuntimeStateKind.PhaseTransition)
            {
                _stateMachine.ChangeState(_phaseTransitionState);
                return;
            }

            _stateMachine.Tick(deltaTime);
            SyncAnimator();
        }

        /// <summary>
        /// 第一次有效调用永久锁定 Target；之后拒绝 Retarget。
        /// Event 入口和直接 Scene Bootstrap 共用该方法，因此它不是测试专用 API。
        /// </summary>
        public bool TryActivate(Transform target)
        {
            if (_isEncounterActive ||
                target == null ||
                _definition == null)
            {
                return false;
            }

            _target = target;
            _targetCharacterController =
                target.GetComponent<CharacterController>();
            _targetStatus = target.GetComponent<StatusController>();
            _targetHealth = target.GetComponent<HealthComponent>();
            _isEncounterActive = true;

            _currentPhase = BossPhaseResolver.Resolve(
                BossPhase.Phase1,
                HealthRatio,
                Phase2Threshold,
                Phase3Threshold);
            _pendingPhase = _currentPhase;
            PublishHudInitialized();
            PublishPhaseChanged(_currentPhase, _currentPhase);
            RouteAfterAction();
            return true;
        }

        internal void EnterInactive()
        {
            _stateMachine.ChangeState(_inactiveState);
        }

        internal void EnterApproach()
        {
            _stateMachine.ChangeState(_approachState);
        }

        internal void EnterDecision()
        {
            _stateMachine.ChangeState(_decisionState);
        }

        internal void EnterCast(int programIndex)
        {
            if (!IsValidProgram(programIndex) ||
                _isActionLocked)
            {
                return;
            }

            _selectedProgramIndex = programIndex;
            _lockedAimPoint = _target != null
                ? _target.position + _aimOffset
                : transform.position + transform.forward;
            _stateMachine.ChangeState(_castState);
        }

        internal void EnterTeleport(int fallbackProgramIndex)
        {
            if (_isActionLocked)
            {
                return;
            }

            _teleportFallbackProgramIndex = fallbackProgramIndex;
            _stateMachine.ChangeState(_teleportState);
        }

        internal bool TryAcquireActionLock()
        {
            if (_isActionLocked)
            {
                return false;
            }

            _isActionLocked = true;
            return true;
        }

        internal void ReleaseActionLock()
        {
            _isActionLocked = false;
        }

        internal void MoveTowardTarget(float deltaTime)
        {
            if (_target == null ||
                _definition == null ||
                _characterController == null ||
                !_characterController.enabled)
            {
                return;
            }

            Vector3 toTarget =
                _target.position - transform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            float stopDistance =
                Mathf.Max(0f, _definition.StopDistance);
            if (distance <= stopDistance ||
                distance <= 1e-5f)
            {
                return;
            }

            Vector3 direction = toTarget / distance;
            float statusMultiplier =
                _status != null ? _status.MoveSpeedMultiplier : 1f;
            float maxStep =
                Mathf.Max(0f, _definition.MoveSpeed) *
                Mathf.Max(0f, statusMultiplier) *
                Mathf.Max(0f, deltaTime);
            float step = Mathf.Min(
                maxStep,
                distance - stopDistance);
            _characterController.Move(direction * step);
            FacePoint(_target.position, deltaTime);
        }

        internal void FaceTarget(float deltaTime)
        {
            if (_target != null)
            {
                FacePoint(_target.position, deltaTime);
            }
        }

        internal void FacePoint(Vector3 point, float deltaTime)
        {
            if (_definition == null)
            {
                return;
            }

            Vector3 direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 1e-6f)
            {
                return;
            }

            Quaternion targetRotation =
                Quaternion.LookRotation(direction.normalized);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                Mathf.Max(0f, _definition.TurnSpeedDegrees) *
                Mathf.Max(0f, deltaTime));
        }

        internal int SelectProgram(out float selectedScore)
        {
            if (_definition == null)
            {
                selectedScore = float.NegativeInfinity;
                return -1;
            }

            var context = new BossDecisionContext(
                _currentPhase,
                HorizontalDistanceToTarget,
                _targetStatus != null
                    ? _targetStatus.GetIntensity(StatusKind.Wet)
                    : 0f,
                _targetCharacterController != null
                    ? _targetCharacterController.velocity.magnitude
                    : 0f,
                HealthRatio,
                TargetHealthRatio);
            return BossSpellUtilityEvaluator.SelectProgram(
                _definition.Programs,
                _programRuntime,
                in context,
                out selectedScore);
        }

        internal bool ShouldTeleport(float selectedSpellScore)
        {
            if (_definition == null || _teleportRuntime == null)
            {
                return false;
            }

            BossPhaseDefinition phase =
                _definition.GetPhase(_currentPhase);
            if (phase == null ||
                phase.TeleportWeight <= 0f ||
                _teleportRuntime.CooldownRemaining > 0f)
            {
                return false;
            }

            _actionDecisionSequence++;
            float roll = Mathf.Repeat(
                _actionDecisionSequence * GoldenRatioConjugate,
                1f);
            return BossActionUtilityEvaluator.ShouldTeleport(
                phase.TeleportWeight,
                _teleportRuntime.CooldownRemaining,
                selectedSpellScore,
                roll);
        }

        internal bool TryPrepareTeleport()
        {
            if (_definition == null ||
                _target == null ||
                _characterController == null ||
                _teleportSampler == null)
            {
                return false;
            }

            _teleportSampleSequence++;
            float angleOffset = Mathf.Repeat(
                _teleportSampleSequence * 137.50777f,
                360f);
            var request = new BossTeleportSamplingRequest(
                _target.position,
                _definition.TeleportMinRadius,
                _definition.TeleportMaxRadius,
                angleOffset,
                _definition.NavMeshSampleDistance,
                _definition.MaxGroundSlopeDegrees,
                _definition.TeleportNavMeshAreaMask,
                _definition.TeleportGroundMask.value,
                _definition.TeleportBlockingMask.value);
            var capsuleShape = new BossTeleportCapsuleShape(
                _characterController.center,
                _characterController.radius,
                _characterController.height,
                _characterController.skinWidth,
                transform.lossyScale);

            _teleportSourcePosition = transform.position;
            return _teleportSampler.TryFindDestination(
                in request,
                capsuleShape,
                transform,
                out _teleportDestination);
        }

        internal void PublishTeleportTelegraph()
        {
            PublishTeleportEvent(BossTeleportStage.Telegraph);
        }

        internal void ExecutePreparedTeleport()
        {
            PublishTeleportEvent(BossTeleportStage.Departed);

            bool wasEnabled =
                _characterController != null &&
                _characterController.enabled;
            if (wasEnabled)
            {
                _characterController.enabled = false;
            }

            transform.position = _teleportDestination;

            if (wasEnabled)
            {
                _characterController.enabled = true;
            }

            PublishTeleportEvent(BossTeleportStage.Arrived);
        }

        internal void CompleteTeleport()
        {
            BossPhaseDefinition phase =
                _definition != null
                    ? _definition.GetPhase(_currentPhase)
                    : null;
            _teleportRuntime.MarkUsed(
                phase != null ? phase.TeleportCooldown : 0f);
            _globalCastIntervalRemaining =
                phase != null ? Mathf.Max(0f, phase.CastInterval) : 0f;
            _teleportFallbackProgramIndex = -1;
            RouteAfterAction();
        }

        internal void CancelTeleport(int fallbackProgramIndex)
        {
            BossPhaseDefinition phase =
                _definition != null
                    ? _definition.GetPhase(_currentPhase)
                    : null;
            float retryDelay = phase != null
                ? Mathf.Min(
                    FailedTeleportRetryDelay,
                    Mathf.Max(0f, phase.TeleportCooldown))
                : FailedTeleportRetryDelay;
            _teleportRuntime.MarkUsed(retryDelay);
            PublishTeleportEvent(BossTeleportStage.Cancelled);
            _teleportFallbackProgramIndex = -1;

            if (IsValidProgram(fallbackProgramIndex))
            {
                EnterCastAfterTeleportCancellation(
                    fallbackProgramIndex);
                return;
            }

            _stateMachine.ChangeState(_decisionState);
        }

        private void EnterCastAfterTeleportCancellation(int programIndex)
        {
            // 先离开 Teleport State，让 Exit 释放 Action Lock，再进入 Cast 重新取得所有权。
            _selectedProgramIndex = programIndex;
            _lockedAimPoint = _target != null
                ? _target.position + _aimOffset
                : transform.position + transform.forward;
            _stateMachine.ChangeState(_castState);
        }

        internal void ReleaseSelectedProgram()
        {
            if (!IsValidProgram(_selectedProgramIndex))
            {
                return;
            }

            BossSpellProgramDefinition program =
                _definition.Programs[_selectedProgramIndex];
            if (program == null || program.Wand == null)
            {
                GameLog.Warn(
                    $"Boss Program Index {_selectedProgramIndex} 缺少 Wand。",
                    "Boss");
                return;
            }

            _spellCaster.CastProgram(
                program.Wand,
                _castOrigin.position,
                _lockedAimPoint,
                _health.TeamId,
                _health.Id,
                _casterCollider,
                SpellManaPolicy.IgnoreMana);
        }

        internal void CompleteCast(int programIndex)
        {
            if (IsValidProgram(programIndex))
            {
                BossSpellProgramDefinition program =
                    _definition.Programs[programIndex];
                _programRuntime.MarkUsed(
                    programIndex,
                    program != null ? program.Cooldown : 0f);
            }

            BossPhaseDefinition phase =
                _definition.GetPhase(_currentPhase);
            _globalCastIntervalRemaining =
                phase != null ? Mathf.Max(0f, phase.CastInterval) : 0f;
            _selectedProgramIndex = -1;
            RouteAfterAction();
        }

        internal void CompletePhaseTransition()
        {
            BossPhase previous = _currentPhase;
            _currentPhase = _pendingPhase;
            PublishPhaseChanged(previous, _currentPhase);
            RouteAfterAction();
        }

        internal void TriggerCastAnimation()
        {
            if (_animator != null && _castTriggerHash != 0)
            {
                _animator.SetTrigger(_castTriggerHash);
            }
        }

        internal void TriggerPhaseTransitionAnimation()
        {
            if (_animator != null &&
                _phaseTransitionTriggerHash != 0)
            {
                _animator.SetTrigger(
                    _phaseTransitionTriggerHash);
            }
        }

        private void SyncAnimator()
        {
            if (_animator == null ||
                _characterController == null)
            {
                return;
            }

            // Boss 只有 Approach 会主动位移。先按状态归零，可以避免停止
            // 调用 Move 后 CharacterController 仍保留上一笔 velocity 时卡在 Run。
            if (CurrentStateKind != BossRuntimeStateKind.Approach)
            {
                _animator.SetFloat(SpeedHash, 0f);
                return;
            }

            // CharacterController.velocity 是 Move 产生的实际速度；只取水平
            // 分量，避免未来加入重力后把下落速度误判为 Run。
            Vector3 velocity = _characterController.velocity;
            velocity.y = 0f;
            _animator.SetFloat(SpeedHash, velocity.magnitude);
        }

        private float HealthRatio =>
            _health != null && _health.MaxHp > 0f
                ? Mathf.Clamp01(_health.CurrentHp / _health.MaxHp)
                : 0f;

        private float TargetHealthRatio =>
            _targetHealth != null && _targetHealth.MaxHp > 0f
                ? Mathf.Clamp01(
                    _targetHealth.CurrentHp /
                    _targetHealth.MaxHp)
                : 0f;

        private float Phase2Threshold =>
            _definition != null && _definition.Phase2 != null
                ? _definition.Phase2.EnterAtOrBelowHealthRatio
                : 0.7f;

        private float Phase3Threshold =>
            _definition != null && _definition.Phase3 != null
                ? _definition.Phase3.EnterAtOrBelowHealthRatio
                : 0.35f;

        private void RefreshPendingPhase()
        {
            BossPhase resolved = BossPhaseResolver.Resolve(
                _currentPhase,
                HealthRatio,
                Phase2Threshold,
                Phase3Threshold);
            if (resolved > _pendingPhase)
            {
                _pendingPhase = resolved;
            }
        }

        private void RouteAfterAction()
        {
            if (_stateMachine == null)
            {
                return;
            }

            if (!_isEncounterActive || _target == null)
            {
                _stateMachine.ChangeState(_inactiveState);
                return;
            }

            if (_pendingPhase > _currentPhase)
            {
                _stateMachine.ChangeState(_phaseTransitionState);
                return;
            }

            _stateMachine.ChangeState(
                IsWithinStopDistance
                    ? _decisionState
                    : _approachState);
        }

        private bool IsValidProgram(int programIndex)
        {
            return _definition != null &&
                   _definition.Programs != null &&
                   programIndex >= 0 &&
                   programIndex < _definition.Programs.Length;
        }

        private void OnBossEncounterStarted(
            BossEncounterStartedEvent startedEvent)
        {
            if (_health == null ||
                startedEvent.BossId != _health.Id)
            {
                return;
            }

            PlayerControllerBase player =
                PlayerControllerBase.Current;
            if (player == null ||
                player.gameObject.GetInstanceID() !=
                startedEvent.PlayerId)
            {
                GameLog.Warn(
                    "Boss Encounter 已启动，但没有找到匹配的当前 Player。",
                    "Boss");
                return;
            }

            TryActivate(player.transform);
        }

        private void PublishPhaseChanged(
            BossPhase previous,
            BossPhase current)
        {
            EventBus<BossPhaseChangedEvent>.Publish(
                new BossPhaseChangedEvent
                {
                    BossId = _health != null ? _health.Id : 0,
                    PreviousPhase = (byte)previous,
                    CurrentPhase = (byte)current,
                    HealthRatio = HealthRatio,
                });
        }

        private void PublishHudInitialized()
        {
            if (_health == null || _definition == null)
            {
                return;
            }

            // 这里只发布 Authoring 数据与当前 HP 的值快照，不把 BossDefinition
            // 或 MonoBehaviour 引用交给 UI，避免跨 Assembly 的可变对象所有权泄漏。
            EventBus<BossHudInitializedEvent>.Publish(
                new BossHudInitializedEvent
                {
                    BossId = _health.Id,
                    DisplayName = _definition.DisplayName,
                    CurrentHp = _health.CurrentHp,
                    MaxHp = _health.MaxHp,
                    CurrentPhase = (byte)_currentPhase,
                    Phase2Threshold = Phase2Threshold,
                    Phase3Threshold = Phase3Threshold,
                });
        }

        private void PublishTeleportEvent(BossTeleportStage stage)
        {
            EventBus<BossTeleportEvent>.Publish(
                new BossTeleportEvent
                {
                    BossId = _health != null ? _health.Id : 0,
                    Stage = stage,
                    SourcePosition = _teleportSourcePosition,
                    DestinationPosition = _teleportDestination,
                });
        }

        private static int HashOptionalTrigger(string triggerName)
        {
            return string.IsNullOrWhiteSpace(triggerName)
                ? 0
                : Animator.StringToHash(triggerName);
        }
    }
}
