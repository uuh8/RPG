# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Stack

- **Engine**: Unity 6.3 (6000.3.16f1)
- **Render Pipeline**: URP (Universal Render Pipeline) v17.3.0
- **Language**: C#
- **Input**: Unity Input System (`InputSystem_Actions`) — never use the legacy `Input` class
- **Camera**: Cinemachine (planned, not yet installed)
- **Movement**: `CharacterController` — not `Rigidbody`

## Architecture

### Core Principles

1. **Data-driven**: Game data lives in ScriptableObjects, not hardcoded in MonoBehaviours.
2. **Module isolation**: Each domain is a separate asmdef. Modules must not reference each other directly — they communicate via the EventBus in `Game.Core`.
3. **Unidirectional dependencies**: Core never depends on upper layers.

### Assembly Hierarchy

```
Game.Core          (foundation — no game dependencies)
├── Game.Rendering (depends on Core)
├── Game.Combat    (depends on Core)
│   ├── Game.Skills    (depends on Core + Combat)
│   └── Game.Character (depends on Core + Combat)
```

New code goes in the lowest assembly that satisfies its dependencies. Cross-module communication goes through EventBus events in `Game.Core`.

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

Every script must declare a namespace matching its assembly.

## Performance Rules

The following are **forbidden** inside `Update()`, `FixedUpdate()`, or any per-frame hot path:

- `new` allocations (objects, arrays, closures, delegates)
- LINQ queries (`Where`, `Select`, `FirstOrDefault`, etc.)
- Boxing (casting value types to `object` or a non-generic interface)

State objects are pre-instantiated in `Awake()` and reused — never allocate them at runtime. Verify zero GC Alloc in the Unity Profiler before considering a feature complete.

## Workflow

- **Before any cross-module or architectural change**: write a plan and confirm alignment first.
- **Commits**: use [Conventional Commits](https://www.conventionalcommits.org/). Examples:
  - `feat(combat): add hit-stun state machine`
  - `fix(core): prevent EventBus null-ref on first-frame subscription`
  - `refactor(character): split StatBlock into separate ScriptableObject`

---

## Core Infrastructure

### GameLog — Debug Logging

`Game.Core.GameLog` is the global logging entry point. **Never call `Debug.Log` directly.**

```csharp
GameLog.Info("message");
GameLog.Warn("message", "Combat");    // outputs [Combat] message
GameLog.Error("message", "EventBus");
```

All three methods are marked `[Conditional("DEVELOPMENT_BUILD")]` + `[Conditional("UNITY_EDITOR")]`. In Release builds the entire call site (including string interpolation and boxing) is stripped at the IL level — zero GC Alloc.

### EventBus — Cross-Module Communication

`Game.Core.EventBus<T>` is the only channel for cross-module events.

**Define an event** (must be `struct` + `IGameEvent` — never `class`):
```csharp
public struct PlayerDiedEvent : IGameEvent
{
    public Vector3 Position;
}
```

**Subscribe / Unsubscribe** (standard MonoBehaviour pattern):
```csharp
void OnEnable()  => EventBus<PlayerDiedEvent>.Subscribe(OnPlayerDied);
void OnDisable() => EventBus<PlayerDiedEvent>.Unsubscribe(OnPlayerDied);

void OnPlayerDied(PlayerDiedEvent e) { ... }
```

**Publish**:
```csharp
EventBus<PlayerDiedEvent>.Publish(new PlayerDiedEvent { Position = transform.position });
```

**Scene cleanup** (prevent stale subscriber leaks):
```csharp
EventBus<PlayerDiedEvent>.Clear(); // call for every event type used in the scene
```

`Publish` is zero-GC on the hot path: `T` is constrained to `struct`, so JIT passes by value with no boxing.

---

## Character System

### Overview

The character system lives in `Game.Character` and uses a state machine pattern. The key design decisions:

- **States are plain C# objects**, not MonoBehaviours. `PlayerController` instantiates them in `Awake()` and calls their methods manually — no `Update()` is called automatically by Unity.
- **`CharacterController.Move()` must be called in `Update()`**, never `FixedUpdate()`. This is a Unity constraint.
- **Camera rotation runs in `LateUpdate()`** to ensure it executes after all `Update()` calls.

### Files

| File | Purpose |
|------|---------|
| `PlayerController.cs` | Orchestrator: reads input, drives the state machine, owns `CharacterController`, handles camera and animator sync |
| `GroundChecker.cs` | `SphereCast`-based ground detection; exposes `IsGrounded`, `GroundNormal`, `GroundAngle` |
| `PlayerStateMachine.cs` | Minimal FSM: calls `Exit()` → swaps reference → calls `Enter()` |
| `PlayerStateBase.cs` | Abstract base; provides `_player` reference and shared `HandleRotation()` helper |
| `PlayerGroundedState.cs` | Ground movement, slope detection, jump and attack initiation |
| `PlayerAirborneState.cs` | Split-phase gravity, coyote time, jump buffer consumption |
| `PlayerSlidingState.cs` | Constant-speed sliding along steep slope tangent |
| `States/PlayerAttackState.cs` | Plays attack animation, drives the `MeleeHitDetector` hit window from animation `normalizedTime`, returns to movement at 85% |

### State Transitions

```
GroundedState ──attack buffered──────► AttackState
GroundedState ──jump / leave ground──► AirborneState
GroundedState ──steep slope detected──► SlidingState
AirborneState ──land on normal slope──► GroundedState
AirborneState ──land on steep slope──► SlidingState
SlidingState  ──lost ground──────────► AirborneState
SlidingState  ──slope normalizes──────► GroundedState
AttackState   ──anim ≥ 85% (grounded)─► GroundedState
AttackState   ──anim ≥ 85% (airborne)─► AirborneState
```

### Jump Mechanics

- **Coyote Time**: Brief window after leaving ground where jump is still valid.
- **Jump Buffer**: Jump input pressed while airborne is stored and consumed immediately upon landing.
- **Split-Phase Gravity**: Uses a higher `FallGravityMultiplier` when falling vs. `GravityMultiplier` on the way up, giving snappier feel.

### Movement

Input (`MoveInput`) is transformed to camera-relative world space before being passed to `CharacterController.Move()`. The camera's forward/right vectors define the movement basis.

### Animator Integration

Two categories of Animator parameters:

- **Continuous** (`speed`, `isGrounded`): synced every frame in `PlayerController.Update()`
- **Event triggers** (`jump`, `attack`): fired on-demand by the state that initiates the action

Do not sync triggers every frame — they are consumed once and should not be re-fired on the next frame.

### Input Buffering

Jump and attack inputs are buffered identically: `PlayerController` records a countdown (`JumpBufferCounter` / `AttackBufferCounter`) on the input callback and decrements it every frame. The owning state checks the counter and consumes it. In `PlayerGroundedState.CheckTransition()`, **attack is checked before jump** — a buffered attack wins over a buffered jump on the same frame.

---

## Combat System

### Overview

`Game.Combat` implements the M3 melee core loop: an attack window detects overlapping
targets, a pure-function pipeline resolves damage, and the target's `HealthComponent`
applies it and publishes events. Design doc: `Assets/_Project/Docs/M3_Combat_Design.md`.
Implementation plan: `docs/superpowers/plans/2026-06-12-m3-combat-core-loop.md`.

The damage flow is deliberately split so the math is testable without Unity:

```
MeleeHitDetector (OverlapBox, per-frame while window open)
   └─ builds DamageRequest (value snapshot of attacker) ──► target.ReceiveHit()
        └─ HealthComponent.ReceiveHit()
             ├─ DamagePipeline.Resolve(req, defenseProfile)  ← pure, no MonoBehaviour
             ├─ subtract HP (clamped ≥ 0)
             ├─ Publish DamageReceivedEvent  (same frame)
             └─ Publish DeathEvent           (same frame, if HP ≤ 0)
```

### Files (`Game.Combat`)

| File | Purpose |
|------|---------|
| `IDamageable.cs` | Contract for "anything that can be hit": `TeamId`, `IsAlive`, `ReceiveHit(in DamageRequest)` |
| `HealthComponent.cs` | `MonoBehaviour` + `IDamageable`. Holds HP/team/defense, resolves and applies damage, publishes events |
| `DamagePipeline.cs` | `static` pure function `Resolve(in DamageRequest, in DefenseProfile) → DamageResult`. Unit-testable |
| `DamageRequest.cs` | `readonly struct` — value snapshot of one hit (attacker id/team, base amount, type, hit point/dir) |
| `DamageResult.cs` | `readonly struct` — pipeline output (final amount, type, mitigated flag) |
| `DamageType.cs` | `enum : byte` — `Physical` / `Magical` / `True` |
| `DefenseProfile.cs` | `struct` with `Armor` / `MagicResist`. **Fields reserved** — pipeline is passthrough this round |
| `AttackDefinition.cs` | `ScriptableObject` — data-driven attack: base amount, type, `HalfExtents`, normalized `ActiveStart`/`ActiveEnd` |
| `MeleeHitDetector.cs` | `MonoBehaviour` on the weapon. While window open, `OverlapBoxNonAlloc` each frame, filters self/ally/dead/already-hit, submits `DamageRequest` |
| `CombatDamage.cs` | **Reserved seam** — future `Game.Skills` entry to deal damage directly, bypassing melee detection. Not wired this round |
| `Events/DamageReceivedEvent.cs` | `IGameEvent` published on every hit (for VFX/UI/hit-reaction) |
| `Events/DeathEvent.cs` | `IGameEvent` published when HP reaches 0 |
| `_Debug/MeleeSwingTestDriver.cs` | **Temporary** scaffold that opens/closes the hit window on a timer; delete once verified |
| `_Debug/CombatDebugLogger.cs` | **Temporary** — logs combat events via `GameLog`; delete once verified |

### Key Decisions

- **Value-snapshot damage**: `DamageRequest` captures attacker data by value, so resolution never re-queries a (possibly destroyed) attacker — avoids null-ref failure modes mid-resolve.
- **Pure pipeline**: `DamagePipeline` is a `static` function with no Unity deps, covered by EditMode tests. Mitigation formulas are stubbed (passthrough); `DefenseProfile` fields exist as the future Buff/Debuff hook.
- **Zero-GC hit detection**: `OverlapBoxNonAlloc` into a pre-allocated `Collider[]` (cap `MaxHitsPerFrame = 16`), with a reused `HashSet<int>` for per-swing dedup. No allocation in the hot path.
- **Same-frame events**: `DamageReceivedEvent` and `DeathEvent` publish synchronously inside `ReceiveHit`, so presentation reacts the same frame.
- **Data-driven window**: `PlayerAttackState` opens the hit window only while animation `normalizedTime` is within `[ActiveStart, ActiveEnd]`, so designers retune active frames in the SO without code changes.

### Team Filtering

`MeleeHitDetector._attackerTeam` **must match** the attacker's own `HealthComponent.TeamId`. The detector skips any target whose `TeamId` equals the attacker team, which is how self-hits and friendly fire are prevented. Keep these two serialized values in sync in the Inspector.

### Tests

EditMode tests live in `Assets/_Project/Tests/Combat/` (asmdef `Game.Combat.Tests`, NUnit).
`DamagePipelineTests` covers the pure pipeline (True ignores defense, passthrough cases,
negative-clamp, type preservation). Run them in the Unity Test Runner (EditMode).

---

## Project Status

**Phase**: Character locomotion complete; M3 melee combat core loop implemented (pending in-editor verification).

Implemented:
- 5 asmdef module skeletons (Core / Rendering / Combat / Skills / Character)
- `GameLog`, `IGameEvent`, `EventBus<T>`
- `CharacterController`-based player movement with full state machine (Grounded / Airborne / Sliding / Attack)
- Coyote Time, Jump Buffer, Attack Buffer, split-phase gravity, slope sliding
- Camera-relative input, camera rotation with pitch/yaw clamping
- `GroundChecker` with `SphereCast` and gizmo visualization
- **Combat core loop**: `IDamageable` / `HealthComponent`, pure `DamagePipeline`, `MeleeHitDetector` (OverlapBox), data-driven `AttackDefinition` SO, `DamageReceivedEvent` / `DeathEvent`
- **EditMode tests** for `DamagePipeline` (`Game.Combat.Tests`)
- Reserved seams: `CombatDamage` (Skills entry), `DefenseProfile` mitigation fields

Next (not yet built):
- Verify combat loop in-editor, then remove `_Debug/` scaffolds (`MeleeSwingTestDriver`, `CombatDebugLogger`)
- Real mitigation formulas in `DamagePipeline` (Physical/Magical currently passthrough)
- Hit-reaction / hit-stun, damage VFX/UI consuming `DamageReceivedEvent`
- Scene lifecycle manager (calls `EventBus<T>.Clear()` on unload)
- Character data ScriptableObject (`StatDefinition`)
- Skills system (`Game.Skills`, wires through `CombatDamage`)

---

## Claude's Scope

Claude only edits `.cs` source files and plain-text config files (`.asmdef`, text-serialized `.asset`, `ProjectSettings/`). **Compilation and Play-mode testing are performed manually by the developer in the Unity Editor.** Do not assert that a change "works" — only that it is logically correct based on static code review.
