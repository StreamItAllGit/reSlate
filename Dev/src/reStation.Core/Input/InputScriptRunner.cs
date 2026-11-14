// @module: reStation.Core.Input.InputScriptRunner
// @purpose: Deterministic --input-script runner injecting frame-ranged key/mouse holds, UI drawer actions, screenshots, and RAM assertions.
// @depends-on: System.Globalization, System.Text.Json, Foundation.Core, reStation.Contracts, reStation.Core.Cpu
// @exports: InputScriptSpec, InputHoldStep, InputRamAssertion, InputCaptureStep, DrawerScriptCommand, InputScriptRunner

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core.Cpu;

namespace reStation.Core.Input;

public sealed class InputHoldStep
{
    [JsonPropertyName("startFrame")] public long StartFrame { get; set; }
    [JsonPropertyName("endFrame")] public long EndFrame { get; set; }
    [JsonPropertyName("keys")] public List<string>? Keys { get; set; }
    [JsonPropertyName("mouseButtons")] public List<string>? MouseButtons { get; set; }
    [JsonPropertyName("openDrawer")] public bool? OpenDrawer { get; set; }
    [JsonPropertyName("closeDrawer")] public bool? CloseDrawer { get; set; }
    [JsonPropertyName("drawerTab")] public int? DrawerTab { get; set; }
}

public sealed class InputRamAssertion
{
    [JsonPropertyName("frame")] public long Frame { get; set; }
    [JsonPropertyName("addressHex")] public string AddressHex { get; set; } = string.Empty;
    [JsonPropertyName("maskHex")] public string MaskHex { get; set; } = "0xFFFF";
    [JsonPropertyName("expectedEqualsHex")] public string ExpectedEqualsHex { get; set; } = "0x0000";
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
}

public sealed class InputCaptureStep
{
    [JsonPropertyName("frame")] public long Frame { get; set; }
    [JsonPropertyName("screenshotPath")] public string ScreenshotPath { get; set; } = string.Empty;
}

public sealed class InputScriptSpec
{
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("holdFrames")] public List<InputHoldStep> HoldFrames { get; set; } = new();
    [JsonPropertyName("ramAssertions")] public List<InputRamAssertion> RamAssertions { get; set; } = new();
    [JsonPropertyName("captures")] public List<InputCaptureStep> Captures { get; set; } = new();
}

public readonly record struct DrawerScriptCommand(bool Open, bool Close, int? Tab);

/// <summary>Executes scripted keyboard/mouse holds, UI drawer commands, captures, and RAM assertions frame-by-frame.</summary>
public sealed class InputScriptRunner
{
    private readonly InputScriptSpec _spec;
    private readonly string? _sandboxBoundary;
    private int _assertionsPassed;
    private int _assertionsFailed;

    public InputScriptRunner(InputScriptSpec spec, string? sandboxBoundary = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _sandboxBoundary = string.IsNullOrWhiteSpace(sandboxBoundary) ? null : Path.GetFullPath(sandboxBoundary);
    }

    public int AssertionsPassed => _assertionsPassed;
    public int AssertionsFailed => _assertionsFailed;
    public bool HasFailures => _assertionsFailed > 0;

    public static InputScriptRunner Load(string path, string? sandboxBoundary = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(sandboxBoundary)) SandboxEnvironment.AssertPathContained(full, sandboxBoundary);
        string json = File.ReadAllText(full);
        InputScriptSpec spec = JsonSerializer.Deserialize<InputScriptSpec>(json, RecompSettings.JsonOptions)
            ?? throw new InvalidDataException($"Failed to parse input script '{full}'.");
        return new InputScriptRunner(spec, sandboxBoundary);
    }

    /// <summary>Applies scripted key/mouse holds for <paramref name="frame"/> and outputs drawer/capture actions.</summary>
    public void ApplyFrame(
        long frame,
        Span<byte> keyboardState,
        out uint mouseButtonsMask,
        out DrawerScriptCommand drawerCmd,
        out string? screenshotPath)
    {
        mouseButtonsMask = 0u;
        bool openDrawer = false, closeDrawer = false;
        int? drawerTab = null;

        foreach (InputHoldStep step in _spec.HoldFrames)
        {
            if (frame < step.StartFrame || frame > step.EndFrame) continue;
            if (step.Keys is not null)
            {
                foreach (string key in step.Keys)
                {
                    int sc = ResolveScancode(key);
                    if (sc > 0 && sc < keyboardState.Length) keyboardState[sc] = 1;
                }
            }
            if (step.MouseButtons is not null)
            {
                foreach (string mb in step.MouseButtons) mouseButtonsMask |= ResolveMouseMask(mb);
            }
            if (step.OpenDrawer == true) openDrawer = true;
            if (step.CloseDrawer == true) closeDrawer = true;
            if (step.DrawerTab is { } tab) drawerTab = tab;
        }

        drawerCmd = new DrawerScriptCommand(openDrawer, closeDrawer, drawerTab);
        screenshotPath = null;
        foreach (InputCaptureStep cap in _spec.Captures)
        {
            if (cap.Frame != frame || string.IsNullOrWhiteSpace(cap.ScreenshotPath)) continue;
            string fullOut = Path.GetFullPath(cap.ScreenshotPath);
            if (!string.IsNullOrWhiteSpace(_sandboxBoundary))
                SandboxEnvironment.AssertPathContained(fullOut, _sandboxBoundary);
            screenshotPath = fullOut;
        }
    }

    /// <summary>Checks any RAM assertions scheduled on <paramref name="frame"/> and logs structured results.</summary>
    public void EvaluateAssertions(long frame, IReStationCpu cpu, ReStationLogger logger)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(logger);
        foreach (InputRamAssertion rule in _spec.RamAssertions)
        {
            if (rule.Frame != frame) continue;
            uint addr = ParseHexOrDec(rule.AddressHex);
            ushort mask = (ushort)(ParseHexOrDec(rule.MaskHex) & 0xFFFFU);
            ushort expected = (ushort)(ParseHexOrDec(rule.ExpectedEqualsHex) & 0xFFFFU);
            ushort actual = cpu.Read16(addr);
            bool ok = (actual & mask) == expected;
            if (ok)
            {
                _assertionsPassed++;
                logger.Info("INPUT_ASSERT", $"Frame {frame} '{rule.Label}' addr=0x{addr:X8} val=0x{actual:X4} mask=0x{mask:X4} PASS");
            }
            else
            {
                _assertionsFailed++;
                logger.Error("INPUT_ASSERT", $"Frame {frame} '{rule.Label}' addr=0x{addr:X8} actual=0x{actual:X4} mask=0x{mask:X4} expected=0x{expected:X4} FAIL");
            }
        }
    }

    public static int ResolveScancode(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        string t = name.Trim().ToLowerInvariant();
        if (t.Length == 1 && t[0] is >= 'a' and <= 'z') return 4 + (t[0] - 'a');
        if (t.Length == 1 && t[0] is >= '1' and <= '9') return 30 + (t[0] - '1');
        return t switch
        {
            "0" => 39,
            "enter" or "return" => 40,
            "esc" or "escape" => 41,
            "backspace" or "bksp" => 42,
            "tab" => 43,
            "space" or "spacebar" => 44,
            "f1" => 58,
            "f3" => 60,
            "f5" => 62,
            "f9" => 66,
            "right" => 79,
            "left" => 80,
            "down" => 81,
            "up" => 82,
            "lctrl" or "ctrl" => 224,
            "lshift" or "shift" => 225,
            "lalt" or "alt" => 226,
            _ => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) ? raw : 0
        };
    }

    public static uint ResolveMouseMask(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0u;
        return name.Trim().ToLowerInvariant() switch
        {
            "lclick" or "mouseleft" or "left" or "1001" => 1u,
            "mclick" or "mousemiddle" or "middle" or "1003" => 2u,
            "rclick" or "mouseright" or "right" or "1002" => 4u,
            "mouse4" or "mousex1" or "x1" or "1004" => 8u,
            "mouse5" or "mousex2" or "x2" or "1005" => 16u,
            _ => 0u
        };
    }

    private static uint ParseHexOrDec(string raw)
    {
        string t = raw?.Trim() ?? "0";
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return uint.Parse(t, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
