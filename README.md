# MidMan Projectile System

High-performance networked projectile system for Unity 2022.3 LTS.

## Stack
- **Rust cdylib** — simulation, collision, pattern generation
- **Unity NGO 1.7** on **UTP 1.4** — network layer
- **Graphics.DrawMeshInstanced** — rendering, zero GameObjects per bullet

## Setup
1. Open `UnityProject/` in Unity 2022.3.13f1
2. Install packages from `Packages/manifest.json`
3. Libs are pre-built in `Assets/Plugins/Native/`
4. To rebuild libs manually: `cd projectile_core && cargo build --release`

## Workflows
- `setup-structure` — scaffold empty project (run once)
- `build-rust-libs` — cross-compile and commit native libs
