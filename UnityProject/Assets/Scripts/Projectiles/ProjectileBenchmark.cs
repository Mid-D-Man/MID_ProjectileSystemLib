// ProjectileBenchmark.cs
// Attach to a GameObject in a test scene alongside ProjectileManager.
// Press B to run a full benchmark. Results print to Console.
//
// Tests:
//   1. Struct size validation (must pass before anything else)
//   2. Spawn burst — spawn N projectiles, measure time
//   3. Tick throughput — tick M times, measure time per tick
//   4. Collision throughput — tick with T active targets
//   5. Save/restore round-trip — verify state fidelity + measure cost
//
// All timings use System.Diagnostics.Stopwatch for sub-millisecond accuracy.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MidManStudio.Projectiles
{
    public class ProjectileBenchmark : MonoBehaviour
    {
        [Header("Benchmark Config")]
        [SerializeField] private ushort _configId       = 0;
        [SerializeField] private int    _spawnBurstCount = 2000;
        [SerializeField] private int    _tickCount       = 1000;
        [SerializeField] private int    _targetCount     = 64;

        private ProjectileManager _mgr;

        void Awake()
        {
            _mgr = GetComponent<ProjectileManager>();
            if (_mgr == null)
                _mgr = FindObjectOfType<ProjectileManager>();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.B))
                RunAll();
        }

        public void RunAll()
        {
            Debug.Log("=== ProjectileBenchmark START ===");

            if (!ProjectileLib.ValidateStructSizes())
            {
                Debug.LogError("Struct size validation FAILED — aborting benchmark.");
                return;
            }

            BenchSpawn();
            BenchTick();
            BenchCollision();
            BenchSaveRestore();

            Debug.Log("=== ProjectileBenchmark END ===");
        }

        // ── 1. Spawn burst ────────────────────────────────────────────────────

        void BenchSpawn()
        {
            int n = _spawnBurstCount;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < n; i++)
            {
                _mgr.Spawn(
                    _configId,
                    new Vector2(i * 0.001f, 0f),
                    0f, 10f, 0f,
                    0, (uint)i
                );
            }

            sw.Stop();
            float ms     = sw.Elapsed.Milliseconds + sw.Elapsed.Microseconds / 1000f;
            float ns_per = (float)sw.Elapsed.TotalMilliseconds * 1_000_000f / n;

            Debug.Log($"[Bench/Spawn] {n} spawns in {ms:F2}ms  ({ns_per:F0} ns/spawn)  " +
                      $"active={_mgr.ActiveCount}/{_mgr.MaxProjectiles}");
        }

        // ── 2. Tick throughput ────────────────────────────────────────────────

        void BenchTick()
        {
            int n  = _tickCount;
            float dt = Time.fixedDeltaTime;

            // Access pinned pointer via reflection-free path — use a
            // temporary pinned array that mirrors the manager's state for
            // the benchmark, so we don't disturb live simulation state.
            var projs = new NativeProjectile[_mgr.MaxProjectiles];
            var handle = GCHandle.Alloc(projs, GCHandleType.Pinned);
            IntPtr ptr = handle.AddrOfPinnedObject();

            // Fill with dummy alive projectiles moving in straight lines
            int count = Mathf.Min(2048, _mgr.MaxProjectiles);
            for (int i = 0; i < count; i++)
            {
                projs[i] = new NativeProjectile
                {
                    X = i * 0.01f, Y = 0f,
                    Vx = 10f, Vy = 0f,
                    Lifetime = 10f, MaxLifetime = 10f,
                    ScaleX = 0.2f, ScaleY = 0.2f,
                    Alive = 1,
                    MovementType = 0,
                };
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
                ProjectileLib.tick_projectiles(ptr, count, dt);
            sw.Stop();

            handle.Free();

            float ms     = (float)sw.Elapsed.TotalMilliseconds;
            float us_per = ms * 1000f / n;
            float budget = Time.fixedDeltaTime * 1000f; // ms per FixedUpdate

            Debug.Log($"[Bench/Tick] {n} ticks × {count} projectiles in {ms:F2}ms  " +
                      $"({us_per:F1} µs/tick)  " +
                      $"budget={budget:F1}ms  " +
                      $"{(us_per < budget * 1000f ? "✓ within budget" : "⚠ over budget")}");
        }

        // ── 3. Collision throughput ───────────────────────────────────────────

        void BenchCollision()
        {
            int projCount   = 2048;
            int targetCount = _targetCount;
            int hitMax      = 512;

            var projs   = new NativeProjectile[projCount];
            var targets = new CollisionTarget[targetCount];
            var hits    = new HitResult[hitMax];

            var ph = GCHandle.Alloc(projs,   GCHandleType.Pinned);
            var th = GCHandle.Alloc(targets, GCHandleType.Pinned);
            var hh = GCHandle.Alloc(hits,    GCHandleType.Pinned);

            IntPtr pp = ph.AddrOfPinnedObject();
            IntPtr tp = th.AddrOfPinnedObject();
            IntPtr hp = hh.AddrOfPinnedObject();

            for (int i = 0; i < projCount; i++)
            {
                projs[i].X = i * 0.5f; projs[i].Y = 0f;
                projs[i].ScaleX = 0.2f; projs[i].Alive = 1;
            }
            for (int i = 0; i < targetCount; i++)
            {
                targets[i].X = i * 10f; targets[i].Y = 0f;
                targets[i].Radius = 1f; targets[i].TargetId = (uint)i;
                targets[i].Active = 1;
            }

            int n  = 500;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                ProjectileLib.check_hits_grid(
                    pp, projCount, tp, targetCount, hp, hitMax, out int _);
            }
            sw.Stop();

            ph.Free(); th.Free(); hh.Free();

            float ms     = (float)sw.Elapsed.TotalMilliseconds;
            float us_per = ms * 1000f / n;

            Debug.Log($"[Bench/Collision] {n} checks ({projCount} projs × {targetCount} targets) " +
                      $"in {ms:F2}ms  ({us_per:F1} µs/check)");
        }

        // ── 4. Save / restore round-trip ─────────────────────────────────────

        void BenchSaveRestore()
        {
            int count    = 2048;
            int bufBytes = count * 72; // NativeProjectile = 72 bytes each

            var projs = new NativeProjectile[count];
            var buf   = new byte[bufBytes];
            var restored = new NativeProjectile[count];

            var ph = GCHandle.Alloc(projs,     GCHandleType.Pinned);
            var bh = GCHandle.Alloc(buf,       GCHandleType.Pinned);
            var rh = GCHandle.Alloc(restored,  GCHandleType.Pinned);

            for (int i = 0; i < count; i++)
            {
                projs[i].X = i; projs[i].Alive = 1; projs[i].ProjId = (uint)i;
            }

            var sw = Stopwatch.StartNew();
            int written = ProjectileLib.save_state(
                ph.AddrOfPinnedObject(), count, bh.AddrOfPinnedObject(), bufBytes);
            ProjectileLib.restore_state(
                rh.AddrOfPinnedObject(), count, bh.AddrOfPinnedObject(), written, out int restoredCount);
            sw.Stop();

            // Verify fidelity
            bool ok = restoredCount == count;
            if (ok)
            {
                for (int i = 0; i < count && ok; i++)
                    if (restored[i].ProjId != projs[i].ProjId) ok = false;
            }

            ph.Free(); bh.Free(); rh.Free();

            float ms = (float)sw.Elapsed.TotalMilliseconds;
            Debug.Log($"[Bench/SaveRestore] {count} projectiles save+restore in {ms:F3}ms  " +
                      $"written={written}B  restoredCount={restoredCount}  " +
                      $"fidelity={(ok ? "✓ PASS" : "✗ FAIL")}");
        }
    }
}
