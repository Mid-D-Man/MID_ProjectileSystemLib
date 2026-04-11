// patterns.rs — deterministic pattern generators
// Same seed + same request = same projectiles on all clients

use crate::{NativeProjectile, SpawnRequest};

// Pattern IDs — must match PatternId enum in C#
const PAT_SINGLE:   u8 = 0;
const PAT_SPREAD3:  u8 = 1;
const PAT_SPREAD5:  u8 = 2;
const PAT_SPIRAL:   u8 = 3;
const PAT_RING8:    u8 = 4;

pub fn generate(req: &SpawnRequest, out: &mut [NativeProjectile]) -> usize {
    match req.pattern_id {
        PAT_SINGLE  => gen_spread(req, out, 1, 0.0),
        PAT_SPREAD3 => gen_spread(req, out, 3, 20.0),
        PAT_SPREAD5 => gen_spread(req, out, 5, 15.0),
        PAT_SPIRAL  => gen_ring(req, out, 12),
        PAT_RING8   => gen_ring(req, out, 8),
        _           => gen_spread(req, out, 1, 0.0),
    }
}

/// Spread: `count` bullets fanned around `base_angle`, separated by `spread_deg`
fn gen_spread(
    req: &SpawnRequest,
    out: &mut [NativeProjectile],
    count: usize,
    spread_deg: f32,
) -> usize {
    let n = count.min(out.len());
    let half = (n as f32 - 1.0) * 0.5;
    let speed_variance = lcg_f32(req.rng_seed) * 0.1 + 0.95; // 0.95-1.05

    for i in 0..n {
        let offset_deg = (i as f32 - half) * spread_deg;
        let angle = (req.angle_deg + offset_deg).to_radians();
        let speed = req.speed * if i == 0 { 1.0 } else {
            let s = lcg_f32(req.rng_seed.wrapping_add(i as u32));
            s * 0.1 + 0.95
        };
        out[i] = make_projectile(req, angle, speed, i);
    }
    n
}

/// Ring: `count` bullets evenly distributed in a full circle
fn gen_ring(
    req: &SpawnRequest,
    out: &mut [NativeProjectile],
    count: usize,
) -> usize {
    let n = count.min(out.len());
    let step = std::f32::consts::TAU / n as f32;

    for i in 0..n {
        let angle = req.angle_deg.to_radians() + step * i as f32;
        out[i] = make_projectile(req, angle, req.speed, i);
    }
    n
}

/// Build a single NativeProjectile from a spawn request
fn make_projectile(
    req: &SpawnRequest,
    angle_rad: f32,
    speed: f32,
    index: usize,
) -> NativeProjectile {
    NativeProjectile {
        x: req.origin_x,
        y: req.origin_y,
        vx: angle_rad.cos() * speed,
        vy: angle_rad.sin() * speed,
        ax: 0.0,
        ay: 0.0,
        angle_deg: angle_rad.to_degrees(),
        curve_t: 0.0,

        scale_x: 0.3,         // start small
        scale_y: 0.3,
        scale_target: 1.0,    // grow to full size
        scale_speed: 8.0,     // fast growth

        lifetime: 0.0,        // C# fills from config after generate()
        max_lifetime: 0.0,    // C# fills
        travel_dist: 0.0,

        config_id: req.config_id,
        owner_id: req.owner_id,
        proj_id: req.base_proj_id.wrapping_add(index as u32),

        collision_count: 0,
        movement_type: 0,     // C# fills from config
        piercing_type: 0,     // C# fills from config
        alive: 1,
    }
}

/// Minimal LCG — deterministic pseudo-random f32 in [0, 1)
/// Good enough for speed variance, NOT for anything security-sensitive
fn lcg_f32(seed: u32) -> f32 {
    let s = seed.wrapping_mul(1664525).wrapping_add(1013904223);
    (s >> 8) as f32 / 16777216.0
}
