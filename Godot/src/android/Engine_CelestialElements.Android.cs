using Godot;

namespace Refii.Level;

public static partial class Engine_CelestialElements
{
    /// <summary>
    /// The window's view, for hiding a depth-reading overlay (the glares) that cannot put a pixel
    /// on it under `fx_no_depth_reads`. Its material declares the depth texture, so while it is in
    /// the render list Mobile splits the frame's pass and copies depth even when its vertex stage
    /// collapses it; hidden, it leaves the list. Only where the window's camera is the one view
    /// that draws it: not on split screen, and not while water is in view, whose mirrors reflect
    /// the sky from another eye (water reads depth itself, so nothing is lost there).
    /// </summary>
    private readonly struct OverlayView
    {
        /// <summary>NDC units added on every side: a frame's camera turn, as the camera may move
        /// after this runs.</summary>
        public const float Margin = 0.5f;

        /// <summary>Facing kept below zero for the same reason (about three degrees).</summary>
        public const float FacingMargin = 0.05f;

        private readonly bool _cull;
        private readonly Basis _toView;
        private readonly Projection _projection;

        private OverlayView(Basis toView, Projection projection)
        {
            _cull = true;
            _toView = toView;
            _projection = projection;
        }

        public static OverlayView For(Node3D container)
        {
            if (!Refii.Fx.Engine_FxDepthReads.Enabled || Refii.Core.Engine_Views.Split ||
                Engine_WaterReflection.WaterInView ||
                container.GetViewport()?.GetCamera3D() is not { } camera || !camera.IsInsideTree())
            {
                return default;
            }

            return new OverlayView(camera.GlobalTransform.Basis.Inverse(), camera.GetCameraProjection());
        }

        /// <summary>Whether the overlay toward <paramref name="direction"/> may draw; always when not culling.</summary>
        public bool MayReach(Vector3 direction, float distance, Vector2 half) =>
            !_cull || direction.LengthSquared() <= 1e-12f ||
            Engine_CelestialOverlayReach.MayReach(_toView * direction.Normalized(), distance, half, _projection, Margin, FacingMargin);
    }
}
