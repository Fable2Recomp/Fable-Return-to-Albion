// WHICH VIEW A STAGE DRAWS FOR. On split screen player two's cameras — the view's own and every
// overlay camera that mirrors it — carry layer 28 (Engine_ClassicViews.SecondViewLayer), which no
// node uses, so a stage reads CAMERA_VISIBLE_LAYERS to know it draws player two's view. Globals
// are one value per frame for every view, so a per-view global has a second copy and the stage
// picks. Readable only in the stage bodies, like every CAMERA_ built-in.
#ifndef REFII_VIEWS_INCLUDED
#define REFII_VIEWS_INCLUDED

#define REFII_SECOND_VIEW (((CAMERA_VISIBLE_LAYERS) & (1u << 28u)) != 0u)

// The scene depth after the opaque pass (Engine_FxDepthCaptureEffect), the drawing view's. The
// shader declares `fx_scene_depth` itself. Uses explicit textureLod(..., 0.0) so D3D12 / DXIL
// never emits implicit quad derivatives inside non-uniform depth-test branches.
global uniform sampler2D fx_scene_depth_second;
#define REFII_SCENE_DEPTH(uv) (REFII_SECOND_VIEW ? textureLod(fx_scene_depth_second, (uv), 0.0) : textureLod(fx_scene_depth, (uv), 0.0))

#endif
