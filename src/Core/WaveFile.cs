// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Text;
namespace MasteringCs;

// HU: Kizárólag mono, tömörítetlen PCM16/24; a RIFF ismeretlen chunkjait biztonságosan átugorjuk.
// EN: Mono uncompressed PCM16/24 only; safely skip unknown RIFF chunks.
public static class WaveFile
{
    public record Audio(float[] Samples, int Rate, int Width);
    public static Audio Read(string path)
    {
        using var f = File.OpenRead(path);
        using var r = new BinaryReader(f, Encoding.ASCII);
        if (new string(r.ReadChars(4)) != "RIFF")
            throw new ArgumentException("RIFF WAV szükséges.");
        r.ReadUInt32();
        if (new string(r.ReadChars(4)) != "WAVE")
            throw new ArgumentException("Nem WAVE fájl.");
        int channels = 0, rate = 0, bits = 0, format = 0, align = 0;
        long dataOffset = -1;
        uint dataLength = 0;
        while (f.Position + 8 <= f.Length)
        {
            string tag = new(r.ReadChars(4));
            uint size = r.ReadUInt32();
            long start = f.Position, end = checked(start + size);
            if (end > f.Length)
                throw new ArgumentException("Csonka WAV-chunk.");
            if (tag == "fmt ")
            {
                if (size < 16)
                    throw new ArgumentException("Hibás WAV-formátum.");
                format = r.ReadUInt16();
                channels = r.ReadUInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                align = r.ReadUInt16();
                bits = r.ReadUInt16();
                if (format == 65534 && size >= 40)
                {
                    r.ReadUInt16();
                    r.ReadUInt16();
                    r.ReadUInt32();
                    format = r.ReadUInt16();
                }
            }
            else if (tag == "data" && dataOffset < 0)
            {
                dataOffset = start;
                dataLength = size;
            }
            f.Position = Math.Min(f.Length, end + (size & 1));
        }
        if (format != 1 || channels != 1 || bits is not (16 or 24) || rate is not (44100 or 96000) || align != bits / 8 || dataOffset < 0 || dataLength % align != 0)
            throw new ArgumentException("Mono PCM 16/24 bit, 44,1/96 kHz WAV szükséges.");
        int width = bits / 8, count = checked((int)(dataLength / width));
        var output = new float[count];
        f.Position = dataOffset;
        byte[] data = r.ReadBytes(checked((int)dataLength));
        if (data.Length != dataLength)
            throw new ArgumentException("Csonka WAV-adat.");
        float scale = 1 << (bits - 1);
        for (int i = 0; i < count; i++)
        {
            int p = i * width, value = width == 2 ? (short)(data[p] | data[p + 1] << 8) : (data[p] | data[p + 1] << 8 | data[p + 2] << 16);
            if (width == 3 && (value & 0x800000) != 0)
                value |= unchecked((int)0xff000000);
            output[i] = value / scale;
        }
        return new Audio(output, rate, width);
    }
    public static byte[] Pcm(float[] x, int width, float safety)
    {
        if (width is not (2 or 3))
            throw new ArgumentException("16/24 bit szükséges.");
        float scale = (float)((1 << (width * 8 - 1)) * safety);
        long limit = 1L << (width * 8 - 1);
        var data = new byte[checked(x.Length * width)];
        for (int i = 0; i < x.Length; i++)
        {
            if (!float.IsFinite(x[i]))
                throw new ArgumentException("Nem véges PCM-minta.");
            double number = Math.Round((float)(x[i] * scale), MidpointRounding.ToEven);
            if (number < -limit || number >= limit)
                throw new OverflowException("Output exceeds PCM range");
            int value = (int)number, p = i * width;
            data[p] = (byte)value;
            data[p + 1] = (byte)(value >> 8);
            if (width == 3)
                data[p + 2] = (byte)(value >> 16);
        }
        return data;
    }
    public static string SaveUnique(string folder, string stem, float[] x, int rate, int width, float safety, string suffix, bool protectOverflow, Action<string> log)
    {
        Directory.CreateDirectory(folder);
        byte[]? data = null;
        for (int counter = 0; counter < 100000; counter++)
        {
            string path = Path.Combine(folder, stem + (counter == 0 ? "" : suffix + counter.ToString("_000")) + ".wav");
            if (File.Exists(path) || Directory.Exists(path))
                continue;
            if (data == null)
            {
                try
                {
                    data = Pcm(x, width, safety);
                }
                catch (OverflowException) when (protectOverflow) { float peak = x.Max(v => Math.Abs(v)); float effective = MathF.BitDecrement(Math.Min(safety, (float)((1 - 2d / (1 << (width * 8 - 1))) / peak))); data = Pcm(x, width, effective); log($"Exportvédelem: {20 * Math.Log10(effective / safety):F4} dB egyenletes halkítás; nincs csúcslevágás."); }
            }
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path)) { continue; }
            try
            {
                using (stream)
                using (var w = new BinaryWriter(stream, Encoding.ASCII))
                {
                    w.Write(Encoding.ASCII.GetBytes("RIFF"));
                    int padding = data.Length & 1;
                    w.Write(checked(36 + data.Length + padding));
                    w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    w.Write(16);
                    w.Write((ushort)1);
                    w.Write((ushort)1);
                    w.Write(rate);
                    w.Write(rate * width);
                    w.Write((ushort)width);
                    w.Write((ushort)(width * 8));
                    w.Write(Encoding.ASCII.GetBytes("data"));
                    w.Write(data.Length);
                    w.Write(data);
                    if (padding != 0)
                        w.Write((byte)0);
                    stream.Flush(true);
                }
            }
            catch { try { File.Delete(path); } catch (IOException) { } throw; }
            return path;
        }
        throw new IOException("Túl sok azonos fájlnév.");
    }
}
