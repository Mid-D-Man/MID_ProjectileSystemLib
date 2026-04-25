# MID Projectile System

High-performance server-authoritative projectile system for Unity NGO (Netcode for GameObjects).

**Rust owns simulation. C# owns everything else.**

---

## Stack

| Layer | Technology | Responsibility |
|---|---|---|
| Simulation | Rust `cdylib` | Physics tick, spatial-grid collision, wave/circular param store, state save/restore |
| Spawn fill | Unity Burst | Parallel struct init for 8+ simultaneous spawns |
| Network | NGO 1.7 / UTP 1.4 | Server-authoritative RPCs, snapshots, prediction |
| Rendering | `Graphics.DrawMesh` (combined mesh) | Zero GameObject per bullet |
| Trail | `TrailRenderer` object pool | Pooled, no instantiation per bullet |
| Impact | `LocalParticlePool` + Sprite Flipbook + Shared Emit | No VFX Graph, no GPU instancing |

---

## Benchmark Results

| Test | Result |
|---|---|
| Rust tick — 2048 projectiles | 52.7 µs |
| Spatial grid collision | 522.8 µs |
| Continuous fire — 600 active | **0.061 ms/frame** |
| spawn_batch (any count) | 0.4 µs (single FFI call) |
| Old spawn_pattern per call | 928 µs |

---

## Quick Start

### 1. Scene Setup

Create a persistent `GameObject` named `ProjectileSystem` and attach:

```
MID_MasterProjectileSystem
ProjectileRegistry
ServerProjectileAuthority
LocalProjectileManager
MID_ProjectileNetworkBridge
ClientPredictionManager
RaycastProjectileHandler
ProjectileImpactHandler
TrailObjectPool
ProjectileRenderer2D
ProjectileRenderer3D
```

Wire all serialized references in the inspector.

---

### 2. Create a Projectile Config

`Assets → Create → MidMan → Projectile System → Projectile Config`

| Field | Description |
|---|---|
| `Is3D` | Enable for 3D sim path (NativeProjectile3D buffer) |
| `MovementType` | Straight / Arching / Guided / Teleport / Wave / Circular |
| `DamageCurve` | AnimationCurve — x = normalised distance (0–1), y = damage |
| `UseScaleGrowth` | Enable only for projectiles that grow after spawn |
| `HasTrail` | Enable + assign TrailMaterial |
| `PreferredSimMode` | Override routing for special cases |

**For game-specific fields** (legendary tier, audio, kill effects), derive from `ProjectileConfigSO`:

```csharp
[CreateAssetMenu(...)]
public class MID_ProjectileConfig : ProjectileConfigSO
{
    public LegendaryTier tier;
    public AudioClip flightSound;
}
```

All package systems reference `ProjectileConfigSO` — your derived class slots in transparently.

---

### 3. Create a Shot Pattern

`Assets → Create → MidMan → Projectile System → Projectile Pattern`

Select the asset to open the interactive pattern editor:
- Drag control points to define spread shape in angle space
- Point at (0°, 0°) = straight ahead, ±90° = extreme spread
- Catmull-Rom (default): spline passes through all points — intuitive
- Bezier: alternating anchor/handle — precise

The **Simulate** section shows rays in the Scene view from any selected GameObject.

---

### 4. Register Configs

Drag all `ProjectileConfigSO` assets into `ProjectileRegistry._autoRegister` in the inspector.
Registration happens automatically on `Awake()`.

Or register at runtime:
```csharp
ushort configId = ProjectileRegistry.Instance.Register(myConfig);
```

---

### 5. Weapon Script — Fire

The weapon owns shooting mode (burst, auto, single, shotgun). The system owns simulation.

**RustSim2D / RustSim3D:**
```csharp
void Fire()
{
    // 1. Sample pattern — returns (horizontal°, vertical°) pairs in local space
    Vector2[] angles = _pattern.SampleDirections(_pelletCount);

    // 2. Convert to world-space SpawnPoints
    var spawnPts = new SpawnPoint[angles.Length];
    for (int i = 0; i < angles.Length; i++)
    {
        Vector3 dir = Quaternion.Euler(-angles[i].y, angles[i].x, 0f) * transform.forward;
        spawnPts[i] = new SpawnPoint
        {
            Origin    = _barrelTip.position,
            Direction = dir,
            Speed     = 0f  // 0 = use config's resolved speed
        };
    }

    // 3. Build context
    var context = new WeaponFireContext
    {
        IsNetworked            = true,
        IsRaycastWeapon        = false,
        OwnerMidId             = _ownerMidId,
        FiredByNetworkObjectId = NetworkObject.NetworkObjectId,
        IsBotOwner             = false,
        WeaponLevel            = _weaponLevel,
        DamageMultiplier       = _damageMultiplier
    };

    // 4. Fire — single call, system routes to correct sim
    MID_MasterProjectileSystem.Instance.Fire(_configId, spawnPts, spawnPts.Length, context);
}
```

**Raycast (weapon owns the cast):**
```csharp
void FireRaycast()
{
    RaycastHit2D hit = Physics2D.Raycast(
        _barrelTip.position, _barrelTip.right,
        _config.MaxRange, _hitLayers);

    var result = new RaycastFireResult
    {
        Origin             = _barrelTip.position,
        Direction          = _barrelTip.right,
        HitPoint           = hit ? (Vector3)hit.point
                                 : _barrelTip.position + _barrelTip.right * _config.MaxRange,
        DidHit             = hit,
        HitTargetNetworkId = hit
            ? hit.collider.GetComponentInParent<NetworkObject>()?.NetworkObjectId ?? 0
            : 0,
        IsHeadshot = hit && CheckHeadshot(hit)
    };

    MID_MasterProjectileSystem.Instance.RegisterRaycastFire(result, _configId, _context);
}
```

---

### 6. Register Collision Targets

**Online** — call from character `FixedUpdate` (or `TickDispatcher Tick_0_05`):
```csharp
MID_MasterProjectileSystem.Instance.RegisterTarget2D(new CollisionTarget
{
    X        = transform.position.x,
    Y        = transform.position.y,
    Radius   = _hitRadius,
    TargetId = (uint)NetworkObject.NetworkObjectId,
    Active   = 1
});
```

**Offline** — use `LocalDamageTarget`:
```csharp
var target = new LocalDamageTarget
{
    LocalId      = (uint)gameObject.GetInstanceID(),
    Position     = transform.position,
    Radius       = _hitRadius,
    Active       = true,
    SourceObject = gameObject
};
LocalProjectileManager.Instance.RegisterTarget(target);
```

---

### 7. Handle Damage

**Online** — subscribe to `RustSimAdapter.OnProjectileHit`:
```csharp
_authority.Adapter.OnProjectileHit += OnHit;

void OnHit(ProjectileHitPayload p)
{
    var netObj = GetNetworkObject((ulong)p.TargetId);
    netObj?.GetComponent<HealthComponent>()
           .TakeDamage(p.Damage, p.IsHeadshot, p.GameData);
}
```

**Offline** — subscribe to `LocalProjectileManager.OnHit`:
```csharp
LocalProjectileManager.Instance.OnHit += OnLocalHit;

void OnLocalHit(LocalHitPayload p)
{
    p.Target.SourceObject
     .GetComponent<HealthComponent>()
     .TakeDamage(p.Damage, p.IsHeadshot);
}
```

---

### 8. Guided / Homing Projectiles

Update the homing direction from a `TickDispatcher` subscriber:
```csharp
void OnEnable()
    => MID_TickDispatcher.Subscribe(TickRate.Tick_0_1, UpdateHoming);

void OnDisable()
    => MID_TickDispatcher.Unsubscribe(TickRate.Tick_0_1, UpdateHoming);

void UpdateHoming(float dt)
{
    var target = FindNearestEnemy();
    if (target == null) return;
    Vector2 dir = (target.position - _spawnPos).normalized;
    MID_MasterProjectileSystem.Instance.SetHomingDirection2D(_projId, dir);
}
```

---

### 9. Custom Impact Effects

```csharp
ProjectileImpactHandler.Instance.RegisterStrategy(smgConfigId, new ImpactRegistration
{
    Strategy     = ImpactStrategy.SharedEmit,
    SharedSystem = _dustParticleSystem,
    EmitCount    = 6,
    EmitSpeed    = 2f
});
```

---

## Simulation Modes

| Mode | Who fires it | Description |
|---|---|---|
| `LocalOnly` | Offline/practice | Full Rust sim, no NGO, no RPCs |
| `RustSim2D` | Online default | Server runs Rust 2D tick, clients predict |
| `RustSim3D` | Online + `Is3D=true` | Server runs Rust 3D tick |
| `Raycast` | Weapon script | Weapon owns `Physics2D.Raycast`. System: visual + RPC |
| `PhysicsObject` | Caller handles | Unity Rigidbody — rockets, grenades, bouncy |

**Routing priority** (highest wins):

1. `config.HasSimModeOverride` — explicit per-config override
2. `RequiresPhysicsObject` (rocket / fireball / bouncy / sticky / exploder)
3. `context.IsRaycastWeapon && IsRaycastEligible`
4. `config.Is3D` → `RustSim3D`
5. `!context.IsNetworked` → `LocalOnly`
6. Default → `RustSim2D`

---

## Movement Types

| Type | Byte | Description | Extensible without Rust? |
|---|---|---|---|
| Straight | 0 | Constant velocity + optional accel | — |
| Arching | 1 | Gravity via `GravityScale`. Parabolic arc. | — |
| Guided | 2 | Turns toward ax/ay(/az). C# updates direction via `TickDispatcher`. | Write ax/ay each Tick_0_1 |
| Teleport | 3 | Discrete jumps every 0.12s | — |
| Wave | 4 | Sinusoidal lateral oscillation. Params stored Rust-side. | Vary amplitude/frequency via config |
| Circular | 5 | Helical orbit around travel axis. Params stored Rust-side. | Vary radius/speed via config |

**Adding a truly new movement type:** add a byte constant + tick function in `simulation.rs`, add to the C# enum, CI rebuilds the native lib automatically.

**Sine/spiral paths without a new type:** use `Straight` + write `ax/ay` each `Tick_0_1`. No Rust changes needed.

---

## ID System

| ID | Type | Online source | Offline source |
|---|---|---|---|
| `ProjId` | `uint` | Monotonic counter in `ServerProjectileAuthority` | Monotonic counter in `LocalProjectileManager` |
| `TargetId` | `uint` | `(uint)NetworkObject.NetworkObjectId` | `(uint)gameObject.GetInstanceID()` |
| `OwnerMidId` | `ulong` | Player: NGO `OwnerClientId`. Bot: stable 100–999 range ID | `0` for player, sequential for enemies |
| `ConfigId` | `ushort` | Assigned by `ProjectileRegistry` at registration. Session-stable. | Same |

> **Why MID ID for bots?**  
> Bot projectiles are fired by the server. If they used NGO `OwnerClientId` (which is `0` = server), kill attribution and damage type resolution would incorrectly route through server-player logic. The 100–999 range ID keeps bots distinguishable.

---

## Damage System

Damage is an `AnimationCurve` over normalised travel distance (x = 0 → spawn, x = 1 → `MaxRange`).

- **Flat curve** = constant damage, `IsDamageConstant()` returns `true`, evaluation is skipped
- **Falloff curve** = damage drops with distance (CoD-style damage profiles)
- **Headshot** = curve value × `HeadshotMultiplier`
- **Crit** = pre-rolled at spawn, stored as `isCrit` bool, applied in `RustSimAdapter`

Use `Window → MidMan → Damage Profile Editor` to visualise curves per config.

---

## Network Model

```
Client fires          Server receives          All clients
─────────────────     ─────────────────────    ─────────────────────────
Fire()                FireServerRpc()           SpawnConfirmedClientRpc()
  │                     │                         │
  │                     ├─ Validate speed          ├─ Bind prediction visual
  │                     ├─ BatchSpawnHelper        │   to confirmed projId
  │                     ├─ Rust spawn_batch        │
  │                     └─ tick loop               │
  │                                               │
  │              ───── FixedUpdate ─────           │
  │                   Rust tick                   │
  │                   Rust collision               │
  │                   RustSimAdapter               │
  │                        │                       │
  │                        └──── HitConfirmedClientRpc() ──────────────→
  │                                                       impact effect
  │                                                       stop prediction visual
  │
  └─ ClientPredictionManager
       predict: pos = origin + dir * speed * elapsed
       reconcile: if error > 0.5m → smooth lerp
                  if error > 3.0m → instant snap
```

**What is synced:**
- `SpawnConfirmedClientRpc`: configId, origin, dir, speed, baseId, serverTick
- `HitConfirmedClientRpc`: projId, targetId, damage, hitPos, isHeadshot, isCrit
- `SnapshotClientRpc`: `(projId, x, y[, z])` array every 3–5 FixedUpdates

**What is NOT synced:** position every frame, physics state, damage values in transit.

---

## Project Structure

```
Assets/Scripts/Projectiles/
  Core/
    NativeProjectile.cs          ← 2D struct (72B, unchanged)
    ProjectileLib.cs             ← ALL P/Invoke bindings
    SimulationMode.cs            ← SimulationMode enum
  Managers/
    MID_MasterProjectileSystem.cs  ← Single Fire() entry point
    ServerProjectileAuthority.cs   ← Rust sim buffers, tick loop
    LocalProjectileManager.cs      ← Offline full pipeline
    RaycastProjectileHandler.cs    ← Visual + RPC for raycast weapons
  Network/
    MID_ProjectileNetworkBridge.cs ← ALL NGO RPCs
    ClientPredictionManager.cs     ← Prediction + reconciliation
  Adapters/
    RustSimAdapter.cs            ← HitResult → damage event
    BatchSpawnHelper.cs          ← Single FFI call spawn path
    ProjectileTypeRouter.cs      ← Route fire to correct sim mode
  Visual/
    ProjectileRenderer2D.cs      ← Combined mesh 2D
    ProjectileRenderer3D.cs      ← Combined mesh 3D
    ProjectileImpactHandler.cs   ← Pool / Flipbook / SharedEmit
    TrailObjectPool.cs           ← Pooled TrailRenderers (2D + 3D)
    ProjectileVisual_.cs         ← Client visual (unchanged)
    ProjectileTrailOptimizer.cs  ← Trail change detection (unchanged)
  Config/
    ProjectileConfigSO.cs        ← Core package config (extend, don't modify)
    ProjectileRegistry.cs        ← ushort configId → SO lookup
    ProjectilePatternSO.cs       ← Catmull-Rom / Bezier shot pattern
  Data/
    ServerProjectileData.cs      ← C# gameplay data per projectile
  Editor/
    ProjectilePatternEditor.cs   ← Interactive spline editor

projectile_core/
  src/
    lib.rs            ← All FFI exports
    simulation.rs     ← tick_all, tick_all_3d, all movement types
    collision.rs      ← CellGrid2D, CellGrid3D, check_hits, check_hits_3d
    config_store.rs   ← WaveParams / CircularParams Rust-side store
    patterns.rs       ← Legacy spawn_pattern (5 hardcoded patterns)
    state.rs          ← save/restore memcpy
  Cargo.toml
```

---

## Common Mistakes

| Mistake | Fix |
|---|---|
| Calling `Fire()` with `Raycast` mode | Use `RegisterRaycastFire()`. Weapon owns `Physics2D.Raycast()`. |
| Writing `x`/`y` on `NativeProjectile` in C# | Rust owns position. Write `ax`/`ay` for accel only. |
| Using NGO `OwnerClientId` for bots | Use MID ID (100–999). `isBotOwner` flag disambiguates. |
| Putting game fields in `ProjectileConfigSO` | Derive from it in your game assembly instead. |
| Registering configs after first `Fire()` | Register all configs in `Awake()`. `ConfigId` must be stable. |
| Attaching `ObjectNetSync` to bullets | `ObjectNetSync` is for slow physics objects only (rockets/grenades). |
| Using `TickRate.Tick_0_01` for logic | Use `Tick_0_1` minimum. `Tick_0_01` has negative saving below ~100 fps. |
| `spawn_pattern` for new code | Use `BatchSpawnHelper.SpawnBatch2D/3D` — eliminates 928µs per-call overhead. |
| Skipping `ValidateStructSizes()` | Called in `MID_MasterProjectileSystem.Awake()`. Never disable it in production. |

---

## CI / Native Lib Builds

Rust libs are cross-compiled automatically on push to `main` (or `workflow_dispatch`):

```
.github/workflows/build-rust-libs.yml

Targets:
  macOS   → libprojectile_core.dylib  (universal x86_64 + arm64)
  Windows → projectile_core.dll
  Linux   → libprojectile_core.so
  Android → arm64-v8a, armeabi-v7a, x86_64
  iOS     → libprojectile_core.a (device), libprojectile_core_sim.a (simulator)

Output: UnityProject/Assets/Plugins/Native/
```

To rebuild manually:
```bash
cd projectile_core
cargo build --release
```

---

## License

MIT — see `LICENSE`.

Copyright © 2026 Abdulhamid Manman Suleiman / MidMan Studio.
