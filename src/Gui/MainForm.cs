// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Diagnostics;
using System.Text.Json.Nodes;
namespace MasteringCs;

public static class BuildInfo
{
    public static readonly JsonObject Data = new() { { "version", "0.2.0" }, { "build", "20261101.02" }, { "date", "2026-11-01" }, { "edition", "public-csharp" }, { "framework", "net8.0-windows" } };
    public static string Label => "0.2.0 · Build 20261101.02 · 2026.11.01";
    public static string Credits => L.T("about.credits") + "\n\n" + Label;

}

// HU: Natív Windows Forms felület. A DSP Task.Run alatt dolgozik; a felületre csak BeginInvoke ad visszajelzést.
// EN: Windows Forms interface. DSP runs under Task.Run; only BeginInvoke marshals progress back to the UI.
public sealed class MainForm : Form
{
    private readonly string root;
    private readonly Dictionary<string, Config> profiles = Config.Modes.ToDictionary(x => x, x => new Config(x));
    private readonly Dictionary<string, JsonObject?> sources = Config.Modes.ToDictionary(x => x, x => (JsonObject?)null);
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly TextBox input = new() { Dock = DockStyle.Fill }, output = new() { Dock = DockStyle.Fill };
    private readonly CheckBox expander = new() { Text = "Expander bekapcsolása — kíméletes zajhalkítás", AutoSize = true }, gate = new() { Text = "Gate is — hosszabb szünetekben teljes csend", AutoSize = true };
    private readonly TextBox journal = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, Font = new Font("Consolas", 9) };
    private readonly Label status = new() { AutoSize = true, Text = "Válassz módot és hangmappákat.", MaximumSize = new Size(950, 0) };
    private readonly Button start = new() { Text = "Feldolgozás indítása", AutoSize = true }, pause = new() { Text = "Szünet", AutoSize = true, Enabled = false }, stop = new() { Text = "Megállítás", AutoSize = true, Enabled = false }, settings = new() { Text = "Beállítások…", AutoSize = true };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0 };
    private readonly List<Control> frozen = [];
    private string selected = "gentle"; private BatchControl control = new(); private bool busy, loading, closeAfter, preferencesInvalid;
    public MainForm(string? baseDirectory = null, bool preview = false)
    {
        root = baseDirectory ?? AppContext.BaseDirectory;
        Text = "Mastering Audio Processor — Public Edition";
        Size = new Size(1030, 850);
        MinimumSize = new Size(900, 740);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(239, 243, 248);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 10 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var heading = new Label { Text = "MASTERING  /  PUBLIC", Font = new Font("Segoe UI", 23, FontStyle.Bold), ForeColor = Color.FromArgb(30, 52, 81), AutoSize = true };
        header.Controls.Add(heading, 0, 0);
        var about = new Button { Text = "Névjegy", AutoSize = true };
        about.Click += (_, _) => L.Show(this, BuildInfo.Credits, "Mastering", MessageBoxButtons.OK, MessageBoxIcon.Information);
        header.Controls.Add(about, 1, 0);
        layout.Controls.Add(header, 0, 0);
        var profileRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 10) };
        profileRow.Controls.Add(new Label { Text = "Feldolgozási mód:", AutoSize = true, Margin = new Padding(0, 8, 10, 0) });
        mode.Items.AddRange(Config.Titles);
        profileRow.Controls.Add(mode);
        profileRow.Controls.Add(settings);
        layout.Controls.Add(profileRow, 0, 1);
        frozen.AddRange([mode, settings]);
        settings.Click += (_, _) => EditSettings();
        var toggles = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        toggles.Controls.Add(expander);
        toggles.Controls.Add(gate);
        layout.Controls.Add(toggles, 0, 2);
        frozen.AddRange([expander, gate]);
        expander.CheckedChanged += (_, _) => { gate.Enabled = expander.Checked && !busy; };
        layout.Controls.Add(FolderRow("Bemeneti mappa", input), 0, 3);
        layout.Controls.Add(FolderRow("Kimeneti mappa", output), 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 14, 0, 8) };
        start.BackColor = Color.FromArgb(32, 77, 129);
        start.ForeColor = Color.White;
        start.FlatStyle = FlatStyle.Flat;
        actions.Controls.AddRange([start, pause, stop]);
        var open = new Button { Text = "Kimeneti mappa megnyitása", AutoSize = true };
        open.Click += (_, _) => { if (Directory.Exists(output.Text)) Process.Start(new ProcessStartInfo(output.Text) { UseShellExecute = true }); };
        actions.Controls.Add(open);
        layout.Controls.Add(actions, 0, 5);
        frozen.Add(start);
        layout.Controls.Add(progress, 0, 6);
        status.Margin = new Padding(0, 8, 0, 10);
        layout.Controls.Add(status, 0, 7);
        layout.Controls.Add(journal, 0, 8);
        layout.Controls.Add(new Label { Text = "Mono PCM WAV · 16/24 bit · 44,1/96 kHz · Eredeti fájlokat megőrzi · Gyári cél −23 LUFS", AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, 0, 9);
        start.Click += async (_, _) => await StartBatch();
        pause.Click += (_, _) => { if (!busy) return; if (control.PauseRequested) { control.Resume(); pause.Text = L.Render("Szünet"); status.Text = L.Render("Folytatás kérése…"); } else { control.Pause(); pause.Text = L.Render("Folytatás"); status.Text = L.Render("Szünet kérése: az aktuális fájl még elkészül."); } };
        stop.Click += (_, _) => StopBatch();
        mode.SelectedIndexChanged += (_, _) => ChangeMode();
        input.Text = Path.Combine(root, "input");
        output.Text = Path.Combine(root, "output");
        if (!preview)
            LoadPreferences();
        else
        {
            input.Text = "input";
            output.Text = "output";
        }
        loading = true;
        mode.SelectedIndex = Array.IndexOf(Config.Modes, selected);
        loading = false;
        RestoreToggles();
        ApplyLanguage();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; if (L.Show(this, "Az aktuális fájl még elkészül, utána megállítjuk a köteget és bezárjuk az ablakot. Kéred?", "Megállítás és bezárás", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { closeAfter = true; StopBatch(); } } else { try { SavePreferences(); } catch (Exception ex) { if (L.Show(this, "A beállítás nem menthető: " + ex.Message + "\nBezárod így is?", "Mentési hiba", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true; } } };
    }
    private Control FolderRow(string title, TextBox box)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 6, 0, 4) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label { Text = title, AutoSize = true }, 0, 0);
        row.SetColumnSpan(row.GetControlFromPosition(0, 0)!, 2);
        row.Controls.Add(box, 0, 1);
        var browse = new Button { Text = "Tallózás…", AutoSize = true };
        browse.Click += (_, _) => { using var d = new FolderBrowserDialog { Description = L.T(title), UseDescriptionForTitle = true, SelectedPath = Directory.Exists(box.Text) ? box.Text : root }; if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath; };
        row.Controls.Add(browse, 1, 1);
        frozen.AddRange([box, browse]);
        return row;
    }
    private void CaptureProfile()
    {
        profiles[selected].Set("silenceControl", "enabled", JsonValue.Create(expander.Checked));
        profiles[selected].Set("silenceControl", "gateEnabled", JsonValue.Create(gate.Checked));
    }
    private void RestoreToggles()
    {
        expander.Checked = profiles[selected].B("silenceControl", "enabled");
        gate.Checked = profiles[selected].B("silenceControl", "gateEnabled");
        gate.Enabled = expander.Checked && !busy;
    }
    private void ChangeMode()
    {
        if (loading || mode.SelectedIndex < 0)
            return;
        CaptureProfile();
        selected = Config.Modes[mode.SelectedIndex];
        RestoreToggles();
        TrySave();
    }
    private void EditSettings()
    {
        if (busy)
            return;
        CaptureProfile();
        using var f = new SettingsForm(root, profiles[selected], sources[selected], output.Text);
        if (f.ShowDialog(this) == DialogResult.OK)
            ApplySettings(f);
    }
    private void ApplySettings(SettingsForm f)
    {
        profiles[selected] = f.Result;
        sources[selected] = f.PresetSource;
        L.Language = f.UiLanguage;
        ApplyLanguage();
        RestoreToggles();
        TrySave();
        status.Text = L.Render("Beállítások alkalmazva. A következő futás ezeket használja.");
    }
    // HU: A már nyitott főablak frissül; a kiválasztott profil és a szerkesztett útvonalak megmaradnak.
    // EN: Refresh the existing main window, preserving the selected profile and edited folder paths.
    private void ApplyLanguage()
    {
        L.TranslateTree(this);
        bool previous = loading;
        loading = true;
        mode.Items.Clear();
        mode.Items.AddRange(Config.Titles);
        mode.SelectedIndex = Array.IndexOf(Config.Modes, selected);
        loading = previous;
    }
    private void TrySave()
    {
        try
        {
            SavePreferences();
        }
        catch (Exception ex) { Append("A beállítások nem menthetők: " + ex.Message); }
    }
    private void SavePreferences()
    {
        CaptureProfile();
        var values = new JsonObject();
        var presetSources = new JsonObject();
        foreach (var m in Config.Modes)
        {
            values[m] = profiles[m].Values.DeepClone();
            presetSources[m] = sources[m]?.DeepClone();
        }
        var payload = new JsonObject { { "schema", 2 }, { "language", L.Language }, { "mode", selected }, { "input", input.Text }, { "output", output.Text }, { "profiles", values }, { "presetSources", presetSources } };
        string temp = Path.Combine(root, ".gui-preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, payload.ToJsonString(BatchEngine.JsonOptions), new System.Text.UTF8Encoding(false));
            if (preferencesInvalid && File.Exists(Path.Combine(root, "gui_preferences.json")))
                File.Copy(Path.Combine(root, "gui_preferences.json"), Path.Combine(root, "gui_preferences.invalid-" + Guid.NewGuid().ToString("N") + ".json"));
            File.Move(temp, Path.Combine(root, "gui_preferences.json"), true);
            preferencesInvalid = false;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void LoadPreferences()
    {
        string path = Path.Combine(root, "gui_preferences.json");
        if (!File.Exists(path))
            return;
        try
        {
            if (new FileInfo(path).Length > 1048576)
                throw new ArgumentException("Túl nagy mentés.");
            var j = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (j["schema"]?.GetValue<int>() is not (1 or 2))
                throw new ArgumentException("Ismeretlen mentési séma.");
            string m = j["mode"]!.GetValue<string>();
            if (!Config.Modes.Contains(m))
                throw new ArgumentException("Ismeretlen mód.");
            var loaded = new Dictionary<string, Config>();
            var names = new Dictionary<string, JsonObject?>();
            foreach (var mode in Config.Modes)
            {
                loaded[mode] = new Config(mode, j["profiles"]![mode]!.AsObject());
                var ps = j["presetSources"]?[mode] as JsonObject;
                if (ps != null)
                {
                    Config.ValidateName(ps["name"]!.GetValue<string>());
                    ps = (JsonObject)ps.DeepClone();
                    ps["values"] = new Config(mode, ps["values"]!.AsObject()).Values.DeepClone();
                }
                names[mode] = ps;
            }
            foreach (var mode in Config.Modes)
            {
                profiles[mode] = loaded[mode];
                sources[mode] = names[mode];
            }
            selected = m;
            string language = j["language"]?.GetValue<string>() ?? "hu";
            L.Language = language is "hu" or "en" ? language : "hu";
            input.Text = j["input"]!.GetValue<string>();
            output.Text = j["output"]!.GetValue<string>();
        }
        catch (Exception ex) { preferencesInvalid = true; selected = "gentle"; foreach (var m in Config.Modes) { profiles[m] = new Config(m); sources[m] = null; } Append("A mentett beállítások nem tölthetők be; gyári értékekkel indul: " + ex.Message); }
    }
    // HU: A mintavétel/DSP a felülettől független háttérfeladat; Megállítás mindig csak fájlhatáron történik.
    // EN: Sampling/DSP run in an independent background task; Stop is always honored at a file boundary.
    private async Task StartBatch()
    {
        if (busy)
            return;
        CaptureProfile();
        var cfg = profiles[selected].Copy();
        string inp = input.Text, outp = output.Text;
        var preset = (JsonObject?)sources[selected]?.DeepClone();
        string? name = preset?["name"]?.GetValue<string>();
        bool modified = preset != null && !JsonNode.DeepEquals(preset["values"], cfg.Values);
        TrySave();
        busy = true;
        closeAfter = false;
        control = new BatchControl();
        foreach (var c in frozen)
            c.Enabled = false;
        pause.Enabled = stop.Enabled = true;
        pause.Text = L.Render("Szünet");
        progress.MarqueeAnimationSpeed = 30;
        status.Text = L.Render("Feldolgozás…");
        try
        {
            var result = await Task.Run(() => BatchEngine.Run(cfg, inp, outp, name, modified, control, text => { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => Append(text))); }));
            status.Text = L.Render(result.Stopped ? "Megállítva. Az elkészült fájlok megmaradtak." : result.Failed > 0 ? $"Befejezve: {result.Successful} sikeres, {result.Failed} hibás." : $"Elkészült: {result.Successful} fájl, {result.Seconds:F2} másodperc.");
            if (!result.Stopped && result.Failed == 0 && result.Successful > 0)
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        }
        catch (Exception ex) { Append("Hiba: " + ex.Message); status.Text = L.Render("Nem sikerült feldolgozni. A részletek a naplóban."); }
        finally { busy = false; foreach (var c in frozen) c.Enabled = true; pause.Enabled = stop.Enabled = false; progress.MarqueeAnimationSpeed = 0; RestoreToggles(); if (closeAfter) Close(); }
    }
    private void StopBatch()
    {
        if (!busy)
            return;
        control.Stop();
        pause.Enabled = stop.Enabled = false;
        status.Text = L.Render("Megállítás kérése: az aktuális fájl még elkészül.");
    }
    private void Append(string text)
    {
        if (text == "CONTROL:PAUSED")
        {
            status.Text = L.Render("Szünetel. Folytatás vagy Megállítás választható.");
            progress.MarqueeAnimationSpeed = 0;
            return;
        }
        if (text == "CONTROL:RESUMED")
        {
            progress.MarqueeAnimationSpeed = 30;
            return;
        }
        if (text.StartsWith("Mérések: ") || text.StartsWith("Gate/Expander: "))
        {
            try
            {
                bool standalone = text.StartsWith("Gate/Expander: ");
                var metric = JsonNode.Parse(text[(standalone ? 15 : 9)..]);
                var info = standalone ? metric : metric?["silenceControl"];
                if (info?["enabled"]?.GetValue<bool>() == true)
                {
                    if (info["bypassed"]?.GetValue<bool>() == true)
                        journal.AppendText(L.Render("Expander/Gate kihagyva: nem megbízható zaj–beszéd elkülönítés, vagy néma bemenet." + Environment.NewLine));
                    else
                    {
                        string kind = info["gateEnabled"]?.GetValue<bool>() == true ? "Gate" : "Expander";
                        double? threshold = info["thresholdDbFS"]?.GetValue<double>();
                        double fraction = info["exactZeroGainFraction"]?.GetValue<double>() ?? 0;
                        journal.AppendText(L.Render($"{kind} alkalmazva • Küszöb: {threshold:F1} dBFS RMS • Teljes zárás: {fraction * 100:F1}%" + Environment.NewLine));
                    }
                }
            }
            catch (System.Text.Json.JsonException) { }
            return;
        }
        journal.AppendText(L.Render(text + Environment.NewLine));
    }
}
