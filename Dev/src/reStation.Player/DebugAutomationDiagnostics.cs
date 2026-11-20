#if DEBUG || RESTATION_TEST_AUTOMATION
// @module: reStation.Player.DebugAutomationDiagnostics
// @purpose: Debug/RESTATION_TEST_AUTOMATION-only virtual controller/keyboard injection, live SPU WAV/RMS tap, and guest RAM telemetry seam for deep E2E verification.
// @depends-on: System.Collections.Concurrent, System.Globalization, System.IO, System.Text, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Input
// @exports: DebugAutomationDiagnostics

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Input;

namespace reStation.Player;

/// <summary>Debug/test-only diagnostics helper (100% compiled out of pure Release builds).</summary>
internal sealed class DebugAutomationDiagnostics
{
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly Dictionary<int, int> _heldKeys = new();
    private readonly Dictionary<int, int> _heldPadCodes = new();
    private readonly List<(uint Addr, int Len)> _watches = new();
    private readonly object _stateLock = new();

    private float _stickLX, _stickLY, _stickRX, _stickRY;
    private int _stickLeftFrames, _stickRightFrames;
    private uint _prevVirtualPadMask;
    private double _lastAudioRms;
    private short _lastAudioPeak;
    private long _totalAudioSamples;
    private int _lastDrainSamples;

    private string? _wavTargetPath;
    private int _wavTargetSamples;
    private short[]? _wavBuffer;
    private int _wavPos;
    private string _wavStatus = "idle";
    private volatile string _telemetrySummary = string.Empty;

    public string TelemetrySummary => _telemetrySummary;

    public void EnqueueRawCommands(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return;
        foreach (string line in rawText.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(line))
                _commands.Enqueue(line);
        }
    }

    public int[] ProcessFrameCommands(
        SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay,
        RecompSettings settings,
        string configPath,
        LoopStateStateUpdater? onCaptureHandled = null)
    {
        while (_commands.TryDequeue(out string? cmd))
            ApplyCommand(cmd);

        List<int>? risingPadCodes = null;
        uint currentMask = 0u;
        lock (_stateLock)
        {
            foreach (KeyValuePair<int, int> kv in _heldPadCodes.ToArray())
            {
                if (kv.Key is >= GamepadBindingCode.MinCode and <= GamepadBindingCode.MaxCode)
                    currentMask |= 1u << (kv.Key - GamepadBindingCode.MinCode);
                if (kv.Value <= 1) _heldPadCodes.Remove(kv.Key);
                else _heldPadCodes[kv.Key] = kv.Value - 1;
            }
            uint rising = currentMask & ~_prevVirtualPadMask;
            _prevVirtualPadMask = currentMask;
            if (rising != 0)
            {
                risingPadCodes = new List<int>(4);
                for (int b = 0; b < 32; b++)
                    if ((rising & (1u << b)) != 0)
                        risingPadCodes.Add(GamepadBindingCode.MinCode + b);
            }
        }

        if (risingPadCodes is not null && drawer.IsOpen && drawer.ActiveTab == 2 && drawer.KeybindsTab.IsListening)
        {
            foreach (int code in risingPadCodes)
            {
                if (drawer.TryHandleGamepadCapture(code, settings, configPath))
                {
                    overlay.MarkDirty();
                    onCaptureHandled?.Invoke();
                    break;
                }
            }
        }
        return risingPadCodes?.ToArray() ?? Array.Empty<int>();
    }

    public delegate void LoopStateStateUpdater();

    public bool IsVirtualKeyHeld(int scancode)
    {
        lock (_stateLock) return _heldKeys.TryGetValue(scancode, out int rem) && rem > 0;
    }

    public void ApplyVirtualOverrides(Span<byte> mergedKeys, ref HostInputState st)
    {
        lock (_stateLock)
        {
            foreach (KeyValuePair<int, int> kv in _heldKeys.ToArray())
            {
                if ((uint)kv.Key < (uint)mergedKeys.Length) mergedKeys[kv.Key] = 1;
                if (kv.Value <= 1) _heldKeys.Remove(kv.Key);
                else _heldKeys[kv.Key] = kv.Value - 1;
            }
            if (_prevVirtualPadMask != 0 || _stickLeftFrames > 0 || _stickRightFrames > 0)
            {
                st.ControllerConnected = true;
                st.GamepadButtonsMask |= _prevVirtualPadMask;
            }
            if (_stickLeftFrames > 0)
            {
                st.RawLeftStickX = _stickLX;
                st.RawLeftStickY = _stickLY;
                st.LeftStickX = MathF.Abs(_stickLX) < 0.15f ? 0f : _stickLX;
                st.LeftStickY = MathF.Abs(_stickLY) < 0.15f ? 0f : _stickLY;
                _stickLeftFrames--;
                if (_stickLeftFrames == 0) { _stickLX = 0f; _stickLY = 0f; }
            }
            if (_stickRightFrames > 0)
            {
                st.RightStickX = MathF.Abs(_stickRX) < 0.15f ? 0f : _stickRX;
                st.RightStickY = MathF.Abs(_stickRY) < 0.15f ? 0f : _stickRY;
                _stickRightFrames--;
                if (_stickRightFrames == 0) { _stickRX = 0f; _stickRY = 0f; }
            }
        }
    }

    public void RecordAudioSamples(ReadOnlySpan<short> pcm, int sampleCount)
    {
        if (sampleCount <= 0)
        {
            _lastDrainSamples = 0;
            return;
        }
        _lastDrainSamples = sampleCount;
        _totalAudioSamples += sampleCount;
        double sumSq = 0.0;
        int peak = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = pcm[i];
            int abs = s == short.MinValue ? 32768 : Math.Abs((int)s);
            if (abs > peak) peak = abs;
            sumSq += (double)s * s;
        }
        _lastAudioRms = Math.Sqrt(sumSq / sampleCount);
        _lastAudioPeak = (short)Math.Clamp(peak, 0, short.MaxValue);

        string? finishedPath = null;
        short[]? finishedBuf = null;
        int finishedLen = 0;
        lock (_stateLock)
        {
            if (_wavBuffer is not null && _wavTargetSamples > 0)
            {
                int copy = Math.Min(sampleCount, _wavTargetSamples - _wavPos);
                if (copy > 0)
                {
                    pcm[..copy].CopyTo(_wavBuffer.AsSpan(_wavPos));
                    _wavPos += copy;
                }
                if (_wavPos >= _wavTargetSamples)
                {
                    finishedPath = _wavTargetPath;
                    finishedBuf = _wavBuffer;
                    finishedLen = _wavPos;
                    _wavBuffer = null;
                    _wavTargetPath = null;
                    _wavTargetSamples = 0;
                    _wavPos = 0;
                }
            }
        }
        if (finishedPath is not null && finishedBuf is not null)
            WriteWavFile(finishedPath, finishedBuf, finishedLen);
    }

    public void UpdateTelemetry(ReStationHost host, PsxCpu cpu, in HostInputState lastInput, long hostFrames)
    {
        PsxTelemetrySnapshot pres = host.GetTelemetry();
        (double vblankFps, _) = host.VBlankTelemetry;
        var sb = new StringBuilder(256);
        sb.Append(CultureInfo.InvariantCulture, $"presFps={pres.CurrentFps:F2}|vbFps={vblankFps:F2}|p99={pres.P99FrameMs:F2}|cpuMs={pres.CpuMs:F2}|ovHash={cpu.OverlayHashComputations}|hf={hostFrames}|cf={cpu.FrameCount}");
        sb.Append(CultureInfo.InvariantCulture, $"|pad=0x{cpu.GetControllerButtons(0):X4}|gp=0x{lastInput.GamepadButtonsMask:X6}");
        sb.Append(CultureInfo.InvariantCulture, $"|rms={_lastAudioRms:F1}|peak={_lastAudioPeak}|asmp={_totalAudioSamples}|lastAsm={_lastDrainSamples}|xaDrops={cpu.Spu.XaDrops}|wav={_wavStatus}");

        lock (_stateLock)
        {
            if (_watches.Count > 0)
            {
                sb.Append("|mem=");
                for (int i = 0; i < _watches.Count; i++)
                {
                    (uint addr, int len) = _watches[i];
                    if (i > 0) sb.Append(',');
                    sb.Append(CultureInfo.InvariantCulture, $"0x{addr:X8}:");
                    for (int off = 0; off < len; off += 4)
                    {
                        if (off > 0) sb.Append(':');
                        uint val = (len - off) >= 4 ? cpu.Read32(addr + (uint)off) : cpu.Read16(addr + (uint)off);
                        sb.Append(val.ToString((len - off) >= 4 ? "X8" : "X4", CultureInfo.InvariantCulture));
                    }
                }
            }
        }
        _telemetrySummary = sb.ToString();
    }

    private void ApplyCommand(string cmd)
    {
        string[] parts = cmd.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return;
        string kind = parts[0].ToLowerInvariant();
        if (kind == "pad")
        {
            string target = parts[1].ToLowerInvariant();
            if (target == "clear")
            {
                lock (_stateLock)
                {
                    _heldPadCodes.Clear();
                    _heldKeys.Clear();
                    _stickLeftFrames = 0;
                    _stickRightFrames = 0;
                    _stickLX = _stickLY = _stickRX = _stickRY = 0f;
                }
                return;
            }
            if ((target == "lstick" || target == "rstick") && parts.Length >= 3)
            {
                string[] xy = parts[2].Split(',', StringSplitOptions.TrimEntries);
                int frames = parts.Length >= 5 && parts[3].Equals("hold", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[4], out int f) ? Math.Clamp(f, 1, 6000) : 10;
                if (xy.Length == 2
                    && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float ax)
                    && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ay))
                {
                    lock (_stateLock)
                    {
                        if (target == "lstick") { _stickLX = Math.Clamp(ax, -1f, 1f); _stickLY = Math.Clamp(ay, -1f, 1f); _stickLeftFrames = frames; }
                        else { _stickRX = Math.Clamp(ax, -1f, 1f); _stickRY = Math.Clamp(ay, -1f, 1f); _stickRightFrames = frames; }
                    }
                }
                return;
            }
            int code = ParseGamepadCode(target);
            if (code != GamepadBindingCode.None)
            {
                int frames = parts.Length >= 4 && parts[2].Equals("hold", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[3], out int f) ? Math.Clamp(f, 1, 6000) : 4;
                lock (_stateLock) { _heldPadCodes[code] = frames; }
            }
        }
        else if (kind == "key" && parts.Length >= 2)
        {
            int sc = ParseScancode(parts[1]);
            if (sc > 0)
            {
                int frames = parts.Length >= 4 && parts[2].Equals("hold", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[3], out int f) ? Math.Clamp(f, 1, 6000) : 4;
                lock (_stateLock) { _heldKeys[sc] = frames; }
            }
        }
        else if (kind == "audio" && parts[1].Equals("wav", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
        {
            // Join back in case Windows drive path has colons (e.g. C:\foo.wav -> audio:wav:C:\foo.wav:2.5)
            int lastColon = cmd.LastIndexOf(':');
            int prefixEnd = cmd.IndexOf(":wav:", StringComparison.OrdinalIgnoreCase) + 5;
            if (lastColon > prefixEnd && double.TryParse(cmd[(lastColon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
            {
                string path = cmd[prefixEnd..lastColon].Trim();
                int samples = Math.Clamp((int)Math.Round(secs * 44100.0) * 2, 2048, 44100 * 2 * 10);
                lock (_stateLock)
                {
                    _wavTargetPath = path;
                    _wavTargetSamples = samples;
                    _wavBuffer = new short[samples];
                    _wavPos = 0;
                    _wavStatus = "recording";
                }
            }
        }
        else if (kind == "mem" && parts[1].Equals("watch", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
        {
            string[] specs = parts[2].Split(',', StringSplitOptions.TrimEntries);
            if (specs.Length == 2
                && uint.TryParse(specs[0].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr)
                && int.TryParse(specs[1], out int len))
            {
                lock (_stateLock)
                {
                    if (_watches.Count >= 6) _watches.RemoveAt(0);
                    _watches.Add((addr, Math.Clamp(len, 2, 16)));
                }
            }
        }
    }

    private void WriteWavFile(string path, short[] samples, int count)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            int dataBytes = count * sizeof(short);
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs, Encoding.ASCII);
            bw.Write("RIFF"u8);
            bw.Write(36 + dataBytes);
            bw.Write("WAVEfmt "u8);
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)2);
            bw.Write(44100);
            bw.Write(44100 * 4);
            bw.Write((short)4);
            bw.Write((short)16);
            bw.Write("data"u8);
            bw.Write(dataBytes);
            for (int i = 0; i < count; i++) bw.Write(samples[i]);
            _wavStatus = "ok:" + Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _wavStatus = "err:" + ex.GetType().Name;
        }
    }

    private static int ParseGamepadCode(string token) => token switch
    {
        "south" or "cross" or "a" => GamepadBindingCode.South,
        "east" or "circle" or "b" => GamepadBindingCode.East,
        "west" or "square" or "x" => GamepadBindingCode.West,
        "north" or "triangle" or "y" => GamepadBindingCode.North,
        "back" or "select" => GamepadBindingCode.Back,
        "guide" => GamepadBindingCode.Guide,
        "start" => GamepadBindingCode.Start,
        "l3" => GamepadBindingCode.L3,
        "r3" => GamepadBindingCode.R3,
        "lb" or "l1" => GamepadBindingCode.LB,
        "rb" or "r1" => GamepadBindingCode.RB,
        "dpad_up" or "up" => GamepadBindingCode.DPadUp,
        "dpad_down" or "down" => GamepadBindingCode.DPadDown,
        "dpad_left" or "left" => GamepadBindingCode.DPadLeft,
        "dpad_right" or "right" => GamepadBindingCode.DPadRight,
        "lt" or "l2" => GamepadBindingCode.LT,
        "rt" or "r2" => GamepadBindingCode.RT,
        "touchpad" => GamepadBindingCode.Touchpad,
        _ => int.TryParse(token, out int raw) && GamepadBindingCode.IsGamepadCode(raw) ? raw : GamepadBindingCode.None
    };

    private static int ParseScancode(string token)
    {
        if (int.TryParse(token, out int num) && num is > 0 and <= KeyBindingsConfig.MaxScancode) return num;
        return token.Trim().ToUpperInvariant() switch
        {
            "W" => 26, "A" => 4, "S" => 22, "D" => 7, "E" => 8, "Q" => 20, "F" => 9, "R" => 21, "C" => 6, "Z" => 29,
            "SPACE" => 44, "ENTER" => 40, "ESC" => 41, "BACKSPACE" => 42, "TAB" => 43, "DELETE" => 76,
            "RIGHT" => 79, "LEFT" => 80, "DOWN" => 81, "UP" => 82,
            "F1" => 58, "F3" => 60, "F5" => 62, "F6" => 63, "F7" => 64, "F9" => 66,
            _ => 0
        };
    }
}
#endif
