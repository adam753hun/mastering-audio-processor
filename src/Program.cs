// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
namespace MasteringCs;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? language = Arg(args, "--language");
        if (language != null)
        {
            if (language is not ("hu" or "en"))
                return 1;
            L.Language = language;
        }
        if (args.Contains("--process"))
            return CommandLine(args);
        ApplicationConfiguration.Initialize();
        if (args.Contains("--verify"))
        {
            try
            {
                var result = Verification.Run(Arg(args, "--test-dir") ?? throw new ArgumentException("--test-dir szükséges"));
                File.WriteAllText(Arg(args, "--report") ?? throw new ArgumentException("--report szükséges"), result.ToJsonString(BatchEngine.JsonOptions));
                return 0;
            }
            catch (Exception ex) { string? report = Arg(args, "--report"); if (report != null) File.WriteAllText(report, new JsonObject { { "passed", false }, { "error", ex.ToString() } }.ToJsonString(BatchEngine.JsonOptions)); return 1; }
        }
        if (args.Contains("--self-check"))
        {
            string? image = Arg(args, "--snapshot");
            using var form = new MainForm(preview: image != null);
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            if (image != null)
            {
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(image);
            }
            string? settingsImage = Arg(args, "--settings-snapshot");
            if (settingsImage != null)
            {
                using var settingsForm = new SettingsForm(AppContext.BaseDirectory, new Config("gentle"), null, "output");
                settingsForm.Show();
                var tabs = settingsForm.Controls.Cast<Control>().OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
                tabs.SelectedIndex = tabs.TabCount - 1;
                Application.DoEvents();
                using var bitmap = new Bitmap(settingsForm.Width, settingsForm.Height);
                settingsForm.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(settingsImage);
            }
            string? report = Arg(args, "--report");
            if (report != null)
                File.WriteAllText(report, new JsonObject { { "started", true }, { "framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription }, { "build", BuildInfo.Data.DeepClone() }, { "engine", "managed-csharp" }, { "pythonProcess", false } }.ToJsonString(BatchEngine.JsonOptions));
            return 0;
        }
        Application.Run(new MainForm());
        return 0;
    }
    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    // HU: Automatikus összehasonlító tesztekhez ugyanaz a motor hívható grafikus felület nélkül, JSON-jelentéssel.
    // EN: Automated comparison tests can invoke the same engine without GUI, producing a JSON report.
    private static int CommandLine(string[] args)
    {
        try
        {
            string? settings = Arg(args, "--settings");
            string mode = Arg(args, "--profile") ?? "gentle";
            string? preset = Arg(args, "--preset");
            if (preset != null && settings != null)
                throw new ArgumentException("Use either --preset or --settings.");
            string? loadedName = null;
            Config config = preset != null ? Config.ReadPreset(preset, out loadedName) : settings == null ? new Config(mode) : Config.FromXml(settings, mode);
            string baseDir = settings == null ? AppContext.BaseDirectory : Path.GetDirectoryName(Path.GetFullPath(settings))!;
            var xml = settings == null ? null : XDocument.Load(settings).Root;
            string input = Arg(args, "--input") ?? Path.GetFullPath(xml?.Element("inputDirectory")?.Value ?? "input", baseDir), output = Arg(args, "--output") ?? Path.GetFullPath(xml?.Element("outputDirectory")?.Value ?? "output", baseDir);
            string? presetName = Arg(args, "--preset-name") ?? loadedName ?? xml?.Element("presetOutputFolder")?.Value;
            var result = BatchEngine.Run(config, input, output, presetName, false, new BatchControl(), s => Console.WriteLine(s));
            string? report = Arg(args, "--report");
            if (report != null)
                File.WriteAllText(report, JsonSerializer.Serialize(result, BatchEngine.JsonOptions));
            return result.Stopped ? 130 : result.Failed == 0 ? 0 : 2;
        }
        catch (Exception e) { string? report = Arg(args, "--report"); if (report != null) File.WriteAllText(report, JsonSerializer.Serialize(new { error = L.Render(e.Message) }, BatchEngine.JsonOptions)); Console.Error.WriteLine(L.Render(e.Message)); return 1; }
    }
}
