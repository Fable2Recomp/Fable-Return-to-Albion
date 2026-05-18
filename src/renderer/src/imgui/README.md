# Refii Plume ImGui

This folder owns the renderer-local ImGui path used when `renderer_backend=plume`.
ReXGlue's built-in ImGui drawer depends on its presenter and immediate drawer, so
the Plume renderer mirrors the parts of that stack we need without modifying
ReXGlue itself.

Parity still needed:

- Recreate the ReXGlue renderer settings panels for the Plume path.
- Add a graphics debugger panel for RenderDoc capture, replay launch, and overlay
  controls.
- Add a renderer statistics panel with frame time, present count, backend,
  swapchain size, draw counts, resource counts, and hook state.
- Add shader panels for extracted XenosRecomp shaders, compiled Vulkan shaders,
  and compiled D3D12 shaders.
- Add a frame/debug capture panel that can later list Fable 2 draw calls,
  render targets, textures, vertex buffers, index buffers, shaders, and pipeline
  state.
- Keep keyboard, mouse, wheel, touch, DPI, timing, and style behavior aligned
  with ReXGlue's `rex::ui::ImGuiDrawer`.
