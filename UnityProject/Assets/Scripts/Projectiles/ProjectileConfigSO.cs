// ProjectileConfigSO.cs
// Simplified config — just what the test project needs:
// sprite, scale behaviour, trail, movement type, speed, lifetime.

using UnityEngine;

namespace MidManStudio.Projectiles
{
    [CreateAssetMenu(
        fileName = "ProjectileConfig",
        menuName  = "MidMan/Projectile Config",
        order     = 10)]
    public class ProjectileConfigSO : ScriptableObject
    {
        // ── Identity ──────────────────────────────────────────────────────────
        [Tooltip("Matches the index in ProjectileRegistry. Set automatically.")]
        [HideInInspector]
        public ushort ConfigId;

        // ── Visual ────────────────────────────────────────────────────────────
        [Header("Visual")]
        [Tooltip("The sprite used for this projectile. Must be packed into the atlas.")]
        public Sprite ProjectileSprite;

        [Tooltip("World-space size at full scale (Unity units).")]
        public float FullSizeX = 0.2f;
        public float FullSizeY = 0.2f;

        [Tooltip("Scale the bullet starts at on spawn (0.0 = invisible, 1.0 = full).")]
        [Range(0f, 1f)]
        public float SpawnScaleFraction = 0.2f;

        [Tooltip("How fast the bullet grows to full size. Higher = snappier.")]
        [Range(1f, 30f)]
        public float GrowthSpeed = 8f;

        // ── Trail ─────────────────────────────────────────────────────────────
        [Header("Trail")]
        public bool  HasTrail = true;
        public Material   TrailMaterial;
        public Gradient   TrailColorGradient;
        [Range(0.02f, 2f)]
        public float TrailTime = 0.15f;
        [Range(0f, 1f)]
        public float TrailStartWidth = 0.08f;
        [Range(0f, 1f)]
        public float TrailEndWidth   = 0.0f;

        // ── Movement ──────────────────────────────────────────────────────────
        [Header("Movement")]
        public MovementType Movement = MovementType.Straight;
        public PatternId    Pattern  = PatternId.Single;

        [Range(1f, 50f)]
        public float MinSpeed = 8f;
        [Range(1f, 50f)]
        public float MaxSpeed = 10f;

        /// <summary>Returns a random speed between Min and Max.</summary>
        public float RandomSpeed =>
            UnityEngine.Random.Range(MinSpeed, MaxSpeed);

        // ── Lifetime ──────────────────────────────────────────────────────────
        [Header("Lifetime")]
        [Range(0.1f, 10f)]
        public float Lifetime = 2.5f;

        // ── Piercing ──────────────────────────────────────────────────────────
        [Header("Piercing")]
        public PiercingType Piercing = PiercingType.None;
        [Range(1, 10)]
        public byte MaxCollisions = 1;

        // ── Gravity (for arching) ─────────────────────────────────────────────
        [Header("Physics Override")]
        [Tooltip("Applied as Ay in Rust. Negative = downward pull (arching).")]
        public float GravityScale = 0f;

#if UNITY_EDITOR
        private void OnValidate()
        {
            MaxSpeed = Mathf.Max(MaxSpeed, MinSpeed);
        }
#endif
    }
}
