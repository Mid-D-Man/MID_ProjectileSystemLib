// ProjectileConfigSO.cs
using UnityEngine;

namespace MidManStudio.Projectiles
{
    [CreateAssetMenu(
        fileName = "ProjectileConfig",
        menuName  = "MidMan/Projectile Config",
        order     = 10)]
    public class ProjectileConfigSO : ScriptableObject
    {
        [HideInInspector] public ushort ConfigId;

        [Header("Visual")]
        [Tooltip("If false, no mesh quad is rendered. Useful when the trail IS the visual.")]
        public bool UseSprite = true;
        public Sprite ProjectileSprite;
        public float FullSizeX = 0.2f;
        public float FullSizeY = 0.2f;
        [Range(0f, 1f)]  public float SpawnScaleFraction = 0.2f;
        [Range(1f, 30f)] public float GrowthSpeed = 8f;

        [Tooltip("Optional custom mesh shape. Leave null to use a plain quad.")]
        public ProjectileShapeSO CustomShape;

        [Header("Trail")]
        public bool      HasTrail = true;
        public Material  TrailMaterial;
        public Gradient  TrailColorGradient;
        [Range(0.02f, 2f)] public float TrailTime       = 0.15f;
        [Range(0f, 1f)]    public float TrailStartWidth = 0.08f;
        [Range(0f, 1f)]    public float TrailEndWidth   = 0.0f;
        [Range(0, 10)]     public int   TrailCapVertices = 2;

        [Header("Movement")]
        public MovementType Movement = MovementType.Straight;
        public PatternId    Pattern  = PatternId.Single;
        [Range(1f, 50f)] public float MinSpeed = 8f;
        [Range(1f, 50f)] public float MaxSpeed = 10f;
        public float RandomSpeed => UnityEngine.Random.Range(MinSpeed, MaxSpeed);

        [Header("Lifetime")]
        [Range(0.1f, 10f)] public float Lifetime = 2.5f;

        [Header("Piercing")]
        public PiercingType Piercing     = PiercingType.None;
        [Range(1, 10)] public byte MaxCollisions = 1;

        [Header("Physics Override")]
        public float GravityScale = 0f;

#if UNITY_EDITOR
        private void OnValidate() => MaxSpeed = Mathf.Max(MaxSpeed, MinSpeed);
#endif
    }
}