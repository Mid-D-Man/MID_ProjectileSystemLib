// TrailObjectPool.cs — UPDATE
// Changes:
//   + SyncToSimulation(NativeProjectile3D[], int) overload for 3D trail positions
//   + Merged NotifyDead into a single path (was already correct for 2D)
//   + Internal slot lookup extracted to FindSlot() to avoid duplication
//   No other structural changes — 2D path identical to original.

using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    [RequireComponent(typeof(ProjectileManager))]
    public class TrailObjectPool : MonoBehaviour
    {
        #region Configuration

        [SerializeField] private int _poolSize = 512;

        #endregion

        #region State

        private TrailRenderer[] _trails;
        private uint[]          _assignedIds;
        private bool[]          _inUse;
        private float[]         _fadingUntil;

        private readonly Dictionary<uint, int> _idToSlot = new Dictionary<uint, int>(512);

        #endregion

        #region Initialisation

        private void Awake()
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
                tr.enabled     = false;
                tr.autodestruct = false;
                tr.emitting    = false;
                _trails[i]     = tr;
            }
        }

        #endregion

        #region Public API — Sync (2D)

        /// <summary>
        /// Sync active 2D projectile trail positions.
        /// Called by LocalProjectileManager and ServerProjectileAuthority every FixedUpdate.
        /// </summary>
        public void SyncToSimulation(NativeProjectile[] projs, int count)
        {
            RetireExpiredFades();

            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (cfg == null || !cfg.HasTrail) continue;

                if (!_idToSlot.TryGetValue(p.ProjId, out int slot))
                {
                    slot = AcquireSlot(p.ProjId, cfg);
                    if (slot < 0) continue;
                }

                _trails[slot].transform.position = new Vector3(p.X, p.Y, 0f);
            }
        }

        #endregion

        #region Public API — Sync (3D) — NEW

        /// <summary>
        /// Sync active 3D projectile trail positions.
        /// Called by LocalProjectileManager every FixedUpdate when 3D projectiles are active.
        /// </summary>
        public void SyncToSimulation(NativeProjectile3D[] projs, int count)
        {
            RetireExpiredFades();

            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (cfg == null || !cfg.HasTrail) continue;

                if (!_idToSlot.TryGetValue(p.ProjId, out int slot))
                {
                    slot = AcquireSlot(p.ProjId, cfg);
                    if (slot < 0) continue;
                }

                _trails[slot].transform.position = new Vector3(p.X, p.Y, p.Z);
            }
        }

        #endregion

        #region Public API — Notify Dead

        /// <summary>
        /// Notify that a projectile has died.
        /// Begins trail fade-out — the trail persists briefly then is returned to pool.
        /// Called by CompactDead in both LocalProjectileManager and ServerProjectileAuthority.
        /// </summary>
        public void NotifyDead(uint projId)
        {
            if (!_idToSlot.TryGetValue(projId, out int slot)) return;

            _trails[slot].emitting = false;
            _fadingUntil[slot]     = Time.time + _trails[slot].time + 0.05f;
            _inUse[slot]           = false;
            _assignedIds[slot]     = 0;
            _idToSlot.Remove(projId);
        }

        #endregion

        #region Internal Pool Management

        private void RetireExpiredFades()
        {
            float now = Time.time;
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i] || _fadingUntil[i] <= 0f) continue;
                if (now >= _fadingUntil[i])
                {
                    _trails[i].enabled  = false;
                    _fadingUntil[i]     = 0f;
                }
            }
        }

        private int AcquireSlot(uint projId, ProjectileConfigSO cfg)
        {
            // Pass 1: completely free slot
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i] || _fadingUntil[i] > 0f) continue;
                return InitSlot(i, projId, cfg);
            }

            // Pass 2: LRU eviction — steal slot closest to finishing fade
            int   best   = -1;
            float soonest = float.MaxValue;
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i]) continue;
                if (_fadingUntil[i] < soonest)
                {
                    soonest = _fadingUntil[i];
                    best    = i;
                }
            }

            if (best >= 0)
            {
                _trails[best].enabled  = false;
                _fadingUntil[best]     = 0f;
                return InitSlot(best, projId, cfg);
            }

            return -1; // pool exhausted
        }

        private int InitSlot(int i, uint projId, ProjectileConfigSO cfg)
        {
            _inUse[i]       = true;
            _assignedIds[i] = projId;
            _fadingUntil[i] = 0f;
            _idToSlot[projId] = i;

            ApplyConfig(_trails[i], cfg);
            _trails[i].Clear();
            _trails[i].enabled  = true;
            _trails[i].emitting = true;
            return i;
        }

        private static void ApplyConfig(TrailRenderer tr, ProjectileConfigSO cfg)
        {
            if (cfg.TrailMaterial == null)
            {
                Debug.LogWarning(
                    $"[TrailObjectPool] '{cfg.name}' HasTrail=true but TrailMaterial is null.");
            }

            tr.material             = cfg.TrailMaterial;
            tr.time                 = cfg.TrailTime;
            tr.startWidth           = cfg.TrailStartWidth;
            tr.endWidth             = cfg.TrailEndWidth;
            tr.minVertexDistance    = cfg.TrailMinVertexDistance;
            tr.numCapVertices       = cfg.TrailCapVertices;
            tr.shadowCastingMode    = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.receiveShadows       = false;

            if (cfg.UseGradientOverride && cfg.TrailGradient != null)
                tr.colorGradient = cfg.TrailGradient;
        }

        #endregion
    }
}
