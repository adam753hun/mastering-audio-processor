// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Collections.Concurrent;
using System.Numerics;
namespace MasteringCs;

// HU: Saját menedzselt FFT és FIR-resampler: a futó programnak nincs Python/NumPy/SciPy függősége.
// EN: Managed FFT and FIR resampling: the running application has no Python/NumPy/SciPy dependency.
public static class Numeric
{
    private sealed class Plan
    {
        public int[] Reverse; public double[] Cos, Sin;
        public Plan(int n)
        {
            Reverse = new int[n];
            Cos = new double[n / 2];
            Sin = new double[n / 2];
            int bits = (int)Math.Log2(n);
            for (int i = 0; i < n; i++)
            {
                int v = i, r = 0;
                for (int b = 0; b < bits; b++)
                {
                    r = (r << 1) | (v & 1);
                    v >>= 1;
                }
                Reverse[i] = r;
            }
            for (int i = 0; i < n / 2; i++)
            {
                Cos[i] = Math.Cos(2 * Math.PI * i / n);
                Sin[i] = Math.Sin(2 * Math.PI * i / n);
            }
        }
    }
    private static readonly ConcurrentDictionary<int, Plan> Plans = new();
    public static void Fft(double[] re, double[] im, bool inverse = false)
    {
        int n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0)
            throw new ArgumentException("Az FFT mérete kettőhatvány legyen.");
        var p = Plans.GetOrAdd(n, x => new Plan(x));
        for (int i = 0; i < n; i++)
        {
            int j = p.Reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int size = 2; size <= n; size *= 2)
        {
            int half = size / 2, stride = n / size;
            for (int start = 0; start < n; start += size)
                for (int j = 0; j < half; j++)
                {
                    int a = start + j, b = a + half, k = j * stride;
                    double wi = (inverse ? 1 : -1) * p.Sin[k], wr = p.Cos[k];
                    double r = re[b] * wr - im[b] * wi, t = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - r;
                    im[b] = im[a] - t;
                    re[a] += r;
                    im[a] += t;
                }
        }
    }
    public static void Mirror(double[] re, double[] im)
    {
        int n = re.Length;
        im[0] = im[n / 2] = 0;
        for (int i = 1; i < n / 2; i++)
        {
            re[n - i] = re[i];
            im[n - i] = -im[i];
        }
    }
    public static double Percentile(double[] x, double fraction)
    {
        var a = (double[])x.Clone();
        Array.Sort(a);
        double pos = (a.Length - 1) * fraction;
        int i = (int)pos;
        return a[i] + (a[Math.Min(i + 1, a.Length - 1)] - a[i]) * (pos - i);
    }
    public static double[] FutureMaximum(double[] x, int look, double padding)
    {
        var result = new double[x.Length];
        var deque = new int[x.Length];
        int head = 0, tail = 0;
        for (int i = x.Length - 1; i >= 0; i--)
        {
            while (head < tail && deque[head] > i + look)
                head++;
            while (head < tail && x[deque[tail - 1]] <= x[i])
                tail--;
            deque[tail++] = i;
            result[i] = Math.Max(x[deque[head]], i + look >= x.Length ? padding : double.NegativeInfinity);
        }
        return result;
    }
    public static double Interpolate(double[] a, int index, int step)
    {
        int i = index / step;
        if (i >= a.Length - 1)
            return a[^1];
        double t = (index % step) / (double)step;
        return a[i] + (a[i + 1] - a[i]) * t;
    }
    public static double I0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 100; k++)
        {
            term *= x * x / (4 * k * k);
            sum += term;
            if (term < sum * 1e-17)
                break;
        }
        return sum;
    }
    private static readonly ConcurrentDictionary<(int, int, double), double[]> Filters = new();
    // HU: A scipy.signal.resample_poly alapértelmezett FIR-ablakának matematikai megfelelője, zéró peremkitöltéssel.
    // EN: Mathematical equivalent of scipy.signal.resample_poly's FIR window, using zero boundary padding.
    public static double[] Filter(int up, int down, double beta) => Filters.GetOrAdd((up, down, beta), k =>
    {
        int half = 10 * Math.Max(k.Item1, k.Item2);
        double cutoff = 1d / Math.Max(k.Item1, k.Item2), den = I0(k.Item3);
        var h = new double[2 * half + 1];
        for (int i = 0; i < h.Length; i++)
        {
            double t = (i - half) * cutoff, sinc = t == 0 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t), r = (i - half) / (double)half;
            h[i] = cutoff * sinc * I0(k.Item3 * Math.Sqrt(Math.Max(0, 1 - r * r))) / den;
        }
        double sum = h.Sum();
        for (int i = 0; i < h.Length; i++)
            h[i] *= k.Item1 / sum;
        return h;
    });
    public static double[] Resample(double[] x, int up, int down)
    {
        int g = Gcd(up, down);
        up /= g;
        down /= g;
        if (up == down)
            return x;
        var h = Filter(up, down, 5);
        int half = (h.Length - 1) / 2;
        var output = new double[checked((int)(((long)x.Length * up + down - 1) / down))];
        for (int j = 0; j < output.Length; j++)
        {
            long center = (long)j * down + half;
            int first = (int)Math.Max(0, (center - h.Length + up) / up), last = (int)Math.Min(x.Length - 1, center / up);
            double sum = 0;
            for (int k = first; k <= last; k++)
            {
                long at = center - (long)k * up;
                if (at >= 0 && at < h.Length)
                    sum += x[k] * h[(int)at];
            }
            output[j] = sum;
        }
        return output;
    }
    public static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }
    // HU: 16-szoros csúcsbecslés polifázisú skalár/SIMD pontszorzatokkal; nem készít 16-szor hosszabb hangmásolatot.
    // EN: 16x peak estimation via polyphase scalar/SIMD dot products; avoids allocating a 16x audio copy.
    public static double Peak(double[] x)
    {
        if (x.Length == 0)
            return 0;
        var h = Filter(16, 1, 10);
        var pad = new double[x.Length + 64];
        x.CopyTo(pad, 32);
        var coefficients = new double[16][];
        int width = Vector<double>.Count, terms = ((21 + width - 1) / width) * width;
        for (int phase = 0; phase < 16; phase++)
        {
            coefficients[phase] = new double[terms];
            for (int j = 0; j < 21; j++)
            {
                int at = 160 + phase - (j - 10) * 16;
                if (at >= 0 && at < h.Length)
                    coefficients[phase][j] = h[at];
            }
        }
        double max = 0;
        foreach (double v in x)
            max = Math.Max(max, Math.Abs(v));
        for (int i = -10; i < x.Length + 10; i++)
            for (int phase = 0; phase < 16; phase++)
            {
                double sum = 0;
                int start = i + 22;
                var c = coefficients[phase];
                if (Vector.IsHardwareAccelerated)
                {
                    var acc = Vector<double>.Zero;
                    for (int j = 0; j < terms; j += width)
                        acc += new Vector<double>(pad, start + j) * new Vector<double>(c, j);
                    sum = Vector.Sum(acc);
                }
                else
                    for (int j = 0; j < 21; j++)
                        sum += pad[start + j] * c[j];
                max = Math.Max(max, Math.Abs(sum));
            }
        return max;
    }
    public static double[] ToDouble(float[] x)
    {
        var d = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
            d[i] = x[i];
        return d;
    }
    public static double[] Quantized(float[] x, int width)
    {
        float scale = 1 << (width * 8 - 1);
        var d = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
            d[i] = (float)Math.Round((float)(x[i] * scale), MidpointRounding.ToEven) / scale;
        return d;
    }
}
