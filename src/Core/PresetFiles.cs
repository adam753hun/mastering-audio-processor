// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Text;
using System.Text.Json.Nodes;
namespace MasteringCs;

// HU: Adatfájl mentése explicit ütközésdöntéssel. Írási hiba nem maradhat csendben sorszámozási ciklusban.
// EN: Save data with an explicit collision decision. Write failures must not be mistaken for filename collisions.
public static class PresetFiles
{
    public static string? Save(string folder, string name, Config c, Func<string, DialogResult>? collision = null)
    {
        Config.ValidateName(name);
        c.Validate();
        Directory.CreateDirectory(folder);
        string stem = new(name.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '_' or '-' ? ch : '_').ToArray());
        stem = stem.Trim(' ', '.');
        if (stem.Length == 0)
            stem = "Preset";
        if (stem.Length > 60)
            stem = stem[..60];
        if (BatchEngine.IsReserved(stem))
            stem = "Preset_" + stem;
        var json = new JsonObject { { "format", "mastering-public-preset" }, { "schema", 1 }, { "name", name.Trim() }, { "profile", c.Mode }, { "values", c.Values.DeepClone() }, { "createdWith", BuildInfo.Data.DeepClone() } };
        string text = json.ToJsonString(BatchEngine.JsonOptions);
        for (int i = 0; i < 10000; i++)
        {
            string path = Path.Combine(folder, stem + (i == 0 ? "" : "_" + i.ToString("000")) + ".json");
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                if (i != 0 || collision == null)
                    continue;
                var decision = collision(path);
                if (decision == DialogResult.Cancel)
                    return null;
                if (decision != DialogResult.Yes)
                    continue;
                string temp = Path.Combine(folder, ".preset-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        Write(f, text);
                    File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return path;
            }
            try
            {
                using (stream)
                    Write(stream, text);
            }
            catch { try { File.Delete(path); } catch (IOException) { } throw; }
            return path;
        }
        throw new IOException("Túl sok azonos nevű preset.");
    }
    private static void Write(FileStream stream, string text)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
        writer.Write(text);
        writer.Flush();
        stream.Flush(true);
    }
}
