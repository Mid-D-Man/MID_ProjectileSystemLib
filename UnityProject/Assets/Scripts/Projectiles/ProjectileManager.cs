// ProjectileManager.cs
// Physics tick stays in FixedUpdate.
// Graphics.DrawMesh MUST be called every display frame → moved to LateUpdate.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    [RequireComponent(typeof(ProjectileRenderer2D))]
    [RequireComponent(typeof(TrailObjectPool))]
    public class ProjectileManager : MonoBehaviour
    {
        public static ProjectileManager Instance { get; private set; }

        [Header("Capacity")]
        [SerializeField] private int _maxProjectiles = 2048;

        private NativeProjectile[] _projs;
        private int  _count;
        private uint _nextId = 1;

        private ProjectileRenderer2D _renderer;
        private TrailObjectPool      _trailPool;

        private CollisionTarget[] _targets = new CollisionTarget[128];
        private int               _targetCount;
        private HitResult[]       _hitBuf  = new HitResult[256];

        private GCHandle _projPin;
        private GCHandle _targetPin;
        private GCHandle _hitPin;

        public event Action<HitResult> OnHit;

// Add to ProjectileManager class body:
public int ActiveCount    => _count;
public int MaxProjectiles => _maxProjectiles;
        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            _projs     = new NativeProjectile[_maxProjectiles];
            _renderer  = GetComponent<ProjectileRenderer2D>();
            _trailPool = GetComponent<TrailObjectPool>();

            _projPin   = GCHandle.Alloc(_projs,   GCHandleType.Pinned);
            _targetPin = GCHandle.Alloc(_targets, GCHandleType.Pinned);
            _hitPin    = GCHandle.Alloc(_hitBuf,  GCHandleType.Pinned);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_projPin.IsAllocated)   _projPin.Free();
            if (_targetPin.IsAllocated) _targetPin.Free();
            if (_hitPin.IsAllocated)    _hitPin.Free();
        }

        // ─── Physics: runs at fixed timestep ─────────────────────────────────

        void FixedUpdate()
        {
            if (_count == 0) return;

            // 1. Rust physics tick
            ProjectileLib.tick_projectiles(
                _projPin.AddrOfPinnedObject(), _count, Time.fixedDeltaTime);

            // 2. Collision detection
            if (_targetCount > 0)
                CheckHits();

            // 3. Trail sync (before compact so dead projs still have valid pos)
            _trailPool.SyncToSimulation(_projs, _count);

            // 4. Remove dead projectiles, notify trail pool
            CompactDeadSlots();
        }

        // ─── Rendering: MUST run every display frame, not just physics ────────
        // Graphics.DrawMesh submits a command for the CURRENT render frame.
        // Calling from FixedUpdate at 50 Hz with display at 60 Hz causes frames
        // with no draw call → the mesh disappears → flickering.

        void LateUpdate()
        {
            _renderer.Render(_projs, _count);
        }

        // ─── Public API ───────────────────────────────────────────────────────

        public void Spawn(
            ushort  configId,
            Vector2 origin,
            float   angleDeg,
            float   speed,
            float   latency,
            ushort  ownerId,
            uint    seed)
        {
            if (_count >= _maxProjectiles) return;
            var cfg = ProjectileRegistry.Instance.Get(configId);

            var req = new SpawnRequest
            {
                OriginX    = origin.x,
                OriginY    = origin.y,
                AngleDeg   = angleDeg,
                Speed      = speed,
                ConfigId   = configId,
                OwnerId    = ownerId,
                PatternId  = (byte)cfg.Pattern,
                RngSeed    = seed,
                BaseProjId = _nextId,
            };

            int projStride = Marshal.SizeOf<NativeProjectile>();
            IntPtr projBase = _projPin.AddrOfPinnedObject();

            IntPtr reqPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SpawnRequest>());
            int spawned;
            try
            {
                Marshal.StructureToPtr(req, reqPtr, false);
                IntPtr outPtr = IntPtr.Add(projBase, _count * projStride);
                ProjectileLib.spawn_pattern(reqPtr, outPtr, _maxProjectiles - _count, out spawned);
            }
            finally { Marshal.FreeHGlobal(reqPtr); }

            for (int i = _count; i < _count + spawned; i++)
            {
                ref var p = ref _projs[i];
                p.MaxLifetime  = cfg.Lifetime;
                p.Lifetime     = cfg.Lifetime;
                p.MovementType = (byte)cfg.Movement;
                p.PiercingType = (byte)cfg.Piercing;
                p.ScaleX       = cfg.FullSizeX * cfg.SpawnScaleFraction;
                p.ScaleY       = cfg.FullSizeY * cfg.SpawnScaleFraction;
                p.ScaleTarget  = cfg.FullSizeX;
                p.ScaleSpeed   = cfg.SpawnScaleFraction < 1f ? cfg.GrowthSpeed : 0f;
                p.Ay           = cfg.GravityScale;
            }

            _nextId += (uint)spawned;
            _count  += spawned;

            if (latency > 0f && spawned > 0)
            {
                IntPtr latPtr = IntPtr.Add(projBase, (_count - spawned) * projStride);
                ProjectileLib.tick_projectiles(latPtr, spawned, latency);
            }
        }

        public void RegisterTarget(CollisionTarget t)
        {
            if (_targetCount < _targets.Length)
                _targets[_targetCount++] = t;
        }

        public void ClearTargets() => _targetCount = 0;

        // ─── Private ──────────────────────────────────────────────────────────

        private void CheckHits()
        {
            ProjectileLib.check_hits_grid(
                _projPin.AddrOfPinnedObject(),   _count,
                _targetPin.AddrOfPinnedObject(), _targetCount,
                _hitPin.AddrOfPinnedObject(),    _hitBuf.Length,
                out int hitCount);

            for (int i = 0; i < hitCount; i++)
            {
                var hit = _hitBuf[i];
                int idx = (int)hit.ProjIndex;
                if (idx < 0 || idx >= _count) continue;
                ref var p = ref _projs[idx];
                p.CollisionCount++;
                if (p.PiercingType == (byte)PiercingType.None) p.Alive = 0;
                OnHit?.Invoke(hit);
            }
        }

        private void CompactDeadSlots()
        {
            int w = 0;
            for (int r = 0; r < _count; r++)
            {
                if (_projs[r].Alive == 0)
                { _trailPool.NotifyDead(_projs[r].ProjId); continue; }
                if (w != r) _projs[w] = _projs[r];
                w++;
            }
            _count = w;
        }
    }
}