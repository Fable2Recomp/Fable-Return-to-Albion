using System;
using Godot;
using Refii.Animations;
using Refii.Core;
using RigRole = Refii.Animations.Engine_TitleRig.RigRole;

namespace Refii.Level;

public readonly record struct DynamicFootPose(Vector3 Position, Vector2 Forward);
public readonly record struct DynamicActorFeet(ulong Rig, Vector3 Root, DynamicFootPose Left, DynamicFootPose Right);

public sealed class Engine_DynamicFootContact
{
    private bool _initialized;
    private bool _lifted;
    private float _height;
    private float _cooldown;
    private Vector3 _previous;

    public void Reset()
    {
        _initialized = false;
        _lifted = false;
        _cooldown = 0f;
    }

    public bool Advance(float delta, DynamicFootPose foot, DynamicSurfaceContact ground)
    {
        if (!float.IsFinite(delta) || delta <= 0f || !foot.Position.IsFinite() ||
            !foot.Forward.IsFinite() || foot.Forward.LengthSquared() < 0.001f ||
            !ground.Position.IsFinite() || !ground.Normal.IsFinite() || ground.Normal.Y < 0.55f)
        {
            Reset();
            return false;
        }
        float height = foot.Position.Y - ground.Position.Y;
        if (!_initialized || foot.Position.DistanceSquaredTo(_previous) > 4f || delta > 0.25f)
        {
            _initialized = true;
            _lifted = height > 0.065f;
            _height = height;
            _previous = foot.Position;
            _cooldown = 0f;
            return false;
        }
        _cooldown = MathF.Max(0f, _cooldown - delta);
        if (height > 0.065f) _lifted = true;
        bool planted = _lifted && _cooldown <= 0f && height >= -0.18f && height <= 0.04f &&
            height <= _height + 0.002f;
        if (planted)
        {
            _lifted = false;
            _cooldown = 0.15f;
        }
        _height = height;
        _previous = foot.Position;
        return planted;
    }
}

public sealed class Engine_DynamicFootBones
{
    private Skeleton3D? _skeleton;
    private int _left = -1, _right = -1, _leftToe = -1, _rightToe = -1;
    private Transform3D _leftRest, _rightRest;
    private float _leftSole, _rightSole;

    public bool TryRead(Skeleton3D skeleton, Vector3 root, out DynamicActorFeet feet)
    {
        feet = default;
        if (!ReferenceEquals(_skeleton, skeleton))
        {
            _skeleton = skeleton;
            Engine_TitleRig rig = Engine_TitleGame.Active.Rig;
            _left = rig.Bone(skeleton, RigRole.LeftHeel, RigRole.LeftFoot);
            _right = rig.Bone(skeleton, RigRole.RightHeel, RigRole.RightFoot);
            _leftToe = rig.Bone(skeleton, RigRole.LeftToeTip, RigRole.LeftToe);
            _rightToe = rig.Bone(skeleton, RigRole.RightToeTip, RigRole.RightToe);
            if (_left >= 0 && _right >= 0)
            {
                _leftRest = skeleton.GetBoneGlobalRest(_left);
                _rightRest = skeleton.GetBoneGlobalRest(_right);
                _leftSole = SoleHeight(skeleton, _leftRest, _leftToe);
                _rightSole = SoleHeight(skeleton, _rightRest, _rightToe);
            }
        }
        if (_left < 0 || _right < 0 || !skeleton.IsInsideTree()) return false;
        Transform3D world = skeleton.GlobalTransform;
        feet = new(skeleton.GetInstanceId(), root,
            Read(skeleton, world, _left, _leftToe, _leftRest, _leftSole),
            Read(skeleton, world, _right, _rightToe, _rightRest, _rightSole));
        return feet.Left.Position.IsFinite() && feet.Right.Position.IsFinite();
    }

    private static float SoleHeight(Skeleton3D skeleton, Transform3D heel, int toe)
    {
        Vector3 centre = toe >= 0 ? heel.Origin.Lerp(skeleton.GetBoneGlobalRest(toe).Origin, 0.45f) : heel.Origin;
        return Math.Clamp(centre.Y, 0f, 0.2f);
    }

    private static DynamicFootPose Read(Skeleton3D skeleton, Transform3D world, int heel, int toe,
        Transform3D rest, float sole)
    {
        Transform3D pose = skeleton.GetBoneGlobalPose(heel);
        Vector3 at = world * pose.Origin;
        Vector3 direction = world.Basis * (pose.Basis * (rest.Basis.Inverse() * Vector3.Forward));
        if (toe >= 0)
        {
            Vector3 tip = world * skeleton.GetBoneGlobalPose(toe).Origin;
            direction = tip - at;
            at = at.Lerp(tip, 0.45f);
        }
        Vector2 forward = new(direction.X, direction.Z);
        if (forward.LengthSquared() < 0.001f) forward = new(-world.Basis.Z.X, -world.Basis.Z.Z);
        if (forward.LengthSquared() < 0.001f) forward = Vector2.Up;
        at.Y -= sole * world.Basis.Y.Length();
        return new(at, forward.Normalized());
    }
}
