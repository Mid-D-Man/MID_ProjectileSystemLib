// config_store.rs — Rust-side movement parameter store
//
// Stores per-config-type movement parameters indexed by config_id (u16).
// This is the ONLY data stored Rust-side by config_id — not a general config store.
//
// Why this exists:
//   Wave and circular movement need per-type constants (amplitude, frequency, radius,
//   angular_speed) read EVERY TICK inside the hot loop. Storing them per-projectile
//   would add 8-16 bytes to every NativeProjectile, bloating cache lines for all
//   projectiles to serve the subset that use wave/circular movement.
//
//   Registering once at startup and looking up by config_id costs one HashMap access
//   per tick for wave/circular projectiles only. Straight/arching/guided/teleport
//   projectiles never touch this store.
//
// Thread safety:
//   Registration happens on the main thread at startup before any simulation runs.
//   Tick reads are read-only after registration. No locking needed in practice.
//   Using std::sync::RwLock for correctness if Unity ever calls tick from a job thread.

use std::collections::HashMap;
use std::sync::RwLock;

// ── Wave movement parameters ──────────────────────────────────────────────────

/// Parameters for sine/cosine lateral wave movement.
/// Applied in tick_wave(): projectile travels forward while oscillating perpendicular.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct WaveParams {
    /// Lateral displacement amplitude in world units.
    /// 0.0 = no oscillation (degenerate straight line — don't register this).
    pub amplitude: f32,

    /// Oscillation frequency in cycles per second.
    pub frequency: f32,

    /// Phase offset in radians. Allows multiple wave projectiles from the same
    /// weapon to be out of phase (e.g. helical spread pattern).
    pub phase_offset: f32,

    /// If true, oscillates on Y axis (vertical wave). If false, oscillates on X axis
    /// (horizontal wave relative to travel direction). For 3D, the perpendicular
    /// is computed from velocity cross world-up.
    pub vertical: bool,

    pub _pad: [u8; 3],
}

// ── Circular movement parameters ──────────────────────────────────────────────

/// Parameters for circular orbit movement.
/// The projectile orbits around its own travel axis while moving forward.
/// This produces a helical/corkscrew path.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct CircularParams {
    /// Radius of the circular orbit in world units.
    pub radius: f32,

    /// Angular speed of the orbit in degrees per second.
    /// Positive = counter-clockwise, negative = clockwise.
    pub angular_speed: f32,

    /// Starting angle of the orbit in degrees.
    /// Allows multiple projectiles to start at different positions on the orbit.
    pub start_angle_deg: f32,
}

// ── Global stores ─────────────────────────────────────────────────────────────

lazy_static::lazy_static! {
    static ref WAVE_PARAMS: RwLock<HashMap<u16, WaveParams>> =
        RwLock::new(HashMap::new());

    static ref CIRCULAR_PARAMS: RwLock<HashMap<u16, CircularParams>> =
        RwLock::new(HashMap::new());
}

// ── Registration (called from C# at startup) ──────────────────────────────────

pub fn register_wave(config_id: u16, params: WaveParams) {
    if let Ok(mut map) = WAVE_PARAMS.write() {
        map.insert(config_id, params);
    }
}

pub fn register_circular(config_id: u16, params: CircularParams) {
    if let Ok(mut map) = CIRCULAR_PARAMS.write() {
        map.insert(config_id, params);
    }
}

pub fn unregister_wave(config_id: u16) {
    if let Ok(mut map) = WAVE_PARAMS.write() {
        map.remove(&config_id);
    }
}

pub fn unregister_circular(config_id: u16) {
    if let Ok(mut map) = CIRCULAR_PARAMS.write() {
        map.remove(&config_id);
    }
}

pub fn clear_all() {
    if let Ok(mut map) = WAVE_PARAMS.write()     { map.clear(); }
    if let Ok(mut map) = CIRCULAR_PARAMS.write() { map.clear(); }
}

// ── Tick-time lookups (called from simulation.rs hot loop) ────────────────────

/// Returns WaveParams for the given config_id, or None if not registered.
/// Called only for projectiles with movement_type == MOVE_WAVE.
#[inline(always)]
pub fn get_wave(config_id: u16) -> Option<WaveParams> {
    WAVE_PARAMS.read().ok()?.get(&config_id).copied()
}

/// Returns CircularParams for the given config_id, or None if not registered.
/// Called only for projectiles with movement_type == MOVE_CIRCULAR.
#[inline(always)]
pub fn get_circular(config_id: u16) -> Option<CircularParams> {
    CIRCULAR_PARAMS.read().ok()?.get(&config_id).copied()
}
