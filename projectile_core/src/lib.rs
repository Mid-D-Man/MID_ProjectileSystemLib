// Add at top with other mod declarations:
mod config_store;
pub use config_store::*;

// Add these movement type constants (pub so C# validation can check them):
pub const MOVEMENT_STRAIGHT: u8 = simulation::MOVE_STRAIGHT;
pub const MOVEMENT_ARCHING:  u8 = simulation::MOVE_ARCHING;
pub const MOVEMENT_GUIDED:   u8 = simulation::MOVE_GUIDED;
pub const MOVEMENT_TELEPORT: u8 = simulation::MOVE_TELEPORT;
pub const MOVEMENT_WAVE:     u8 = simulation::MOVE_WAVE;
pub const MOVEMENT_CIRCULAR: u8 = simulation::MOVE_CIRCULAR;

// ─────────────────────────────────────────────────────────────────────────────
//  Movement parameter registration — called from C# at startup
// ─────────────────────────────────────────────────────────────────────────────

/// Register sine/cosine wave movement parameters for a config ID.
/// Call once per wave-type config at game startup, before any projectiles spawn.
/// amplitude     — lateral displacement in world units
/// frequency     — oscillations per second
/// phase_offset  — starting phase in radians (use for multi-pellet spread variety)
/// vertical      — 1 = oscillate vertically, 0 = oscillate horizontally
#[no_mangle]
pub extern "C" fn register_wave_params(
    config_id:    u16,
    amplitude:    f32,
    frequency:    f32,
    phase_offset: f32,
    vertical:     u8,
) {
    config_store::register_wave(config_id, config_store::WaveParams {
        amplitude,
        frequency,
        phase_offset,
        vertical:  vertical != 0,
        _pad: [0u8; 3],
    });
}

/// Register circular/helical orbit parameters for a config ID.
/// radius         — orbit radius in world units
/// angular_speed  — degrees per second (positive = CCW, negative = CW)
/// start_angle    — starting angle in degrees
#[no_mangle]
pub extern "C" fn register_circular_params(
    config_id:   u16,
    radius:      f32,
    angular_speed: f32,
    start_angle: f32,
) {
    config_store::register_circular(config_id, config_store::CircularParams {
        radius,
        angular_speed,
        start_angle_deg: start_angle,
    });
}

/// Unregister wave params for a config ID (e.g. on scene unload).
#[no_mangle]
pub extern "C" fn unregister_wave_params(config_id: u16) {
    config_store::unregister_wave(config_id);
}

/// Unregister circular params for a config ID.
#[no_mangle]
pub extern "C" fn unregister_circular_params(config_id: u16) {
    config_store::unregister_circular(config_id);
}

/// Clear all registered movement params. Call on full system shutdown.
#[no_mangle]
pub extern "C" fn clear_movement_params() {
    config_store::clear_all();
}
