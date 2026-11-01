// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace MasteringCs;

// HU: Fájlhatáron szüneteltethető köteg. A futás pillanatképet kap a beállításokról, a már mentett fájlokat nem írja felül.
// EN: Batch with file-boundary pause/stop. A run uses a settings snapshot and never overwrites completed audio.
public sealed class BatchControl
{
    private readonly ManualResetEventSlim ready = new(true);
    private volatile bool stopRequested, pauseRequested;
    public bool StopRequested => stopRequested;
    public bool PauseRequested => pauseRequested;
    public void Pause()
    {
        pauseRequested = true;
        ready.Reset();
    }
    public void Resume()
    {
        pauseRequested = false;
        ready.Set();
    }
    public void Stop()
    {
        stopRequested = true;
        ready.Set();
    }
    public bool Boundary(Action<string> log)
    {
        if (StopRequested)
            return false;
        bool announced = PauseRequested;
        if (announced)
            log("CONTROL:PAUSED");
        ready.Wait();
        if (StopRequested)
            return false;
        if (announced)
            log("CONTROL:RESUMED");
        return true;
    }
}
public record BatchResult(int Successful, int Failed, bool Stopped, double Seconds);
public static class BatchEngine
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static BatchResult Run(Config config, string input, string output, string? presetName, bool modified, BatchControl control, Action<string> log)
    {
        var c = config.Copy();
        input = Path.GetFullPath(input);
        output = Path.GetFullPath(output);
        if (!Directory.Exists(input))
            throw new ArgumentException("A bemeneti mappa nem található.");
        string inp = CanonicalFolder(input).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, outp = CanonicalFolder(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (inp.StartsWith(outp, StringComparison.OrdinalIgnoreCase) || outp.StartsWith(inp, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A bemeneti és kimeneti mappa legyen különálló.");
        var files = Directory.EnumerateFiles(input).Where(f => Path.GetExtension(f).Equals(".wav", StringComparison.OrdinalIgnoreCase)).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
            throw new ArgumentException("Nincs WAV a bemeneti mappában: " + input + ". Az almappákat nem dolgozza fel.");
        Directory.CreateDirectory(output);
        using var journal = new StreamWriter(Path.Combine(output, "log.txt"), true, new UTF8Encoding(false)) { AutoFlush = true };
        void Report(string text)
        {
            journal.WriteLine(text);
            log(text);
        }
        string FineFolder()
        {
            string path = output;
            if (!string.IsNullOrEmpty(presetName))
                path = Path.Combine(path, SafeFolder(presetName, modified));
            string? cursor = path;
            while (cursor != null && !cursor.Equals(output, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("A finomhangolt célmappa nem lehet külső helyre mutató hivatkozás.");
                cursor = Path.GetDirectoryName(cursor);
            }
            return path;
        }
        int ok = 0, failed = 0;
        var watch = Stopwatch.StartNew();
        Report("Indítás: " + DateTimeOffset.Now.ToString("O"));
        Report("Motor: teljes C# / .NET 8; zajszűrés: " + c.S("noiseReduction", "method"));
        for (int index = 0; index < files.Length; index++)
        {
            if (!control.Boundary(Report))
                break;
            string source = files[index], stem = Path.GetFileNameWithoutExtension(source);
            Report($"[{index + 1}/{files.Length}] {Path.GetFileName(source)}");
            var timer = Stopwatch.StartNew();
            try
            {
                var audio = WaveFile.Read(source);
                // HU: Csak a nyilvános stationary zajszűrő fut; nincs rejtett alapfeldolgozó ág.
                // EN: Only public stationary denoising runs; there is no hidden preprocessing branch.
                var denoised = c.S("noiseReduction", "method") == "bypass"
                    ? (float[])audio.Samples.Clone()
                    : StationaryDenoiser.Apply(audio.Samples, audio.Rate, c);
                float[] adjusted = denoised;
                if (c.B("finishing", "enabled"))
                {
                    var result = Finishing.Apply(denoised, audio.Rate, audio.Width, c);
                    adjusted = result.Samples;
                    Report("Mérések: " + JsonSerializer.Serialize(result.Info, new JsonSerializerOptions(JsonOptions) { WriteIndented = false }));
                    Report($"Elért hangerő: {result.Info.OutputLUFS:F2} LUFS • Csúcsszint: {result.Info.OutputTruePeakEstimateDbTP:F2} dBTP");
                }
                else if (c.B("silenceControl", "enabled"))
                {
                    var result = Finishing.Gate(Numeric.ToDouble(denoised), audio.Rate, c);
                    adjusted = result.Samples.Select(v => (float)v).ToArray();
                    Report("Gate/Expander: " + JsonSerializer.Serialize(result.Info, new JsonSerializerOptions(JsonOptions) { WriteIndented = false }));
                }
                string path = WaveFile.SaveUnique(FineFolder(), stem, adjusted, audio.Rate, audio.Width,
                    1, c.S("outputNaming", "duplicateSuffix"), true, Report);
                Report("Kész: " + path);
                ok++;
                Report($"Fájl feldolgozási ideje: {timer.Elapsed.TotalSeconds:F3} s");
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { failed++; Report("Hiba: " + Path.GetFileName(source) + ": " + ex.Message); }
        }
        var resultBatch = new BatchResult(ok, failed, control.StopRequested, watch.Elapsed.TotalSeconds);
        Report($"Befejezve. Sikeres: {ok}; hibás: {failed}; megállítva: {control.StopRequested}; idő: {watch.Elapsed.TotalSeconds:F2} s");
        File.WriteAllText(Path.Combine(output, "last-run.json"), JsonSerializer.Serialize(resultBatch, JsonOptions), new UTF8Encoding(false));
        return resultBatch;
    }
    // HU: A preset neve adat, nem útvonal; egyetlen biztonságos komponens készül, szóköz helyett aláhúzással.
    // EN: Preset names are data, not paths; produce one safe component, replacing whitespace with underscores.
    public static string SafeFolder(string name, bool modified = false)
    {
        string value = name.Normalize(NormalizationForm.FormKC);
        value = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        value = Regex.Replace(value, "_+", "_").Trim('_');
        if (value.Length == 0)
            value = "Preset";
        value = value[..Math.Min(value.Length, modified ? 64 : 80)];
        if (IsReserved(value))
            value = "Preset_" + value;
        return value + (modified ? "_modositott" : "");
    }
    public static bool IsReserved(string value) => new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(value, StringComparer.OrdinalIgnoreCase) || Regex.IsMatch(value, @"^(COM|LPT)[1-9]$", RegexOptions.IgnoreCase);
    // HU: A mappák összehasonlításakor a szülőkönyvtári junction/szimbolikus linket is feloldjuk, nem csak a szöveges útvonalat nézzük.
    // EN: Resolve parent junctions/symlinks before checking folder separation, rather than comparing path text alone.
    private static string CanonicalFolder(string path)
    {
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current))
            {
                var target = new DirectoryInfo(current).ResolveLinkTarget(true);
                if (target != null)
                    current = target.FullName;
            }
        }
        return Path.GetFullPath(current);
    }
}
