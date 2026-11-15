// @module: reStation.Contracts.GamepadBindingCode
// @purpose: Canonical SDL3 gamepad button/trigger binding codes (2001..2032) and classification/label helpers shared across Input, Keybinds UI, and Modules.
// @depends-on: System, System.Collections.Generic
// @exports: GamepadBindingCode

using System;
using System.Collections.Generic;

namespace reStation.Contracts;

/// <summary>
/// Canonical integer binding codes for SDL3 gamepad digital buttons and analog triggers (<c>2001..2032</c>),
/// keeping keyboard scancodes (<c>4..511</c>) and mouse button codes (<c>1001..1005</c>) collision-free.
/// </summary>
public static class GamepadBindingCode
{
    /// <summary>Unbound / no binding code.</summary>
    public const int None = 0;

    /// <summary>Minimum reserved gamepad binding code.</summary>
    public const int MinCode = 2001;

    /// <summary>Maximum reserved gamepad binding code.</summary>
    public const int MaxCode = 2032;

    /// <summary>South face button (Xbox A / PlayStation Cross).</summary>
    public const int South = 2001;

    /// <summary>East face button (Xbox B / PlayStation Circle).</summary>
    public const int East = 2002;

    /// <summary>West face button (Xbox X / PlayStation Square).</summary>
    public const int West = 2003;

    /// <summary>North face button (Xbox Y / PlayStation Triangle).</summary>
    public const int North = 2004;

    /// <summary>Back / View / Select / Share button.</summary>
    public const int Back = 2005;

    /// <summary>Guide / Home / PlayStation button.</summary>
    public const int Guide = 2006;

    /// <summary>Start / Menu / Options button.</summary>
    public const int Start = 2007;

    /// <summary>Left analog stick click (L3).</summary>
    public const int L3 = 2008;

    /// <summary>Right analog stick click (R3).</summary>
    public const int R3 = 2009;

    /// <summary>Left bumper / shoulder button (LB / L1).</summary>
    public const int LB = 2010;

    /// <summary>Right bumper / shoulder button (RB / R1).</summary>
    public const int RB = 2011;

    /// <summary>D-Pad Up.</summary>
    public const int DPadUp = 2012;

    /// <summary>D-Pad Down.</summary>
    public const int DPadDown = 2013;

    /// <summary>D-Pad Left.</summary>
    public const int DPadLeft = 2014;

    /// <summary>D-Pad Right.</summary>
    public const int DPadRight = 2015;

    /// <summary>Left analog trigger treated as digital binding (LT / L2).</summary>
    public const int LT = 2016;

    /// <summary>Right analog trigger treated as digital binding (RT / R2).</summary>
    public const int RT = 2017;

    /// <summary>Miscellaneous controller button 1 (e.g. Capture / Mute).</summary>
    public const int Misc1 = 2018;

    /// <summary>Rear paddle 1 (Right Paddle 1).</summary>
    public const int Paddle1 = 2019;

    /// <summary>DualShock / DualSense center touchpad click.</summary>
    public const int Touchpad = 2020;

    /// <summary>Rear paddle 2 (Left Paddle 1).</summary>
    public const int Paddle2 = 2021;

    /// <summary>Rear paddle 3 (Right Paddle 2).</summary>
    public const int Paddle3 = 2022;

    /// <summary>Rear paddle 4 (Left Paddle 2).</summary>
    public const int Paddle4 = 2023;

    /// <summary>Ordered standard SDL3 gamepad codes exposed in keybind pickers and normalization.</summary>
    public static IReadOnlyList<int> StandardCodes { get; } =
    [
        South, East, West, North,
        Back, Guide, Start,
        L3, R3, LB, RB,
        DPadUp, DPadDown, DPadLeft, DPadRight,
        LT, RT,
        Misc1, Paddle1, Touchpad, Paddle2, Paddle3, Paddle4
    ];

    /// <summary>Returns <see langword="true"/> when <paramref name="code"/> is a valid keyboard scancode (<c>4..511</c>).</summary>
    public static bool IsKeyboardCode(int code) => code is >= 4 and <= 511;

    /// <summary>Returns <see langword="true"/> when <paramref name="code"/> is a valid mouse button code (<c>1001..1005</c>).</summary>
    public static bool IsMouseCode(int code) => code is >= 1001 and <= 1005;

    /// <summary>Returns <see langword="true"/> when <paramref name="code"/> is a valid SDL gamepad binding code (<c>2001..2032</c>).</summary>
    public static bool IsGamepadCode(int code) => code is >= MinCode and <= MaxCode;

    /// <summary>Returns <see langword="true"/> when <paramref name="code"/> identifies an analog trigger treated as digital (<see cref="LT"/> or <see cref="RT"/>).</summary>
    public static bool IsTriggerCode(int code) => code is LT or RT;

    /// <summary>Returns <see langword="true"/> when <paramref name="code"/> is a valid keyboard, mouse, or gamepad binding code.</summary>
    public static bool IsValidBindingCode(int code) =>
        IsKeyboardCode(code) || IsMouseCode(code) || IsGamepadCode(code);

    /// <summary>Clamps any invalid binding code to <see cref="None"/>.</summary>
    public static int Normalize(int code) => IsValidBindingCode(code) ? code : None;

    /// <summary>Maps a zero-based SDL3 <c>SDL_GamepadButton</c> index to a canonical <see cref="GamepadBindingCode"/> or <see cref="None"/>.</summary>
    public static int FromSdlButtonIndex(int sdlButtonIndex) => sdlButtonIndex switch
    {
        >= 0 and <= 14 => MinCode + sdlButtonIndex,
        15 => Misc1,
        16 => Paddle1,
        17 => Paddle2,
        18 => Paddle3,
        19 => Paddle4,
        20 => Touchpad,
        >= 21 and <= (MaxCode - MinCode) => MinCode + sdlButtonIndex,
        _ => None
    };

    /// <summary>Attempts to map a digital <see cref="GamepadBindingCode"/> back to an SDL3 <c>SDL_GamepadButton</c> index; triggers (<see cref="LT"/>, <see cref="RT"/>) return <see langword="false"/>.</summary>
    public static bool TryToSdlButtonIndex(int code, out int sdlButtonIndex)
    {
        sdlButtonIndex = code switch
        {
            >= South and <= DPadRight => code - MinCode,
            Misc1 => 15,
            Paddle1 => 16,
            Paddle2 => 17,
            Paddle3 => 18,
            Paddle4 => 19,
            Touchpad => 20,
            > Paddle4 and <= MaxCode => code - MinCode,
            _ => -1
        };
        return sdlButtonIndex >= 0;
    }

    /// <summary>Formats a concise human-readable UI label for a gamepad binding code.</summary>
    public static string FormatLabel(int code) => code switch
    {
        None => "Unbound",
        South => "Pad South (A/Cross)",
        East => "Pad East (B/Circle)",
        West => "Pad West (X/Square)",
        North => "Pad North (Y/Triangle)",
        Back => "Pad Back/Select",
        Guide => "Pad Guide",
        Start => "Pad Start",
        L3 => "Pad L3",
        R3 => "Pad R3",
        LB => "Pad LB (L1)",
        RB => "Pad RB (R1)",
        DPadUp => "Pad D-Up",
        DPadDown => "Pad D-Down",
        DPadLeft => "Pad D-Left",
        DPadRight => "Pad D-Right",
        LT => "Pad LT (L2)",
        RT => "Pad RT (R2)",
        Misc1 => "Pad Misc",
        Paddle1 => "Pad Paddle 1",
        Touchpad => "Pad Touchpad",
        Paddle2 => "Pad Paddle 2",
        Paddle3 => "Pad Paddle 3",
        Paddle4 => "Pad Paddle 4",
        >= MinCode and <= MaxCode => $"Pad Btn #{code - MinCode}",
        _ => $"Code {code}"
    };
}
