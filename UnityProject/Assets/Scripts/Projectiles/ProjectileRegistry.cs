// ProjectileRegistry.cs
// Singleton that maps config_id (ushort) → ProjectileConfigSO
// and caches UV rects from the sprite atlas at startup.

using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    public class ProjectileRegistry : MonoBehaviour
    {
        public static ProjectileRegistry Instance { get; private set; }

        [Tooltip("Drag all ProjectileConfigSOs here in order. " +
                 "Index = ConfigId used in NativeProjectile.")]
        [SerializeField] private ProjectileConfigSO[] _configs;

        // UV rect per config_id (x, y, width, height) in 0-1 atlas space
        private Vector4[] _uvRects;

        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            BuildRegistry();
            ProjectileLib.ValidateStructSizes();
        }

        private void BuildRegistry()
        {
            _uvRects = new Vector4[_configs.Length];

            for (int i = 0; i < _configs.Length; i++)
            {
                _configs[i].ConfigId = (ushort)i;

                var spr = _configs[i].ProjectileSprite;
                if (spr == null)
                {
                    Debug.LogWarning(
                        $"[ProjectileRegistry] Config [{i}] '{_configs[i].name}' " +
                        $"has no sprite assigned. Using full atlas UV.");
                    _uvRects[i] = new Vector4(0, 0, 1, 1);
                    continue;
                }

                var tex  = spr.texture;
                var rect = spr.textureRect;
                _uvRects[i] = new Vector4(
                    rect.x      / tex.width,
                    rect.y      / tex.height,
                    rect.width  / tex.width,
                    rect.height / tex.height
                );
            }

            Debug.Log(
                $"[ProjectileRegistry] Registered {_configs.Length} projectile configs.");
        }

        // ─── Public API ───────────────────────────────────────────────────────

        public ProjectileConfigSO Get(ushort configId)
        {
            if (configId >= _configs.Length)
            {
                Debug.LogError(
                    $"[ProjectileRegistry] ConfigId {configId} out of range " +
                    $"(max {_configs.Length - 1})");
                return _configs[0];
            }
            return _configs[configId];
        }

        public Vector4 GetUVRect(ushort configId)
        {
            if (configId >= _uvRects.Length) return new Vector4(0, 0, 1, 1);
            return _uvRects[configId];
        }

        public int Count => _configs.Length;
    }
}
