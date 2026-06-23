# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Stack

- **Engine**: Unity 6.3 (6000.3.16f1)
- **Render Pipeline**: URP (Universal Render Pipeline) v17.3.0
- **Language**: C#
- **Input**: Unity Input System (`InputSystem_Actions`) — never use the legacy `Input` class
- **Camera**: orbit rig — a `CameraRoot` pivot rotated by mouse in `LateUpdate`, followed by a Cinemachine virtual camera
- **Movement**: `CharacterController` — not `Rigidbody` (projectiles use `Rigidbody`)

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

New code goes in the lowest assembly that satisfies its dependencies. Cross-module communication goes through EventBus events in `Game.Core`. Presentation (e.g. UI in `Game.Rendering`) reacts to gameplay only by subscribing to EventBus events — gameplay never references presentation.

### Folder Conventions

| Path | Purpose |
|------|---------|
| `Assets/_Project/Scripts/<Assembly>/` | C# source, one subfolder per assembly |
| `Assets/_Project/Prefabs/` & `Assets/_Project/Art/Prefabs/` | Prefabs |
| `Assets/_Project/ScriptableObjects/` | Runtime data (SO instances) |
| `Assets/_Project/Art/` | Textures, models, audio, Animator Controllers |
| `Assets/_Project/Scenes/` | Unity scene files |
| `Assets/ThirdParty/` | Third-party packages not via Package Manager |
| `docs/superpowers/specs/` & `docs/superpowers/plans/` | Feature design specs and implementation plans |
| `Assets/_Project/Docs/` | In-depth design analyses (e.g. `M3_Combat_Design.md`, `Dash_System_Design.md`) |

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

State objects are pre-instantiated in `Awake()` and reused — never allocate them at runtime. One-shot allocations on discrete input (e.g. `Instantiate` an arrow on release) are fine — only per-frame allocation is the concern. Verify zero GC Alloc in the Unity Profiler before considering a feature complete.

## Workflow

- **Before any cross-module or architectural change**: write a plan and confirm alignment first.
- **Feature flow**: non-trivial features go brainstorm → design spec → implementation plan → execute, with artifacts saved under `docs/superpowers/specs/` and `docs/superpowers/plans/`. Large features ship in **phases**, each gated by the developer verifying in the Editor before the next begins.
- **Renaming a MonoBehaviour script**: move the `.cs` **and its `.meta` together** (`git mv` both) so the script GUID survives and prefab/scene references don't break — never delete-and-recreate (that orphans the reference and resets serialized fields).
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

`Game.Character` drives players with a hand-rolled state machine. Two architectural constraints:

- **States are plain C# objects**, not MonoBehaviours — pre-instantiated in `Awake()` and driven manually (Unity never calls their `Update`). `PlayerStateMachine.ChangeState` runs `Exit()` → swaps reference → `Enter()`, **unconditionally** — so `Exit()` is the single guaranteed place to release per-state resources (start dash cooldown, close a hit window).
- **`CharacterController.Move()` runs in `Update()`**, never `FixedUpdate()` (Unity constraint). The camera pivot rotates in `LateUpdate()`, after all moves.

### Multi-character architecture (base + subclass)

Two playable archetypes share locomotion but **not animations** — each has its own Animator Controller and clips:

```
PlayerControllerBase (abstract MonoBehaviour)   ← move / jump / dash / camera / state machine + Grounded/Airborne/Sliding/Dash
├── WarriorController   — melee: MeleeHitDetector, ComboDefinition, BladeTrail, PlayerAttackState
└── ArcherController    — ranged: arrow prefab/spawn, ComboDefinition, ChargeAttackDefinition, PlayerBowAttackState + PlayerChargeAttackState
```

- **`PlayerControllerBase`** owns everything shared (components, input timers, state machine, the four shared states) and exposes data to states via properties. Subclasses add only attack specifics.
- **Attack seam**: the shared `PlayerGroundedState` never names a concrete attack state — it calls virtual `bool TryStartAttack()`. Base returns `false`; `WarriorController` enters its combo state, `ArcherController` routes tap→normal / hold→charge. This keeps the `Dash → Attack → Jump` priority in one shared place while attack semantics vary per character.
- **State typing**: `PlayerStateBase._player` is typed `PlayerControllerBase`; shared states use only base members. Character-specific states (`PlayerAttackState`, `PlayerBowAttackState`, `PlayerChargeAttackState`) take the concrete controller in their constructor and stash a typed field (`_warrior` / `_archer`) for subclass-only members. **No generics.**
- **`Awake` is `protected virtual`** (template method): the subclass override calls `base.Awake()`, then builds its attack state(s) and pre-hashes its animation state names.

### Files (`Game.Character`)

| File | Purpose |
|------|---------|
| `PlayerControllerBase.cs` | Abstract base: input, timers, camera, state machine, shared states, `TryStartAttack()` hook, data-driven `_dashStateName` |
| `WarriorController.cs` | Melee subclass (combo attack + blade trail). **Was renamed from `PlayerController.cs` keeping its `.meta`/GUID** |
| `ArcherController.cs` | Ranged subclass: normal/charge attack input routing, arrow spawn refs, aim mask |
| `GroundChecker.cs` | `SphereCast` ground detection; `IsGrounded`, `GroundNormal`, `GroundAngle` |
| `PlayerStateMachine.cs` | Minimal FSM: `Exit()` → swap → `Enter()` |
| `States/PlayerStateBase.cs` | Abstract state base; `_player` (`PlayerControllerBase`) + shared `HandleRotation()` |
| `States/PlayerGroundedState.cs` | Ground move, slope, and the transition priority chain |
| `States/PlayerAirborneState.cs` | Split-phase gravity, coyote time, jump-buffer consumption |
| `States/PlayerSlidingState.cs` | Constant-speed slide down steep slope |
| `States/PlayerDashState.cs` | Locked-direction dash via CrossFade; `Exit()` starts the cooldown |
| `States/PlayerAttackState.cs` | Warrior melee combo: per-segment hit + blade-trail windows, `ComboResolver`-driven |
| `States/PlayerBowAttackState.cs` | Archer normal shot: 1-segment combo, spawns an `Arrow` at `ArrowSpawnTime` |
| `States/PlayerChargeAttackState.cs` | Archer charge heavy: draw→hold→release, charge-scaled straight aimed shot, crosshair events |

> `PlayerLocomotion.cs` is an empty leftover stub (no namespace) — ignore/remove, not part of the system.

### Animator integration (read before touching combat/dash animations)

- **Continuous params** (`speed`, `isGrounded`) are synced every frame in `PlayerControllerBase.Update()`. **Event triggers** (`jump`) are fired once by the initiating state — never synced per-frame.
- **Code-driven entry**: attack/dash/charge states are entered with `Animator.CrossFadeInFixedTime(stateHash, …)` — code names the target state directly, so **no incoming transition line is needed** in the Controller. The target **state-name string is data-driven** and pre-hashed once via `Animator.StringToHash` in `Awake` (never per-frame): `_dashStateName` (base), `AttackDefinition.AnimationStateName` (per combo segment), the three names in `ChargeAttackDefinition`. A wrong/empty name fails **silently** (CrossFade to a nonexistent state = no anim change) — empty names log `GameLog.Warn`; a wrong name is a common bug when adding a new character.
- **Gotcha — CrossFade-entered states still need EXIT transitions.** Entry is code-driven, but *leaving* is not: when a dash/attack ends, the code only swaps the FSM state — it does **not** CrossFade back. Each such Animator state must carry its own outgoing transitions (e.g. `Dash_Bow → Idle` with Has Exit Time) or the character freezes in that pose. The per-character Controllers (`SingleTwoHandSwordHero`, `BowHero`) wire these; C# only data-drives the *entry* name. Pure locomotion states (Idle/Run/Jump*) are driven the normal way — by `speed`/`isGrounded`/`jump` transitions in the Controller.

### State transitions (shared)

```
Grounded ──dash buffered & off cooldown────────► Dash       (highest priority)
Grounded ──TryStartAttack() (per character)────► Attack / Bow / Charge state
Grounded ──jump / leave ground─────────────────► Airborne
Grounded ──steep slope─────────────────────────► Sliding
Airborne ──land (normal / steep slope)─────────► Grounded / Sliding
Sliding  ──lost ground / slope normalizes──────► Airborne / Grounded
Dash     ──duration elapsed (Exit→cooldown)────► Grounded / Airborne
Attack*  ──anim ≥ ~85%──────────────────────────► Grounded / Airborne
```

### Input: buffers and the timer pattern

A uniform **buffer-counter pattern** underlies all forgiveness windows: an input callback sets `Counter = <time>`; `PlayerControllerBase.Update()` decrements every counter each frame; the owning state treats `> 0` as "pending" and zeroes it on consume. Applied to Jump Buffer, Coyote Time, Attack Buffer, **Dash Buffer + Cooldown** (two orthogonal counters: buffer = sub-frame forgiveness, cooldown = ability lock), and the combo input window.

- **`PlayerGroundedState.CheckTransition` order = priority**: Dash → Attack → Jump → leave-ground → slope.
- **Tap vs hold** (Archer charge): instead of a buffer, `ArcherController.TryStartAttack` polls `IsAttackHeld` and accumulates hold time — past `TapThreshold` → charge state; released earlier (or a sub-frame tap caught by the buffer) → normal shot. `IsAttackHeld` is exposed on the base.
- Jump feel: **Coyote Time** (jump shortly after leaving ground), **Jump Buffer** (jump pressed airborne fires on landing), **split-phase gravity** (`FallGravityMultiplier` > `GravityMultiplier`).

---

## Combat System

`Game.Combat` is data-driven and shared by both characters. Melee uses an OverlapBox hit window; ranged uses flying `Arrow` projectiles — both submit damage through the **same** `IDamageable.ReceiveHit(in DamageRequest)` path. Design doc: `Assets/_Project/Docs/M3_Combat_Design.md`.

The damage flow is deliberately split so the math is testable without Unity:

```
MeleeHitDetector (OverlapBox, per-frame while window open)  ─┐
Arrow (OnCollisionEnter in flight)                          ─┤─► builds DamageRequest (value snapshot) ─► target.ReceiveHit()
                                                                  └─ HealthComponent.ReceiveHit()
                                                                       ├─ DamagePipeline.Resolve(req, defense)  ← pure, no MonoBehaviour
                                                                       ├─ subtract HP (clamped ≥ 0)
                                                                       ├─ Publish DamageReceivedEvent  (same frame)
                                                                       └─ Publish DeathEvent           (same frame, if HP ≤ 0)
```

### Animation-driven timing (no Animation Events)

**All combat timing reads `GetCurrentAnimatorStateInfo(0).normalizedTime`** against values in the SO — there is deliberately no parallel Animation-Event mechanism. The same idea covers: melee hit window (`HitActiveStart/End`), blade-trail window (`TrailActiveStart/End`), combo-input window (`ComboInputStart/End`), and the single-point arrow spawn (`ArrowSpawnTime`). **Single-point** triggers use a `bool` "already fired this play" guard (the projectile analogue of `MeleeHitDetector`'s `HashSet` per-swing dedup), and check the current state's `shortNameHash` to avoid mis-firing during a CrossFade transition.

### Files (`Game.Combat`)

| File | Purpose |
|------|---------|
| `IDamageable.cs` | Contract for "anything hittable": `TeamId`, `IsAlive`, `ReceiveHit(in DamageRequest)` |
| `HealthComponent.cs` | `MonoBehaviour` + `IDamageable`. HP/team/defense, resolves & applies damage, publishes events |
| `DamagePipeline.cs` | `static` pure `Resolve(in DamageRequest, in DefenseProfile) → DamageResult`. Unit-tested |
| `DamageRequest.cs` / `DamageResult.cs` | `readonly struct` value snapshots (request in, result out) |
| `DamageType.cs` / `DefenseProfile.cs` | `enum : byte` (`Physical`/`Magical`/`True`); defense struct (mitigation **reserved**, passthrough) |
| `AttackDefinition.cs` | `ScriptableObject` for one attack/segment: base amount, type, `HalfExtents`, the normalized windows above, `ArrowSpawnTime`, `AnimationStateName` |
| `ComboDefinition.cs` | `ScriptableObject` — ordered `AttackDefinition[]` segments (the chain); a 1-segment combo = a single attack |
| `ComboResolver.cs` | `static` pure function → `ComboDecision` (Continue / Advance / End) from index/count, anim progress, buffered input + window |
| `MeleeHitDetector.cs` | Weapon `MonoBehaviour`. While window open, `OverlapBoxNonAlloc` each frame, filters self/ally/dead/already-hit, submits `DamageRequest` |
| `Arrow.cs` | Projectile `MonoBehaviour`: `Rigidbody` ballistics + `OnCollisionEnter`. `Init(...)` snapshots damage/team; same-team pass-through, gravity toggle (straight aimed shots), `IgnoreCollision` with shooter, lifetime self-destruct |
| `ChargeAttackDefinition.cs` | `ScriptableObject` — charge tunables: 3 anim state names, tap threshold, max charge time, min/max damage & speed (linear by ratio), `ArrowSpawnTime`, `AimMaxDistance` |
| `CombatDamage.cs` | **Reserved seam** — future `Game.Skills` direct-damage entry. Not wired |
| `Events/DamageReceivedEvent.cs`, `Events/DeathEvent.cs` | `IGameEvent` published every hit / on death |
| `_Debug/*` | **Temporary** scaffolds (`MeleeSwingTestDriver`, `CombatDebugLogger`) — remove when no longer needed |

### Key Decisions

- **Value-snapshot damage**: `DamageRequest` captures attacker data by value, so resolution never re-queries a (possibly destroyed) attacker.
- **Pure pipeline / pure resolver**: `DamagePipeline` and `ComboResolver` are `static`, Unity-free, unit-testable. Mitigation is stubbed passthrough; `DefenseProfile` is the future Buff/Debuff hook.
- **Zero-GC hit detection**: `OverlapBoxNonAlloc` into a pre-allocated `Collider[]` (`MaxHitsPerFrame = 16`) + reused `HashSet<int>` per-swing dedup.
- **Same-frame events**: damage/death events publish synchronously inside `ReceiveHit`, so presentation reacts the same frame (e.g. `CrosshairUI`, debug logger).
- **Team filtering**: targets whose `TeamId == attackerTeam` are skipped (prevents self/friendly hits). For melee, `MeleeHitDetector._attackerTeam` **must match** the owner's `HealthComponent.TeamId` (keep in sync in the Inspector); `Arrow` reads team from the shooter's `HealthComponent`.

### Tests

EditMode tests live in `Assets/_Project/Tests/Combat/` (asmdef `Game.Combat.Tests`, NUnit). `DamagePipelineTests` covers the pure pipeline. Run in the Unity Test Runner (EditMode).

---

## Project Status

**Phase**: Warrior + Archer playable prototypes complete (locomotion + M3 melee + dash + combo + ranged + charge + aimed charge). Each feature was developer-verified in Play mode.

Implemented:
- 5 asmdef modules (Core / Rendering / Combat / Skills / Character)
- `GameLog`, `IGameEvent`, `EventBus<T>`
- `PlayerControllerBase` + `WarriorController` / `ArcherController`; shared state machine (Grounded / Airborne / Sliding / Dash) + per-character attack states
- Locomotion: camera-relative move, orbit camera, jump (coyote/buffer/split-gravity), slope sliding, dash (locked-direction CrossFade, cooldown + buffer)
- Melee combat: `IDamageable`/`HealthComponent`, pure `DamagePipeline`, `MeleeHitDetector`, `ComboDefinition`/`ComboResolver` combo, blade-trail VFX (Trail Renderer driven by SO window)
- Archer: `BowHero` Animator, ranged normal attack + `Arrow` projectile, `ChargeAttackDefinition` charge heavy (linear damage/speed by hold), aimed charge with screen-center raycast + `CrosshairUI` (via `AimStateChangedEvent`)
- EditMode tests for `DamagePipeline`

Next (not yet built):
- Real mitigation formulas in `DamagePipeline` (currently passthrough)
- Hit-reaction / hit-stun; damage VFX/UI consuming `DamageReceivedEvent`
- Scene lifecycle manager calling `EventBus<T>.Clear()` on unload
- Character data SO (`StatDefinition`); Skills system (`Game.Skills` via `CombatDamage`)
- Remove `_Debug/` combat scaffolds once unused

---

## Claude's Scope

Claude only edits `.cs` source files and plain-text config files (`.asmdef`, text-serialized `.asset`/`.controller`, `ProjectSettings/`). **Compilation, Animator wiring, and Play-mode testing are performed manually by the developer in the Unity Editor.** Do not assert that a change "works" — only that it is logically correct based on static code review. Do not create `.meta` files for new assets (Unity generates them); the one exception is moving an existing `.meta` alongside its file during a rename.
