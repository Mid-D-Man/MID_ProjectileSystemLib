// collision.rs — spatial grid broad-phase, circle-circle narrow-phase
//
// STRATEGY: Index targets (few, <=128) into a spatial hash grid.
//           For each alive projectile (many, <=2048) look up only the
//           1-4 cells it touches and narrow-phase against those targets.
//
// Complexity: O(P * k)  where k = avg targets per projectile neighbourhood.
//             At 2048 projs x 64 targets in a 200-unit world, the grid
//             typically reduces checks by 40-60x vs the old O(P*T) scan.
//
// No heap allocation in the hot path: the grid uses a fixed inline bucket
// array (GRID_BUCKETS slots, open-addressing) so the function is alloc-free.

use crate::{NativeProjectile, CollisionTarget, HitResult};

// ── Grid tunables ─────────────────────────────────────────────────────────────

/// Number of hash buckets. MUST be a power of two.
const GRID_BUCKETS: usize = 256;

/// Sentinel meaning "empty bucket".
const EMPTY: u32 = u32::MAX;

/// Max target entries stored per bucket before the cell silently overflows.
/// With 64 targets and 256 buckets this is almost never > 2 in practice.
/// Increase if you run many tightly-clustered targets.
const BUCKET_ENTRIES: usize = 8;

// ── Inline open-addressing spatial hash map ───────────────────────────────────
//
// Maps packed (cell_x, cell_y) -> list of target indices (stored as u8).
// Avoids Vec / HashMap and keeps the struct on the stack (~6 KB at these sizes).

struct CellGrid {
    keys:    [u32; GRID_BUCKETS],
    counts:  [u8;  GRID_BUCKETS],
    entries: [[u8; BUCKET_ENTRIES]; GRID_BUCKETS],
}

impl CellGrid {
    #[inline(always)]
    fn new() -> Self {
        // SAFETY: all-zeros is valid for [u32; N], [u8; N], [[u8; N]; M].
        // Using unsafe zeroed() instead of Default::default() avoids the
        // compiler emitting a slow 6 KB memcpy from a static init table.
        unsafe { core::mem::zeroed::<Self>() }.with_empty_keys()
    }

    // Separate step so zeroed() stays a single call.
    fn with_empty_keys(mut self) -> Self {
        for k in self.keys.iter_mut() { *k = EMPTY; }
        self
    }

    /// Pack (cx, cy) pair into a u32 key.
    /// Values are clamped to i16 range — cells beyond +/-32 767 at
    /// CELL_SIZE=4 alias, acceptable for typical game-scale maps.
    #[inline(always)]
    fn pack(cx: i32, cy: i32) -> u32 {
        let x = cx.clamp(-32768, 32767) as u16 as u32;
        let y = cy.clamp(-32768, 32767) as u16 as u32;
        (x << 16) | y
    }

    #[inline(always)]
    fn hash(key: u32) -> usize {
        // Fibonacci hashing — good distribution for low-entropy spatial keys.
        (key.wrapping_mul(0x9e37_79b9) as usize) & (GRID_BUCKETS - 1)
    }

    /// Insert `target_idx` into the bucket for cell (cx, cy).
    /// If the bucket is full the insertion is silently dropped — increase
    /// BUCKET_ENTRIES or GRID_BUCKETS if this becomes a problem.
    fn insert(&mut self, cx: i32, cy: i32, target_idx: usize) {
        debug_assert!(target_idx < 255, "target index overflows u8 — raise BUCKET_ENTRIES guard");
        if target_idx > 254 { return; }   // guard: stored as u8

        let key  = Self::pack(cx, cy);
        let mut slot = Self::hash(key);

        for _ in 0..GRID_BUCKETS {
            if self.keys[slot] == EMPTY {
                // Claim empty slot for this cell.
                self.keys[slot]   = key;
                self.counts[slot] = 0;
            }

            if self.keys[slot] == key {
                let c = self.counts[slot] as usize;
                if c < BUCKET_ENTRIES {
                    self.entries[slot][c] = target_idx as u8;
                    self.counts[slot]     = (c + 1) as u8;
                }
                return;
            }

            // Hash collision — probe next slot.
            slot = (slot + 1) & (GRID_BUCKETS - 1);
        }
        // Grid completely full — degrade gracefully.
    }

    /// Return stored target indices for cell (cx, cy).
    #[inline(always)]
    fn query(&self, cx: i32, cy: i32) -> &[u8] {
        let key  = Self::pack(cx, cy);
        let mut slot = Self::hash(key);

        for _ in 0..GRID_BUCKETS {
            if self.keys[slot] == EMPTY  { return &[]; }
            if self.keys[slot] == key    {
                let c = self.counts[slot] as usize;
                return &self.entries[slot][..c];
            }
            slot = (slot + 1) & (GRID_BUCKETS - 1);
        }
        &[]
    }
}

// ── Public API ────────────────────────────────────────────────────────────────

/// Spatial-grid projectile-target collision check.
///
/// `cell_size` — world units per grid cell.  Pass 0.0 to use the default (4.0).
///   Rule of thumb: set to 2x the largest target radius.  Too small = many cells
///   per target, more inserts.  Too large = many targets per cell, more narrow checks.
pub fn check_hits(
    projs:     &[NativeProjectile],
    targets:   &[CollisionTarget],
    out:       &mut [HitResult],
    cell_size: f32,
) -> usize {
    let cell     = if cell_size > 0.0 { cell_size } else { 4.0 };
    let inv      = 1.0 / cell;
    let max_hits = out.len();

    if targets.is_empty() || projs.is_empty() || max_hits == 0 {
        return 0;
    }

    // ── Phase 1: insert active targets into the grid ──────────────────────────
    // Each target is inserted into every cell it overlaps.
    // A target with radius 1.0 and cell_size 4.0 typically touches 1 cell.
    // Worst case (radius >= cell_size) is 4 cells for a 2D circle.

    let mut grid = CellGrid::new();

    for (ti, t) in targets.iter().enumerate() {
        if t.active == 0 { continue; }

        let min_cx = ((t.x - t.radius) * inv).floor() as i32;
        let max_cx = ((t.x + t.radius) * inv).floor() as i32;
        let min_cy = ((t.y - t.radius) * inv).floor() as i32;
        let max_cy = ((t.y + t.radius) * inv).floor() as i32;

        for cx in min_cx..=max_cx {
            for cy in min_cy..=max_cy {
                grid.insert(cx, cy, ti);
            }
        }
    }

    // ── Phase 2: for each alive projectile, query its cells ───────────────────
    // Typical projectile radius is 0.05-0.15 world units, so it almost always
    // falls within a single cell (min_cx == max_cx and min_cy == max_cy).

    let mut hit_count = 0usize;

    'proj: for (pi, p) in projs.iter().enumerate() {
        if p.alive == 0     { continue; }
        if hit_count >= max_hits { break;    }

        let proj_r = p.scale_x * 0.5;
        let min_cx = ((p.x - proj_r) * inv).floor() as i32;
        let max_cx = ((p.x + proj_r) * inv).floor() as i32;
        let min_cy = ((p.y - proj_r) * inv).floor() as i32;
        let max_cy = ((p.y + proj_r) * inv).floor() as i32;

        for cx in min_cx..=max_cx {
            for cy in min_cy..=max_cy {
                for &ti_u8 in grid.query(cx, cy) {
                    // SAFETY: target_idx was bounds-checked on insert (<= 254).
                    let t = unsafe { targets.get_unchecked(ti_u8 as usize) };

                    let dx       = p.x - t.x;
                    let dy       = p.y - t.y;
                    let combined = proj_r + t.radius;

                    if dx * dx + dy * dy <= combined * combined {
                        out[hit_count] = HitResult {
                            proj_id:     p.proj_id,
                            proj_index:  pi as u32,
                            target_id:   t.target_id,
                            travel_dist: p.travel_dist,
                            hit_x:       p.x,
                            hit_y:       p.y,
                        };
                        hit_count += 1;
                        // One hit per projectile per tick.
                        // Piercing logic lives in C# — it can call us again
                        // with the hit projectile's alive flag cleared.
                        continue 'proj;
                    }
                }
            }
        }
    }

    hit_count
}

// ── Performance notes ─────────────────────────────────────────────────────────
//
// Benchmark targets at 2048 projs x 64 targets:
//   Old O(P*T) brute-force : ~450 us/check  (131 072 distance tests)
//   Grid O(P*k)            : target <50 us  (~2048 x 1-3 tests at 4.0 cell)
//
// Tuning guide:
//   - cell_size = 4.0 fits most 2D games with target radii of 0.5-2.0 units.
//   - If steady-state target count exceeds 128, raise GRID_BUCKETS to 512.
//   - If targets cluster (e.g. dense enemy groups), lower cell_size to 2.0
//     so they spread across more buckets.
//
// 3D extension:
//   - Add z / vz fields to NativeProjectile.
//   - Change distance test to: dx*dx + dy*dy + dz*dz <= combined*combined
//   - Change pack() to combine cx/cy/cz — store in u64, adjust hash.
//   - GRID_BUCKETS may need to increase (more spatial spread in 3D).
