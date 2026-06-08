# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Stack

- **Engine**: Unity 6.3 (6000.3.16f1)
- **Render Pipeline**: URP (Universal Render Pipeline) v17.3.0
- **Language**: C#
- **Input**: Unity Input System — use this, never the legacy `Input` class
- **Camera**: Cinemachine (planned, not yet installed)

## Architecture

### Core Principles

1. **Data-driven**: Game data lives in ScriptableObjects, not hardcoded in MonoBehaviours.
2. **Module isolation**: Each domain is a separate asmdef. Modules must not reference each other directly — they communicate via an EventBus defined in Game.Core.
3. **Unidirectional dependencies**: Core never depends on upper layers. Dependency flows strictly downward.

### Assembly Hierarchy

```
Game.Core          (foundation — no game dependencies)
├── Game.Rendering (depends on Core)
├── Game.Combat    (depends on Core)
│   ├── Game.Skills    (depends on Core + Combat)
│   └── Game.Character (depends on Core + Combat)
```

New code goes in the lowest assembly that satisfies its dependencies. If two leaf assemblies need to talk (e.g., Skills triggers a Character effect), they do so through an event raised on the EventBus in Game.Core.

### Folder Conventions

| Path | Purpose |
|------|---------|
| `Assets/_Project/Scripts/<Assembly>/` | C# source, one subfolder per assembly |
| `Assets/_Project/Prefabs/` | All prefabs |
| `Assets/_Project/ScriptableObjects/` | Runtime data (SO instances) |
| `Assets/_Project/Art/` | Textures, models, audio |
| `Assets/_Project/Scenes/` | Unity scene files |
| `Assets/ThirdParty/` | Third-party packages not via Package Manager |

## Code Conventions

| Category | Style | Example |
|----------|-------|---------|
| Private / protected fields | `_camelCase` | `_moveSpeed` |
| Public members & types | `PascalCase` | `MoveSpeed`, `DamageSystem` |
| Interfaces | `IXxx` | `IDamageable`, `IHealable` |
| Namespaces | `Game.<Assembly>` | `Game.Combat`, `Game.Core` |

Every script must declare a namespace matching its assembly (e.g., all files under `Scripts/Combat/` use `namespace Game.Combat`).

## Performance Rules

The following are **forbidden** inside `Update()`, `FixedUpdate()`, or any per-frame hot path:

- `new` allocations (objects, arrays, closures, delegates)
- LINQ queries (`Where`, `Select`, `FirstOrDefault`, etc.)
- Boxing (casting value types to `object` or a non-generic interface)

Use the Unity Profiler's GC Alloc column to verify zero allocations on hot paths before considering a feature complete.

## Workflow

- **Before any cross-module or architectural change**: write a plan and confirm alignment first.
- **Commits**: use [Conventional Commits](https://www.conventionalcommits.org/). Examples:
  - `feat(combat): add hit-stun state machine`
  - `fix(core): prevent EventBus null-ref on first-frame subscription`
  - `refactor(character): split StatBlock into separate ScriptableObject`

## Core Infrastructure (已实现)

### GameLog — 调试日志

`Game.Core.GameLog` 是全局日志入口，**不要直接调用 `Debug.Log`**。

```csharp
GameLog.Info("message");                   // 无分类
GameLog.Warn("message", "Combat");         // 输出 [Combat] message
GameLog.Error("message", "EventBus");
```

三个方法均标记 `[Conditional("DEVELOPMENT_BUILD")]` + `[Conditional("UNITY_EDITOR")]`，
Release 构建中调用方的整个 call site（含字符串插值、装箱）由编译器在 IL 层面抹除，GC Alloc 为零。

### EventBus — 模块间通信

`Game.Core.EventBus<T>` 是所有跨模块事件的唯一通道。

**定义事件**（必须是 struct + IGameEvent）：
```csharp
// 放在事件所属模块的 Scripts 目录下
public struct PlayerDiedEvent : IGameEvent
{
    public Vector3 Position;
}
```

**订阅 / 取消（MonoBehaviour 标准模式）**：
```csharp
void OnEnable()  => EventBus<PlayerDiedEvent>.Subscribe(OnPlayerDied);
void OnDisable() => EventBus<PlayerDiedEvent>.Unsubscribe(OnPlayerDied);

void OnPlayerDied(PlayerDiedEvent e) { ... }
```

**发布**：
```csharp
EventBus<PlayerDiedEvent>.Publish(new PlayerDiedEvent { Position = transform.position });
```

**换场景时清理**（防止 stale subscriber 内存泄漏）：
```csharp
// 在场景卸载回调中，对本场景使用的每个事件类型调用：
EventBus<PlayerDiedEvent>.Clear();
```

`Publish` 热路径零 GC Alloc：T 约束为 struct，JIT 按值传递，不装箱。

---

## 项目当前进度

**阶段**：工程地基完成，尚无游戏玩法代码。

已完成：
- 5 个 asmdef 模块骨架（Core / Rendering / Combat / Skills / Character）
- `.gitignore` / `.gitattributes`（含 Git LFS 美术资产规则）/ `.editorconfig`
- `Game.Core.GameLog` — 零开销条件日志
- `Game.Core.IGameEvent` + `Game.Core.EventBus<T>` — 模块间事件总线

下一步（待开发）：
- 场景生命周期管理器（负责调用 EventBus.Clear）
- 角色数据 ScriptableObject（StatDefinition）
- 输入处理（Input System Actions asset）

---

## Claude's Scope in This Project

Claude only edits `.cs` source files and plain-text config files (`.asmdef`, text-serialised `.asset`, `ProjectSettings/`). **Compilation and Play-mode testing are performed manually by the developer in the Unity Editor.** Do not assert that a change "works" — only that it is logically correct based on static code review.
