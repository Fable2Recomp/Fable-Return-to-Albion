using Godot;

namespace Refii.Core;

/// <summary>
/// Pure-C# camera world-to-viewport projector caching a camera's view and projection matrices once
/// per frame, eliminating per-sprite <c>IsPositionBehind</c> and <c>UnprojectPosition</c> interop calls.
/// </summary>
public sealed class Engine_CameraProjector
{
	private Camera3D? _camera;
	private ulong _frame = ulong.MaxValue;
	private Transform3D _view;
	private Projection _projection;
	private Vector2 _viewportSize;
	private float _near;
	private bool _valid;

	/// <summary>True when a valid camera snapshot is cached.</summary>
	public bool Valid => _valid;

	/// <summary>Cached viewport pixel dimensions.</summary>
	public Vector2 ViewportSize => _viewportSize;

	/// <summary>Populates the projector directly from explicit view/projection matrices (for unit testing or custom views).</summary>
	public void Set(Transform3D view, Projection projection, Vector2 viewportSize, float near)
	{
		_camera = null;
		_frame = ulong.MaxValue;
		_view = view;
		_projection = projection;
		_viewportSize = viewportSize;
		_near = near;
		_valid = viewportSize.X > 0f && viewportSize.Y > 0f;
	}

	/// <summary>
	/// Captures <paramref name="camera"/>'s view/projection matrices once per <paramref name="frame"/>,
	/// viewport size, or camera change.
	/// </summary>
	public bool Update(Camera3D? camera, Vector2 viewportSize, ulong frame)
	{
		if (camera is null || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree() ||
			viewportSize.X <= 0f || viewportSize.Y <= 0f)
		{
			_valid = false;
			return false;
		}

		if (!_valid || frame != _frame || !ReferenceEquals(camera, _camera) || viewportSize != _viewportSize)
		{
			_camera = camera;
			_frame = frame;
			_viewportSize = viewportSize;
			_view = camera.GlobalTransform.AffineInverse();
			_projection = camera.GetCameraProjection();
			_near = camera.Near;
			_valid = true;
		}

		return true;
	}

	/// <summary>
	/// Projects a Godot world-space point into viewport pixel coordinates (origin top-left, Y down)
	/// and Godot reversed-Z depth (1 at near plane, 0 at far plane). Returns false if behind the
	/// near plane (<c>-eye.Z &lt; Near</c>) or non-finite.
	/// </summary>
	public bool TryProject(Vector3 world, out Vector2 pixel, out float reversedDepth)
	{
		if (!_valid || !world.IsFinite())
		{
			pixel = Vector2.Zero;
			reversedDepth = 0f;
			return false;
		}

		Vector3 eye = _view * world;
		// Matches Camera3D::is_position_behind() / Godot projection convention (-Z forward).
		if (-eye.Z < _near)
		{
			pixel = Vector2.Zero;
			reversedDepth = 0f;
			return false;
		}

		Vector4 clip = _projection * new Vector4(eye.X, eye.Y, eye.Z, 1f);
		if (!float.IsFinite(clip.W) || clip.W <= 1e-4f)
		{
			pixel = Vector2.Zero;
			reversedDepth = 0f;
			return false;
		}

		float invW = 1f / clip.W;
		float ndcX = clip.X * invW;
		float ndcY = clip.Y * invW;
		float ndcZ = clip.Z * invW;
		if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY) || !float.IsFinite(ndcZ))
		{
			pixel = Vector2.Zero;
			reversedDepth = 0f;
			return false;
		}

		pixel = new Vector2(
			((ndcX * 0.5f) + 0.5f) * _viewportSize.X,
			((-ndcY * 0.5f) + 0.5f) * _viewportSize.Y);
		reversedDepth = 0.5f - (0.5f * ndcZ);
		return true;
	}
}
