using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.Host;
using Refii.Save;

namespace Refii.Titles.Fable2;

using Refii.Game;

public partial class GameGoldenCockCollision : CharacterBody3D
{
    public const uint PropLayer = 1u << 20;
    public const float Radius = 0.25f;
    public const float Height = 1.2f;
    public const float TeleportDistance = 2f;

    private static GameGoldenCockCollision? _current;
    private (HostBridge.GuestEntity Entity, uint Frame)? _observed;
    private Vector3? _safe;
    private ulong _hero;
    private uint _frame;
    private bool _consumed;
    private string _level = string.Empty;
    private ulong _corrections;
    private uint _queued;

    private static bool Enabled => GameGoldenCock.GuestRuns && HostBridge.IsInGame &&
        GameUi.WorldInteractionAllowed && !GameGoldenCock.OwnsInput &&
        GameGoldenCock.SessionAllowsInteraction(CoopSession.State, CoopSession.Side, CoopSession.LocalCoop);

    public static Dictionary<string, object?> Diagnostics()
    {
        var current = _current;
        Vector3? desired = current?._observed is { } sample ? ToGodot(sample.Entity.Position) : null;
        float[]? Point(Vector3? point) => point is { } p ? new[] { p.X, p.Y, p.Z } : null;
        return new()
        {
            ["active"] = current is not null,
            ["enabled"] = current is not null && Enabled,
            ["session"] = CoopSession.State.ToString(),
            ["role"] = CoopSession.Side.ToString(),
            ["worldInteraction"] = GameUi.WorldInteractionAllowed,
            ["paused"] = GameSession.Current?.Paused,
            ["hero"] = Point(desired),
            ["resolved"] = Point(current?._safe),
            ["nearCollider"] = desired is { } position && GameGoldenCock.HasColliderNear(position),
            ["corrections"] = current?._corrections ?? 0,
            ["lastQueueDepth"] = current?._queued ?? 0,
            ["frame"] = current?._frame ?? 0,
            ["level"] = current?._level
        };
    }

    public override void _Ready()
    {
        ProcessPriority = int.MaxValue - 2;
        CollisionLayer = 0;
        CollisionMask = PropLayer;
        AddChild(new CollisionShape3D
        {
            Name = "HeroCapsule",
            Position = Vector3.Up * Height * 0.5f,
            Shape = new CapsuleShape3D { Radius = Radius, Height = Height }
        });
        _level = Engine_GoldenCockPlacements.Load()?.LevelName ?? string.Empty;
        _current = this;
    }

    public static void ObserveHero(HostBridge.GuestEntity entity, uint frame)
    {
        if (_current is { } controller) controller._observed = (entity, frame);
    }

    public override void _Process(double delta)
    {
        if (_observed is not { } sample) return;
        ApplySample(sample.Entity, sample.Frame, Enabled, HostBridge.PostFrontEndAction);
    }

    public void ApplySample(HostBridge.GuestEntity entity, uint frame, bool enabled, Func<string, uint> post)
    {
        if (!enabled)
        {
            _safe = null;
            _consumed = false;
            return;
        }
        if (_consumed && _frame == frame && _hero == entity.UniqueId) return;
        _consumed = true;
        _frame = frame;
        Vector3 desired = ToGodot(entity.Position);
        if (!desired.IsFinite()) { _safe = null; return; }
        if (_hero != entity.UniqueId || _safe is not { } previous ||
            previous.DistanceSquaredTo(desired) > TeleportDistance * TeleportDistance ||
            !GameGoldenCock.HasColliderNear(desired))
        {
            _hero = entity.UniqueId;
            _safe = desired;
            return;
        }
        Vector3 resolved = ResolveMovement(previous, desired);
        _safe = resolved;
        if (resolved.DistanceSquaredTo(desired) < 0.000004f) return;
        string action = CorrectionAction(entity.Position, ToGuest(resolved), _level);
        _queued = action.Length == 0 ? 0 : post(action);
        if (_queued == 0) _safe = desired;
        else _corrections++;
    }

    public Vector3 ResolveMovement(Vector3 from, Vector3 desired)
    {
        GlobalPosition = from;
        Vector3 remaining = desired - from;
        for (int slide = 0; slide < 4; slide++)
        {
            using var collision = MoveAndCollide(remaining, safeMargin: 0.002f, recoveryAsCollision: true);
            if (collision is null) break;
            remaining = collision.GetRemainder().Slide(collision.GetNormal());
            if (remaining.LengthSquared() < 0.000001f) break;
        }
        return GlobalPosition;
    }

    public static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);
    public static Vector3 ToGuest(Vector3 position) => new(-position.X, position.Z, position.Y);

    public static string CorrectionAction(Vector3 expected, Vector3 target, string level)
    {
        if (!expected.IsFinite() || !target.IsFinite() || expected.DistanceSquaredTo(target) > TeleportDistance * TeleportDistance ||
            level.Length == 0 || !level.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return string.Empty;
        string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        string Vector(Vector3 value) => $"CVector3({Number(value.X)},{Number(value.Y)},{Number(value.Z)})";
        string script = $"local h=QuestManager and QuestManager.HeroEntity if h and h:IsAlive() and IsLevelLoaded('{level}') then " +
            $"local p=h:GetPosition() local d=p-{Vector(expected)} if d:GetSquaredLength()<0.5625 then h:SetPosition({Vector(target)}) end end";
        return script.Length < 0x3b0 ? "debug-gamelua:" + script : string.Empty;
    }

    public override void _ExitTree()
    {
        if (_current == this) _current = null;
    }
}
