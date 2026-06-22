# 设计文档：PlayerControllerBase 基类抽取（Archer 原型 · Phase 1）

- **日期**：2026-06-22
- **分支**：`feat/player-controller-base`
- **状态**：已通过 brainstorm，待编写实现计划
- **所属模块**：`Game.Character`（不触碰 `Game.Combat`）

---

## 1. 背景与全局阶段地图

Warrior（战士）原型已跑通 M0~M3（移动+轨道相机 / 跳跃 / 冲刺 / 连段攻击 + 刀光拖尾）。
现在要做第二个角色原型 Archer（弓箭手）。两个角色**共享"能做哪些事"（移动 / 跳跃 / 冲刺 / 坡度滑落这套逻辑），不共享动画数据本身**——各自有独立的 Animator Controller 与动画 Clip，共享逻辑只通过 Animator 参数名 / 状态节点名跟各自的 Controller 对话。

整个 Archer 工作分解为 4 个**顺序交付**的阶段，每个阶段独立 spec → plan → 实现 → 验证：

| 阶段 | 交付物 | 进入下一阶段的闸门 |
|------|--------|--------------------|
| **1. 基类抽取（本文档）** | `PlayerControllerBase` + 共享状态改类型 + Dash 状态名数据驱动。**仅 Warrior，零行为变更。** | **开发者 Play 模式回归** move/jump/dash/slope，确认 Warrior 行为未改坏 |
| 2. Archer 移动 | `ArcherController` 充实 + （开发者）Archer Animator Controller 与 Clip | Archer 用自己的动画跑通移动/跳跃/冲刺 |
| 3. Archer 普通攻击 | 单段 `ComboDefinition` 复用 `PlayerAttackState` 骨架 + `ArrowSpawnTime` 字段 + 箭矢系统 | 点按发射一支箭 |
| 4. 蓄力重击 | `PlayerChargeAttackState` + 长按蓄力输入 + 蓄力比例映射伤害/箭速 | 长按蓄力、松开发射 |

**本文档只覆盖 Phase 1。** 后续阶段存在尚未拍板的真实设计岔路（Phase 4 的 tap/hold 路由与"点按是否经过拉弓态"、输入检测机制选型、箭矢生命周期/对象池归属等），到对应阶段再 brainstorm，本阶段不预判。

---

## 2. 需求与验收标准

### 2.1 功能需求

把现有单体 `PlayerController.cs` 拆成三层：

- **`PlayerControllerBase`（abstract）**：移动 / 跳跃 / 冲刺 / 相机 / 输入计时器 / 状态机基础设施 / 接地三态（Grounded/Airborne/Sliding）+ Dash 态。
- **`WarriorController : PlayerControllerBase`**：连段攻击专属（`MeleeHitDetector` / `ComboDefinition` / `BladeTrail` / `PlayerAttackState` / 连段 hash 缓存 / 攻击触发 seam 实现）。
- **`ArcherController : PlayerControllerBase`**：本阶段为**空骨架**（仅继承，验证基类可被复用、不偷偷耦合 Warrior），phases 2–4 充实。

并顺带处理两个连带影响：

- **连带影响 1**：`PlayerStateBase._player` 字段类型与构造函数参数由 `PlayerController` 改为 `PlayerControllerBase`；共享状态（Grounded/Airborne/Sliding/Dash）随之改类型。
- **连带影响 2**：`PlayerDashState` 中硬编码的 `Animator.StringToHash("DashForward_SingleTwohandSword")` 改为**数据驱动状态名**（见 §4.3），消除"两个 Controller 节点名不一致 → CrossFade 静默失败"的隐蔽坑。

### 2.2 验收标准（**核心闸门**）

本阶段是对已稳定一整个 M1/M2 周期代码的重构，**唯一验收口径是"Warrior 行为零变更"**。开发者在 Play 模式手动回归以下项（Claude 不声称"能跑"，只保证静态逻辑等价）：

1. 移动：八方向相机相对移动、转向平滑（Slerp）手感不变
2. 跳跃：起跳初速度、分段重力（上升/下落不同倍率）手感不变
3. Coyote Time：走出平台边缘的宽限期仍可跳
4. Jump Buffer：空中提前按跳、落地立即起跳
5. 冲刺：方向锁定、固定时长、CrossFade 进入 `DashForward_SingleTwohandSword` 动画正常
6. Dash 冷却 / 缓冲：冷却期不可再冲、缓冲亚帧容错正常
7. 坡度滑落：站上超坡自动下滑、滑到平地恢复、滑出坡底悬空转空中
8. 连段攻击：Dash→Attack→Jump 优先级、连段推进/结束、命中判定、刀光拖尾窗口均不变
9. Profiler：热路径仍零 GC Alloc

**补充（待验证）**：把 Player 预制体上的脚本从 `PlayerController` 换成 `WarriorController` 后，Inspector 上原有序列化字段按名字自动迁移、引用不丢（这是 Unity 行为，需开发者在编辑器确认）。

---

## 3. 当前耦合点（已读真实代码确认）

- 所有共享状态都通过 `_player`（`PlayerController`）读取移动数据。**唯一**从共享状态伸向 Warrior 专属功能的引用是 `PlayerGroundedState.CheckTransition()` 里的 `_player.AttackBufferCounter` / `_player.AttackState` 这一支——这是重构必须抽象掉的 seam。
- `PlayerDashState` 额外硬编码了 `"DashForward_SingleTwohandSword"` 状态名（连带影响 2）。
- `PlayerStateBase._player` 是 `PlayerController` 类型，是连带影响 1 的根。

---

## 4. 架构设计与三个关键决策

### 4.1 成员归属表（base vs Warrior）

**`PlayerControllerBase`（abstract MonoBehaviour）持有：**

- 序列化：`_moveSpeed`/`_rotationSpeed`、`_gravityMultiplier`/`_jumpForce`/`_fallGravityMultiplier`/`_coyoteTime`/`_jumpBufferTime`、`_slideSpeed`、`_pushForce`、相机四项（`_cameraRoot`/`_lookSensitivity`/`_pitchMin`/`_pitchMax`）、Dash 四项（`_dashSpeed`/`_dashDuration`/`_dashCooldown`/`_dashBufferTime`）、**新增 `_dashStateName`**、**通用攻击缓冲 `_attackBufferTime`**（见 §4.2 理由）
- 组件引用：`_characterController`/`_animator`/`_inputActions`/`_groundChecker`/`_mainCamera`
- 状态机与共享状态：`_stateMachine`、`_groundedState`、`_airborneState`、`_slidingState`、`_dashState`
- 运行时数据：`_moveInput`/`_moveDirection`/`_lookInput`/`_cameraYaw`/`_cameraPitch`
- Hash：`SpeedHash`/`IsGroundedHash`、**新增 `_dashStateHash`**
- 属性：全部 locomotion 属性、Dash 全套（含 `DashStateHash`、`DashCooldownCounter`、`DashBufferCounter`）、`AttackBufferCounter`/`AttackBufferTime`（通用）、`StateMachine`/`GroundedState`/`AirborneState`/`SlidingState`/`DashState`
- 方法：`Start`（基类即可）、`OnEnable`/`OnDisable`（绑定 Jump/Attack/Dash 回调，本阶段三者皆通用）、`Update`、`LateUpdate`、`OnControllerColliderHit`、`SyncAnimatorParameters`、`CalculateMoveDirection`、`HandleCameraRotation`、`OnJumpPerformed`/`OnAttackPerformed`/`OnDashPerformed`
- 钩子：`protected virtual void Awake()`（共享初始化 + 预 hash dash 状态名）；**新增 `public virtual bool TryStartAttack() => false;`**

**`WarriorController : PlayerControllerBase` 持有：**

- 序列化：`_meleeHitDetector`、`_combo`、`_bladeTrail`
- 状态：`_attackState`、连段 hash 缓存 `_comboStateHashes`
- 属性：`MeleeHitDetector`、`Combo`、`BladeTrail`、`AttackState`
- 方法：`GetComboStateHash`、`BuildComboStateHashes`、`protected override void Awake()`（`base.Awake()` 后 `new PlayerAttackState(this)` + `BuildComboStateHashes()`）、`public override bool TryStartAttack()`（见 §4.2）

**`ArcherController : PlayerControllerBase`**：本阶段 `public class ArcherController : PlayerControllerBase { }` 空体即可（基类无 abstract 成员，可直接落地为纯移动角色）。

### 4.2 决策一 / 二：状态→控制器类型 + 攻击 seam

- **状态类型（决策一）**：`PlayerStateBase._player` 改为 `PlayerControllerBase`。共享状态（Grounded/Airborne/Sliding/Dash）无需更多改动。Warrior 专属的 `PlayerAttackState` 在自己的构造函数里接 `WarriorController`、存一个 typed 字段 `_warrior`（同时 `: base(player)` 上传为基类引用）；内部把 `_player.Combo`/`MeleeHitDetector`/`GetComboStateHash`/`BladeTrail` 改读 `_warrior.*`，其余（`Animator`/`GroundChecker`/`StateMachine`/`VerticalVelocity`/`AttackBufferCounter` 等基类成员）继续走 `_player`。**不引入泛型**（`PlayerStateBase<T>` 会逼共享状态背负用不到的类型参数）。
- **攻击 seam（决策二）**：把 `PlayerGroundedState` 里硬编码的攻击分支替换为虚调用 `if (_player.TryStartAttack()) return;`。基类默认返回 `false`。`WarriorController` 重写：
  ```csharp
  public override bool TryStartAttack()
  {
      if (AttackBufferCounter > 0f)
      {
          StateMachine.ChangeState(AttackState);
          return true;
      }
      return false;
  }
  ```
  与现状**行为完全等价**，并保留 Dash→Attack→Jump 优先级（Dash 仍由共享状态直接判定，Attack 走虚钩子，Jump 在其后）。
- **通用攻击缓冲留在基类的理由**：`_attackBufferTime`/`AttackBufferCounter`/`OnAttackPerformed` 是"按了攻击键"的通用基础设施，留基类可让现有 `Update` 计时器递减循环**原样搬迁、Warrior 逐位等价、风险最低**。Archer 的 tap/hold 输入在 Phase 4 重做时再决定是否复用该缓冲。被否方案：把攻击输入整体下沉到子类——本阶段否决，它对稳定的 Update 循环扰动过大。

### 4.3 决策三：Dash 状态名数据驱动（连带影响 2）

仿 `AttackDefinition.AnimationStateName` + `BuildComboStateHashes` 的"序列化字符串 + Awake 预 hash"模式：

- 基类新增 `[SerializeField] private string _dashStateName = "DashForward_SingleTwohandSword";`
- 基类 `Awake` 里 `_dashStateHash = Animator.StringToHash(_dashStateName);`（仅一次，绝不每帧/每次触发）；空串时记 0 并 `GameLog.Warn`（CrossFade 0 不切动画，与连段空状态名同款防御）
- 基类暴露 `public int DashStateHash => _dashStateHash;`
- `PlayerDashState` 删掉自己的 `static readonly DashStateHash` 常量，改用 `_player.DashStateHash`
- Warrior 在 Inspector 保留默认 `"DashForward_SingleTwohandSword"`；Archer 填自己新建 Controller 里的实际节点名

消除"两边 Controller 必须手动维护同一个魔法字符串、不一致就静默失败"的坑。

### 4.4 生命周期模板方法

`Awake` 用模板方法：基类 `protected virtual void Awake()` 做共享初始化（取组件、`new` 状态机与四个共享状态、预 hash dash 状态名）；子类 `protected override void Awake()` 先 `base.Awake()` 再做自己的（Warrior：`new PlayerAttackState(this)` + `BuildComboStateHashes()`）。`Start`（`ChangeState(GroundedState)` + 锁鼠标）、`OnEnable`/`OnDisable`、`Update`、`LateUpdate`、`OnControllerColliderHit` 本阶段全部留基类、非虚（Warrior 无需扩展，行为逐位不变）。

---

## 5. 数据流 / 边界示意

```
按键 (InputSystem_Actions)
  └─ PlayerControllerBase: OnJump/OnAttack/OnDash → 写各自缓冲计时器
       └─ Update: 读输入 → 算 MoveDirection → 递减计时器 → StateMachine.Update() → SyncAnimatorParameters
            └─ 当前 State (共享: Grounded/Airborne/Sliding/Dash) 经 _player(PlayerControllerBase) 读数据
                 └─ GroundedState.CheckTransition: Dash(共享) → _player.TryStartAttack()(虚) → Jump(共享) → 离地 → 超坡
                      └─ WarriorController.TryStartAttack() 重写 → ChangeState(WarriorController.AttackState)
                           └─ PlayerAttackState 经 _warrior 读 Combo/MeleeHitDetector/BladeTrail
```

模块边界：全程 `Game.Character`。攻击经 `PlayerAttackState` → `MeleeHitDetector`（`Game.Combat`）的现有路径不变，本阶段不动 `Game.Combat` 任何文件。

---

## 6. 开发者编辑器任务（Claude 不可代劳）

1. 把 Player 预制体上的脚本组件由 `PlayerController` 替换为 `WarriorController`；确认序列化字段（速度/跳跃/相机/Dash/MeleeHitDetector/Combo/BladeTrail 等）按名字迁移无丢失。
2. 在 `WarriorController` 的 `_dashStateName` 字段确认值为 `"DashForward_SingleTwohandSword"`。
3. 执行 §2.2 的 9 项 Play 模式回归 + Profiler 零 GC 检查。

> **`PlayerController.cs` 将被删除**：Unity 要求 MonoBehaviour 文件名 == 类名，无法把它"改名"为 `WarriorController` 还保留同一 GUID。因此其内容拆进新文件 `PlayerControllerBase.cs` + `WarriorController.cs`，原 `PlayerController.cs` 删除。后果：Player 预制体原本按 GUID 引用 `PlayerController` 脚本，删除后该引用失效，必须由开发者在编辑器把组件改挂 `WarriorController`（即上面任务 1）——这是预期内的、不可由 Claude 代劳的一步。`ArcherController` 何时挂到 Bow 预制体留待 Phase 2。

---

## 7. 非目标（本阶段明确不做）

- 不实现任何 Archer 专属功能（移动动画、普攻、蓄力、箭矢）——那是 phases 2–4。
- 不改 `Game.Combat`（`MeleeHitDetector`/`AttackDefinition`/`ComboResolver` 等）。
- 不重构无关代码（如空壳 `PlayerLocomotion.cs`，仅记录、不在本阶段处理）。
- 不改任何 locomotion / 攻击的**手感数值或逻辑**——纯结构搬迁。

---

## 8. 风险

- **行为漂移**：搬迁过程中误改 Update 顺序 / 计时器 / 优先级会破坏稳定手感。缓解：逐位搬迁、seam 用行为等价的虚调用、§2.2 回归闸门把关。
- **序列化迁移**：换脚本组件可能丢 Inspector 引用。缓解：字段名保持不变（Unity 按名迁移），开发者编辑器确认。
- **CrossFade 静默失败**：dash 状态名数据驱动后，Archer 填错名仍会静默失败——但已从"隐藏的硬编码"变成"显式可见的 Inspector 字段 + 空串告警"，可发现性大幅提升。
