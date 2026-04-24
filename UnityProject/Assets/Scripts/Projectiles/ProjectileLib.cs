// ProjectileLib.cs
// All P/Invoke bindings to the Rust native library (projectile_core).
// Also defines all C#-side struct types that mirror Rust FFI structs (2D only;
// 3D structs live in NativeProjectile3D.cs).
//
// iOS note: IL2CPP static libraries are linked as __Internal.
//           The conditional compilation switches the DLL name at build time.
//
// Layout validation: call ProjectileLib.ValidateStructSizes() on game start.
// A mismatch silently corrupts memory on every FFI call — validate first.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  2D struct types (unchanged from original)
    //  NativeProjectile is defined in NativeProjectile.cs (keep_unchanged per plan).
    //  SpawnRequest, CollisionTarget, HitResult are defined here.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn request written by BatchSpawnHelper before calling spawn_pattern.
    /// 32 bytes — must match Rust SpawnRequest exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct SpawnRequest
    {
        [FieldOffset(0)]  public float  OriginX;
        [FieldOffset(4)]  public float  OriginY;
        [FieldOffset(8)]  public float  AngleDeg;
        [FieldOffset(12)] public float  Speed;
        [FieldOffset(16)] public ushort ConfigId;
        [FieldOffset(18)] public ushort OwnerId;
        [FieldOffset(20)] public byte   PatternId;   // PatternId enum cast to byte
        // 3 bytes padding at 21-23
        [FieldOffset(24)] public uint   RngSeed;
        [FieldOffset(28)] public uint   BaseProjId;
    }

    /// <summary>
    /// 2D hit event from check_hits_grid. 24 bytes — must match Rust HitResult.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct HitResult
    {
        [FieldOffset(0)]  public uint  ProjId;
        [FieldOffset(4)]  public uint  ProjIndex;    // index in sim buffer
        [FieldOffset(8)]  public uint  TargetId;
        [FieldOffset(12)] public float TravelDist;
        [FieldOffset(16)] public float HitX;
        [FieldOffset(20)] public float HitY;
    }

    /// <summary>
    /// 2D collision target sphere. 20 bytes — must match Rust CollisionTarget.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct CollisionTarget
    {
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Radius;
        [FieldOffset(12)] public uint  TargetId;
        [FieldOffset(16)] public byte  Active;
        // 3 bytes padding
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Enums (shared 2D/3D)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn pattern passed in SpawnRequest.PatternId.
    /// Values match Rust patterns.rs constants exactly.
    /// For custom patterns: use BatchSpawnHelper directly — bypass spawn_pattern entirely.
    /// </summary>
    public enum PatternId : byte
    {
        Single  = 0,  // 1 projectile straight ahead
        Spread3 = 1,  // 3 projectiles at ±20°
        Spread5 = 2,  // 5 projectiles at ±15° steps
        Spiral  = 3,  // 12 projectiles in a ring (360° / 12)
        Ring8   = 4   // 8 projectiles in a ring (360° / 8)
    }

    /// <summary>
    /// Matches Rust PiercingType — kept here for spawn helper usage.
    /// See SimulationMode.cs for ProjectilePiercingType (same semantic, separate type for clarity).
    /// </summary>
    public enum PiercingType : byte
    {
        None   = 0,
        Piecer = 1,
        Random = 2
    }

    /// <summary>
    /// Matches Rust MovementType byte constants.
    /// See SimulationMode.cs for ProjectileMovementType.
    /// </summary>
    public enum MovementType : byte
    {
        Straight  = 0,
        Arching   = 1,
        Guided    = 2,
        Teleport  = 3
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  P/Invoke bindings
    // ─────────────────────────────────────────────────────────────────────────

    public static class ProjectileLib
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string DLL = "__Internal";
#else
        private const string DLL = "projectile_core";
#endif

        // ── Layout validation ─────────────────────────────────────────────────

        [DllImport(DLL)] private static extern int projectile_struct_size();
        [DllImport(DLL)] private static extern int hit_result_struct_size();
        [DllImport(DLL)] private static extern int collision_target_struct_size();
        [DllImport(DLL)] private static extern int spawn_request_struct_size();

        // 3D validation (new)
        [DllImport(DLL)] private static extern int projectile3d_struct_size();
        [DllImport(DLL)] private static extern int hit_result3d_struct_size();
        [DllImport(DLL)] private static extern int collision_target3d_struct_size();

        // ── 2D Tick ───────────────────────────────────────────────────────────

        /// <summary>
        /// Advance all 2D projectiles by dt seconds.
        /// Returns the number that died this tick (for CompactDeadSlots).
        /// </summary>
        [DllImport(DLL)]
        public static extern int tick_projectiles(IntPtr projs, int count, float dt);

        // ── 3D Tick (new) ──────────────────────────────────────────────────────

        /// <summary>
        /// Advance all 3D projectiles by dt seconds.
        /// Returns the number that died this tick.
        /// </summary>
        [DllImport(DLL)]
        public static extern int tick_projectiles_3d(IntPtr projs, int count, float dt);

        // ── 2D Collision ──────────────────────────────────────────────────────

        /// <summary>
        /// Spatial-grid collision check (2D). cell_size = 4.0 default.
        /// </summary>
        [DllImport(DLL)]
        public static extern void check_hits_grid(
            IntPtr projs,       int projCount,
            IntPtr targets,     int targetCount,
            IntPtr outHits,     int maxHits,
            out int outHitCount);

        /// <summary>
        /// Spatial-grid collision check (2D) with explicit cell_size.
        /// Pass 0.0f for cell_size to use the default (4.0 world units).
        /// Tune to ~2× largest target radius.
        /// </summary>
        [DllImport(DLL)]
        public static extern void check_hits_grid_ex(
            IntPtr projs,       int projCount,
            IntPtr targets,     int targetCount,
            IntPtr outHits,     int maxHits,
            float  cellSize,
            out int outHitCount);

        // ── 3D Collision (new) ─────────────────────────────────────────────────

        /// <summary>
        /// Spatial-grid collision check (3D). Pass 0.0f for default cell_size (4.0).
        /// </summary>
        [DllImport(DLL)]
        public static extern void check_hits_grid_3d(
            IntPtr projs,       int projCount,
            IntPtr targets,     int targetCount,
            IntPtr outHits,     int maxHits,
            float  cellSize,
            out int outHitCount);

        // ── Spawn — pattern path (2D, kept for backward compat) ───────────────

        /// <summary>
        /// Write up to maxOut NativeProjectiles using hardcoded Rust pattern math.
        /// Prefer spawn_batch for new code — it avoids the 928µs per-call FFI cost.
        /// C# writes Lifetime, MovementType, Scale etc. AFTER this returns.
        /// </summary>
        [DllImport(DLL)]
        public static extern void spawn_pattern(
            IntPtr req, IntPtr outProjs, int maxOut, out int outCount);

        // ── Spawn — batch path (new, replaces per-call FFI cost) ─────────────

        /// <summary>
        /// Copy a pre-filled 2D projectile array into the sim buffer in one FFI call.
        /// C# or Burst fills projs_in (possibly in parallel for 8+ spawns),
        /// then calls this once. Eliminates the 928µs per-call overhead.
        ///
        /// projs_in  — pointer to temp array filled by BatchSpawnHelper
        /// projs_out — pointer to current end of the 2D sim buffer
        /// max_out   — remaining capacity in sim buffer
        /// out_count — how many were written; caller adds this to its active count
        /// </summary>
        [DllImport(DLL)]
        public static extern void spawn_batch(
            IntPtr projsIn, int count,
            IntPtr projsOut, int maxOut,
            out int outCount);

        /// <summary>
        /// Copy a pre-filled 3D projectile array into the 3D sim buffer in one FFI call.
        /// </summary>
        [DllImport(DLL)]
        public static extern void spawn_batch_3d(
            IntPtr projsIn, int count,
            IntPtr projsOut, int maxOut,
            out int outCount);

        // ── State save / restore ──────────────────────────────────────────────

        /// <summary>
        /// Snapshot 2D sim state into buf (count * 72 bytes required).
        /// Returns bytes written, or 0 if buf too small.
        /// </summary>
        [DllImport(DLL)]
        public static extern int save_state(IntPtr projs, int count, IntPtr buf, int bufLen);

        /// <summary>Restore 2D sim state from snapshot.</summary>
        [DllImport(DLL)]
        public static extern void restore_state(
            IntPtr outProjs, int maxCount, IntPtr buf, int bufLen, out int outCount);

        // ─────────────────────────────────────────────────────────────────────
        //  Startup validation — call once in MID_MasterProjectileSystem.Awake()
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verify that C# struct sizes match the compiled Rust library.
        /// A mismatch = silent memory corruption on every FFI call.
        /// Throws InvalidOperationException if any size mismatches — fix before shipping.
        /// </summary>
        public static void ValidateStructSizes()
        {
            bool ok = true;

            ok &= Check("NativeProjectile (2D)",
                Marshal.SizeOf<NativeProjectile>(), projectile_struct_size(), 72);

            ok &= Check("HitResult (2D)",
                Marshal.SizeOf<HitResult>(), hit_result_struct_size(), 24);

            ok &= Check("CollisionTarget (2D)",
                Marshal.SizeOf<CollisionTarget>(), collision_target_struct_size(), 20);

            ok &= Check("SpawnRequest",
                Marshal.SizeOf<SpawnRequest>(), spawn_request_struct_size(), 32);

            ok &= Check("NativeProjectile3D",
                Marshal.SizeOf<NativeProjectile3D>(), projectile3d_struct_size(), 84);

            ok &= Check("HitResult3D",
                Marshal.SizeOf<HitResult3D>(), hit_result3d_struct_size(), 28);

            ok &= Check("CollisionTarget3D",
                Marshal.SizeOf<CollisionTarget3D>(), collision_target3d_struct_size(), 24);

            if (!ok)
                throw new InvalidOperationException(
                    "[ProjectileLib] Struct size mismatch detected. " +
                    "Check the Unity console for which struct failed. " +
                    "Update field offsets in the C# struct to match the Rust repr(C) layout.");
        }

        private static bool Check(string name, int csharpSize, int rustSize, int expected)
        {
            bool ok = csharpSize == rustSize && csharpSize == expected;
            if (!ok)
            {
                Debug.LogError(
                    $"[ProjectileLib] STRUCT SIZE MISMATCH: {name}\n" +
                    $"  C# Marshal.SizeOf = {csharpSize}\n" +
                    $"  Rust sizeof        = {rustSize}\n" +
                    $"  Expected           = {expected}\n" +
                    $"  All P/Invoke calls for this type are UNSAFE until fixed.");
            }
            return ok;
        }
    }
}
