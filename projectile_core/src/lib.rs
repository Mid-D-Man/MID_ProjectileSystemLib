// lib.rs — all public FFI exports live here
// Unity calls these via P/Invoke (DllImport / __Internal on iOS)
//
// Layout rules:
//   - All exported structs use #[repr(C)] — never reorder fields without
//     updating the matching C# StructLayout(LayoutKind.Explicit) counterpart.
//   - All exported functions are #[no_mangle] extern "C".
//   - No panics cross the FFI boundary — every unsafe block guards against
//     null pointers and zero counts before dereferencing.
//
// 2D struct sizes (unchanged):
//   NativeProjectile  = 72 bytes
//   HitResult         = 24 bytes
//   CollisionTarget   = 20 bytes
//   SpawnRequest      = 32 bytes
//
// 3D struct sizes (new):
//   NativeProjectile3D  = 84 bytes
//   HitResult3D         = 28 bytes
//   CollisionTarget3D   = 24 bytes
//
// iOS note: on IL2CPP the dylib is a static lib linked as __Internal.

mod simulation;
mod collision;
mod patterns;
mod state;

pub use simulation::*;
pub use collision::*;
pub use patterns::*;
pub use state::*;

use std::slice;

// ─────────────────────────────────────────────────────────────────────────────
//  2D shared data types (unchanged) — layout verified against C# counterparts
// ─────────────────────────────────────────────────────────────────────────────

/// Core 2D projectile state.  Matches NativeProjectile.cs exactly.
/// Layout: 15 × f32 (60 B) + 2 × u16 (4 B) + 1 × u32 (4 B) + 4 × u8 (4 B) = 72 bytes
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeProjectile {
    pub x:          f32,   // 0
    pub y:          f32,   // 4
    pub vx:         f32,   // 8
    pub vy:         f32,   // 12
    pub ax:         f32,   // 16 — lateral accel / guided homing X
    pub ay:         f32,   // 20 — gravity / guided homing Y
    pub angle_deg:  f32,   // 24 — visual rotation, derived from velocity each tick
    pub curve_t:    f32,   // 28 — elapsed time param for arching / teleport timer

    pub scale_x:      f32, // 32
    pub scale_y:      f32, // 36
    pub scale_target: f32, // 40
    pub scale_speed:  f32, // 44 — 0.0 = no growth (skip tick_scale entirely)

    pub lifetime:     f32, // 48
    pub max_lifetime: f32, // 52
    pub travel_dist:  f32, // 56

    pub config_id: u16,    // 60
    pub owner_id:  u16,    // 62
    pub proj_id:   u32,    // 64

    pub collision_count: u8, // 68
    pub movement_type:   u8, // 69 — 0=straight 1=arching 2=guided 3=teleport
    pub piercing_type:   u8, // 70 — 0=none 1=piercer 2=random
    pub alive:           u8, // 71
    // Total: 72 bytes
}

/// 2D hit event returned by check_hits_grid.
/// 24 bytes — matches HitResult.cs.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct HitResult {
    pub proj_id:     u32,  // 0
    pub proj_index:  u32,  // 4
    pub target_id:   u32,  // 8
    pub travel_dist: f32,  // 12
    pub hit_x:       f32,  // 16
    pub hit_y:       f32,  // 20
    // Total: 24 bytes
}

/// 2D collision target (enemy, player, obstacle).
/// 20 bytes — matches CollisionTarget.cs.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct CollisionTarget {
    pub x:         f32,    // 0
    pub y:         f32,    // 4
    pub radius:    f32,    // 8
    pub target_id: u32,    // 12
    pub active:    u8,     // 16
    pub _pad:      [u8; 3],// 17
    // Total: 20 bytes
}

/// Spawn request written by C# before calling spawn_pattern.
/// 32 bytes — matches SpawnRequest.cs.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct SpawnRequest {
    pub origin_x:     f32,  // 0
    pub origin_y:     f32,  // 4
    pub angle_deg:    f32,  // 8
    pub speed:        f32,  // 12
    pub config_id:    u16,  // 16
    pub owner_id:     u16,  // 18
    pub pattern_id:   u8,   // 20 — 0=single 1=spread3 2=spread5 3=spiral 4=ring8
    pub _pad:         [u8; 3], // 21
    pub rng_seed:     u32,  // 24
    pub base_proj_id: u32,  // 28
    // Total: 32 bytes
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D shared data types (new)
// ─────────────────────────────────────────────────────────────────────────────

/// Core 3D projectile state.  Matches NativeProjectile3D.cs exactly.
///
/// Field layout (C# [StructLayout(Size = 84)]):
///   9 × f32  pos/vel/accel XYZ              = 36 B  (offsets 0-35)
///   5 × f32  scale X/Y/Z + target + speed   = 20 B  (offsets 36-55)
///   4 × f32  lifetime, max_lifetime,
///            travel_dist, timer_t            = 16 B  (offsets 56-71)
///   2 × u16  config_id, owner_id            =  4 B  (offsets 72-75)
///   1 × u32  proj_id                        =  4 B  (offsets 76-79)
///   4 × u8   flags                          =  4 B  (offsets 80-83)
///                                             ─────
///                                             84 B total
///
/// Notes vs 2D:
///   - No angle_deg: C# computes visual rotation from velocity direction.
///   - curve_t renamed timer_t: used by arching (elapsed time) and teleport (interval timer).
///   - scale_z added: uniform 3D scale. tick_scale_3d sets x/y/z identically.
///   - ax/ay/az: gravity/accel OR homing target direction (movement_type=2).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeProjectile3D {
    pub x:  f32,  // 0
    pub y:  f32,  // 4
    pub z:  f32,  // 8
    pub vx: f32,  // 12
    pub vy: f32,  // 16
    pub vz: f32,  // 20
    pub ax: f32,  // 24 — lateral/gravity X or homing direction X
    pub ay: f32,  // 28 — gravity Y or homing direction Y
    pub az: f32,  // 32 — gravity Z or homing direction Z

    pub scale_x:      f32, // 36
    pub scale_y:      f32, // 40
    pub scale_z:      f32, // 44
    pub scale_target: f32, // 48
    pub scale_speed:  f32, // 52 — 0.0 = no growth

    pub lifetime:     f32, // 56
    pub max_lifetime: f32, // 60
    pub travel_dist:  f32, // 64
    pub timer_t:      f32, // 68 — arching elapsed time / teleport interval timer

    pub config_id: u16,    // 72
    pub owner_id:  u16,    // 74
    pub proj_id:   u32,    // 76

    pub collision_count: u8, // 80
    pub movement_type:   u8, // 81
    pub piercing_type:   u8, // 82
    pub alive:           u8, // 83
    // Total: 84 bytes
}

/// 3D hit event returned by check_hits_grid_3d.
/// 28 bytes — matches HitResult3D.cs.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct HitResult3D {
    pub proj_id:     u32,  // 0
    pub proj_index:  u32,  // 4
    pub target_id:   u32,  // 8
    pub travel_dist: f32,  // 12
    pub hit_x:       f32,  // 16
    pub hit_y:       f32,  // 20
    pub hit_z:       f32,  // 24
    // Total: 28 bytes
}

/// 3D collision target.
/// 24 bytes — matches CollisionTarget3D.cs.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct CollisionTarget3D {
    pub x:         f32,    // 0
    pub y:         f32,    // 4
    pub z:         f32,    // 8
    pub radius:    f32,    // 12
    pub target_id: u32,    // 16
    pub active:    u8,     // 20
    pub _pad:      [u8; 3],// 21
    // Total: 24 bytes
}

// ─────────────────────────────────────────────────────────────────────────────
//  Tick — 2D (unchanged)
// ─────────────────────────────────────────────────────────────────────────────

/// Advance the 2D simulation by `dt` seconds.
/// Returns the number of projectiles that died this tick.
#[no_mangle]
pub unsafe extern "C" fn tick_projectiles(
    projs: *mut NativeProjectile,
    count: i32,
    dt:    f32,
) -> i32 {
    if projs.is_null() || count <= 0 { return 0; }
    let slice = slice::from_raw_parts_mut(projs, count as usize);
    simulation::tick_all(slice, dt)
}

// ─────────────────────────────────────────────────────────────────────────────
//  Tick — 3D (new)
// ─────────────────────────────────────────────────────────────────────────────

/// Advance the 3D simulation by `dt` seconds.
/// Returns the number of projectiles that died this tick.
#[no_mangle]
pub unsafe extern "C" fn tick_projectiles_3d(
    projs: *mut NativeProjectile3D,
    count: i32,
    dt:    f32,
) -> i32 {
    if projs.is_null() || count <= 0 { return 0; }
    let slice = slice::from_raw_parts_mut(projs, count as usize);
    simulation::tick_all_3d(slice, dt)
}

// ─────────────────────────────────────────────────────────────────────────────
//  Collision — 2D (unchanged)
// ─────────────────────────────────────────────────────────────────────────────

/// Spatial-grid collision check (2D).  Uses cell_size = 4.0 default.
#[no_mangle]
pub unsafe extern "C" fn check_hits_grid(
    projs:         *const NativeProjectile,
    proj_count:    i32,
    targets:       *const CollisionTarget,
    target_count:  i32,
    out_hits:      *mut HitResult,
    max_hits:      i32,
    out_hit_count: *mut i32,
) {
    check_hits_grid_ex(projs, proj_count, targets, target_count,
                       out_hits, max_hits, 0.0, out_hit_count);
}

/// Spatial-grid collision check (2D) with explicit cell_size.
/// Pass 0.0 for cell_size to use the default (4.0 world units).
#[no_mangle]
pub unsafe extern "C" fn check_hits_grid_ex(
    projs:         *const NativeProjectile,
    proj_count:    i32,
    targets:       *const CollisionTarget,
    target_count:  i32,
    out_hits:      *mut HitResult,
    max_hits:      i32,
    cell_size:     f32,
    out_hit_count: *mut i32,
) {
    let zero_out = |p: *mut i32| { if !p.is_null() { unsafe { *p = 0; } } };

    if projs.is_null() || targets.is_null() || out_hits.is_null() {
        zero_out(out_hit_count);
        return;
    }
    let projs_s   = slice::from_raw_parts(projs,    proj_count   as usize);
    let targets_s = slice::from_raw_parts(targets,  target_count as usize);
    let hits_s    = slice::from_raw_parts_mut(out_hits, max_hits as usize);

    let count = collision::check_hits(projs_s, targets_s, hits_s, cell_size);

    if !out_hit_count.is_null() { *out_hit_count = count as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Collision — 3D (new)
// ─────────────────────────────────────────────────────────────────────────────

/// Spatial-grid collision check (3D) with explicit cell_size.
/// Pass 0.0 for cell_size to use the default (4.0 world units).
#[no_mangle]
pub unsafe extern "C" fn check_hits_grid_3d(
    projs:         *const NativeProjectile3D,
    proj_count:    i32,
    targets:       *const CollisionTarget3D,
    target_count:  i32,
    out_hits:      *mut HitResult3D,
    max_hits:      i32,
    cell_size:     f32,
    out_hit_count: *mut i32,
) {
    let zero_out = |p: *mut i32| { if !p.is_null() { unsafe { *p = 0; } } };

    if projs.is_null() || targets.is_null() || out_hits.is_null() {
        zero_out(out_hit_count);
        return;
    }
    let projs_s   = slice::from_raw_parts(projs,    proj_count   as usize);
    let targets_s = slice::from_raw_parts(targets,  target_count as usize);
    let hits_s    = slice::from_raw_parts_mut(out_hits, max_hits as usize);

    let count = collision::check_hits_3d(projs_s, targets_s, hits_s, cell_size);

    if !out_hit_count.is_null() { *out_hit_count = count as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Spawn / patterns — 2D (unchanged)
// ─────────────────────────────────────────────────────────────────────────────

/// Write up to `max_out` new NativeProjectiles using pattern math.
/// C# overwrites Lifetime, MovementType, Scale etc. after this returns.
#[no_mangle]
pub unsafe extern "C" fn spawn_pattern(
    req:       *const SpawnRequest,
    out_projs: *mut NativeProjectile,
    max_out:   i32,
    out_count: *mut i32,
) {
    if req.is_null() || out_projs.is_null() {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let req_ref = &*req;
    let out_s   = slice::from_raw_parts_mut(out_projs, max_out as usize);
    let count   = patterns::generate(req_ref, out_s);
    if !out_count.is_null() { *out_count = count as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Spawn batch (new) — both 2D and 3D
//
//  Collapses the 928µs-per-call FFI overhead into a single call.
//  C# or Burst fills the input array (possibly in parallel for 8+ spawns),
//  then calls spawn_batch ONCE to copy all structs to the sim buffer.
//  No pattern math — that responsibility belongs to BatchSpawnHelper.cs.
// ─────────────────────────────────────────────────────────────────────────────

/// Copy a pre-filled 2D projectile array into the output (sim) buffer.
/// projs_in  — temp array filled by C# or Burst with ready-to-simulate structs
/// projs_out — pointer to the current end of the sim buffer (offset by active count)
/// max_out   — remaining capacity in the sim buffer
/// out_count — how many were written; C# adds this to its active count
#[no_mangle]
pub unsafe extern "C" fn spawn_batch(
    projs_in:  *const NativeProjectile,
    count:     i32,
    projs_out: *mut NativeProjectile,
    max_out:   i32,
    out_count: *mut i32,
) {
    if projs_in.is_null() || projs_out.is_null() || count <= 0 {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let n   = (count as usize).min(max_out as usize);
    let src = slice::from_raw_parts(projs_in, n);
    let dst = slice::from_raw_parts_mut(projs_out, n);
    dst.copy_from_slice(src);
    if !out_count.is_null() { *out_count = n as i32; }
}

/// Copy a pre-filled 3D projectile array into the output (sim) buffer.
#[no_mangle]
pub unsafe extern "C" fn spawn_batch_3d(
    projs_in:  *const NativeProjectile3D,
    count:     i32,
    projs_out: *mut NativeProjectile3D,
    max_out:   i32,
    out_count: *mut i32,
) {
    if projs_in.is_null() || projs_out.is_null() || count <= 0 {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let n   = (count as usize).min(max_out as usize);
    let src = slice::from_raw_parts(projs_in, n);
    let dst = slice::from_raw_parts_mut(projs_out, n);
    dst.copy_from_slice(src);
    if !out_count.is_null() { *out_count = n as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  State save / restore (unchanged)
// ─────────────────────────────────────────────────────────────────────────────

/// Memcpy the entire 2D projectile array into `buf`.
/// Required buf size = count * 72.
#[no_mangle]
pub unsafe extern "C" fn save_state(
    projs:   *const NativeProjectile,
    count:   i32,
    buf:     *mut u8,
    buf_len: i32,
) -> i32 {
    if projs.is_null() || buf.is_null() { return 0; }
    let slice = slice::from_raw_parts(projs, count as usize);
    state::save(slice, buf, buf_len as usize) as i32
}

/// Restore 2D projectile state from a previously saved buffer.
#[no_mangle]
pub unsafe extern "C" fn restore_state(
    out_projs:  *mut NativeProjectile,
    max_count:  i32,
    buf:        *const u8,
    buf_len:    i32,
    out_count:  *mut i32,
) {
    if out_projs.is_null() || buf.is_null() {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let out_s = slice::from_raw_parts_mut(out_projs, max_count as usize);
    let n     = state::restore(out_s, buf, buf_len as usize);
    if !out_count.is_null() { *out_count = n as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Debug / layout validation
//  C# calls these on startup to verify struct sizes match.
//  A mismatch = silent memory corruption on every P/Invoke call.
// ─────────────────────────────────────────────────────────────────────────────

/// sizeof(NativeProjectile) — C# expects 72.
#[no_mangle]
pub extern "C" fn projectile_struct_size() -> i32 {
    core::mem::size_of::<NativeProjectile>() as i32
}

/// sizeof(HitResult) — C# expects 24.
#[no_mangle]
pub extern "C" fn hit_result_struct_size() -> i32 {
    core::mem::size_of::<HitResult>() as i32
}

/// sizeof(CollisionTarget) — C# expects 20.
#[no_mangle]
pub extern "C" fn collision_target_struct_size() -> i32 {
    core::mem::size_of::<CollisionTarget>() as i32
}

/// sizeof(SpawnRequest) — C# expects 32.
#[no_mangle]
pub extern "C" fn spawn_request_struct_size() -> i32 {
    core::mem::size_of::<SpawnRequest>() as i32
}

/// sizeof(NativeProjectile3D) — C# expects 84.
#[no_mangle]
pub extern "C" fn projectile3d_struct_size() -> i32 {
    core::mem::size_of::<NativeProjectile3D>() as i32
}

/// sizeof(HitResult3D) — C# expects 28.
#[no_mangle]
pub extern "C" fn hit_result3d_struct_size() -> i32 {
    core::mem::size_of::<HitResult3D>() as i32
}

/// sizeof(CollisionTarget3D) — C# expects 24.
#[no_mangle]
pub extern "C" fn collision_target3d_struct_size() -> i32 {
    core::mem::size_of::<CollisionTarget3D>() as i32
}

// ─────────────────────────────────────────────────────────────────────────────
//  Compile-time layout assertions — catch mismatches at build time.
// ─────────────────────────────────────────────────────────────────────────────

const _: () = assert!(core::mem::size_of::<NativeProjectile>()   == 72);
const _: () = assert!(core::mem::size_of::<HitResult>()          == 24);
const _: () = assert!(core::mem::size_of::<CollisionTarget>()     == 20);
const _: () = assert!(core::mem::size_of::<SpawnRequest>()        == 32);
const _: () = assert!(core::mem::size_of::<NativeProjectile3D>()  == 84);
const _: () = assert!(core::mem::size_of::<HitResult3D>()         == 28);
const _: () = assert!(core::mem::size_of::<CollisionTarget3D>()   == 24);
