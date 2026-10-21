---
name: refii-performance
description: >-
  Use this skill when writing new code, debugging, or auditing existing C# (Godot/) and
  C++ (src/hostbridge/, src/vfs/, guest hooks) code in Refii to minimize CPU overhead.
  Covers early-outs, caching, dirty/generation tracking, avoiding Godot C#->C++ interop
  crossings, consolidating hot-path loops, zero-allocation patterns, and guest memory walk
  optimizations.
---

# Refii CPU Performance & Overhead Reduction Guide

Refii runs a real-time Xbox 360 static recompilation alongside a Godot 4 (.NET 8) presentation host. Hundreds of entities, skeletons, lights, cloth bodies, and VFS streams are evaluated every frame/tick. Small per-entity or per-bone inefficiencies multiply across the scene and cause frame-time spikes or main-thread bottlenecks.

Whenever you inspect existing code or write new code, **actively look for CPU overhead to eliminate and design new logic to do the minimum possible work per frame/tick.**

---

## 1. Core Principles

1. **Do Nothing When Nothing Changed**: Static or stationary objects, settled animations, and unchanged lighting states should skip recomputation via dirty flags, generation counters, or reconcile cadences.
2. **Order Checks by Cost (Early-Out First)**: Check cheap booleans, bitmasks, or integer comparisons before dictionary lookups, math transformations, or native interop calls.
3. **Cross Boundaries Once**: Crossing the C# $\leftrightarrow$ Godot C++ interop boundary or the C# $\leftrightarrow$ `refii_hostbridge` P/Invoke boundary inside inner loops is expensive. Cache values on the managed side or batch-transfer via flat spans/arrays.
4. **Zero Allocations on Hot Paths**: Frame and tick loops (`_Process`, `Update`, `Fold`, `Write`) must never allocate heap objects, closures, LINQ enumerators, or temporary collections.

---

## 2. C# Host Side (`Godot/src/`) Optimization Patterns

### 2.1. Avoid Redundant Godot C# $\leftrightarrow$ C++ Interop Calls
Every property access on a `GodotObject` / `Node3D` (such as `node.GlobalTransform`, `node.Transform`, `node.Visible`, `node.Layers`, `node.IsInsideTree()`, or `GetMeta()`) crosses the managed-to-unmanaged interop boundary and marshals structs.

- **Cache Placed Transforms**: If the host already tracks the entity's world transform in a managed dictionary (e.g., `_written[address]`), read `.Origin` or `.Basis` from that cached `Transform3D` rather than querying `node.GlobalTransform.Origin` every frame.
- **Guard State Writes**: Do not assign `node.Layers = layers` or `node.Visible = visible` unconditionally every frame; check if the value actually changed before invoking the Godot setter, as setters often trigger internal scene-tree notifications and render server updates.
- **Avoid Per-Frame Wrapper Allocations**: Calling `node.GetChildren()` or querying Godot `Array`/`Dictionary` variants allocates managed wrappers on every call. Cache child node references (`MeshInstance3D`, `Skeleton3D`, shadow proxies) at spawn/build time.

### 2.2. Loop Consolidation & Single-Pass Evaluation
Hot methods like `Engine_ClipAnimator.Fold()` or entity update passes often evolve to have multiple `foreach` loops over the same collection.

- **Merge Passes**: If one pass accumulates weights, a second pass picks the highest priority item, and a third pass finds a specific reference, merge them into a single loop over the collection.
- **Dead Loop Pruning**: When refactoring, verify that intermediate values computed in a loop are still consumed downstream; remove orphaned scans immediately.

### 2.3. Conditional Buffer Clearing & Early-Outs
Clearing large per-rig or per-frame scratch arrays (`Array.Clear(_layerRotationWeight, 0, bones)`) has a measurable cost when multiplied across 50–100 rigs per frame.

- **Clear Only When Active**: Guard scratch buffer clears behind the exact condition that uses them (e.g., only clear partner/layer weight arrays when `runnerUid != 0 && topShare < 1f` or when `_overlays.Count > 0`).
- **Distance & Visibility Rate Gating**: Separate *timeline advancement* (cheap scalar math) from *bone sampling and pose writing* (expensive quaternion slerps and `Skeleton3D` server writes). Advance playheads for culled/distant rigs, but early-out before sampling bones when `!writeBones` or outside draw reach.

### 2.4. Zero-Allocation Collections & Struct Keys
- **Reuse Scratch Lists & Dictionaries**: Store working collections (`_dropped`, `_overlays`, `_recordWeight`) as `readonly` instance fields and `.Clear()` them each frame instead of allocating `new List<T>()` or `new Dictionary<K,V>()`.
- **Avoid LINQ & Closures in Frame Loops**: Never use `.Where()`, `.Select()`, `.OrderBy()`, or lambda captures inside per-frame code. If sorting a small reusable list (`_overlays.Sort(...)`), use a static comparison delegate or `IComparer<T>` to avoid delegate allocation.
- **Value-Type Dictionary Keys**: Use `readonly record struct` or `IEquatable<T>` structs (like `ChannelKey`) so `Dictionary<K, V>` lookups do not box or allocate strings.
- **Use `stackalloc` / `Span<T>`**: For P/Invoke calls into `HostBridge` or small temporary buffers, use `stackalloc` or pooled arrays (`ArrayPool<T>`).

### 2.5. Staggered Reconcile Cadences & Generation Counters
Not every world property needs 60 Hz+ verification:
- **Generation Counters**: Track global state changes with a monotonic `long` counter (e.g., `LightingGeneration`). Entities store `(LastPosition, LastGeneration)` and skip relighting unless generation changed or `LastPosition.DistanceSquaredTo(currentPos) > thresholdSq` (always compare squared distances to avoid `Mathf.Sqrt`).
- **Hash-Staggered Reconcile (`Due(address, periodTicks)`)**: Spread periodic maintenance checks (zone resolution, attachment verification, empty-controller retries) across ticks by hashing the entity address modulo the tick period so all entities don't spike on the same frame.

---

## 3. C++ Bridge & Guest Side (`src/hostbridge/`, `src/vfs/`, `src/hooks/`) Optimization Patterns

### 3.1. Minimize Guest Memory & Component Table Walks
In `host_bridge.cpp`, resolving an entity component (`EntityComponent(base, entity, component_id)`) inspects the guest's component table or sorted pair array in big-endian memory.

- **Eliminate Unused Component Lookups**: Never probe for a component on every entity per frame unless the result is actively used.
- **Check Component Bitmasks First**: Before walking a component's pointer table or linked list, check the entity's component presence bitmask or fast accelerator table (`+0x8C`).
- **Cache Resolved Pointers Within a Call**: When inspecting multiple fields of the same component or controller in a single bridge export, resolve the base address once and pass it to helper lambdas.

### 3.2. Early-Out Before Tree Flattening or Linked-List Traversal
Guest data structures (like component 19 animation blend trees or component 22 attachment lists) live in shared guest memory and require bounds/plausibility validation (`plausible(addr)`).

- **Validate Counts and Sentinels Upfront**: Check list size (`size == 0` or `size > kMaxReasonable`) and sentinel equality (`head == sentinel`) before entering traversal loops.
- **Skip Zero-Weight Subtrees**: When flattening blend trees (`flatten` in `host_bridge.cpp`), if a branch's multiplied weight falls below `1e-4f`, do not push its children onto the traversal stack.
- **Bound Iteration Steps**: Always cap linked-list and tree walks (`++steps < 512`) to prevent torn guest reads from causing infinite or runaway loops.

### 3.3. Batch P/Invoke Exports & Flat Word Buffers
P/Invoke call overhead between C# and C++ adds up if called per-entity or per-bone.

- **Pack Multi-Record Exports**: Prefer bulk exports that write flat `uint32_t*` or `float*` word buffers in a single P/Invoke call (e.g., `RefiiHostGetAttachments`, `RefiiHostGetEntityAnimState` writing all leaves at once) rather than exposing fine-grained getters called in a C# loop.
- **Avoid String Formatting/Allocation in C++ Hot Paths**: Use fixed-size byte spans, `string_view`, or FNV-1 hashes (`uint32_t`) instead of constructing `std::string` objects on per-frame bridge calls.
- **Relaxed Atomics for Telemetry**: When incrementing high-frequency diagnostic counters (`RefiiAnimControllerFound`), use `std::memory_order_relaxed` to avoid unnecessary CPU memory barrier stalls across threads.

---

## 4. Performance Review Checklist

Whenever completing a bug fix or feature, run through this checklist on the code you touched:

1. **Per-Frame Audit**: Is any code I added or modified running every frame or every tick?
2. **Early-Out Check**: Can this function return immediately in the common case (e.g., count is 0, flag is unset, entity hasn't moved)?
3. **Cache Check**: Is this recomputing or looking up something (a node, a transform, a component, a hash) that was already computed earlier in the frame or on a previous frame?
4. **Loop Count Check**: Am I iterating over the same collection multiple times when one pass would suffice?
5. **Interop & Allocation Check**: Did I introduce any `new` reference allocations, LINQ queries, closures, or unnecessary Godot property getters/setters inside a hot loop?
