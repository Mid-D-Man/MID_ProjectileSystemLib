// ProjectileRenderer2D.cs
// Single-call instanced rendering for all alive projectiles.
// Reads position + scale from the Rust-driven NativeProjectile array.

using UnityEngine;

namespace MidManStudio.Projectiles
{
    [RequireComponent(typeof(ProjectileManager))]
    public class ProjectileRenderer2D : MonoBehaviour
    {
        [Header("Rendering")]
        [SerializeField] private Material _atlasMaterial;   // uses InstancedProjectile shader
        [SerializeField] private Mesh     _quadMesh;        // unit quad, generated if null

        // DrawMeshInstanced hard limit per call
        private const int BATCH_SIZE = 1023;

        private Matrix4x4[]          _matrices;
        private Vector4[]            _uvRects;
        private Vector4[]            _colors;
        private MaterialPropertyBlock _mpb;

        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            _matrices = new Matrix4x4[BATCH_SIZE];
            _uvRects  = new Vector4[BATCH_SIZE];
            _colors   = new Vector4[BATCH_SIZE];
            _mpb      = new MaterialPropertyBlock();

            if (_quadMesh == null)
                _quadMesh = BuildQuadMesh();
        }

        // Called by ProjectileManager after Rust tick
        public void Render(NativeProjectile[] projs, int count)
        {
            if (count == 0 || _atlasMaterial == null) return;

            int batchStart = 0;
            while (batchStart < count)
            {
                int renderCount = 0;
                int batchEnd    = Mathf.Min(batchStart + BATCH_SIZE, count);

                for (int i = batchStart; i < batchEnd; i++)
                {
                    ref var p = ref projs[i];
                    if (p.Alive == 0) continue;

                    _matrices[renderCount] = Matrix4x4.TRS(
                        new Vector3(p.X, p.Y, 0f),
                        Quaternion.Euler(0f, 0f, p.AngleDeg),
                        new Vector3(p.ScaleX, p.ScaleY, 1f)
                    );

                    _uvRects[renderCount] =
                        ProjectileRegistry.Instance.GetUVRect(p.ConfigId);

                    _colors[renderCount] = ComputeTint(ref p);

                    renderCount++;
                }

                if (renderCount > 0)
                {
                    _mpb.SetVectorArray("_UVRect", _uvRects);
                    _mpb.SetVectorArray("_Color",  _colors);

                    Graphics.DrawMeshInstanced(
                        _quadMesh, 0, _atlasMaterial,
                        _matrices, renderCount, _mpb,
                        UnityEngine.Rendering.ShadowCastingMode.Off,
                        receiveShadows: false,
                        layer: gameObject.layer
                    );
                }

                batchStart = batchEnd;
            }
        }

        // ─── Tint ─────────────────────────────────────────────────────────────

        private static Vector4 ComputeTint(ref NativeProjectile p)
        {
            // Fade out in last 15% of lifetime
            float lifeFrac = p.Lifetime / Mathf.Max(p.MaxLifetime, 0.0001f);
            float alpha    = lifeFrac < 0.15f ? lifeFrac / 0.15f : 1f;
            return new Vector4(1f, 1f, 1f, alpha);
        }

        // ─── Quad mesh builder ────────────────────────────────────────────────

        private static Mesh BuildQuadMesh()
        {
            var mesh = new Mesh { name = "ProjectileQuad" };
            mesh.vertices  = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(1, 1),
                new Vector2(0, 1),
            };
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true); // mark static, no CPU readback
            return mesh;
        }
    }
}
