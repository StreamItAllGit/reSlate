// @module: reStation.Contracts.IReStationHostEx
// @purpose: Additive v0.1.1 host contract exposing display, timing, capture, mouse-look, and sub-tick presentation APIs.
// @depends-on: reStation.Contracts.IRecompModule
// @exports: IReStationHostEx

namespace reStation.Contracts;

/// <summary>Additive host surface enabling sub-tick pacing, high-Hz mouse look polling, and frame presentation from modules.</summary>
public interface IReStationHostEx : IReStationHost
{
    /// <summary>True when running in headless verification mode without a visible window.</summary>
    bool IsHeadless { get; }
    /// <summary>True when a screenshot/RGBA frame capture is pending for the current frame.</summary>
    bool HasPendingCapture { get; }
    /// <summary>Configured host presentation rate in Hz (0 = unlocked).</summary>
    int TargetFps { get; }
    /// <summary>True when modern free-look mouse capture is currently enabled.</summary>
    bool IsModernMouseLookActive { get; }
    /// <summary>Configured mouse sensitivity multiplier for modern look input.</summary>
    float MouseSensitivity { get; }
    /// <summary>Paces execution until <paramref name="targetStopwatchTicks"/> using high-resolution host wait.</summary>
    void WaitUntilTimestamp(long targetStopwatchTicks);
    /// <summary>Polls accumulated mouse deltas and analog stick axes for the current sub-tick slice.</summary>
    (float MouseDx, float MouseDy, float StickX, float StickY) PollSubTickLook();
    /// <summary>Presents the rendered sub-tick frame and drains any queued GPU commands on the render thread.</summary>
    void PresentSubTickFrame();
}
