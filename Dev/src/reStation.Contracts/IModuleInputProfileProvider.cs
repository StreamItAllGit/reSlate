// @module: reStation.Contracts.IModuleInputProfileProvider
// @purpose: Game-module contract declaring Tier 3 Modern PC named actions with multi-device default bindings and Gameplay vs. Menu pad masks.
// @depends-on: System, System.Collections.Generic, reStation.Contracts.GamepadBindingCode, reStation.Contracts.IReStationCpu
// @exports: GameActionDescriptor, IModuleInputProfileProvider

using System;
using System.Collections.Generic;

namespace reStation.Contracts;

/// <summary>
/// Describes a single rebindable Modern PC action exported by a game module for Tier 3 input routing
/// across keyboard/mouse (<c>4..511</c>, <c>1001..1005</c>) and SDL3 gamepad (<c>2001..2032</c>) bindings.
/// </summary>
public sealed record GameActionDescriptor
{
    /// <summary>Maximum number of default or user-assigned bindings per device category.</summary>
    public const int MaxBindingsPerDevice = 3;

    /// <summary>
    /// Initializes a <see cref="GameActionDescriptor"/> with multi-slot keyboard/mouse and controller defaults.
    /// </summary>
    /// <param name="ActionId">Canonical camelCase Standard 18 action key stored in <c>modernActionBindings</c>.</param>
    /// <param name="Label">Human-readable UI label displayed inside the F1 Keybinds drawer.</param>
    /// <param name="DefaultBindingCodes">Default SDL3 keyboard (<c>4..511</c>) or mouse (<c>1001..1005</c>) binding codes.</param>
    /// <param name="DefaultControllerCodes">Default SDL3 gamepad binding codes (<c>2001..2032</c>).</param>
    /// <param name="GameplayPadMask">Active-high <c>PsxPadButton</c> bitmask emitted during 3D/world gameplay.</param>
    /// <param name="MenuPadMask">Active-high <c>PsxPadButton</c> bitmask emitted while menus, title screens, or dialogs are active.</param>
    /// <param name="IsQuickSave">True when rising edge triggers engine Quick Save F5.</param>
    /// <param name="IsQuickLoad">True when rising edge triggers engine Quick Load F9.</param>
    public GameActionDescriptor(
        string ActionId,
        string Label,
        IReadOnlyList<int>? DefaultBindingCodes,
        IReadOnlyList<int>? DefaultControllerCodes,
        ushort GameplayPadMask,
        ushort MenuPadMask,
        bool IsQuickSave = false,
        bool IsQuickLoad = false)
    {
        this.ActionId = ActionId;
        this.Label = Label;
        this.DefaultBindingCodes = NormalizeBindings(DefaultBindingCodes);
        this.DefaultControllerCodes = NormalizeBindings(DefaultControllerCodes);
        this.GameplayPadMask = GameplayPadMask;
        this.MenuPadMask = MenuPadMask;
        this.IsQuickSave = IsQuickSave;
        this.IsQuickLoad = IsQuickLoad;
    }

    /// <summary>
    /// Legacy v0.1.5 constructor preserving single-binding modules by promoting a non-zero
    /// <paramref name="DefaultBindingCode"/> to <c>[DefaultBindingCode]</c> and an empty controller list.
    /// </summary>
    public GameActionDescriptor(
        string ActionId,
        string Label,
        int DefaultBindingCode,
        ushort GameplayPadMask,
        ushort MenuPadMask,
        bool IsQuickSave = false,
        bool IsQuickLoad = false)
        : this(
            ActionId,
            Label,
            DefaultBindingCode > 0 ? new[] { DefaultBindingCode } : Array.Empty<int>(),
            Array.Empty<int>(),
            GameplayPadMask,
            MenuPadMask,
            IsQuickSave,
            IsQuickLoad)
    {
    }

    /// <summary>Canonical camelCase Standard 18 action key stored in <c>modernActionBindings</c>.</summary>
    public string ActionId { get; init; }

    /// <summary>Human-readable UI label displayed inside the F1 Keybinds drawer.</summary>
    public string Label { get; init; }

    /// <summary>Ordered default keyboard scancodes (<c>4..511</c>) and/or mouse button codes (<c>1001..1005</c>).</summary>
    public IReadOnlyList<int> DefaultBindingCodes { get; init; }

    /// <summary>Ordered default SDL3 gamepad binding codes (<c>2001..2032</c>).</summary>
    public IReadOnlyList<int> DefaultControllerCodes { get; init; }

    /// <summary>Primary keyboard/mouse default binding code (<c>0</c> when unbound), preserved for legacy callers.</summary>
    public int DefaultBindingCode => DefaultBindingCodes.Count > 0 ? DefaultBindingCodes[0] : 0;

    /// <summary>Primary SDL3 gamepad default binding code (<see cref="GamepadBindingCode.None"/> when unbound).</summary>
    public int DefaultControllerCode => DefaultControllerCodes.Count > 0 ? DefaultControllerCodes[0] : GamepadBindingCode.None;

    /// <summary>Active-high <c>PsxPadButton</c> bitmask emitted during 3D/world gameplay.</summary>
    public ushort GameplayPadMask { get; init; }

    /// <summary>Active-high <c>PsxPadButton</c> bitmask emitted while menus, title screens, or dialogs are active.</summary>
    public ushort MenuPadMask { get; init; }

    /// <summary>True when rising edge triggers engine Quick Save F5.</summary>
    public bool IsQuickSave { get; init; }

    /// <summary>True when rising edge triggers engine Quick Load F9.</summary>
    public bool IsQuickLoad { get; init; }

    private static IReadOnlyList<int> NormalizeBindings(IReadOnlyList<int>? codes)
    {
        if (codes is null || codes.Count == 0)
            return Array.Empty<int>();

        List<int> normalized = new(Math.Min(codes.Count, MaxBindingsPerDevice));
        foreach (int code in codes)
        {
            if (code <= 0 || normalized.Contains(code))
                continue;
            normalized.Add(code);
            if (normalized.Count >= MaxBindingsPerDevice)
                break;
        }
        return normalized.Count == 0 ? Array.Empty<int>() : Array.AsReadOnly(normalized.ToArray());
    }
}

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
