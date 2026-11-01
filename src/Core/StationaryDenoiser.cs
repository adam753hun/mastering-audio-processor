// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
// Based on noisereduce: Copyright (c) 2019 Tim Sainburg; MIT notice in LICENSES/noisereduce-MIT.txt.
namespace MasteringCs;

// HU: A noisereduce 3.0.3 stationary eljárásának saját C# megvalósítása; nem adaptív, és nem futtat Pythont.
// EN: C# implementation of noisereduce 3.0.3 stationary processing; non-adaptive and no Python execution.
// Algorithm reference: timsainb/noisereduce, MIT; attribution and license accompany this port.
public static class StationaryDenoiser
{
    private sealed record Spectrum(double[][] Re, double[][] Im);
    public static float[] Apply(float[] x, int rate, Config cfg, CancellationToken cancel = default)
    {
        int count = (int)Math.Round(rate * cfg.N("noiseReduction", "noiseSampleMs") / 1000), size = rate == 44100 ? 1024 : 2048, hop = size / 4, bins = size / 2 + 1;
        double strength = cfg.N("noiseReduction", "strength");
        if (x.Length < count)
            throw new ArgumentException("Rövid felvétel a kezdő zajmintához.");
        if (strength == 0)
            return (float[])x.Clone();
        var noise = x.AsSpan(0, count).ToArray().Select(v => (double)v).ToArray();
        var profile = Stft(noise, size, hop, true);
        var noiseDb = Db(profile, true);
        var threshold = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            float sum = 0;
            for (int t = 0; t < noiseDb.Length; t++)
                sum += (float)noiseDb[t][k];
            float mean = sum / noiseDb.Length;
            float variance = 0;
            for (int t = 0; t < noiseDb.Length; t++)
            {
                float d = (float)((float)noiseDb[t][k] - mean);
                variance += (float)(d * d);
            }
            threshold[k] = (float)(mean + (float)(MathF.Sqrt(variance / noiseDb.Length) * 1.5f));
        }
        var output = new float[x.Length];
        int chunkSize = 600000, padding = 2 * rate, gradFreq = (int)(250 / (rate / (size / 2d))), gradTime = (int)(40 / (hop / (double)rate * 1000));
        var fw = Triangle(gradFreq);
        var tw = Triangle(gradTime);
        for (int start = 0; start < x.Length; start += chunkSize)
        {
            cancel.ThrowIfCancellationRequested();
            int end = x.Length > chunkSize ? start + chunkSize : x.Length, length = end - start + 2 * padding;
            var chunk = new double[length];
            int offset = start - padding;
            for (int i = Math.Max(0, offset); i < Math.Min(x.Length, end + padding); i++)
                chunk[i - offset] = x[i];
            var spectrum = Stft(chunk, size, hop, false);
            var db = Db(spectrum, false);
            var mask = new double[db.Length][];
            for (int t = 0; t < mask.Length; t++)
            {
                mask[t] = new double[bins];
                for (int k = 0; k < bins; k++)
                    mask[t][k] = db[t][k] > threshold[k] ? 1 : 1 - strength;
            }
            // HU: A kétdimenziós háromszög-simítás szeparálható; az idő és frekvencia peremén nullakitöltést használunk.
            // EN: The 2D triangular smoothing kernel is separable, with zero padding at frequency/time boundaries.
            var temporal = new double[mask.Length][];
            for (int t = 0; t < mask.Length; t++)
            {
                temporal[t] = new double[bins];
                for (int j = -gradTime; j <= gradTime; j++)
                {
                    int at = t + j;
                    if (at < 0 || at >= mask.Length)
                        continue;
                    double w = tw[j + gradTime];
                    for (int k = 0; k < bins; k++)
                        temporal[t][k] += mask[at][k] * w;
                }
            }
            for (int t = 0; t < mask.Length; t++)
                for (int k = 0; k < bins; k++)
                {
                    double gain = 0;
                    for (int j = -gradFreq; j <= gradFreq; j++)
                    {
                        int at = k + j;
                        if (at >= 0 && at < bins)
                            gain += temporal[t][at] * fw[j + gradFreq];
                    }
                    spectrum.Re[t][k] *= gain;
                    spectrum.Im[t][k] *= gain;
                }
            var restored = Istft(spectrum, size, hop);
            int take = Math.Min(chunkSize, x.Length - start);
            for (int i = 0; i < take; i++)
                output[start + i] = (float)(padding + i < restored.Length ? restored[padding + i] : 0);
        }
        return output;
    }
    private static double[] Triangle(int grad)
    {
        var w = new double[2 * grad + 1];
        double sum = 0;
        for (int i = -grad; i <= grad; i++)
        {
            w[i + grad] = 1 - Math.Abs(i) / (double)(grad + 1);
            sum += w[i + grad];
        }
        for (int i = 0; i < w.Length; i++)
            w[i] /= sum;
        return w;
    }
    private static Spectrum Stft(double[] x, int n, int hop, bool single)
    {
        int frames = x.Length / hop + 1, bins = n / 2 + 1;
        var re = new double[frames][];
        var im = new double[frames][];
        var window = new double[n];
        for (int i = 0; i < n; i++)
            window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / n);
        for (int t = 0; t < frames; t++)
        {
            var r = new double[n];
            var m = new double[n];
            int start = t * hop - n / 2;
            for (int i = 0; i < n; i++)
            {
                int at = start + i;
                double a = at >= 0 && at < x.Length ? x[at] : 0;
                r[i] = single ? (float)((float)a * (float)window[i]) : a * window[i];
            }
            Numeric.Fft(r, m);
            re[t] = new double[bins];
            im[t] = new double[bins];
            for (int k = 0; k < bins; k++)
            {
                re[t][k] = single ? (float)((float)r[k] * (float)(2d / n)) : r[k] * 2 / n;
                im[t][k] = single ? (float)((float)m[k] * (float)(2d / n)) : m[k] * 2 / n;
            }
        }
        return new Spectrum(re, im);
    }
    private static double[][] Db(Spectrum s, bool single)
    {
        int frames = s.Re.Length, bins = s.Re[0].Length;
        var db = new double[frames][];
        for (int t = 0; t < frames; t++)
            db[t] = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            double maximum = double.NegativeInfinity;
            for (int t = 0; t < frames; t++)
            {
                double amplitude = Math.Sqrt(s.Re[t][k] * s.Re[t][k] + s.Im[t][k] * s.Im[t][k]);
                double value = single ? (float)(20f * MathF.Log10((float)((float)amplitude + (float)2.220446049250313e-16))) : 20 * Math.Log10(amplitude + 2.220446049250313e-16);
                db[t][k] = value;
                maximum = Math.Max(maximum, value);
            }
            for (int t = 0; t < frames; t++)
                db[t][k] = Math.Max(db[t][k], maximum - 80);
        }
        return db;
    }
    private static double[] Istft(Spectrum s, int n, int hop)
    {
        int length = n + (s.Re.Length - 1) * hop;
        var output = new double[length];
        var norm = new double[length];
        var window = new double[n];
        for (int i = 0; i < n; i++)
            window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / n);
        for (int t = 0; t < s.Re.Length; t++)
        {
            var r = new double[n];
            var m = new double[n];
            for (int k = 0; k <= n / 2; k++)
            {
                r[k] = s.Re[t][k];
                m[k] = s.Im[t][k];
            }
            Numeric.Mirror(r, m);
            Numeric.Fft(r, m, true);
            for (int i = 0; i < n; i++)
            {
                int at = t * hop + i;
                output[at] += r[i] * .5 * window[i];
                norm[at] += window[i] * window[i];
            }
        }
        int begin = n / 2, end = length - n / 2;
        var trimmed = new double[Math.Max(0, end - begin)];
        for (int i = begin; i < end; i++)
            trimmed[i - begin] = norm[i] > 1e-10 ? output[i] / norm[i] : output[i];
        return trimmed;
    }
}
