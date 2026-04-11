// simulation.rs — per-projectile physics, one movement type at a time

use crate::NativeProjectile;

// Movement type constants — must match C# enum
const MOVE_STRAIGHT: u8 = 0;
const MOVE_ARCHING:  u8 = 1;
const MOVE_GUIDED:   u8 = 2;
const MOVE_TELEPORT: u8 = 3;

/// Tick all alive projectiles. Returns number that died this tick.
pub fn tick_all(projs: &mut [NativeProjectile], dt: f32) -> i32 {
    let mut died = 0i32;
    for p in projs.iter_mut() {
        if p.alive == 0 { continue; }

        // Lifetime
        p.lifetime -= dt;
        if p.lifetime <= 0.0 {
            p.alive = 0;
            died += 1;
            continue;
        }

        // Movement
        match p.movement_type {
            MOVE_STRAIGHT => tick_straight(p, dt),
            MOVE_ARCHING  => tick_arching(p, dt),
            MOVE_GUIDED   => tick_guided(p, dt),
            MOVE_TELEPORT => tick_teleport(p, dt),
            _             => tick_straight(p, dt),
        }

        // Scale (growth / shrink)
        tick_scale(p, dt);

        // Angle — always face velocity direction for non-teleport types
        if p.movement_type != MOVE_TELEPORT {
            if p.vx != 0.0 || p.vy != 0.0 {
                p.angle_deg = p.vy.atan2(p.vx).to_degrees();
            }
        }

        // Accumulate travel distance
        let dx = p.vx * dt;
        let dy = p.vy * dt;
        p.travel_dist += (dx * dx + dy * dy).sqrt();
    }
    died
}

// ─── Movement implementations ───────────────────────────────────────────────

/// Straight: constant velocity, apply any lateral acceleration (for slight curves)
fn tick_straight(p: &mut NativeProjectile, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
}

/// Arching: gravity-affected, like an arrow or grenade
fn tick_arching(p: &mut NativeProjectile, dt: f32) {
    // ay acts as gravity (set negative for downward pull)
    p.vy += p.ay * dt;
    p.vx += p.ax * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
    // curve_t tracks arc phase — used by C# to know when to start trail fade
    p.curve_t += dt;
}

/// Guided: homing — steers toward a direction stored in (ax, ay) as a unit vector.
/// C# updates (ax, ay) each frame from target position.
/// The projectile turns toward that direction at a fixed turn rate.
fn tick_guided(p: &mut NativeProjectile, dt: f32) {
    // ax, ay = desired direction (unit vector), set by C# from target
    let turn_rate = 180.0f32.to_radians() * dt; // 180 deg/sec max turn
    let cur_angle = p.vy.atan2(p.vx);
    let tgt_angle = p.ay.atan2(p.ax);

    // shortest rotation delta
    let mut delta = tgt_angle - cur_angle;
    if delta >  std::f32::consts::PI { delta -= std::f32::consts::TAU; }
    if delta < -std::f32::consts::PI { delta += std::f32::consts::TAU; }
    let delta = delta.clamp(-turn_rate, turn_rate);

    let new_angle = cur_angle + delta;
    let speed = (p.vx * p.vx + p.vy * p.vy).sqrt();
    p.vx = new_angle.cos() * speed;
    p.vy = new_angle.sin() * speed;
    p.x += p.vx * dt;
    p.y += p.vy * dt;
}

/// Teleport: jumps a fixed distance each tick (like a phased projectile).
/// curve_t holds the interval timer.
fn tick_teleport(p: &mut NativeProjectile, dt: f32) {
    let interval = 0.12f32; // seconds between jumps
    p.curve_t += dt;
    if p.curve_t >= interval {
        p.curve_t -= interval;
        // jump forward by one interval's worth of travel
        let jump_dist = interval * (p.vx * p.vx + p.vy * p.vy).sqrt();
        let len = (p.vx * p.vx + p.vy * p.vy).sqrt().max(0.0001);
        p.x += (p.vx / len) * jump_dist;
        p.y += (p.vy / len) * jump_dist;
        p.travel_dist += jump_dist;
    }
    // angle stays fixed for teleporter (it doesn't rotate visually)
}

// ─── Scale ──────────────────────────────────────────────────────────────────

fn tick_scale(p: &mut NativeProjectile, dt: f32) {
    let diff = p.scale_target - p.scale_x;
    if diff.abs() > 0.001 {
        let step = diff * p.scale_speed * dt;
        p.scale_x += step;
        p.scale_y  = p.scale_x; // uniform scale; split later if needed
    }
          }
