// ProjectileLib.cs
// All P/Invoke bindings to the Rust cdylib / staticlib.
//
// DllImport name resolution:
//   macOS   → libprojectile_core.dylib
//   Windows → projectile_core.dll
//   Linux   → libprojectile_core.so
//   Android → libprojectile_core.so  (inside APK/AAB)
//   iOS     → __Internal             (static lib linked into app binary)
//
// On iOS, IL2CPP requires the symbol "__Internal" for static libs.
// The preprocessor directive below switches at compile time.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    public static class ProjectileLib
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string DLL = "__Internal";
#else
        private const string DLL = "projectile_core";
#endif

        // ── Core tick ─────────────────────────────────────────────────────────

        /// <summary>
        /// Advance entire simulation by dt seconds.
        /// Returns number of projectiles that died this tick.
        /// </summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int tick_projectiles(
            IntPtr projs,
            int    count,
            float  dt
        );

        // ── Collision ─────────────────────────────────────────────────────────

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

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void spawn_pattern(
            IntPtr req,
            IntPtr outProjs,
            int    maxOut,
            out int outCount
        );

        // ── State ─────────────────────────────────────────────────────────────

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern int save_state(
            IntPtr projs,
            int    count,
            IntPtr buf,
            int    bufLen
        );

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void restore_state(
            IntPtr outProjs,
            int    maxCount,
            IntPtr buf,
            int    bufLen,
            out int outCount
        );

        // ── Struct size validation ─────────────────────────────────────────────

        public static bool ValidateStructSizes()
        {
            bool ok = true;

            int projSize = Marshal.SizeOf<NativeProjectile>();
            if (projSize != 72)
            {
                Debug.LogError(
                    $"[ProjectileLib] NativeProjectile size mismatch: " +
                    $"C#={projSize} bytes, expected 72. P/Invoke WILL corrupt memory.");
                ok = false;
            }

            int hitSize = Marshal.SizeOf<HitResult>();
            if (hitSize != 24)
            {
                Debug.LogError(
                    $"[ProjectileLib] HitResult size mismatch: " +
                    $"C#={hitSize} bytes, expected 24.");
                ok = false;
            }

            int targetSize = Marshal.SizeOf<CollisionTarget>();
            if (targetSize != 20)
            {
                Debug.LogError(
                    $"[ProjectileLib] CollisionTarget size mismatch: " +
                    $"C#={targetSize} bytes, expected 20.");
                ok = false;
            }

            int reqSize = Marshal.SizeOf<SpawnRequest>();
            if (reqSize != 32)
            {
                Debug.LogError(
                    $"[ProjectileLib] SpawnRequest size mismatch: " +
                    $"C#={reqSize} bytes, expected 32. " +
                    $"Patterns will use garbage seeds — check field offsets.");
                ok = false;
            }

            if (ok)
                Debug.Log("[ProjectileLib] All struct sizes OK: " +
                          "NativeProjectile=72 HitResult=24 CollisionTarget=20 SpawnRequest=32");

            return ok;
        }
    }
}
