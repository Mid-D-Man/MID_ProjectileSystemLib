// TrailObjectPool.cs
// Pool of featherweight GameObjects (Transform + TrailRenderer only).
// No scripts, no physics, no Update loops on the trail GOs themselves.
//
// Changes from original:
//   1. DisableAfterDelay coroutine removed entirely — replaced by a float
//      _fadingUntil[] timestamp checked each SyncToSimulation pass.
//      Eliminates the coroutine stampede that occurred when hundreds of
//      projectiles died in the same frame (B-spam scenario).
//
//   2. LRU-style eviction when pool is full: instead of silently dropping
//      new projectiles, the slot closest to completing its fade is stolen
//      and hard-disabled immediately.  Keeps trail coverage consistent
//      when the pool is saturated rather than just dropping new entries.
//
//   3. Pool default raised to 512 (was 256).  Recommendation:
//        _poolSize ≈ max_alive_projectiles × avg_trail_time / avg_projectile_lifetime
//      For 2048 max, 0.15 s trail, 2.5 s lifetime: 2048 × 0.06 ≈ 123 slots needed.
//      512 gives generous headroom.
//
//   4. AcquireSlot linear scan replaced by two-pass scan:
//      pass 1 — find any immediately-free slot (fast common case).
//      pass 2 — if none free, find the soonest-releasing fading slot (eviction).
//
//   5. _inUse[] semantics are now:
//        true  → slot is actively tracking a live projectile
//        false → slot is free or fading (check _fadingUntil to distinguish)

using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    [RequireComponent(typeof(ProjectileManager))]
    public class TrailObjectPool : MonoBehaviour
    {
        [SerializeField] private int _poolSize = 512;

        private TrailRenderer[] _trails;
        private uint[]          _assignedIds;  // proj_id owning this slot, 0 = none
        private bool[]          _inUse;        // true = actively tracking a live projectile
        private float[]         _fadingUntil;  // Time.time after which slot is truly free

        // proj_id → slot index (only for currently-active projectiles, not fading ones)
        private Dictionary<uint, int> _idToSlot = new Dictionary<uint, int>(512);

        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            _trails      = new TrailRenderer[_poolSize];
            _assignedIds = new uint[_poolSize];
            _inUse       = new bool[_poolSize];
            _fadingUntil = new float[_poolSize];

            for (int i = 0; i < _poolSize; i++)
            {
                var go = new GameObject($"Trail_{i}");
                go.transform.SetParent(transform);
                go.hideFlags = HideFlags.HideInHierarchy;

                var tr = go.AddComponent<TrailRenderer>();
                tr.enabled      = false;
                tr.autodestruct = false;
                tr.emitting     = false;

                _trails[i] = tr;
            }
        }

        // ─── Called by ProjectileManager each FixedUpdate ─────────────────────

        public void SyncToSimulation(NativeProjectile[] projs, int count)
        {
            float now = Time.time;

            // Pass 1: disable fully-faded trails (replaces DisableAfterDelay coroutine).
            // O(_poolSize) — cheap linear scan, no allocation.
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i] || _fadingUntil[i] <= 0f) continue;
                if (now >= _fadingUntil[i])
                {
                    _trails[i].enabled  = false;
                    _fadingUntil[i]     = 0f;
                }
            }

            // Pass 2: sync alive projectile positions.
            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (!cfg.HasTrail) continue;

                if (!_idToSlot.TryGetValue(p.ProjId, out int slot))
                {
                    slot = AcquireSlot(p.ProjId, cfg);
                    if (slot < 0) continue; // pool fully saturated with active slots
                }

                _trails[slot].transform.position = new Vector3(p.X, p.Y, 0f);
            }
        }

        // Called by ProjectileManager.CompactDeadSlots for every dead projectile.
        public void NotifyDead(uint projId)
        {
            if (!_idToSlot.TryGetValue(projId, out int slot)) return;

            // Stop emitting so the trail renderer starts its natural fade.
            _trails[slot].emitting = false;

            // Schedule hard-disable after the trail has visually faded out.
            // Checked in SyncToSimulation — no coroutine, no GC pressure.
            _fadingUntil[slot] = Time.time + _trails[slot].time + 0.05f;

            // Release ownership immediately so the slot can be evicted under
            // pressure, while the visual fade continues independently.
            _inUse[slot]       = false;
            _assignedIds[slot] = 0;
            _idToSlot.Remove(projId);
        }

        // ─── Internals ────────────────────────────────────────────────────────

        private int AcquireSlot(uint projId, ProjectileConfigSO cfg)
        {
            float now = Time.time;

            // Pass 1: find a slot that is completely free (not in use, not fading).
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i] || _fadingUntil[i] > 0f) continue;
                return InitSlot(i, projId, cfg);
            }

            // Pass 2: pool is full or all slots are fading.
            // Find the slot closest to completing its fade and evict it.
            int   bestSlot    = -1;
            float soonest     = float.MaxValue;

            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i]) continue; // can't evict an active slot
                if (_fadingUntil[i] < soonest)
                {
                    soonest  = _fadingUntil[i];
                    bestSlot = i;
                }
            }

            if (bestSlot >= 0)
            {
                // Hard-disable the fading trail and reclaim the slot immediately.
                _trails[bestSlot].enabled  = false;
                _fadingUntil[bestSlot]     = 0f;
                return InitSlot(bestSlot, projId, cfg);
            }

            // Truly exhausted (all _inUse) — drop this projectile's trail.
            return -1;
        }

        private int InitSlot(int i, uint projId, ProjectileConfigSO cfg)
        {
            _inUse[i]       = true;
            _assignedIds[i] = projId;
            _fadingUntil[i] = 0f;
            _idToSlot[projId] = i;

            ApplyConfig(_trails[i], cfg);
            _trails[i].Clear();          // purge any geometry from previous owner
            _trails[i].enabled  = true;
            _trails[i].emitting = true;

            return i;
        }

        private static void ApplyConfig(TrailRenderer tr, ProjectileConfigSO cfg)
        {
            tr.material          = cfg.TrailMaterial;
            tr.colorGradient     = cfg.TrailColorGradient;
            tr.time              = cfg.TrailTime;
            tr.startWidth        = cfg.TrailStartWidth;
            tr.endWidth          = cfg.TrailEndWidth;
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.receiveShadows    = false;
        }
    }
}
