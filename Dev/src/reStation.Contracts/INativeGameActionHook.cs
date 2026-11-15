// @module: reStation.Contracts.INativeGameActionHook
// @purpose: Optional Tier 3 native PC action injection contract allowing modules to bypass PS1 pad bitmasks and write engine-native controls directly.
// @depends-on: System, reStation.Contracts.IReStationCpuEx
// @exports: NativeInputModeIds, NativeGameActionContext, INativeGameActionHook

using System;

namespace reStation.Contracts;

/// <summary>
/// Standard 18 string identifiers for controller D-pad behavior and isometric movement projection settings.
/// </summary>
public static class NativeInputModeIds
{
    /// <summary>Default D-pad mode mapping D-pad directions to quick-action slots while sticks handle movement.</summary>
    public const string ControllerDpadQuickActions = "quickActions";

    /// <summary>D-pad drives walking speed while the left stick drives running speed.</summary>
    public const string ControllerDpadWalkStickRun = "dpadWalkStickRun";

    /// <summary>Left stick drives walking speed while the D-pad drives running speed.</summary>
    public const string ControllerStickWalkDpadRun = "stickWalkDpadRun";

    /// <summary>Default isometric projection aligned with the square game grid.</summary>
    public const string IsometricMoveCropGrid = "cropGrid";

    /// <summary>Isometric movement rotated to match 2D screen-space WASD / stick directions.</summary>
    public const string IsometricMoveScreenSpace = "screenSpace";

    /// <summary>Normalizes a persisted <c>controllerDpadMode</c> value to one of the canonical Standard 18 tokens.</summary>
    public static string NormalizeControllerDpadMode(string? mode) => mode?.Trim() switch
    {
        var m when string.Equals(m, ControllerDpadWalkStickRun, StringComparison.OrdinalIgnoreCase) => ControllerDpadWalkStickRun,
        var m when string.Equals(m, ControllerStickWalkDpadRun, StringComparison.OrdinalIgnoreCase) => ControllerStickWalkDpadRun,
        _ => ControllerDpadQuickActions
    };

    /// <summary>Normalizes a persisted <c>isometricMoveMode</c> value to one of the canonical Standard 18 tokens.</summary>
    public static string NormalizeIsometricMoveMode(string? mode) => mode?.Trim() switch
    {
        var m when string.Equals(m, IsometricMoveScreenSpace, StringComparison.OrdinalIgnoreCase) => IsometricMoveScreenSpace,
        _ => IsometricMoveCropGrid
    };
}

/// <summary>
/// Rich per-frame native action input payload passed to modules implementing context-aware input overrides.
/// </summary>
/// <param name="MoveX">Normalized horizontal movement axis (<c>-1..1</c>).</param>
/// <param name="MoveY">Normalized vertical movement axis (<c>-1..1</c>, positive = forward/up).</param>
/// <param name="LookX">Horizontal look delta / analog look rate.</param>
/// <param name="LookY">Vertical look delta / analog look rate.</param>
/// <param name="ControllerDpadMode">Normalized Standard 18 <c>controllerDpadMode</c> token.</param>
/// <param name="IsometricMoveMode">Normalized Standard 18 <c>isometricMoveMode</c> token.</param>
/// <param name="RawLeftStickX">Unmerged left analog stick X axis (<c>-1..1</c>).</param>
/// <param name="RawLeftStickY">Unmerged left analog stick Y axis (<c>-1..1</c>).</param>
/// <param name="ControllerConnected">True when at least one active SDL3 gamepad is attached.</param>
public readonly record struct NativeGameActionContext(
    float MoveX,
    float MoveY,
    float LookX,
    float LookY,
    string ControllerDpadMode = NativeInputModeIds.ControllerDpadQuickActions,
    string IsometricMoveMode = NativeInputModeIds.IsometricMoveCropGrid,
    float RawLeftStickX = 0f,
    float RawLeftStickY = 0f,
    bool ControllerConnected = false);

/// <summary>
/// Optional interface implemented by a game module to handle Modern PC named actions directly in PS1 RAM
/// (analog camera steering, native movement injection, mouse-capture yield on UI screens).
/// </summary>
public interface INativeGameActionHook
{
    /// <summary>
    /// Applies held (<paramref name="activeActions"/>), rising-edge (<paramref name="pressedActions"/>), and
    /// directional/look <paramref name="axes"/> (<c>MoveX, MoveY, LookX, LookY</c>) for the current frame.
    /// </summary>
    void ApplyNativeActions(
        IReStationCpuEx cpu,
        ReadOnlySpan<string> activeActions,
        ReadOnlySpan<string> pressedActions,
        (float MoveX, float MoveY, float LookX, float LookY) axes,
        bool gameplayActive,
        out bool releaseMouseCapture);

    /// <summary>
    /// Extended overload carrying normalized <see cref="NativeGameActionContext"/> mode flags alongside analog axes;
    /// defaults to delegating to the 6-parameter <see cref="ApplyNativeActions(IReStationCpuEx, ReadOnlySpan{string}, ReadOnlySpan{string}, ValueTuple{float, float, float, float}, bool, out bool)"/>.
    /// </summary>
    void ApplyNativeActions(
        IReStationCpuEx cpu,
        ReadOnlySpan<string> activeActions,
        ReadOnlySpan<string> pressedActions,
        in NativeGameActionContext context,
        bool gameplayActive,
        out bool releaseMouseCapture)
        => ApplyNativeActions(
            cpu,
            activeActions,
            pressedActions,
            (context.MoveX, context.MoveY, context.LookX, context.LookY),
            gameplayActive,
            out releaseMouseCapture);
}
