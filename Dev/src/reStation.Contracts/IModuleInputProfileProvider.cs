// @module: reStation.Contracts.IModuleInputProfileProvider
// @purpose: Game-module contract declaring Tier 3 Modern PC named actions with Gameplay vs. Menu context PS1 pad masks.
// @depends-on: System.Collections.Generic, reStation.Contracts.IReStationCpu
// @exports: GameActionDescriptor, IModuleInputProfileProvider

using System.Collections.Generic;

namespace reStation.Contracts;

/// <summary>
/// Describes a single rebindable Modern PC action exported by a game module for Tier 3 input routing.
/// </summary>
/// <param name="ActionId">Canonical camelCase Standard 18 action key stored in <c>modernActionBindings</c>.</param>
/// <param name="Label">Human-readable UI label displayed inside the F1 Keybinds drawer.</param>
/// <param name="DefaultBindingCode">Default SDL3 keyboard scancode (<c>4..511</c>) or mouse button code (<c>1001..1005</c>).</param>
/// <param name="GameplayPadMask">Active-high <c>PsxPadButton</c> bitmask emitted during 3D/world gameplay.</param>
/// <param name="MenuPadMask">Active-high <c>PsxPadButton</c> bitmask emitted while menus, title screens, or dialogs are active.</param>
/// <param name="IsQuickSave">True when rising edge triggers engine Quick Save F5.</param>
/// <param name="IsQuickLoad">True when rising edge triggers engine Quick Load F9.</param>
public sealed record GameActionDescriptor(
    string ActionId,
    string Label,
    int DefaultBindingCode,
    ushort GameplayPadMask,
    ushort MenuPadMask,
    bool IsQuickSave = false,
    bool IsQuickLoad = false);

/// <summary>
/// Optional interface implemented by a game module to provide Tier 3 Modern PC action definitions and gameplay/menu context detection.
/// </summary>
public interface IModuleInputProfileProvider
{
    /// <summary>Display title of the Modern PC control scheme shown in the Keybinds drawer.</summary>
    string ProfileDisplayName { get; }

    /// <summary>Ordered list of rebindable actions exposed to the Modern PC keybinds UI.</summary>
    IReadOnlyList<GameActionDescriptor> ModernActions { get; }

    /// <summary>Returns <see langword="true"/> when active in-world gameplay controls apply, or <see langword="false"/> for menus/title.</summary>
    bool IsGameplayContextActive(IReStationCpu cpu);
}
