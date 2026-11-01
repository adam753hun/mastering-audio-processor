// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MasteringCs;

// HU: Az összes fül egy munkapéldányt szerkeszt. Csak a Mentés alkalmazza, Mégse elveti; presetírás külön művelet.
// EN: All tabs edit one working copy. Save applies it, Cancel discards it; writing a preset is a separate action.
public sealed class SettingsForm : Form
{
    private readonly string root; private readonly Config initial; private readonly Dictionary<string, Control> editors = [];
    private readonly Label note = new() { AutoSize = true, MaximumSize = new Size(840, 0) };
    private readonly Label preview = new() { AutoSize = true, MaximumSize = new Size(840, 0), ForeColor = Color.FromArgb(30, 70, 110) };
    private readonly string outputFolder;
    private readonly ComboBox presets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly List<(string Name, string Path)> presetFiles = [];
    private readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    public string UiLanguage { get; private set; } = L.Language;
    public Config Result
    {
        get; private set;
    }
    public JsonObject? PresetSource
    {
        get; private set;
    }
    private bool loading;
    public SettingsForm(string root, Config config, JsonObject? presetSource, string? outputFolder = null)
    {
        this.root = root;
        this.outputFolder = outputFolder ?? Path.Combine(root, "output");
        initial = config.Copy();
        Result = config.Copy();
        PresetSource = (JsonObject?)presetSource?.DeepClone();
        Text = L.T("Beállítások") + " — " + Config.Titles[Array.IndexOf(Config.Modes, config.Mode)];
        Size = new Size(960, 820);
        MinimumSize = new Size(850, 720);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(242, 245, 249);
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(outer);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        outer.Controls.Add(tabs, 0, 0);
        foreach (string title in new[] { "Hangerő", "Külön kompresszor", "Expander / Gate", "Zajszűrés", "Kimenet" })
        {
            var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(12), BackColor = Color.White };
            tabs.TabPages.Add(page);
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            page.Controls.Add(grid);
            foreach (var field in Config.Fields.Where(f => f.Tab == title && !f.Hidden))
            {
                int row = grid.RowCount++;
                var label = new Label { Text = L.T(field.Label), AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(4, 10, 8, 6) };
                grid.Controls.Add(label, 0, row);
                Control editor;
                if (field.Kind == "bool")
                    editor = new CheckBox { AutoSize = true, Margin = new Padding(4, 10, 4, 4) };
                else if (field.Kind == "text" && field.Key == "method")
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
                    combo.Items.AddRange([L.T("noisereduce — kezdő zajminta"), L.T("Bypass — zajszűrés nélkül")]);
                    editor = combo;
                }
                else if (field.Kind == "text")
                    editor = new TextBox { Width = 250 };
                else
                    editor = new NumericUpDown { Width = 180, DecimalPlaces = field.Kind == "int" ? 0 : 3, Minimum = (decimal)field.Min, Maximum = (decimal)field.Max, Increment = field.Kind == "int" ? 1 : .1m };
                editor.Margin = new Padding(4, 8, 4, 5);
                grid.Controls.Add(editor, 1, row);
                editors[field.Group + "/" + field.Key] = editor;
                if (editor is CheckBox cb)
                    cb.CheckedChanged += (_, _) => UpdateStates();
                if (editor is ComboBox co)
                    co.SelectedIndexChanged += (_, _) => UpdateStates();
                if (editor is NumericUpDown number)
                    number.ValueChanged += (_, _) => UpdatePreview();
                if (editor is TextBox text)
                    text.TextChanged += (_, _) => UpdatePreview();
            }
            string help = title switch
            {
                "Zajszűrés" => "A stationary zajszűrő a felvétel elejéből vesz zajmintát. Ebben a szakaszban csak háttérzaj legyen, beszéd ne. A Bypass a zajszűrést kapcsolja ki. Nincs adaptív mód vagy automatikus vágás.",
                "Expander / Gate" => "Automatikus küszöbnél a kézi érték nem érvényes. A Gate Hold + Release idő után zárhat teljes nullára; az arány, tartomány és knee csak az Expanderé. Bizonytalan automatikus érzékelésnél a halkítás kimaradhat.",
                "Hangerő" => "Gyári cél: −23 LUFS. A csúcshatár elsőbbséget élvez; ha nem érhető el a cél, kisebb erősítés készül. Statikus csúcsvédelem, nem dinamikus limiter.",
                "Kimenet" => "Egy kimenet készül fájlonként, a kimeneti mappába vagy annak presetnév almappájába. Szóköz helyett aláhúzás, módosított presetnél _modositott. Meglévő WAV-ot nem ír felül.",
                _ => "A változtatás csak a Mentés gombbal válik aktívvá."
            };
            var explanation = new Label { Text = help, AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(6, 22, 6, 12) };
            grid.Controls.Add(explanation, 0, grid.RowCount++);
            grid.SetColumnSpan(explanation, 2);
        }
        // HU: A nyelv külön felületi beállítás. Mentésig nincs globális változás, a Mégse elveti.
        // EN: Language is a separate UI preference. No global change until Save; Cancel discards it.
        var interfacePage = new TabPage(L.T("Felület")) { Padding = new Padding(20), BackColor = Color.White };
        var interfaceLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        interfaceLayout.Controls.Add(new Label { Text = L.T("Nyelv / Language"), AutoSize = true, Margin = new Padding(4, 8, 4, 12) });
        language.Items.AddRange(["Magyar", "English"]);
        language.SelectedIndex = L.Language == "en" ? 1 : 0;
        interfaceLayout.Controls.Add(language);
        interfaceLayout.Controls.Add(new Label { Text = L.T("A nyelv a Mentés után változik meg. A presetek hangbeállításait nem módosítja."), AutoSize = true, MaximumSize = new Size(800, 0), Margin = new Padding(4, 22, 4, 8) });
        interfacePage.Controls.Add(interfaceLayout);
        tabs.TabPages.Add(interfacePage);
        var choose = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        outer.Controls.Add(choose, 0, 1);
        var reset = new Button { Text = "Profil alapértékei", AutoSize = true };
        reset.Click += (_, _) => { PresetSource = null; Fill(new Config(initial.Mode)); note.Text = L.Render("Gyári értékek betöltve. A Mentés alkalmazza."); };
        choose.Controls.Add(reset);
        choose.Controls.Add(new Label { Text = "Preset:", AutoSize = true, Margin = new Padding(14, 10, 5, 5) });
        choose.Controls.Add(presets);
        presets.DropDown += (_, _) => RefreshPresets();
        presets.SelectionChangeCommitted += (_, _) => { if (presets.SelectedIndex >= 0) LoadPreset(presetFiles[presets.SelectedIndex].Path); };
        var savePreset = new Button { Text = "Preset mentése…", AutoSize = true };
        savePreset.Click += (_, _) => SavePreset();
        choose.Controls.Add(savePreset);
        var open = new Button { Text = "Betöltés…", AutoSize = true };
        open.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Mastering preset|*.json", InitialDirectory = Path.Combine(root, "Presets") }; if (d.ShowDialog(this) == DialogResult.OK) LoadPreset(d.FileName); };
        choose.Controls.Add(open);
        outer.Controls.Add(preview, 0, 2);
        outer.Controls.Add(note, 0, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        outer.Controls.Add(buttons, 0, 4);
        var cancel = new Button { Text = "Mégse", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Mentés", AutoSize = true };
        save.Click += (_, _) => { try { Result = Collect(); UiLanguage = language.SelectedIndex == 1 ? "en" : "hu"; DialogResult = DialogResult.OK; Close(); } catch (Exception e) { Error(e); } };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        CancelButton = cancel;
        Fill(config);
        RefreshPresets();
        note.Text = L.Render("Mentés után a következő feldolgozás használja az értékeket.");
        L.TranslateTree(this);
    }
    private void Fill(Config c)
    {
        loading = true;
        foreach (var field in Config.Fields.Where(f => !f.Hidden))
        {
            var e = editors[field.Group + "/" + field.Key];
            if (e is CheckBox cb)
                cb.Checked = c.B(field.Group, field.Key);
            else if (e is ComboBox co)
                co.SelectedIndex = c.S(field.Group, field.Key) == "noisereduce_stationary" ? 0 : 1;
            else if (e is NumericUpDown n)
                n.Value = (decimal)c.N(field.Group, field.Key);
            else
                e.Text = c.S(field.Group, field.Key);
        }
        loading = false;
        UpdateStates();
    }
    private Config Collect()
    {
        var c = initial.Copy();
        foreach (var f in Config.Fields.Where(f => !f.Hidden))
        {
            var e = editors[f.Group + "/" + f.Key];
            JsonNode v = e switch
            {
                CheckBox cb => JsonValue.Create(cb.Checked)!,
                ComboBox co => JsonValue.Create(co.SelectedIndex == 0 ? "noisereduce_stationary" : "bypass")!,
                NumericUpDown n => JsonValue.Create((double)n.Value)!,
                _ => JsonValue.Create(e.Text)!
            };
            c.Set(f.Group, f.Key, v);
        }
        c.Set("finishing", "enabled", JsonValue.Create(c.Mode != "clean"));
        c.Validate();
        return c;
    }
    private void UpdateStates()
    {
        if (loading || editors.Count == 0)
            return;
        bool Check(string g, string k) => ((CheckBox)editors[g + "/" + k]).Checked;
        bool finish = initial.Mode != "clean", auto = Check("silenceControl", "autoThreshold"), gate = Check("silenceControl", "gateEnabled"), silence = Check("silenceControl", "enabled"), alternate = ((ComboBox)editors["noiseReduction/method"]).SelectedIndex == 0;
        foreach (var f in Config.Fields.Where(f => !f.Hidden))
        {
            bool enabled = true;
            if (f.Group == "finishing")
            {
                enabled = finish;
                if (f.Key == "targetLUFS")
                    enabled &= Check("finishing", "loudnessEnabled");
                if (f.Key.StartsWith("compression"))
                    enabled &= Check("finishing", "extraCompressionEnabled");
            }
            else if (f.Group == "noiseReduction" && f.Key != "method")
                enabled = alternate;
            else if (f.Group == "silenceControl" && f.Key is not ("enabled" or "gateEnabled" or "autoThreshold"))
            {
                enabled = silence;
                if (f.Key == "thresholdDbFS")
                    enabled &= !auto;
                else if (f.Key is "noiseMarginDb" or "minimumContrastDb")
                    enabled &= auto;
                else if (f.Key is "ratio" or "rangeDb" or "kneeDb")
                    enabled &= !gate;
            }
            editors[f.Group + "/" + f.Key].Enabled = enabled;
        }
        UpdatePreview();
    }
    // HU: A látható név változatlan marad; a célmappa a módosított állapotot és a biztonságos fájlnevet mutatja.
    // EN: Keep the display name intact; preview the safe output folder and dirty-state suffix.
    private void UpdatePreview()
    {
        if (loading)
            return;
        try
        {
            var current = Collect();
            string name = PresetSource?["name"]?.GetValue<string>() ?? "";
            bool modified = PresetSource != null && !JsonNode.DeepEquals(PresetSource["values"], current.Values);
            string folder = outputFolder;
            if (name.Length > 0)
                folder = Path.Combine(folder, BatchEngine.SafeFolder(name, modified));
            preview.Text = L.Render("Kimenet: " + folder);
        }
        catch (ArgumentException) { preview.Text = L.Render("A mentés előtt ellenőrizd a mezők értékeit."); }
    }
    private void RefreshPresets()
    {
        string directory = Path.Combine(root, "Presets");
        Directory.CreateDirectory(directory);
        presetFiles.Clear();
        presets.Items.Clear();
        foreach (string p in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var c = Config.ReadPreset(p, out var name);
                if (c.Mode != initial.Mode)
                    continue;
                presetFiles.Add((name, p));
                presets.Items.Add(name + " [" + Path.GetFileName(p) + "]");
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
    }
    private void LoadPreset(string path)
    {
        try
        {
            var c = Config.ReadPreset(path, out var name);
            if (c.Mode != initial.Mode)
                throw new ArgumentException("A preset másik módhoz tartozik. Válaszd azt a főablakban.");
            Fill(c);
            PresetSource = new JsonObject { { "name", name }, { "values", c.Values.DeepClone() } };
            UpdatePreview();
            note.Text = L.Render("Betöltve: " + name + ". A Mentés alkalmazza, a Mégse elveti.");
        }
        catch (Exception e) { Error(e); }
    }
    // HU: Felülírás csak kifejezett választásra, teljes ideiglenes fájlból cserével; Megtart sorszámoz.
    // EN: Overwrite only on explicit choice with a complete temporary file replacement; Keep numbers collisions.
    private void SavePreset()
    {
        try
        {
            var c = Collect();
            string? name = TextPrompt.Ask(this, "Preset neve", "Az összes fül beállításai együtt mentődnek.");
            if (name == null)
                return;
            name = name.Trim();
            Config.ValidateName(name);
            string? path = PresetFiles.Save(Path.Combine(root, "Presets"), name, c, existing => ConflictForm.Ask(this, Path.GetFileName(existing)));
            if (path == null)
                return;
            PresetSource = new JsonObject { { "name", name }, { "values", c.Values.DeepClone() } };
            UpdatePreview();
            RefreshPresets();
            note.Text = L.Render("Preset elmentve: " + Path.GetFileName(path) + ". A Mentés alkalmazza a profilra.");
        }
        catch (Exception e) { Error(e); }
    }
    private void Error(Exception e) => L.Show(this, e.Message, "Ellenőrizd a beállításokat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
public static class TextPrompt
{
    public static string? Ask(IWin32Window owner, string title, string message)
    {
        using var f = new Form { Text = title, Size = new Size(520, 210), StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 10), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var l = new Label { Text = message, Left = 16, Top = 15, Width = 470 };
        var t = new TextBox { Left = 16, Top = 50, Width = 470, MaxLength = 80 };
        var ok = new Button { Text = "Mentés", Left = 300, Top = 100, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Mégse", Left = 395, Top = 100, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange([l, t, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        L.TranslateTree(f);
        return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
    }
}
public static class ConflictForm
{
    public static DialogResult Ask(IWin32Window owner, string name)
    {
        using var f = new Form { Text = "Már létezik ilyen preset", Size = new Size(560, 230), StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 10), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        f.Controls.Add(new Label { Text = "Ezen a néven már mentettél: " + name + "\nFelülír: lecseréli. Megtart: új, sorszámozott fájlt készít.", Left = 16, Top = 18, Width = 510, Height = 70 });
        Button B(string text, int left, DialogResult result)
        {
            var b = new Button { Text = text, Left = left, Top = 115, Width = 145, DialogResult = result };
            f.Controls.Add(b);
            return b;
        }
        B("Felülír", 16, DialogResult.Yes);
        var keep = B("Megtart", 190, DialogResult.No);
        var cancel = B("Mégse", 365, DialogResult.Cancel);
        f.AcceptButton = keep;
        f.CancelButton = cancel;
        L.TranslateTree(f);
        return f.ShowDialog(owner);
    }
}
