using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师陨石重击状态：长按进入引导（原地定身，脚下光圈 + 地面落点圆框，鼠标经相机改变落点准心）；
    /// 松手进入施法（前摇后从落点斜上方天空生成陨石 NovaFireball，直线砸向落点）。数据来自 MeteorAttackDefinition。
    /// 引导/施法动画可选（状态名非空才 CrossFade）；施法节奏用计时器驱动，不依赖动画 normalizedTime。
    /// 平行于 PlayerChargeAttackState，但落点是地面（屏幕中心打地面）、且引导期间完全定身。
    /// </summary>
    public class PlayerWizardHeavyState : PlayerStateBase
    {
        private const int MaxAimHits = 16; // 落点射线一次最多记录命中数（预分配，零 GC）

        private readonly WizardController _wizard;
        private readonly RaycastHit[] _aimHits = new RaycastHit[MaxAimHits];

        private bool _released;            // 是否已松手进入施法阶段
        private bool _meteorSpawned;       // 施法阶段是否已生成过陨石（单点去重）
        private float _releaseTimer;       // 松手后计时（驱动生成与结束）
        private Vector3 _aimPoint;         // 当前(引导)/锁定(施法) 的落点
        private GameObject _ring;          // 脚下光圈实例
        private GameObject _indicator;     // 落点圆框实例

        public PlayerWizardHeavyState(WizardController player) : base(player)
        {
            _wizard = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _released = false;
            _meteorSpawned = false;
            _releaseTimer = 0f;
            _player.AttackBufferCounter = 0f;

            if (_wizard.MeteorData == null)
            {
                GameLog.Warn("法师 MeteorAttackDefinition 未配置，无法施放陨石", "Combat");
                TransitionToMovement();
                return;
            }

            _aimPoint = ComputeAimPoint();

            // 脚下光圈（跟随玩家，作为子物体）
            if (_wizard.ChannelRingPrefab != null)
                _ring = Object.Instantiate(_wizard.ChannelRingPrefab,
                    _player.transform.position, Quaternion.identity, _player.transform);
            // 落点圆框（世界空间，引导期每帧移动；旋转用 SO 配置把竖直环形平铺到地面）
            if (_wizard.TargetIndicatorPrefab != null)
                _indicator = Object.Instantiate(_wizard.TargetIndicatorPrefab, _aimPoint,
                    Quaternion.Euler(_wizard.MeteorData.IndicatorRotationEuler));

            // 引导动画（可空）
            if (_wizard.MeteorChannelHash != 0)
                _player.Animator.CrossFadeInFixedTime(_wizard.MeteorChannelHash, _wizard.MeteorData.CrossFadeDuration, 0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleRooted(); // 原地定身：只贴地，不水平移动

            if (!_released)
            {
                // 引导：每帧更新落点 + 移动圆框 + 转向落点；松手 → 施法
                _aimPoint = ComputeAimPoint();
                if (_indicator != null) _indicator.transform.position = _aimPoint;
                FaceTarget(_aimPoint);

                if (!_player.IsAttackHeld)
                    BeginRelease();
                return;
            }

            // 施法阶段：计时驱动（不依赖动画进度）
            _releaseTimer += Time.deltaTime;
            MeteorAttackDefinition data = _wizard.MeteorData;
            if (!_meteorSpawned && _releaseTimer >= data.MeteorSpawnDelay)
            {
                SpawnMeteor();
                _meteorSpawned = true;
            }
            if (_releaseTimer >= data.ReleaseDuration)
                TransitionToMovement();
        }

        public override void Exit()
        {
            _player.AttackBufferCounter = 0f;
            // 清理引导期视觉（任何退出路径都清，防泄漏）
            if (_ring != null) { Object.Destroy(_ring); _ring = null; }
            if (_indicator != null) { Object.Destroy(_indicator); _indicator = null; }
        }

        #endregion

        #region 处理流程

        private void BeginRelease()
        {
            _released = true;
            _releaseTimer = 0f;
            // 落点已锁定在 _aimPoint（施法阶段不再更新）；收起引导视觉
            if (_indicator != null) { Object.Destroy(_indicator); _indicator = null; }
            if (_ring != null) { Object.Destroy(_ring); _ring = null; }
            if (_wizard.MeteorReleaseHash != 0)
                _player.Animator.CrossFadeInFixedTime(_wizard.MeteorReleaseHash, _wizard.MeteorData.CrossFadeDuration, 0);
        }

        private void SpawnMeteor()
        {
            if (_wizard.MeteorPrefab == null)
            {
                GameLog.Warn("法师 MeteorPrefab 未配置，无法生成陨石", "Combat");
                return;
            }
            MeteorAttackDefinition data = _wizard.MeteorData;

            // 从落点斜上方天空生成：上方 SpawnHeight，并沿"玩家→落点"反方向后退 SpawnHorizontalBack，制造斜入射
            Vector3 horizDir = _aimPoint - _player.transform.position;
            horizDir.y = 0f;
            if (horizDir.sqrMagnitude < 1e-6f) horizDir = _player.transform.forward;
            horizDir.Normalize();

            Vector3 spawnPos = _aimPoint + Vector3.up * data.SpawnHeight - horizDir * data.SpawnHorizontalBack;
            Vector3 dir = _aimPoint - spawnPos;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.down;
            dir.Normalize();

            GameObject go = Object.Instantiate(_wizard.MeteorPrefab, spawnPos, Quaternion.LookRotation(dir));
            NovaFireball meteor = go.GetComponent<NovaFireball>();
            if (meteor == null)
            {
                GameLog.Warn("MeteorPrefab 上没有 NovaFireball 组件", "Combat");
                return;
            }

            byte team = _wizard.Health != null ? _wizard.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            // 直线飞向落点：关重力
            meteor.Init(team, attackerId, data.Damage, data.Type, dir * data.MeteorSpeed, _player.CharacterController, false);
        }

        /// <summary>
        /// 屏幕中心射线打地面求落点：命中(跳过自身) → 命中点；未命中 → 相机朝向远点投影到玩家脚高。
        /// 再按 AimMaxDistance 做水平距离上限钳制。
        /// </summary>
        private Vector3 ComputeAimPoint()
        {
            MeteorAttackDefinition data = _wizard.MeteorData;
            Camera cam = _player.MainCamera;
            Vector3 point;
            if (cam != null)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                if (!TryRaycastGround(ray, data.AimMaxDistance, out point))
                {
                    point = ray.GetPoint(data.AimMaxDistance);
                    point.y = _player.transform.position.y;
                }
            }
            else
            {
                point = _player.transform.position + _player.transform.forward * data.AimMaxDistance;
            }

            // 水平距离上限钳制（防落点过远）
            Vector3 flat = point - _player.transform.position;
            flat.y = 0f;
            float max = data.AimMaxDistance;
            if (flat.sqrMagnitude > max * max)
            {
                flat = flat.normalized * max;
                point.x = _player.transform.position.x + flat.x;
                point.z = _player.transform.position.z + flat.z;
            }
            return point;
        }

        /// <summary>RaycastNonAlloc 求最近地面命中，跳过自身碰撞体（零 GC）。</summary>
        private bool TryRaycastGround(Ray ray, float maxDistance, out Vector3 point)
        {
            point = default;
            int count = Physics.RaycastNonAlloc(ray, _aimHits, maxDistance,
                                                _wizard.AimMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (_aimHits[i].collider.transform.IsChildOf(_player.transform)) continue; // 跳过自身
                if (_aimHits[i].distance < nearest)
                {
                    nearest = _aimHits[i].distance;
                    point = _aimHits[i].point;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>引导期间把角色平滑转向落点的水平方向（只 yaw）。</summary>
        private void FaceTarget(Vector3 target)
        {
            Vector3 dir = target - _player.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;
            Quaternion rot = Quaternion.LookRotation(dir);
            _player.transform.rotation = Quaternion.Slerp(
                _player.transform.rotation, rot, _player.RotationSpeed * Time.deltaTime);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        /// <summary>原地定身：只施加垂直贴地速度，不响应水平移动输入。</summary>
        private void HandleRooted()
        {
            Vector3 velocity = Vector3.up * _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void TransitionToMovement()
        {
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}
