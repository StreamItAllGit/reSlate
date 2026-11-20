// @module: reStation.Player.Sdl3Native.Vulkan
// @purpose: SDL3 Vulkan window flag constant aligned with reEngine.Host.Rendering.Sdl3Native.
// @depends-on: reStation.Player.Sdl3Native
// @exports: Sdl3Native.WindowVulkan
// @owner: devv-player-host (T6)

namespace reStation.Player;

internal static unsafe partial class Sdl3Native
{
    /// <summary>SDL3 <c>SDL_WINDOW_VULKAN</c> window creation flag.</summary>
    public const ulong WindowVulkan = 0x10000000UL;
}
