# Fable 2 Havok Collision Architecture & Hooking Reference

This document provides a comprehensive technical reference for the static and dynamic collision architecture in *Fable 2 (Refii)*, the reverse-engineered Havok 5.1 runtime mechanics, the experiments conducted with runtime CD-point interception, why blind bounding-box exclusion causes terrain void fall-throughs, and viable future roadmaps for static collision modification.

---

## 1. Engine Collision Architecture Overview

*Fable 2* utilizes the **Havok Physics SDK version 5.1.0-r1** on the Xbox 360 (Big-Endian PPC32). Collision handling is split into two distinct tiers:

```
┌────────────────────────────────────────────────────────────────────────┐
│                          Fable 2 Collision Engine                       │
├─────────────────────────────────────┬──────────────────────────────────┤
│         1. Static Collision         │        2. Dynamic Collision       │
│    (Monolithic Scenario Packfile)   │     (Per-Entity GDB Components)   │
├─────────────────────────────────────┼──────────────────────────────────┤
│ • <scenario>.havok_scenario         │ • Standalone .hkx rigid bodies   │
│ • hkpMoppBvTreeShape                │ • PhysicsSimulationStaticComp    │
│ • hkpExtendedMeshShape              │ • PhysicsSimulationDynamicComp   │
│ • Baked terrain + scenery props     │ • Characters, NPCs, chests,      │
│ • Baked in 2008 by Lionhead baker   │   doors, physics props           │
└─────────────────────────────────────┴──────────────────────────────────┘
```

### Static Scenario Packfile
Every level scenario contains a static collision packfile listed in its scenario manifest (`.list` inside `levels.bnk`). For example:
- `worlds/albion/bwsmarket/defaultscenario/defaultscenario.havok_scenario` (~4.1 MB)
- Packfile Header Magic: `57 E0 E0 57 10 C0 C0 10` (Havok 5.1.0-r1 binary layout)

When loaded into memory, this file deserializes into a single monolithic `hkpPhysicsSystem` containing a static `hkpRigidBody`. Its shape hierarchy is:
1. `hkpMoppBvTreeShape`: Havok's Memory-Optimized Partial Plücker bounding volume tree for ultra-fast spatial broadphase queries.
2. `hkpExtendedMeshShape`: A single unified indexed triangle soup containing the entire world's geometry merged together: terrain, cliffs, roads, buildings, and static scenery props (market stalls, stone benches, tables, street lamps, fences, barrels).

### Dynamic & Placed Collision (GDB Components)
Entities with moving parts, interactive physics, or non-static placement are not baked into the monolithic packfile. Instead, they reference individual Havok assets:
- **`PhysicsSimulationStaticComponent`** (`0x5883C406`): Fixed collision shape attached to an entity instance.
- **`PhysicsSimulationDynamicComponent`** (`0xFC8A57C5`): Dynamic rigid body subject to gravity, impulses, and Havok solver forces.
- **`StaticMeshComponent`** (`0x5010B138`): Pure visual representation referencing a `.mdl` mesh.

When a modder adds a new object or custom building, attaching a `PhysicsSimulationStaticComponent` referencing a dedicated `.hkx` file is the engine's native, intended path.

---

## 2. Hooking the Collision System (`hkpAllCdPointCollector`)

To investigate runtime modification of the monolithic static mesh without re-baking the packfile, we reverse-engineered the collision detection pipeline in `default.xex`.

### The Narrow-Phase Interception Point
During collision detection, the Havok narrowphase pipeline executes manifold tests between broadphase pairs and emits contact points via a callback collector. The primary function for collecting all contact manifolds is `hkpAllCdPointCollector::addCdPoint`:

- **Guest Virtual Address**: `0x82DB0E48` (PPC32)
- **Calling Convention**: `__thiscall` / standard register passing (`r3 = this`, `r4 = const hkContactPoint& point`)
- **Symbol / Signature**:
  ```cpp
  void hkpAllCdPointCollector_addCdPoint(void* this_ptr, const hkContactPoint* point);
  ```

### Data Structures

```cpp
// Alignment: 16 bytes (Havok SIMD vector convention)
struct hkVector4 {
    float x;
    float y;
    float z;
    float w;
};

// hkContactPoint representation in Havok 5.1:
struct hkContactPoint {
    hkVector4 m_position;          // World-space contact location (Fable coords: +Z is Up)
    hkVector4 m_separatingNormal;  // Contact normal pointing away from the colliding body;
                                   // w-component stores the penetration distance / distance
};
```

### Hook Implementation Mechanics (C++)
The hook intercepts every contact point before it is added to the solver pool. If the contact point's world coordinates fall within an active exclusion bounding box, the function returns immediately without forwarding the point to the real collector:

```cpp
#include <cstdint>
#include <cmath>

struct RefiiExclusionZone {
    float min_x, min_y, min_z;
    float max_x, max_y, max_z;
};

// Global exclusion zone registry:
static constexpr size_t kMaxExclusionZones = 256;
static RefiiExclusionZone g_exclusion_zones[kMaxExclusionZones];
static size_t g_exclusion_zone_count = 0;

// Original trampoline:
typedef void (*hkpAllCdPointCollector_addCdPoint_t)(void* this_ptr, const void* cd_point);
static hkpAllCdPointCollector_addCdPoint_t g_original_addCdPoint = nullptr;

static void Hooked_hkpAllCdPointCollector_addCdPoint(void* this_ptr, const void* cd_point) {
    if (cd_point != nullptr && g_exclusion_zone_count > 0) {
        // In Havok 5.1 PPC32, coordinates are big-endian floats:
        const uint32_t* raw = reinterpret_cast<const uint32_t*>(cd_point);
        
        // Byte-swap from guest PPC to host x86_64:
        uint32_t raw_x = _byteswap_ulong(raw[0]);
        uint32_t raw_y = _byteswap_ulong(raw[1]);
        uint32_t raw_z = _byteswap_ulong(raw[2]);
        
        float px = *reinterpret_cast<float*>(&raw_x);
        float py = *reinterpret_cast<float*>(&raw_y);
        float pz = *reinterpret_cast<float*>(&raw_z);

        // Check against active exclusion zones:
        for (size_t i = 0; i < g_exclusion_zone_count; ++i) {
            const auto& zone = g_exclusion_zones[i];
            if (px >= zone.min_x && px <= zone.max_x &&
                py >= zone.min_y && py <= zone.max_y &&
                pz >= zone.min_z && pz <= zone.max_z) {
                // Drop collision: silently ignore contact point!
                return;
            }
        }
    }

    // Call through to original Havok collector:
    g_original_addCdPoint(this_ptr, cd_point);
}
```

---

## 3. Why Blind AABB Exclusion Failed: The "Floor Void" Problem

While the hook functioned flawlessly from an execution standpoint (correctly dropping contact points within specified 3D boxes), practical testing revealed an architectural roadblock rooted in Lionhead's 2008 build pipeline.

### The Terrain Culling Optimization
Back in 2008, the Xbox 360 possessed 512 MB of unified system and video RAM. To optimize memory footprint and Mopp BV-Tree traversal times:
1. Lionhead's level baker placed static scenery props (tables, market stalls, houses, planters) onto the terrain mesh.
2. The baker ran a boolean CSG / occlusion-cull pass: **all terrain and ground polygons directly underneath static props were permanently excised from the static mesh**.
3. Consequently, there is **no continuous ground surface** underneath vanilla props. The top or base of the prop *is* the only collision surface keeping the player from falling into the void.

```
                    VANILLA STATIC COLLISION MESH
                    
        Ground Level                   Ground Level
  ───────────────┐               ┌─────────────────
                 │ ┌───────────┐ │
                 │ │MarketStall│ │
                 │ │ (Coll.)   │ │
                 └─┴───────────┴─┘
                   ▲           ▲
                   │ NO GROUND │
                   │ UNDERNEATH│
```

### The Exclusion Paradox
When a modder moves a market stall to a new position:
1. **Without exclusion**: The visual model moves, but the player runs into an invisible solid collision box at the stall's old vanilla position ("ghost wall").
2. **With AABB exclusion**: The hook suppresses all contact points in that bounding box. But because there is no ground polygon beneath the stall, **a literal hole in the world is opened**, and the player falls through the floor into the abyss.
3. **With Ground Clearance Margin ($Z + 0.15\text{m}$)**: Lowering the exclusion box above the ground avoids the abyss, but the lower portion of the prop collision remains solid, causing the player to trip on invisible stubs.
4. **With Upward-Normal Filtering ($nz \ge 0.50\text{f}$)**: Attempting to drop vertical walls while keeping horizontal floors fails because props have horizontal table surfaces, stall counters, and benches ($nz = 1.0\text{f}$). The tabletop collision remains floating in midair, blocking movement just as severely as before.

Because geometric bounding boxes cannot distinguish between a "prop top" and the "terrain surface" in a merged triangle soup, blind runtime suppression is mathematically inadequate.

---

## 4. Current Modding Guidelines

As a result of these findings, runtime collision exclusion has been completely removed from the production codebase to ensure **zero runtime overhead and zero instability**.

The official modding rules are:

| Action | Supported? | Consequence / Recommendation |
| :--- | :---: | :--- |
| **Move Vanilla Static Scenery** | ⚠️ Avoid | Leaves the baked static collision mesh behind at the vanilla position, creating an invisible wall. |
| **Swap Visual Mesh (`ModelFile`)** | ✅ Fully Supported | Visual mesh updates cleanly in-engine. Collision remains the vanilla shape. Safe for props of similar proportions. |
| **Add New Props / Buildings** | ✅ Fully Supported | Place freely. To provide collision, modders attach a `PhysicsSimulationStaticComponent` referencing a custom `.hkx` file. |
| **Hide / Delete Vanilla Scenery** | ⚠️ Caveat | Hides the visual model, but static collision remains in-place. Recommended only if the area is blocked by another custom object. |

---

## 5. Viable Future Solution Paths

If the team or community revisits static level collision modification in the future, three viable engineering paths exist:

### Path A: Havok Static Mesh Viewport Wireframe Visualizer
- **Concept**: Parse `defaultscenario.havok_scenario` (`hkpMoppBvTreeShape` $\to$ `hkpExtendedMeshShape`) inside the Refii Godot Level Editor and render the actual collision polygons as a semi-transparent wireframe overlay.
- **Advantage**: Modders can visually inspect whether continuous ground exists beneath a prop before deciding to move it.
- **Implementation**: Write a pure C# packfile reader for Havok 5.1 binary tags (`hkClass`, `hkType`, vertex buffers) into Godot `ArrayMesh`.

### Path B: Offline Level Collision Re-Baker
- **Concept**: A standalone tool or editor pipeline that extracts the entire scenario collision geometry to an intermediate format (OBJ / glTF), allows cutting, filling, and prop removal, and then re-bakes the `hkpMoppBvTreeShape` into a new `.havok_scenario` packfile.
- **Advantage**: Completely solves the problem at the root. The level can have a seamless floor, no ghost collision, and no runtime hooking required.
- **Implementation**: Leverage Havok Content Tools 5.5 / 2010 or an open-source Mopp builder (such as the Skyrim / Dark Souls Havok community toolchains).

### Path C: Modular Entity Decomposition
- **Concept**: For major world overhaul mods, extract vanilla prop clusters out of the static scenario mesh and convert each prop into an individual GDB entity equipped with its own `PhysicsSimulationStaticComponent` and `.hkx` asset.
- **Advantage**: 100% modular, fully non-destructive, and natively compatible with Refii's multi-mod load order layering.
