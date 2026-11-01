using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Fx;

/// <summary>
/// `fx_cpu_fastpaths` (<see cref="Engine_FxFastPaths"/>) in an effect's presentation: the whole
/// mesh-particle buffers and the step alphas sent per surface. FableFx.cs calls each one where it
/// stood, behind its reading of the switch.
/// </summary>
public partial class FableFx
{
	/// <summary>
	/// A mesh-particle node's whole buffer as the fast paths last sent it
	/// (<see cref="Engine_FxFastPaths.WriteInstance"/>), so it goes in one server call and a frame
	/// between steps rewrites only the transforms that move.
	/// </summary>
	private sealed class MeshBuffer
	{
		public float[] Data = System.Array.Empty<float>();

		/// <summary>Instances drawn, as last set; -1 when the original path set it.</summary>
		public int Visible = -1;

		/// <summary>Whether <see cref="Data"/> is what the server holds.</summary>
		public bool Sent;
	}

	private readonly Dictionary<string, MeshBuffer> _meshBuffers = new();

	/// <summary>
	/// The fast paths' step alphas as each target last received them: full and half for the
	/// scene surface, the additive and the displacement instance. NaN is not yet sent.
	/// </summary>
	private readonly float[] _stepSent = { float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN };

	/// <summary>The layer instances <see cref="_stepSent"/> describes: a new one starts unsent.</summary>
	private Rid _stepSentAdd;
	private Rid _stepSentDisp;

	private MeshBuffer MeshBufferOf(string path)
	{
		if (!_meshBuffers.TryGetValue(path, out MeshBuffer? buffer))
		{
			buffer = new MeshBuffer();
			_meshBuffers[path] = buffer;
		}

		return buffer;
	}

	/// <summary>
	/// A mesh node whose particles the guest's twins draw: whether its drawn count is still to be
	/// set to zero. The fast paths skip that call when their last one already did.
	/// </summary>
	private bool MeshBufferStandsDown(string path, bool fast)
	{
		MeshBuffer buffer = MeshBufferOf(path);
		bool hide = !fast || buffer.Visible != 0;
		buffer.Visible = fast ? 0 : -1;
		buffer.Sent = false;
		return hide;
	}

	/// <summary>The original path writes this node itself, so the fast paths' buffer no longer holds.</summary>
	private void ForgetMeshBuffer(string path)
	{
		MeshBuffer buffer = MeshBufferOf(path);
		buffer.Visible = -1;
		buffer.Sent = false;
	}

	/// <summary>
	/// FAST: an emit's mesh particles in one `multimesh_set_buffer` instead of a transform and a
	/// colour call per instance: the same floats in the server's layout, the undrawn tail a copy of
	/// the first instance (never drawn, and inside the box the drawn ones make).
	/// </summary>
	private void WriteMeshBuffer(MultiMesh multi, string path, Engine_FxSystem.MeshInstanceData data, int capacity, int count)
	{
		MeshBuffer buffer = MeshBufferOf(path);
		int floats = capacity * Engine_FxFastPaths.MeshInstanceStride;
		if (buffer.Data.Length != floats)
		{
			buffer.Data = new float[floats];
		}

		buffer.Visible = count;
		buffer.Sent = false;
		if (count == 0)
		{
			return;
		}

		Span<float> floatsOut = buffer.Data;
		for (int i = 0; i < count; i++)
		{
			(Transform3D transform, Color colour, Vector4 motion) = data.Instances[i];
			Engine_FxFastPaths.WriteInstance(floatsOut, i, Between(transform, motion), colour);
		}

		Engine_FxFastPaths.FillTail(floatsOut, count);
		Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Fx);
		RenderingServer.MultimeshSetBuffer(multi.GetRid(), (ReadOnlySpan<float>)floatsOut);
		buffer.Sent = true;
	}

	/// <summary>
	/// FAST, between steps: a buffer the last emit sent whole is updated in place and sent once,
	/// with no call for the drawn count. True when it placed the node; otherwise the node is
	/// placed as before and its buffer, if any, is no longer what the server holds.
	/// </summary>
	private bool PlaceFromMeshBuffer(string path, Engine_FxSystem.MeshInstanceData data, MultiMesh multi, bool fast)
	{
		if (fast && _meshBuffers.TryGetValue(path, out MeshBuffer? sent) && sent.Sent)
		{
			Span<float> floats = sent.Data;
			int drawn = Math.Min(data.Instances.Count, sent.Visible);
			bool moved = false;
			for (int i = 0; i < drawn; i++)
			{
				(Transform3D transform, _, Vector4 motion) = data.Instances[i];
				if (motion.X == 0f && motion.Y == 0f && motion.Z == 0f)
				{
					continue;
				}

				Engine_FxFastPaths.WriteTransform(floats, i, Between(transform, motion));
				moved = true;
			}

			if (moved)
			{
				Engine_FxFastPaths.FillTail(floats, sent.Visible);
				Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Fx);
				RenderingServer.MultimeshSetBuffer(multi.GetRid(), (ReadOnlySpan<float>)floats);
			}

			return true;
		}

		if (_meshBuffers.TryGetValue(path, out MeshBuffer? placed))
		{
			placed.Sent = false;
		}

		return false;
	}

	/// <summary>The step alphas the fast paths recorded, dropped when <see cref="PublishStepAlpha"/> writes.</summary>
	private void ForgetStepSent() => System.Array.Fill(_stepSent, float.NaN);

	/// <summary>
	/// FAST: <see cref="PublishStepAlpha"/> after the emit, per target and per uniform. A vertex
	/// that did not move on its last step is drawn where it is whatever the uniform holds, so a
	/// surface is sent only the uniforms its moving vertices read (most effects step at one rate:
	/// one of the two), and only a value it does not already hold. A surface with no moving vertex,
	/// and one with nothing on it, is sent nothing.
	/// </summary>
	private void PublishStepAlphaFast()
	{
		float alpha = _system.StepAlpha;
		float half = _system.StepAlphaHalf;

		// The original path compares against its own record; it must not skip after this one wrote.
		_publishedAlpha = float.NaN;
		if (_surface is not null && IsInstanceValid(_surface))
		{
			int classes = _system.MotionClasses(_mesh);
			if ((classes & 1) != 0 && _stepSent[0] != alpha)
			{
				_surface.SetInstanceShaderParameter(StepAlphaParam, alpha);
				_stepSent[0] = alpha;
			}

			if ((classes & 2) != 0 && _stepSent[1] != half)
			{
				_surface.SetInstanceShaderParameter(StepAlphaHalfParam, half);
				_stepSent[1] = half;
			}
		}

		if (_emitAddActive && _addInstance is not null)
		{
			if (_stepSentAdd != _addInstance.Rid)
			{
				_stepSentAdd = _addInstance.Rid;
				_stepSent[2] = _stepSent[3] = float.NaN;
			}

			PublishLayerStep(_addInstance, _system.MotionClasses(_addMesh), alpha, half, 2);
		}

		if (_emitDispActive && _dispInstance is not null)
		{
			if (_stepSentDisp != _dispInstance.Rid)
			{
				_stepSentDisp = _dispInstance.Rid;
				_stepSent[4] = _stepSent[5] = float.NaN;
			}

			PublishLayerStep(_dispInstance, _system.MotionClasses(_dispMesh), alpha, half, 4);
		}
	}

	private void PublishLayerStep(Engine_FxLayerInstance instance, int classes, float alpha, float half, int slot)
	{
		if ((classes & 1) != 0 && _stepSent[slot] != alpha)
		{
			instance.SetShaderParameter(StepAlphaParam, alpha);
			_stepSent[slot] = alpha;
		}

		if ((classes & 2) != 0 && _stepSent[slot + 1] != half)
		{
			instance.SetShaderParameter(StepAlphaHalfParam, half);
			_stepSent[slot + 1] = half;
		}
	}
}
