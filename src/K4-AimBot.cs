using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Core.Attributes;
using System.Text.Json.Serialization;

namespace K4AimBot;

public sealed class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("permission")]
    public string Permission { get; set; } = "@css/root";

    // Snap aim settings
    [JsonPropertyName("snap-aim-fov")]
    public float FieldOfView { get; set; } = 180.0f;

    [JsonPropertyName("snap-aim-distance")]
    public float MaxDistance { get; set; } = 1000.0f;

    [JsonPropertyName("snap-aim-prediction-time")]
    public float SnapAimPredictionTime { get; set; } = 0.025f;

    [JsonPropertyName("snap-aim-bone-target")]
    public int SnapAimBoneTarget { get; set; } = 6;

    [JsonPropertyName("snap-aim-no-recoil")]
    public bool SnapAimNoRecoil { get; set; } = true;

    // Silent aim settings
    [JsonPropertyName("silent-aim-fov")]
    public float SilentAimFov { get; set; } = 60.0f;

    [JsonPropertyName("silent-aim-distance")]
    public float SilentAimDistance { get; set; } = 500.0f;

    [JsonPropertyName("silent-aim-speed")]
    public float SilentAimSpeed { get; set; } = 15.0f;

    [JsonPropertyName("silent-aim-min-speed")]
    public float SilentAimMinSpeed { get; set; } = 5.0f;

    [JsonPropertyName("silent-aim-max-speed")]
    public float SilentAimMaxSpeed { get; set; } = 25.0f;

    [JsonPropertyName("silent-aim-bone-target")]
    public int SilentAimBoneTarget { get; set; } = 0;

    [JsonPropertyName("silent-aim-no-recoil")]
    public bool SilentAimNoRecoil { get; set; } = false;

    // Target selection weights
    [JsonPropertyName("target-fov-weight")]
    public float TargetFovWeight { get; set; } = 3.0f;

    [JsonPropertyName("target-distance-weight")]
    public float TargetDistanceWeight { get; set; } = 1.0f;

    [JsonPropertyName("target-health-weight")]
    public float TargetHealthWeight { get; set; } = 0.5f;

    // Movement compensation
    [JsonPropertyName("movement-compensation-factor")]
    public float MovementCompensationFactor { get; set; } = 0.01f;

    // Smooth aim settings
    [JsonPropertyName("smooth-aim-min-adjustment")]
    public float SmoothAimMinAdjustment { get; set; } = 0.005f;

    [JsonPropertyName("smooth-aim-distance-factor-min")]
    public float SmoothAimDistanceFactorMin { get; set; } = 0.3f;

    [JsonPropertyName("smooth-aim-angle-factor-min")]
    public float SmoothAimAngleFactorMin { get; set; } = 0.3f;

    [JsonPropertyName("smooth-aim-combined-factor")]
    public float SmoothAimCombinedFactor { get; set; } = 0.75f;

    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 3;
}

[MinimumApiVersion(304)]
public partial class Plugin : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "CS2 Server AimBot";
    public override string ModuleVersion => "1.1.2";
    public override string ModuleAuthor => "K4ryuu @ KitsuneLab";
    public override string ModuleDescription => "Server side AimBot for Counter-Strike: 2";

    private static readonly MemoryFunctionVoid<CBasePlayerPawn, QAngle> SnapViewAngles
        = new(GameData.GetSignature("CCSBot_SnapViewAngles"));

    public required PluginConfig Config { get; set; } = new PluginConfig();
    public void OnConfigParsed(PluginConfig config)
    {
        if (config.Version < Config.Version)
            base.Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", this.Config.Version, config.Version);

        this.Config = config;
    }

    public class PlayerAimbotState
    {
        public bool Enabled { get; set; }
        public bool SmoothAim { get; set; }
        public bool NoRecoil { get; set; }
        public float SmoothSpeed { get; set; }
        public QAngle? LastTargetAngle { get; set; }
        public CCSPlayerController? LastTarget { get; set; }
        public float LastTargetTime { get; set; }
    }

    private readonly Dictionary<CCSPlayerController, PlayerAimbotState> AimbotStates = [];

    public override void Load(bool hotReload)
    {
        AddCommand("css_aim", "Toggle aimbot", CommandAim);
        AddCommand("css_silentaim", "Toggle smooth aim", CommandSmoothAim);

        RegisterListener<Listeners.OnTick>(OnTick);

        RegisterEventHandler((EventPlayerDisconnect @event, GameEventInfo info) =>
        {
            CCSPlayerController? player = @event.Userid;
            if (player is null || !player.IsValid)
                return HookResult.Continue;

            AimbotStates.Remove(player);
            return HookResult.Continue;
        });
    }

    private Vector PredictTargetPosition(CCSPlayerController player, CCSPlayerController target, Vector currentTargetPos)
    {
        var playerVelocity = player.PlayerPawn.Value!.AbsVelocity!;
        var targetVelocity = target.PlayerPawn.Value!.AbsVelocity!;

        var predictedPos = currentTargetPos;

        if (targetVelocity.Length() > 1.0f)
        {
            predictedPos = new Vector(
                predictedPos.X + (targetVelocity.X * Config.SnapAimPredictionTime),
                predictedPos.Y + (targetVelocity.Y * Config.SnapAimPredictionTime),
                predictedPos.Z
            );
        }

        float playerXCompensationMultiplier = 1.4f;
        float playerYCompensationMultiplier = 2.7f;

        if (playerVelocity.Length() > 1.0f)
        {
            predictedPos = new Vector(
                predictedPos.X - (playerVelocity.X * Config.SnapAimPredictionTime * playerXCompensationMultiplier),
                predictedPos.Y - (playerVelocity.Y * Config.SnapAimPredictionTime * playerYCompensationMultiplier),
                predictedPos.Z
            );
        }

        return predictedPos;
    }

    private void OnTick()
    {
        if (AimbotStates.Count == 0)
            return;

        foreach (var kvp in AimbotStates.ToList())
        {
            var player = kvp.Key;
            var state = kvp.Value;

            if (!IsValidPlayer(player))
            {
                AimbotStates.Remove(player);
                continue;
            }

            if (state.Enabled)
            {
                if (state.NoRecoil && player.PlayerPawn.Value?.IsValid == true)
                {
                    player.PlayerPawn.Value.AimPunchTickBase = 0;
                    player.PlayerPawn.Value.AimPunchTickFraction = 0;

                    player.PlayerPawn.Value.AimPunchAngle.X = 0;
                    player.PlayerPawn.Value.AimPunchAngle.Y = 0;
                    player.PlayerPawn.Value.AimPunchAngle.Z = 0;

                    player.PlayerPawn.Value.AimPunchAngleVel.X = 0;
                    player.PlayerPawn.Value.AimPunchAngleVel.Y = 0;
                    player.PlayerPawn.Value.AimPunchAngleVel.Z = 0;
                }

                var maxDistance = state.SmoothAim ? Config.SilentAimDistance : Config.MaxDistance;
                var maxFov = state.SmoothAim ? Config.SilentAimFov : Config.FieldOfView;

                var target = FindClosestEnemy(player, maxDistance, maxFov, state);
                if (target != null)
                {
                    var skeleton = target.PlayerPawn.Value!.CBodyComponent!.SceneNode!.GetSkeletonInstance();
                    var targetHeadPos = GetBonePosition(skeleton, GetTargetBone(state.SmoothAim));
                    var playerEyePos = GetEyePosition(player);

                    var predictedPos = PredictTargetPosition(player, target, targetHeadPos);
                    var targetAngle = CalculateAimAngle(playerEyePos, predictedPos, player.PlayerPawn.Value!.AbsVelocity!);

                    if (targetAngle != null)
                    {
                        state.LastTargetAngle = targetAngle;
                        state.LastTarget = target;
                        state.LastTargetTime = Server.TickCount;

                        if (state.SmoothAim)
                        {
                            ApplySmoothAim(player, state, targetAngle, playerEyePos, predictedPos);
                        }
                        else
                        {
                            SnapViewAngles.Invoke(player.PlayerPawn.Value!, targetAngle);
                        }
                    }
                }
            }
        }
    }

    private void ApplySmoothAim(CCSPlayerController player, PlayerAimbotState state, QAngle targetAngle, Vector playerEyePos, Vector predictedPos)
    {
        var currentAngle = player.PlayerPawn.Value!.EyeAngles!;
        float deltaPitch = NormalizeAngle(targetAngle.X - currentAngle.X);
        float deltaYaw = NormalizeAngle(targetAngle.Y - currentAngle.Y);

        float distance = (playerEyePos - predictedPos).Length();
        float distanceFactor = Math.Max(
            Config.SmoothAimDistanceFactorMin,
            Math.Min(1.0f, 300f / distance)
        );

        float angleDiff = (float)Math.Sqrt(deltaPitch * deltaPitch + deltaYaw * deltaYaw);
        float angleFactor = Math.Max(
            Config.SmoothAimAngleFactorMin,
            Math.Min(1.0f, 45f / angleDiff)
        );

        float combinedFactor = (distanceFactor + angleFactor) * Config.SmoothAimCombinedFactor;

        float baseStep = Math.Clamp(
            state.SmoothSpeed * (1.0f / 64.0f),
            Config.SilentAimMinSpeed * (1.0f / 64.0f),
            Config.SilentAimMaxSpeed * (1.0f / 64.0f)
        );

        float finalStep = baseStep * combinedFactor;

        float newPitch = currentAngle.X;
        float newYaw = currentAngle.Y;

        if (Math.Abs(deltaPitch) > Config.SmoothAimMinAdjustment)
            newPitch += deltaPitch * finalStep;

        if (Math.Abs(deltaYaw) > Config.SmoothAimMinAdjustment)
            newYaw += deltaYaw * finalStep;

        var newAngle = new QAngle(newPitch, newYaw, 0);
        SnapViewAngles.Invoke(player.PlayerPawn.Value!, newAngle);
    }

    private CCSPlayerController? FindClosestEnemy(CCSPlayerController player, float maxDistance, float maxFov, PlayerAimbotState state)
    {
        float bestScore = float.MaxValue;
        CCSPlayerController? bestTarget = null;
        var currentAngle = player.PlayerPawn.Value!.EyeAngles!;
        var playerEyePos = GetEyePosition(player);

        if (state.LastTarget != null && IsValidPlayer(state.LastTarget) && Server.TickCount - state.LastTargetTime < 32)
        {
            var lastTargetDistance = (playerEyePos - GetEyePosition(state.LastTarget)).Length();
            if (lastTargetDistance <= maxDistance * 1.2f)
            {
                return state.LastTarget;
            }
        }

        foreach (var target in Utilities.GetPlayers())
        {
            if (!IsValidPlayer(target) || target.TeamNum == player.TeamNum)
                continue;

            var distance = (playerEyePos - GetEyePosition(target)).Length();
            if (distance > maxDistance)
                continue;

            var targetHeadPos = GetBonePosition(target.PlayerPawn.Value!.CBodyComponent!.SceneNode!.GetSkeletonInstance(), GetTargetBone(state.SmoothAim));
            var predictedPos = PredictTargetPosition(player, target, targetHeadPos);
            var aimAngle = CalculateAimAngle(playerEyePos, predictedPos, player.PlayerPawn.Value!.AbsVelocity!);

            if (aimAngle == null)
                continue;

            float yawDiff = Math.Abs(NormalizeAngle(aimAngle.Y - currentAngle.Y));
            float pitchDiff = Math.Abs(NormalizeAngle(aimAngle.X - currentAngle.X));
            float fov = (float)Math.Sqrt(yawDiff * yawDiff + pitchDiff * pitchDiff);

            if (fov > maxFov)
                continue;

            float normalizedFov = fov / maxFov;
            float normalizedDistance = distance / maxDistance;
            float normalizedHealth = target.PlayerPawn.Value!.Health / 100.0f;

            float score = (normalizedFov * Config.TargetFovWeight) +
                         (normalizedDistance * Config.TargetDistanceWeight) +
                         (normalizedHealth * Config.TargetHealthWeight);

            if (score < bestScore)
            {
                bestScore = score;
                bestTarget = target;
            }
        }

        return bestTarget;
    }

    private QAngle? CalculateAimAngle(Vector start, Vector target, Vector playerVelocity)
    {
        Vector compensatedStart = new Vector(
            start.X - (playerVelocity.X * Config.MovementCompensationFactor),
            start.Y - (playerVelocity.Y * Config.MovementCompensationFactor),
            start.Z
        );

        Vector delta = target - compensatedStart;
        float hyp = (float)Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

        float yaw = NormalizeAngle((float)(Math.Atan2(delta.Y, delta.X) * 180.0 / Math.PI));
        float pitch = Math.Clamp(-(float)(Math.Atan2(delta.Z, hyp) * 180.0 / Math.PI), -89f, 89f);

        return new QAngle(pitch, yaw, 0);
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > 180) angle -= 360;
        while (angle < -180) angle += 360;
        return angle;
    }

    private static bool IsValidPlayer(CCSPlayerController? player) =>
        player is { IsValid: true, IsHLTV: false } &&
        player.PlayerPawn?.IsValid == true &&
        player.TeamNum > 1 &&
        player.PlayerPawn.Value!.LifeState == (byte)LifeState_t.LIFE_ALIVE;

    private static Vector GetEyePosition(CCSPlayerController player)
    {
        if (player?.PlayerPawn?.Value == null)
            return Vector.Zero;

        var origin = player.PlayerPawn.Value!.AbsOrigin!;
        var viewOffset = player.PlayerPawn.Value!.CameraServices!.OldPlayerViewOffsetZ;
        return new Vector(origin.X, origin.Y, origin.Z + viewOffset);
    }

    public static unsafe Vector GetBonePosition(CSkeletonInstance skeletonInstance, BoneNumber boneType)
    {
        var boneMatrixPointer = Unsafe.Read<IntPtr>((void*)(skeletonInstance.ModelState.Handle + 0x80));
        var boneBaseAddr = boneMatrixPointer + (int)boneType * 32;

        return new Vector(
            Unsafe.Read<float>((void*)(boneBaseAddr + 0)),
            Unsafe.Read<float>((void*)(boneBaseAddr + 4)),
            Unsafe.Read<float>((void*)(boneBaseAddr + 8)));
    }

    public void CommandSmoothAim(CCSPlayerController? player, CommandInfo command)
    {
        if (!ValidateCommand(player))
            return;

        SetAimState(player!, true, Config.SilentAimNoRecoil);
    }

    private bool ValidateCommand(CCSPlayerController? player)
        => player != null && player.IsValid && AdminManager.PlayerHasPermissions(player, Config.Permission);

    public void CommandAim(CCSPlayerController? player, CommandInfo command)
    {
        if (!ValidateCommand(player))
            return;

        SetAimState(player!, false, Config.SnapAimNoRecoil);
    }

    public void SetAimState(CCSPlayerController player, bool smoothAim, bool noRecoil)
    {
        if (AimbotStates.TryGetValue(player, out var state))
        {
            if (state.SmoothAim == smoothAim)
            {
                state.Enabled = !state.Enabled;
            }
            else
            {
                state.SmoothAim = smoothAim;
            }

            state.NoRecoil = noRecoil;

            player!.PrintToCenterAlert($"AIMBOT: {(state.Enabled ? "On" : "Off")}\nMODE: {(state.SmoothAim ? "Silent" : "Snap")}\nNORECOIL: {(state.NoRecoil ? "On" : "Off")}");

            player.ReplicateConVar("weapon_accuracy_nospread", state.Enabled ? "1" : "0");
            player.ReplicateConVar("weapon_air_spread_scale", state.Enabled ? "0" : "1");
        }
        else
        {
            var newState = new PlayerAimbotState
            {
                Enabled = true,
                SmoothAim = smoothAim,
                NoRecoil = noRecoil,
                SmoothSpeed = Config.SilentAimSpeed,
                LastTargetTime = 0
            };

            AimbotStates.Add(player, newState);

            player!.PrintToCenterAlert($"AIMBOT: {(newState.Enabled ? "On" : "Off")}\nMODE: {(newState.SmoothAim ? "Silent" : "Snap")}\nNORECOIL: {(newState.NoRecoil ? "On" : "Off")}");

            player.ReplicateConVar("weapon_accuracy_nospread", "1");
            player.ReplicateConVar("weapon_air_spread_scale", "0");
        }
    }

    private BoneNumber GetTargetBone(bool isSilentAim)
    {
        int boneTarget = isSilentAim ? Config.SilentAimBoneTarget : Config.SnapAimBoneTarget;

        if (Enum.IsDefined(typeof(BoneNumber), boneTarget))
        {
            return (BoneNumber)boneTarget;
        }

        return isSilentAim ? BoneNumber.Waist : BoneNumber.Head;
    }

    public enum BoneNumber
    {
        Waist = 0,
        Neck = 5,
        Head = 6,
        ShoulderLeft = 8,
        ForeLeft = 9,
        HandLeft = 11,
        ShoulderRight = 13,
        ForeRight = 14,
        HandRight = 16,
        KneeLeft = 23,
        FeetLeft = 24,
        KneeRight = 26,
        FeetRight = 27
    }
}