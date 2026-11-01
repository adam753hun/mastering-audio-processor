// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
namespace MasteringCs;

// HU: Beszédkompresszor, expander/gate és hangerőillesztés. A csúcsvédelem egyenletes erősítést korlátoz.
// EN: Speech compressor, expander/gate and loudness matching. Peak protection caps uniform gain.
public static class Finishing
{
    public sealed record GateInfo(bool Enabled, bool GateEnabled, bool Bypassed, string? Reason, double? ThresholdDbFS, double? NoiseEstimateDbFS, double? ContrastDb, double MaxReductionDb, double ExactZeroGainFraction);
    public sealed record Info(double? InputLUFS, double? OutputLUFS, double? RequestedTargetLUFS, double AppliedGainDb, double? OutputTruePeakEstimateDbTP, double CeilingDbTP, bool PeakLimited, GateInfo? SilenceControl);
    public static double? Loudness(double[] samples, int rate)
    {
        var x = rate == 48000 ? samples : Numeric.Resample(samples, 48000, rate);
        if (x.Length < 19200)
            return null;
        var z = Filter(x, [1.53512485958697, -2.69169618940638, 1.19839281085285], [-1.69065929318241, .73248077421585]);
        z = Filter(z, [1, -2, 1], [-1.99004745483398, .99007225036621]);
        var sums = new double[z.Length + 1];
        for (int i = 0; i < z.Length; i++)
            sums[i + 1] = sums[i] + z[i] * z[i];
        var powers = new List<double>();
        for (int start = 0; start + 19200 <= z.Length; start += 4800)
            powers.Add(Math.Max(0, (sums[start + 19200] - sums[start]) / 19200));
        double absolute = Math.Pow(10, (-70 + .691) / 10);
        var selected = powers.Where(p => p >= absolute).ToArray();
        if (selected.Length == 0)
            return null;
        double relative = selected.Average() * .1;
        selected = powers.Where(p => p >= absolute && p >= relative).ToArray();
        return -.691 + 10 * Math.Log10(selected.Average());
    }
    private static double[] Filter(double[] x, double[] b, double[] a)
    {
        var y = new double[x.Length];
        double z0 = 0, z1 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = b[0] * x[i] + z0;
            z0 = b[1] * x[i] - a[0] * v + z1;
            z1 = b[2] * x[i] - a[1] * v;
            y[i] = v;
        }
        return y;
    }
    public static double[] Compress(double[] x, int rate, Config c)
    {
        if (x.Length == 0)
            return x;
        int step = Math.Max(1, (int)Math.Round(rate / 1000d)), frames = (x.Length + step - 1) / step;
        var levels = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double peak = 1e-15;
            for (int j = i * step; j < Math.Min(x.Length, (i + 1) * step); j++)
                peak = Math.Max(peak, Math.Abs(x[j]));
            levels[i] = peak;
        }
        int look = (int)Math.Ceiling(c.N("finishing", "compressionLookaheadMs") * rate / (1000 * step));
        var detector = Numeric.FutureMaximum(levels, look, 0);
        var smooth = new double[frames];
        double knee = c.N("finishing", "compressionKneeDb"), slope = 1 - 1 / c.N("finishing", "compressionRatio"), attack = Math.Exp(-step / (rate * c.N("finishing", "compressionAttackMs") / 1000)), release = Math.Exp(-step / (rate * c.N("finishing", "compressionReleaseMs") / 1000)), current = 0;
        for (int i = 0; i < frames; i++)
        {
            double over = 20 * Math.Log10(detector[i]) - c.N("finishing", "compressionThresholdDbFS"), demand = Math.Max(over, 0) * slope;
            if (knee > 0 && Math.Abs(over) < knee / 2)
                demand = slope * Math.Pow(over + knee / 2, 2) / (2 * knee);
            else if (knee > 0 && over <= -knee / 2)
                demand = 0;
            double coefficient = demand > current ? attack : release;
            current = coefficient * current + (1 - coefficient) * demand;
            smooth[i] = current;
        }
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
            y[i] = x[i] * Math.Pow(10, -Numeric.Interpolate(smooth, i, step) / 20);
        return y;
    }
    // HU: A Gate nullára zárhat; az Expander véges csillapítást ad. Automatikus módban bizonytalan beszéd/zaj esetén bypass marad.
    // EN: Gate can close to zero; expander has finite attenuation. Automatic mode bypasses uncertain speech/noise separation.
    public static (double[] Samples, GateInfo Info) Gate(double[] x, int rate, Config c)
    {
        bool enabled = c.B("silenceControl", "enabled"), gate = c.B("silenceControl", "gateEnabled");
        GateInfo report = new(enabled, gate, false, null, null, null, null, 0, 0);
        if (!enabled || x.Length == 0)
            return (x, report);
        int step = Math.Max(1, (int)Math.Round(rate / 1000d)), frames = (x.Length + step - 1) / step, half = Math.Max(1, (int)Math.Round(rate * .005));
        var sums = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++)
            sums[i + 1] = sums[i] + x[i] * x[i];
        var levels = new double[frames];
        var valid = new List<double>();
        for (int i = 0; i < frames; i++)
        {
            int at = i * step, l = Math.Max(0, at - half), r = Math.Min(x.Length, at + half);
            double rms = Math.Sqrt(Math.Max(0, (sums[r] - sums[l]) / (r - l)));
            levels[i] = 20 * Math.Log10(Math.Max(rms, 1e-12));
            if (rms > 1e-12)
                valid.Add(levels[i]);
        }
        if (valid.Count == 0)
            return (x, report with
            {
                Bypassed = true,
                Reason = "silentInput"
            });
        double noise = Numeric.Percentile(valid.ToArray(), .1), speech = Numeric.Percentile(valid.ToArray(), .9), threshold = c.N("silenceControl", "thresholdDbFS");
        if (c.B("silenceControl", "autoThreshold"))
        {
            if (x.Length < rate * .5 || speech - noise < c.N("silenceControl", "minimumContrastDb"))
                return (x, report with
                {
                    Bypassed = true,
                    Reason = "insufficientLevelSeparation",
                    NoiseEstimateDbFS = noise,
                    ContrastDb = speech - noise
                });
            threshold = Math.Max(-120, Math.Min(noise + c.N("silenceControl", "noiseMarginDb"), speech - 18));
        }
        int look = (int)Math.Ceiling(c.N("silenceControl", "lookaheadMs") * rate / (1000 * step)), hold = (int)Math.Ceiling(c.N("silenceControl", "holdMs") * rate / (1000 * step));
        var detector = Numeric.FutureMaximum(levels, look, -240);
        double slope = c.N("silenceControl", "ratio") - 1, knee = c.N("silenceControl", "kneeDb"), range = c.N("silenceControl", "rangeDb"), attack = Math.Exp(-step / (rate * c.N("silenceControl", "attackMs") / 1000)), release = Math.Exp(-step / (rate * c.N("silenceControl", "releaseMs") / 1000)), gateStep = step / (rate * c.N("silenceControl", "releaseMs") / 1000), hysteresis = c.N("silenceControl", "hysteresisDb"), gain = 1;
        int quiet = 0;
        bool opened = true;
        var gains = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double level = detector[i], under = threshold - level, reduction = Math.Max(under, 0) * slope;
            if (knee > 0)
            {
                if (under <= -knee / 2)
                    reduction = 0;
                else if (Math.Abs(under) < knee / 2)
                    reduction = slope * Math.Pow(under + knee / 2, 2) / (2 * knee);
            }
            reduction = Math.Min(reduction, range);
            if (level >= threshold)
            {
                opened = true;
                quiet = 0;
            }
            else if (opened)
            {
                quiet = level < threshold - hysteresis ? quiet + 1 : 0;
                if (quiet >= hold)
                    opened = false;
            }
            double target = opened ? 1 : gate ? 0 : Math.Pow(10, -reduction / 20);
            if (target > gain)
                gain = target + (gain - target) * attack;
            else if (gate)
                gain = Math.Max(target, gain - gateStep);
            else
                gain = target + (gain - target) * release;
            gains[i] = gain;
        }
        var y = new double[x.Length];
        int zero = 0;
        double min = 1;
        for (int i = 0; i < x.Length; i++)
        {
            double envelope = Numeric.Interpolate(gains, i, step);
            y[i] = x[i] * envelope;
            min = Math.Min(min, envelope);
            if (envelope == 0)
                zero++;
        }
        return (y, report with
        {
            ThresholdDbFS = threshold,
            NoiseEstimateDbFS = noise,
            ContrastDb = speech - noise,
            MaxReductionDb = -20 * Math.Log10(Math.Max(min, 1e-12)),
            ExactZeroGainFraction = zero / (double)x.Length
        });
    }
    public static (float[] Samples, Info Info) Apply(float[] samples, int rate, int width, Config c)
    {
        var x = Numeric.ToDouble(samples);
        double? before = Loudness(x, rate);
        double inputPeak = Numeric.Peak(x);
        bool compression = c.B("finishing", "extraCompressionEnabled"), silence = c.B("silenceControl", "enabled");
        GateInfo? gate = null;
        if (compression)
            x = Compress(x, rate, c);
        if (silence)
        {
            var g = Gate(x, rate, c);
            x = g.Samples;
            gate = g.Info;
        }
        // HU: Változatlan jel esetén a mérési eredmények újrahasználhatók, a kiszámított erősítés nem változik.
        // EN: Reuse measurements for unchanged audio; calculated gain remains the same.
        double? after = compression || silence ? Loudness(x, rate) : before;
        double peak = compression || silence ? Numeric.Peak(x) : inputPeak;
        double requested = c.N("finishing", "gainDb");
        if (c.B("finishing", "loudnessEnabled") && after.HasValue)
            requested += c.N("finishing", "targetLUFS") - after.Value;
        double ceiling = Math.Pow(10, (c.N("finishing", "maxTruePeakDbTP") - .1) / 20), allowed = peak > 0 ? 20 * Math.Log10(ceiling / peak) : requested, applied = Math.Min(requested, allowed), factor = Math.Pow(10, applied / 20);
        var y = new float[x.Length];
        for (int i = 0; i < y.Length; i++)
            y[i] = (float)(x[i] * factor);
        double measured = 0;
        bool valid = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            measured = Numeric.Peak(Numeric.Quantized(y, width));
            if (measured <= ceiling)
            {
                valid = true;
                break;
            }
            double correction = 20 * Math.Log10(ceiling / measured) - .001;
            factor = Math.Pow(10, correction / 20);
            for (int i = 0; i < y.Length; i++)
                y[i] = (float)(y[i] * factor);
            applied += correction;
        }
        if (!valid)
            throw new ArgumentException("A csúcsszint-ellenőrzés sikertelen.");
        var actual = Numeric.Quantized(y, width);
        if (actual.Any(v => Math.Abs(v) >= 1))
            throw new ArgumentException("PCM-túlvezérlés megelőzve.");
        return (y, new Info(before, Loudness(actual, rate), c.B("finishing", "loudnessEnabled") ? c.N("finishing", "targetLUFS") : null, applied, measured > 0 ? 20 * Math.Log10(measured) : null, c.N("finishing", "maxTruePeakDbTP"), applied < requested - 1e-5, gate));
    }
}
