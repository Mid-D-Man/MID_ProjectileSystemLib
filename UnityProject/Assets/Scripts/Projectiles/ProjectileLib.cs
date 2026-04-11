// ProjectileLib.cs
// All P/Invoke bindings to the Rust cdylib.
// DllImport name "projectile_core" resolves to:
//   macOS   → libprojectile_core.dylib
//   Windows → projectile_core.dll
//   Linux   → libprojectile_core.so

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    public static class ProjectileLib
    {
        private const string DLL = "projectile_core";

        // ── Core tick ─────────────────────────────────────────────────────────

        /// <summary>
        /// Advance entire simulation by dt. Returns number of projectiles
        /// that died this tick (for C# to recycle trail slots).
        /// </summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int tick_projectiles(
            IntPtr projs,
            int    count,
            float  dt
        );

        // ── Collision ─────────────────────────────────────────────────────────

        /// <summary>
        /// Broad-phase + narrow-phase collision. Writes HitResults into outHits.
        /// </summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void check_hits_grid(
            IntPtr projs,
            int    projCount,
            IntPtr targets,
            int    targetCount,
            IntPtr outHits,
            int    maxHits,
            out int outHitCount
        );

        // ── Spawn ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Generate a pattern into outProjs. Returns number written.
        /// </summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void spawn_pattern(
            IntPtr req,
            IntPtr outProjs,
            int    maxOut,
            out int outCount
        );

        // ── State ─────────────────────────────────────────────────────────────

        /// <summary>Save snapshot. Returns bytes written, 0 if buf too small.</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int save_state(
            IntPtr projs,
            int    count,
            IntPtr buf,
            int    bufLen
        );

        /// <summary>Restore snapshot into outProjs.</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void restore_state(
            IntPtr outProjs,
            int    maxCount,
            IntPtr buf,
            int    bufLen,
            out int outCount
        );

        // ── Sanity check ──────────────────────────────────────────────────────

        /// <summary>
        /// Call this once on startup to validate the struct layout matches.
        /// Logs an error if sizes disagree.
        /// </summary>
        public static void ValidateStructSizes()
        {
            int csharpSize = Marshal.SizeOf<NativeProjectile>();
            if (csharpSize != 72)
            {
                Debug.LogError(
                    $"[ProjectileLib] NativeProjectile size mismatch! " +
                    $"C# reports {csharpSize} bytes, expected 72. " +
                    $"P/Invoke will corrupt memory. Check field offsets.");
            }
            else
            {
                Debug.Log("[ProjectileLib] Struct size OK: 72 bytes");
            }
        }
    }
}
