// WeaponNetworkBridge.cs
// NGO 1.x RPC bridge — keeps network traffic to a minimum.
// Projectile POSITIONS are never synced. Only spawn events + hit confirms.

using Unity.Netcode;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    public class WeaponNetworkBridge : NetworkBehaviour
    {
        [Header("Firing")]
        [SerializeField] private ushort _defaultConfigId = 0;
        [SerializeField] private Transform _muzzle;

        private ProjectileManager _manager;
        private float _lastFireTime;

        // cooldown fetched from config at runtime
        private const float MIN_FIRE_INTERVAL = 0.05f;

        // ─────────────────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            _manager = ProjectileManager.Instance;

            // Server subscribes to hit events from the manager
            if (IsServer)
                _manager.OnHit += OnServerHitDetected;
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && _manager != null)
                _manager.OnHit -= OnServerHitDetected;
        }

        // ─── Client: request fire ─────────────────────────────────────────────

        /// <summary>
        /// Call this from your input handler on the owning client.
        /// </summary>
        public void RequestFire(ushort configId, float angleDeg)
        {
            if (!IsOwner) return;
            if (Time.time - _lastFireTime < MIN_FIRE_INTERVAL) return;
            _lastFireTime = Time.time;

            var origin = _muzzle != null
                ? (Vector2)_muzzle.position
                : (Vector2)transform.position;

            uint seed = (uint)Random.Range(int.MinValue, int.MaxValue);

            FireServerRpc(configId, origin, angleDeg, seed);
        }

        // ─── Server: validate and broadcast ───────────────────────────────────

        [ServerRpc]
        private void FireServerRpc(
            ushort configId,
            Vector2 origin,
            float angleDeg,
            uint seed,
            ServerRpcParams rpc = default)
        {
            // Basic anti-cheat: validate configId is in range
            if (configId >= ProjectileRegistry.Instance.Count)
            {
                Debug.LogWarning(
                    $"[WeaponNetworkBridge] Client {rpc.Receive.SenderClientId} " +
                    $"sent invalid configId {configId}");
                return;
            }

            var cfg   = ProjectileRegistry.Instance.Get(configId);
            float speed = Random.Range(cfg.MinSpeed, cfg.MaxSpeed);

            // Server ticks for latency compensation
            uint serverTick = (uint)NetworkManager.Singleton
                .NetworkTickSystem.ServerTime.Tick;

            SpawnProjectileClientRpc(
                configId, origin, angleDeg, speed, seed, serverTick,
                (ushort)rpc.Receive.SenderClientId);
        }

        // ─── All clients: run same deterministic spawn ────────────────────────

        [ClientRpc]
        private void SpawnProjectileClientRpc(
            ushort configId,
            Vector2 origin,
            float angleDeg,
            float speed,
            uint seed,
            uint spawnTick,
            ushort ownerId)
        {
            uint localTick = (uint)NetworkManager.Singleton
                .NetworkTickSystem.LocalTime.Tick;

            float latency = (localTick > spawnTick)
                ? (localTick - spawnTick) * Time.fixedDeltaTime
                : 0f;

            _manager.Spawn(
                configId, origin, angleDeg,
                speed, latency, ownerId, seed);
        }

        // ─── Server: receive hit event from ProjectileManager ─────────────────

        private void OnServerHitDetected(HitResult hit)
        {
            // Compute damage — travel_dist for falloff would go here
            int damage = 10; // placeholder until you wire config damage

            HitConfirmedClientRpc(hit.TargetId, damage);
        }

        // ─── All clients: apply confirmed damage ──────────────────────────────

        [ClientRpc]
        private void HitConfirmedClientRpc(uint targetId, int damage)
        {
            // targetId maps to whatever health component you have
            // This is a stub — wire to your health system
            Debug.Log(
                $"[WeaponNetworkBridge] Hit confirmed: target={targetId} " +
                $"damage={damage}");
        }
    }
}
