// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Reflection;
using System.Text.Json.Nodes;
namespace MasteringCs;

// HU: Csak generált hanggal, új tesztmappában dolgozunk; nincs felhasználói felvétel vagy személyes preset.
// EN: Use generated audio in a new test directory; no personal recordings or presets are required.
public static class Verification
{
    public static JsonObject Run(string directory)
    {
        if (Directory.Exists(directory))
            throw new ArgumentException("New test directory required.");
        Directory.CreateDirectory(directory);
        var checks = new JsonArray();
        void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException(label);
            checks.Add(label);
        }
        foreach (string mode in Config.Modes)
        {
            var c = new Config(mode);
            Check(c.S("noiseReduction", "method") == "noisereduce_stationary", "Stationary default: " + mode);
            Check(c.N("finishing", "targetLUFS") == -23, "Default -23 LUFS: " + mode);
        }
        var config = new Config("gentle");
        bool rejected = false;
        try
        {
            _ = new Config("gentle", new JsonObject { ["unknownModule"] = new JsonObject() });
        }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Unknown configuration groups rejected");
        config.Set("noiseReduction", "method", JsonValue.Create("unsupported"));
        rejected = false;
        try
        {
            config.Validate();
        }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Unsupported denoiser rejected");
        config = new Config("gentle");
        string presets = Path.Combine(directory, "Presets");
        string preset = PresetFiles.Save(presets, "Voice One", config)!;
        byte[] previous = File.ReadAllBytes(preset);
        config.Set("finishing", "targetLUFS", JsonValue.Create(-14d));
        Check(PresetFiles.Save(presets, "Voice One", config, _ => DialogResult.Cancel) == null, "Preset cancel");
        Check(File.ReadAllBytes(preset).SequenceEqual(previous), "Cancel preserves original");
        Check(PresetFiles.Save(presets, "Voice One", config, _ => DialogResult.No)!.EndsWith("_001.json"), "Keep both numbers collision");
        PresetFiles.Save(presets, "Voice One", config, _ => DialogResult.Yes);
        Check(Config.ReadPreset(preset, out _).N("finishing", "targetLUFS") == -14, "Explicit preset overwrite");
        Check(BatchEngine.SafeFolder("Voice One") == "Voice_One", "Safe preset folder");
        Check(BatchEngine.SafeFolder("CON") == "Preset_CON", "Reserved Windows folder");
        var control = new BatchControl();
        control.Pause();
        using var arrived = new ManualResetEventSlim();
        var pending = Task.Run(() => control.Boundary(_ => arrived.Set()));
        Check(arrived.Wait(3000) && !pending.IsCompleted, "Pause blocks at boundary");
        control.Resume();
        Check(pending.GetAwaiter().GetResult(), "Resume");
        control.Stop();
        Check(!control.Boundary(_ => { }), "Stop");
        string input = Path.Combine(directory, "input");
        Directory.CreateDirectory(input);
        string odd = WaveFile.SaveUnique(directory, "odd-pcm24", [0, .25f, -.5f], 44100, 3, 1, "_new", false, _ => { });
        Check(new FileInfo(odd).Length % 2 == 0 && WaveFile.Read(odd).Samples.Length == 3, "Odd PCM24 RIFF padding");
        foreach (int rate in new[] { 44100, 96000 })
            foreach (int width in new[] { 2, 3 })
            {
                var rng = new Random(123);
                var x = new float[rate * 2];
                for (int i = 0; i < x.Length; i++)
                    x[i] = (float)((rng.NextDouble() - .5) * .002 + (i > rate / 2 && i < rate * 1.5 ? .12 * Math.Sin(2 * Math.PI * 220 * i / rate) : 0));
                string wave = WaveFile.SaveUnique(input, $"synthetic-{rate}-{width}", x, rate, width, 1, "_new", false, _ => { });
                var read = WaveFile.Read(wave);
                var nr = new Config("clean");
                nr.Set("noiseReduction", "strength", JsonValue.Create(0d));
                Check(StationaryDenoiser.Apply(read.Samples, rate, nr).SequenceEqual(read.Samples), "Zero strength bypass: " + rate + "/" + width);
                nr.Set("noiseReduction", "strength", JsonValue.Create(.6));
                var filtered = StationaryDenoiser.Apply(read.Samples, rate, nr);
                Check(filtered.Length == x.Length && filtered.All(float.IsFinite), "Denoising length and finite samples: " + rate + "/" + width);
                double before = read.Samples.Take(rate / 3).Average(v => (double)v * v);
                double after = filtered.Take(rate / 3).Average(v => (double)v * v);
                Check(after < before, "Noise energy reduced: " + rate + "/" + width);
                var finish = Finishing.Apply(filtered, rate, width, new Config("gentle"));
                Check(finish.Info.OutputTruePeakEstimateDbTP <= -1, "Quantized true-peak ceiling: " + rate + "/" + width);
                Check(Math.Abs(finish.Info.OutputLUFS!.Value + 23) < .1, "Loudness target on feasible signal: " + rate + "/" + width);
            }
        var gateConfig = new Config("gentle");
        gateConfig.Set("silenceControl", "enabled", JsonValue.Create(true));
        gateConfig.Set("silenceControl", "gateEnabled", JsonValue.Create(true));
        gateConfig.Set("silenceControl", "autoThreshold", JsonValue.Create(false));
        gateConfig.Set("silenceControl", "thresholdDbFS", JsonValue.Create(-50d));
        var quiet = Enumerable.Repeat(.00001, 44100 * 2).ToArray();
        Check(Finishing.Gate(quiet, 44100, gateConfig).Info.ExactZeroGainFraction > .5, "Gate reaches exact zero");
        gateConfig.Set("silenceControl", "gateEnabled", JsonValue.Create(false));
        Check(Finishing.Gate(quiet, 44100, gateConfig).Info.ExactZeroGainFraction == 0, "Expander finite attenuation");
        var cfg = new Config("gentle");
        var stop = new BatchControl();
        string output = Path.Combine(directory, "output");
        var batch = BatchEngine.Run(cfg, input, output, "Voice One", false, stop,
            s => { if (s.StartsWith("Kész: ")) stop.Stop(); });
        Check(batch.Successful == 1 && batch.Stopped, "Stop preserves completed file");
        Check(Directory.GetFiles(output, "*.wav").Length == 0 && Directory.GetFiles(Path.Combine(output, "Voice_One"), "*.wav").Length == 1, "Only one processed output, preset folder");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using (var settings = new SettingsForm(directory, cfg, null, output))
        {
            var editors = (Dictionary<string, Control>)typeof(SettingsForm).GetField("editors", flags)!.GetValue(settings)!;
            Check(!editors["silenceControl/thresholdDbFS"].Enabled, "Auto threshold disables manual field");
            ((ComboBox)editors["noiseReduction/method"]).SelectedIndex = 1;
            Check(!editors["noiseReduction/strength"].Enabled, "Denoiser bypass disables controls");
            Check(!editors["finishing/compressionRatio"].Enabled, "Compressor bypass disables controls");
            settings.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(settings.Width, settings.Height);
            settings.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(Path.Combine(directory, "settings.png"));
        }
        using (var form = new MainForm(directory))
            typeof(MainForm).GetMethod("SavePreferences", flags)!.Invoke(form, null);
        Check(File.Exists(Path.Combine(directory, "gui_preferences.json")), "GUI preferences saved");
        VerifyLanguages(directory, Check);
        return new JsonObject { ["passed"] = true, ["checks"] = checks, ["framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription };
    }
    // HU: A nyelvváltás valódi mentési útját és a DSP változatlanságát is ellenőrizzük.
    // EN: Exercise the actual language-save path and verify that audio processing stays identical.
    private static void VerifyLanguages(string directory, Action<bool, string> check)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IEnumerable<Control> Tree(Control c) => new[] { c }.Concat(c.Controls.Cast<Control>().SelectMany(Tree));
        foreach (var f in Config.Fields.Where(f => !f.Hidden))
            check(L.Catalog.ContainsKey(f.Label), "Translated field: " + f.Group + "/" + f.Key);
        foreach (var item in L.Catalog)
        {
            check(!string.IsNullOrWhiteSpace(item.Value.hu) && !string.IsNullOrWhiteSpace(item.Value.en), "Complete translation: " + item.Key.Split('\n')[0]);
            int Count(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\{[^{}]+\}").Count;
            check(Count(item.Key) == Count(item.Value.hu) && Count(item.Key) == Count(item.Value.en), "Translation placeholders: " + item.Key.Split('\n')[0]);
        }
        L.Language = "hu";
        check(L.T("Előretekintés (ms)") == "Lookahead (ms)" && L.T("Küszöb (dBFS)") == "Threshold (dBFS)", "Hungarian UI retains international audio terms");
        string uiRoot = Path.Combine(directory, "language-checks");
        Directory.CreateDirectory(uiRoot);
        using (var main = new MainForm(uiRoot))
        {
            var profiles = (Dictionary<string, Config>)typeof(MainForm).GetField("profiles", flags)!.GetValue(main)!;
            string before = profiles["gentle"].Values.ToJsonString();
            using (var cancel = new SettingsForm(uiRoot, profiles["gentle"], null))
            {
                var choice = (ComboBox)typeof(SettingsForm).GetField("language", flags)!.GetValue(cancel)!;
                choice.SelectedIndex = 1;
                check(L.Language == "hu" && cancel.UiLanguage == "hu", "Pending language does not apply before Save");
                cancel.DialogResult = DialogResult.Cancel;
            }
            check(L.Language == "hu" && profiles["gentle"].Values.ToJsonString() == before, "Cancel preserves language and DSP settings");
            using var settings = new SettingsForm(uiRoot, profiles["gentle"], null);
            var language = (ComboBox)typeof(SettingsForm).GetField("language", flags)!.GetValue(settings)!;
            language.SelectedIndex = 1;
            settings.Show();
            Application.DoEvents();
            Tree(settings).OfType<Button>().Single(b => b.Text == "Mentés").PerformClick();
            check(settings.UiLanguage == "en" && settings.DialogResult == DialogResult.OK, "Settings Save selects English");
            typeof(MainForm).GetMethod("ApplySettings", flags)!.Invoke(main, [settings]);
            check(Tree(main).OfType<Button>().Any(b => b.Text == "Start processing"), "Live main-window translation without restart");
            check(profiles["gentle"].Values.ToJsonString() == before, "Language change leaves DSP configuration identical");
            var payload = JsonNode.Parse(File.ReadAllText(Path.Combine(uiRoot, "gui_preferences.json")))!;
            check(payload["language"]!.GetValue<string>() == "en", "Selected language persisted separately");
        }
        L.Language = "hu";
        using (var reloaded = new MainForm(uiRoot))
            check(L.Language == "en" && Tree(reloaded).OfType<Button>().Any(b => b.Text == "Start processing"), "English restored on next startup");
        using (var english = new SettingsForm(uiRoot, new Config("gentle"), null))
        {
            var tab = Tree(english).OfType<TabControl>().Single();
            check(tab.TabPages.Cast<TabPage>().Any(p => p.Text == "Interface") && tab.TabPages.Cast<TabPage>().Any(p => p.Text == "Noise reduction"), "English settings tabs");
            check(Tree(english).OfType<Label>().Any(l => l.Text == "True Peak ceiling (dBTP)"), "English technical field labels");
            english.Show();
            Application.DoEvents();
            tab.SelectedTab = tab.TabPages.Cast<TabPage>().Single(p => p.Text == "Interface");
            Application.DoEvents();
            using var picture = new Bitmap(english.Width, english.Height);
            english.DrawToBitmap(picture, new Rectangle(0, 0, picture.Width, picture.Height));
            picture.Save(Path.Combine(directory, "language-settings-en.png"));
        }
        check(L.Render("Betöltve: Voice One. A Mentés alkalmazza, a Mégse elveti.") == "Loaded: Voice One. Save applies it; Cancel discards it.", "Translated preset message preserves user name");
        check(L.Render("Betöltve: Mentés. A Mentés alkalmazza, a Mégse elveti.") == "Loaded: Mentés. Save applies it; Cancel discards it.", "Preset name equal to a UI word is not translated");
        check(L.Render("Kész: C:\\Audio\\Mentés.wav\r\n") == "Saved: C:\\Audio\\Mentés.wav\r\n", "Translated log preserves filename and line ending");
        check(L.Render("Nincs WAV a bemeneti mappában: C:\\Audio\\Voice One. Az almappákat nem dolgozza fel.") == "No WAV files in the input folder: C:\\Audio\\Voice One. Subfolders are not processed.", "Translated error preserves folder path");
        L.Language = "hu";
        var sample = Enumerable.Range(0, 44100).Select(i => (float)(.1 * Math.Sin(2 * Math.PI * 220 * i / 44100))).ToArray();
        var hu = Finishing.Apply(sample, 44100, 2, new Config("gentle")).Samples;
        L.Language = "en";
        var en = Finishing.Apply(sample, 44100, 2, new Config("gentle")).Samples;
        check(hu.SequenceEqual(en), "Audio output is sample-identical across UI languages");
        string prefsPath = Path.Combine(uiRoot, "gui_preferences.json");
        var old = JsonNode.Parse(File.ReadAllText(prefsPath))!.AsObject();
        old.Remove("language");
        File.WriteAllText(prefsPath, old.ToJsonString());
        using (var legacyPreferences = new MainForm(uiRoot))
            check(L.Language == "hu", "Existing preferences without language default to Hungarian");
    }
}
