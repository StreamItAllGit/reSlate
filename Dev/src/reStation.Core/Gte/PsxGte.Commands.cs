// @module: reStation.Core.Gte.PsxGte.Commands
// @purpose: GTE command decoder: RTPS/RTPT perspective (+PGXP, 16:9), NCLIP, AVSZ3/4, MVMVA, OP, SQR, GPF/GPL, lighting & depth-cue.
// @depends-on: reStation.Core.Gte.PsxGte (register file), reStation.Core.Gte.PgxpTracker
// @exports: PsxGte (partial)
// @owner: devv-cpu-cd (T2)

namespace reStation.Core.Gte;

public sealed partial class PsxGte
{
    /// <summary>COP2 imm25 command entry (bits: 19 = sf, 17-18 = mx, 15-16 = v, 13-14 = cv, 10 = lm, 0-5 = opcode).</summary>
    public void Execute(uint command)
    {
        _flag = 0;
        CommandsExecuted++;
        int sf = (command & (1U << 19)) != 0 ? 12 : 0;
        bool lm = (command & (1U << 10)) != 0;
        switch (command & 0x3FU)
        {
            case 0x01: Rtp(0, sf, lm, true); break;
            case 0x06: Nclip(); break;
            case 0x0C: OuterProduct(sf, lm); break;
            case 0x10: DepthCue(Rgbc(0) << 16, Rgbc(1) << 16, Rgbc(2) << 16, sf, lm); break;
            case 0x11: DepthCue((long)Ir(1) << 12, (long)Ir(2) << 12, (long)Ir(3) << 12, sf, lm); break;
            case 0x12: Mvmva((int)((command >> 17) & 3), (int)((command >> 15) & 3), (int)((command >> 13) & 3), sf, lm); break;
            case 0x13: NormalColor(0, sf, lm, ColorMode.DepthCue); break;
            case 0x14: LightColor(sf, lm); ModulatedDepthCue(sf, lm); break;
            case 0x16: for (int v = 0; v < 3; v++) NormalColor(v, sf, lm, ColorMode.DepthCue); break;
            case 0x1B: NormalColor(0, sf, lm, ColorMode.Modulate); break;
            case 0x1C: LightColor(sf, lm); Modulate(sf, lm); break;
            case 0x1E: NormalColor(0, sf, lm, ColorMode.Plain); break;
            case 0x20: for (int v = 0; v < 3; v++) NormalColor(v, sf, lm, ColorMode.Plain); break;
            case 0x28: for (int i = 1; i <= 3; i++) SetMacIr(i, (long)Ir(i) * Ir(i), sf, lm); break;
            case 0x29: ModulatedDepthCue(sf, lm); break;
            case 0x2A:
                for (int n = 0; n < 3; n++)
                {
                    uint c = _d[20];
                    DepthCue((long)(c & 0xFF) << 16, (long)((c >> 8) & 0xFF) << 16, (long)((c >> 16) & 0xFF) << 16, sf, lm);
                }
                break;
            case 0x2D: Average(_c[29], Sz(1) + Sz(2) + Sz(3)); break;
            case 0x2E: Average(_c[30], Sz(0) + Sz(1) + Sz(2) + Sz(3)); break;
            case 0x30: Rtp(0, sf, lm, false); Rtp(1, sf, lm, false); Rtp(2, sf, lm, true); break;
            case 0x3D: for (int i = 1; i <= 3; i++) SetMacIr(i, (long)Ir(0) * Ir(i), sf, lm); PushColorFromMac(); break;
            case 0x3E:
                for (int i = 1; i <= 3; i++) SetMacIr(i, ((long)Mac(i) << sf) + (long)Ir(0) * Ir(i), sf, lm);
                PushColorFromMac();
                break;
            case 0x3F: for (int v = 0; v < 3; v++) NormalColor(v, sf, lm, ColorMode.Modulate); break;
            default: return; // undefined opcodes leave registers untouched (FLAG cleared)
        }
        if ((_flag & 0x7F87E000U) != 0) _flag |= 0x80000000U;
    }

    private enum ColorMode { Plain, Modulate, DepthCue }

    private long Rgbc(int channel) => (_d[6] >> (channel * 8)) & 0xFF;

    /// <summary>RTPS/RTPT single-vertex perspective transform with PGXP sub-pixel capture and optional 16:9 squash.</summary>
    private void Rtp(int vertex, int sf, bool lm, bool depthCue)
    {
        (short vx, short vy, short vz) = Vector(vertex);
        long mac3Raw = 0;
        for (int i = 1; i <= 3; i++)
        {
            long sum = ((long)(int)_c[4 + i] << 12) + MatrixElement(0, i - 1, 0) * (long)vx
                     + MatrixElement(0, i - 1, 1) * (long)vy + MatrixElement(0, i - 1, 2) * (long)vz;
            long mac = SetMac(i, sum, sf);
            if (i < 3) { SetIr(i, mac, false); continue; }
            mac3Raw = sum;
            long check = sum >> 12; // IR3 FLAG uses the sf=1 view regardless of sf (hardware quirk)
            if (check is < -0x8000 or > 0x7FFF) _flag |= 1U << 22;
            _d[11] = (uint)(int)(short)Math.Clamp(mac, lm ? 0 : -0x8000, 0x7FFF);
        }

        PushSz(mac3Raw >> 12);
        ushort sz3 = Sz(3);
        ushort h = (ushort)_c[26];
        uint div = Divide(h, sz3);
        bool wide = Settings.IsWidescreen16By9;
        long xTerm = (long)div * Ir(1);
        if (wide) xTerm = xTerm * 3 / 4;
        long macX = SetMac0(xTerm + (int)_c[24]);
        long macY = SetMac0((long)div * Ir(2) + (int)_c[25]);
        short sx = SaturateScreen(macX >> 16, 1U << 14, ref _flag);
        short sy = SaturateScreen(macY >> 16, 1U << 13, ref _flag);
        PushSxy(((uint)(ushort)sy << 16) | (ushort)sx);

        if (Pgxp.IsEnabled)
        {
            double precise = sz3 == 0 ? 0x1FFFF : Math.Min(0x1FFFF, h * 65536.0 / sz3);
            double ir1 = Math.Clamp(Mac(1), -0x8000, 0x7FFF), ir2 = Math.Clamp(Mac(2), -0x8000, 0x7FFF);
            double fx = ((int)_c[24] + precise * ir1 * (wide ? 0.75 : 1.0)) / 65536.0;
            double fy = ((int)_c[25] + precise * ir2) / 65536.0;
            Pgxp.Record(sx, sy, (float)fx, (float)fy, sz3);
        }

        if (depthCue)
        {
            long mac0 = SetMac0((long)div * (short)_c[27] + (int)_c[28]);
            SetIr0(mac0 >> 12);
        }
    }

    private void Nclip()
    {
        long v = (long)Sx(0) * Sy(1) + (long)Sx(1) * Sy(2) + (long)Sx(2) * Sy(0)
               - (long)Sx(0) * Sy(2) - (long)Sx(1) * Sy(0) - (long)Sx(2) * Sy(1);
        SetMac0(v);
    }

    private void Average(uint zsf, int sum)
    {
        long mac0 = SetMac0((long)(short)zsf * sum);
        SetOtz(mac0);
    }

    private void OuterProduct(int sf, bool lm)
    {
        long d1 = MatrixElement(0, 0, 0), d2 = MatrixElement(0, 1, 1), d3 = MatrixElement(0, 2, 2);
        long ir1 = Ir(1), ir2 = Ir(2), ir3 = Ir(3);
        SetMacIr(1, ir3 * d2 - ir2 * d3, sf, lm);
        SetMacIr(2, ir1 * d3 - ir3 * d1, sf, lm);
        SetMacIr(3, ir2 * d1 - ir1 * d2, sf, lm);
    }

    /// <summary>MVMVA: [MAC] = (T * 1000h + M * V) SAR sf (mx 0=RT,1=LLM,2=LCM,3=garbage; tx 0=TR,1=BK,2=FC,3=none).</summary>
    private void Mvmva(int mx, int v, int tx, int sf, bool lm)
    {
        (short vx, short vy, short vz) = Vector(v);
        for (int i = 1; i <= 3; i++)
        {
            long t = tx switch { 0 => (int)_c[4 + i], 1 => (int)_c[12 + i], 2 => (int)_c[20 + i], _ => 0 };
            long sum = (t << 12) + MvmvaElement(mx, i - 1, 0) * (long)vx + MvmvaElement(mx, i - 1, 1) * (long)vy
                     + MvmvaElement(mx, i - 1, 2) * (long)vz;
            SetMacIr(i, sum, sf, lm);
        }
    }

    private short MvmvaElement(int mx, int row, int col)
    {
        if (mx < 3) return MatrixElement(mx * 8, row, col);
        // mx=3 garbage matrix (psx-spx): row0 = [-R<<4, R<<4, IR0], row1 = RT13 x3, row2 = RT22 x3
        return row switch
        {
            0 => col switch { 0 => (short)-(Rgbc(0) << 4), 1 => (short)(Rgbc(0) << 4), _ => Ir(0) },
            1 => MatrixElement(0, 0, 2),
            _ => MatrixElement(0, 1, 1)
        };
    }

    /// <summary>NCS/NCT/NCCS/NCCT/NCDS/NCDT: LLM*V -> BK + LCM*IR, then optional colour modulation / depth cue.</summary>
    private void NormalColor(int vertex, int sf, bool lm, ColorMode mode)
    {
        Mvmva(1, vertex, 3, sf, lm);
        LightColor(sf, lm);
        switch (mode)
        {
            case ColorMode.Plain: PushColorFromMac(); break;
            case ColorMode.Modulate: Modulate(sf, lm); break;
            default: ModulatedDepthCue(sf, lm); break;
        }
    }

    /// <summary>[IR] = [MAC] = (BK * 1000h + LCM * IR) SAR sf.</summary>
    private void LightColor(int sf, bool lm) => Mvmva(2, 3, 1, sf, lm);

    /// <summary>NCCS/CC tail: [MAC] = [R*IR1, G*IR2, B*IR3] SHL 4, SAR sf, push colour.</summary>
    private void Modulate(int sf, bool lm)
    {
        for (int i = 1; i <= 3; i++) SetMacIr(i, (Rgbc(i - 1) * Ir(i)) << 4, sf, lm);
        PushColorFromMac();
    }

    /// <summary>NCDS/CDP/DCPL tail: [MAC] = [R*IR1, G*IR2, B*IR3] SHL 4, then depth-cue toward the far colour.</summary>
    private void ModulatedDepthCue(int sf, bool lm) =>
        DepthCue((Rgbc(0) * Ir(1)) << 4, (Rgbc(1) * Ir(2)) << 4, (Rgbc(2) * Ir(3)) << 4, sf, lm);

    /// <summary>MAC + (FC - MAC) * IR0, SAR sf, colour FIFO push (DPCS/DPCT/INTPL/NCDS family).</summary>
    private void DepthCue(long mac1, long mac2, long mac3, int sf, bool lm)
    {
        Span<long> mac = stackalloc long[3] { mac1, mac2, mac3 };
        long ir0 = Ir(0);
        for (int i = 1; i <= 3; i++)
        {
            long far = ((long)(int)_c[20 + i] << 12) - mac[i - 1];
            SetIr(i, SetMac(i, far, sf), false);
            mac[i - 1] = Ir(i) * ir0 + mac[i - 1];
        }
        for (int i = 1; i <= 3; i++) SetMacIr(i, mac[i - 1], sf, lm);
        PushColorFromMac();
    }
}
