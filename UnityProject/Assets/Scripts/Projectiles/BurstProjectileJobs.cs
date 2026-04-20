// BurstProjectileJobs.cs
// All Burst-compiled jobs used by BurstProjectileSimulation and ProjectileBenchmark.
// Keep jobs here so both systems reference the same compiled code — Burst only
// compiles each unique job struct once regardless of how many callers exist.
//
// Jobs in this file:
//   BurstTickJob         — IJobParallelFor, straight + arching + guided movement
//   BurstCollisionJob    — IJob, spatial-grid broadphase (mirrors collision.rs)
//   BurstSpawnJob        — IJobParallelFor, pattern-free direct spawn
//   BurstCompactJob      — IJob, swap-compact dead slots, writes new active count
//
// Requires: com.unity.burst, com.unity.collections, com.unity.mathematics

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Tick — parallel per-projectile update
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile(CompileSynchronously = false, FloatMode = FloatMode.Fast)]
    public struct BurstTickJob : IJobParallelFor
    {
        public NativeArray<NativeProjectile> Projectiles;
        public float DeltaTime;

        // Movement type constants — must match simulation.rs
        const byte STRAIGHT  = 0;
        const byte ARCHING   = 1;
        const byte GUIDED    = 2;
        const byte TELEPORT  = 3;

        const float TURN_RATE_RAD = math.PI; // 180 deg/s guided turn limit

        public void Execute(int i)
        {
            var p = Projectiles[i];
            if (p.Alive == 0) return;

            p.Lifetime -= DeltaTime;
            if (p.Lifetime <= 0f)
            {
                p.Alive = 0;
                Projectiles[i] = p;
                return;
            }

            switch (p.MovementType)
            {
                case STRAIGHT: TickStraight(ref p); break;
                case ARCHING:  TickArching(ref p);  break;
                case GUIDED:   TickGuided(ref p);   break;
                case TELEPORT: TickTeleport(ref p); break;
                default:       TickStraight(ref p); break;
            }

            // Scale lerp — only runs when C# set ScaleSpeed > 0 on spawn.
            if (p.ScaleSpeed > 0f)
            {
                float diff = p.ScaleTarget - p.ScaleX;
                if (math.abs(diff) > 0.001f)
                {
                    p.ScaleX += diff * p.ScaleSpeed * DeltaTime;
                    p.ScaleY  = p.ScaleX;
                }
            }

            // Angle from velocity — skip for teleport (angle is set per-jump).
            if (p.MovementType != TELEPORT && (p.Vx != 0f || p.Vy != 0f))
                p.AngleDeg = math.degrees(math.atan2(p.Vy, p.Vx));

            // Travel distance accumulation.
            float dx = p.Vx * DeltaTime, dy = p.Vy * DeltaTime;
            p.TravelDist += math.sqrt(dx * dx + dy * dy);

            Projectiles[i] = p;
        }

        void TickStraight(ref NativeProjectile p)
        {
            p.Vx += p.Ax * DeltaTime;
            p.Vy += p.Ay * DeltaTime;
            p.X  += p.Vx * DeltaTime;
            p.Y  += p.Vy * DeltaTime;
        }

        void TickArching(ref NativeProjectile p)
        {
            p.Vy     += p.Ay * DeltaTime;
            p.Vx     += p.Ax * DeltaTime;
            p.X      += p.Vx * DeltaTime;
            p.Y      += p.Vy * DeltaTime;
            p.CurveT += DeltaTime;
        }

        void TickGuided(ref NativeProjectile p)
        {
            float turnRate  = TURN_RATE_RAD * DeltaTime;
            float curAngle  = math.atan2(p.Vy, p.Vx);
            float tgtAngle  = math.atan2(p.Ay, p.Ax);

            float delta = tgtAngle - curAngle;
            // Wrap to [-PI, PI]
            if (delta >  math.PI) delta -= math.TAU;
            if (delta < -math.PI) delta += math.TAU;
            delta = math.clamp(delta, -turnRate, turnRate);

            float newAngle = curAngle + delta;
            float speed    = math.sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            p.Vx  = math.cos(newAngle) * speed;
            p.Vy  = math.sin(newAngle) * speed;
            p.X  += p.Vx * DeltaTime;
            p.Y  += p.Vy * DeltaTime;
        }

        void TickTeleport(ref NativeProjectile p)
        {
            const float interval = 0.12f;
            p.CurveT += DeltaTime;
            if (p.CurveT >= interval)
            {
                p.CurveT -= interval;
                float speed     = math.sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
                float jumpDist  = interval * speed;
                float len       = math.max(speed, 0.0001f);
                p.X += (p.Vx / len) * jumpDist;
                p.Y += (p.Vy / len) * jumpDist;
                p.TravelDist += jumpDist;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Collision — spatial grid, mirrors collision.rs implementation
    //
    //  Indexed grid over targets (few) — for each projectile query the 1-4
    //  cells it touches.  Allocation-free: uses a fixed NativeArray for the
    //  hash table rather than a Dictionary.
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile(CompileSynchronously = false)]
    public struct BurstCollisionJob : IJob
    {
        [ReadOnly]  public NativeArray<NativeProjectile> Projectiles;
        [ReadOnly]  public NativeArray<CollisionTarget>  Targets;
        [WriteOnly] public NativeArray<HitResult>        OutHits;
        public NativeArray<int> HitCountOut; // length 1

        // Grid config — must match what the calling manager passes.
        public float CellSize;

        // Inline flat hash table: 256 buckets, 8 entries each.
        // Stored as parallel flat arrays in a NativeArray<int> blob.
        // Caller allocates GridBuffer = new NativeArray<int>(GRID_INTS, Allocator.TempJob).
        public NativeArray<int> GridBuffer; // size = GRID_TOTAL_INTS

        // Layout constants — must match BurstProjectileSimulation.GridBufferSize.
        internal const int GRID_BUCKETS  = 256;
        internal const int BUCKET_SLOTS  = 8;
        internal const int GRID_TOTAL_INTS =
            GRID_BUCKETS             // keys
          + GRID_BUCKETS             // counts
          + GRID_BUCKETS * BUCKET_SLOTS; // entries

        // Offsets within GridBuffer.
        const int KEY_OFFSET   = 0;
        const int CNT_OFFSET   = GRID_BUCKETS;
        const int ENT_OFFSET   = GRID_BUCKETS * 2;

        const int EMPTY = unchecked((int)0xFFFF_FFFF);

        public void Execute()
        {
            float cell = CellSize > 0f ? CellSize : 4f;
            float inv  = 1f / cell;

            // ── Build grid ────────────────────────────────────────────────────
            // Clear keys to EMPTY (counts and entries default to 0 via Allocator.TempJob).
            for (int b = 0; b < GRID_BUCKETS; b++)
                GridBuffer[KEY_OFFSET + b] = EMPTY;

            for (int ti = 0; ti < Targets.Length; ti++)
            {
                var t = Targets[ti];
                if (t.Active == 0) continue;

                int minCx = FloorToInt((t.X - t.Radius) * inv);
                int maxCx = FloorToInt((t.X + t.Radius) * inv);
                int minCy = FloorToInt((t.Y - t.Radius) * inv);
                int maxCy = FloorToInt((t.Y + t.Radius) * inv);

                for (int cx = minCx; cx <= maxCx; cx++)
                for (int cy = minCy; cy <= maxCy; cy++)
                    GridInsert(cx, cy, ti);
            }

            // ── Query grid per projectile ─────────────────────────────────────
            int hits    = 0;
            int maxHits = OutHits.Length;

            for (int pi = 0; pi < Projectiles.Length && hits < maxHits; pi++)
            {
                var p = Projectiles[pi];
                if (p.Alive == 0) continue;

                float projR  = p.ScaleX * 0.5f;
                int minCx    = FloorToInt((p.X - projR) * inv);
                int maxCx    = FloorToInt((p.X + projR) * inv);
                int minCy    = FloorToInt((p.Y - projR) * inv);
                int maxCy    = FloorToInt((p.Y + projR) * inv);

                bool hit = false;
                for (int cx = minCx; cx <= maxCx && !hit; cx++)
                for (int cy = minCy; cy <= maxCy && !hit; cy++)
                {
                    int slot  = GridFind(cx, cy);
                    if (slot < 0) continue;
                    int count = GridBuffer[CNT_OFFSET + slot];

                    for (int e = 0; e < count && !hit; e++)
                    {
                        int ti    = GridBuffer[ENT_OFFSET + slot * BUCKET_SLOTS + e];
                        var t     = Targets[ti];
                        float dx  = p.X - t.X;
                        float dy  = p.Y - t.Y;
                        float cr  = projR + t.Radius;

                        if (dx * dx + dy * dy <= cr * cr)
                        {
                            OutHits[hits++] = new HitResult
                            {
                                ProjId      = p.ProjId,
                                ProjIndex   = (uint)pi,
                                TargetId    = t.TargetId,
                                TravelDist  = p.TravelDist,
                                HitX        = p.X,
                                HitY        = p.Y,
                            };
                            hit = true;
                        }
                    }
                }
            }

            HitCountOut[0] = hits;
        }

        // ── Grid helpers ──────────────────────────────────────────────────────

        static int PackKey(int cx, int cy)
        {
            int x = math.clamp(cx, -32768, 32767) & 0xFFFF;
            int y = math.clamp(cy, -32768, 32767) & 0xFFFF;
            return (x << 16) | y;
        }

        static int HashKey(int key) =>
            (int)(((uint)key * 0x9e37_79b9u) & (GRID_BUCKETS - 1));

        void GridInsert(int cx, int cy, int targetIdx)
        {
            int key  = PackKey(cx, cy);
            int slot = HashKey(key);

            for (int probe = 0; probe < GRID_BUCKETS; probe++)
            {
                int kv = GridBuffer[KEY_OFFSET + slot];
                if (kv == EMPTY) GridBuffer[KEY_OFFSET + slot] = key;

                if (GridBuffer[KEY_OFFSET + slot] == key)
                {
                    int cnt = GridBuffer[CNT_OFFSET + slot];
                    if (cnt < BUCKET_SLOTS)
                    {
                        GridBuffer[ENT_OFFSET + slot * BUCKET_SLOTS + cnt] = targetIdx;
                        GridBuffer[CNT_OFFSET + slot] = cnt + 1;
                    }
                    return;
                }
                slot = (slot + 1) & (GRID_BUCKETS - 1);
            }
        }

        int GridFind(int cx, int cy)
        {
            int key  = PackKey(cx, cy);
            int slot = HashKey(key);

            for (int probe = 0; probe < GRID_BUCKETS; probe++)
            {
                int kv = GridBuffer[KEY_OFFSET + slot];
                if (kv == EMPTY)  return -1;
                if (kv == key)    return slot;
                slot = (slot + 1) & (GRID_BUCKETS - 1);
            }
            return -1;
        }

        static int FloorToInt(float v) => (int)math.floor(v);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Spawn — parallel direct struct init, no FFI
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile(CompileSynchronously = false)]
    public struct BurstSpawnJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<NativeProjectile> Out;
        public float  OriginX;
        public float  OriginY;
        public float  AngleDeg;   // base direction — spread applied per-index
        public float  Speed;
        public float  SpreadDeg;  // degrees between projectiles (0 = no spread)
        public float  Lifetime;
        public ushort ConfigId;
        public ushort OwnerId;
        public uint   BaseProjId;
        public byte   MovementType;
        public float  Ay;         // gravity / homing (set from config)
        public float  ScaleX;
        public float  ScaleY;
        public float  ScaleTarget;
        public float  ScaleSpeed;

        public void Execute(int i)
        {
            float n      = Out.Length;
            float offset = (i - (n - 1) * 0.5f) * SpreadDeg;
            float angle  = math.radians(AngleDeg + offset);

            Out[i] = new NativeProjectile
            {
                X           = OriginX,
                Y           = OriginY,
                Vx          = math.cos(angle) * Speed,
                Vy          = math.sin(angle) * Speed,
                Ax          = 0f,
                Ay          = Ay,
                AngleDeg    = AngleDeg + offset,
                CurveT      = 0f,
                ScaleX      = ScaleX,
                ScaleY      = ScaleY,
                ScaleTarget = ScaleTarget,
                ScaleSpeed  = ScaleSpeed,
                Lifetime    = Lifetime,
                MaxLifetime = Lifetime,
                TravelDist  = 0f,
                ConfigId    = ConfigId,
                OwnerId     = OwnerId,
                ProjId      = BaseProjId + (uint)i,
                MovementType = MovementType,
                PiercingType = 0,
                Alive        = 1,
            };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Compact — single-threaded swap-compact of dead projectile slots
    //
    //  Runs after tick + collision each frame.
    //  Writes the new active count to ActiveCountOut[0].
    //  Also writes a death bitmask (32 uint words = 1024 bits) so the caller
    //  can check proj_ids for trail release without scanning the full array.
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile(CompileSynchronously = false)]
    public struct BurstCompactJob : IJob
    {
        public NativeArray<NativeProjectile> Projectiles;
        public NativeArray<int> ActiveCountInOut; // [0] = current active count in, new count out
        // Optional: write dead ProjIds so C# trail pool can release them.
        // Sized to max dead per frame (= max active = MaxProjectiles).
        [WriteOnly] public NativeArray<uint> DeadProjIds;
        public NativeArray<int> DeadCountOut; // [0]

        public void Execute()
        {
            int active   = ActiveCountInOut[0];
            int deadCnt  = 0;
            int i = 0;

            while (i < active)
            {
                if (Projectiles[i].Alive == 0)
                {
                    // Record dead id for trail release.
                    if (deadCnt < DeadProjIds.Length)
                        DeadProjIds[deadCnt++] = Projectiles[i].ProjId;

                    active--;
                    if (i < active) Projectiles[i] = Projectiles[active];
                    // Don't advance i — new slot[i] might also be dead.
                }
                else
                {
                    i++;
                }
            }

            ActiveCountInOut[0] = active;
            DeadCountOut[0]     = deadCnt;
        }
    }
}
