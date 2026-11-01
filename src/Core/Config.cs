// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
namespace MasteringCs;

// HU: Önálló publikus konfiguráció; a presetek csak adatot tartalmaznak.
// EN: Standalone public configuration; presets contain data only.
public sealed class Config
{
    public static readonly string[] Modes = ["clean", "gentle", "game"];
    public static string[] Titles => [L.T("Zajszűrés"), L.T("Kíméletes"), L.T("Játék")];
    public static readonly JsonObject Factories = JsonNode.Parse(Resource("default_profiles.json"))!.AsObject();
    public static readonly FieldSpec[] Fields = JsonSerializer.Deserialize<FieldSpec[]>(Resource("fields.json"))!;
    public JsonObject Values
    {
        get;
    }
    public string Mode
    {
        get;
    }
    public Config(string mode, JsonObject? values = null)
    {
        if (!Modes.Contains(mode))
            throw new ArgumentException("Ismeretlen profil.");
        Mode = mode;
        Values = (JsonObject)Factories[mode]!.DeepClone();
        if (values != null)
            foreach (var group in values)
            {
                if (Values[group.Key] is not JsonObject target || group.Value is not JsonObject input)
                    throw new ArgumentException("Ismeretlen vagy hibás beállításcsoport: " + group.Key);
                foreach (var item in input)
                {
                    if (!target.ContainsKey(item.Key))
                        throw new ArgumentException("Ismeretlen beállítás: " + group.Key + "/" + item.Key);
                    target[item.Key] = item.Value?.DeepClone();
                }
            }
        Validate();
    }
    public Config Copy() => new(Mode, Values);
    public double N(string group, string key) => double.Parse(Values[group]![key]!.ToJsonString(), CultureInfo.InvariantCulture);
    public bool B(string group, string key) => Values[group]![key]!.GetValue<bool>();
    public string S(string group, string key) => Values[group]![key]!.GetValue<string>();
    public void Set(string group, string key, JsonNode? value) => Values[group]![key] = value;
    // HU: Minden mezőt, kikapcsolt modulnál is ellenőrzünk; nem futtatunk beágyazott parancsot vagy kódot.
    // EN: Validate every field, even in bypassed modules; never evaluate embedded commands or code.
    public void Validate()
    {
        foreach (var f in Fields)
        {
            try
            {
                if (f.Kind == "bool")
                {
                    _ = B(f.Group, f.Key);
                    continue;
                }
                if (f.Kind == "text")
                {
                    _ = S(f.Group, f.Key);
                    continue;
                }
                double n = N(f.Group, f.Key);
                if (!double.IsFinite(n) || n < f.Min || n > f.Max || (f.Kind == "int" && n != Math.Truncate(n)))
                    throw new ArgumentException();
                Set(f.Group, f.Key, JsonValue.Create(n));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new ArgumentException($"{f.Label}: {f.Min} … {f.Max}", ex); }
        }
        if (S("noiseReduction", "method") is not ("bypass" or "noisereduce_stationary"))
            throw new ArgumentException("Ismeretlen zajszűrés.");
        var suffix = S("outputNaming", "duplicateSuffix");
        if (suffix.Length > 0 && !suffix.StartsWith('_'))
            suffix = "_" + suffix;
        if (suffix.Length > 32 || suffix.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
            throw new ArgumentException("Érvénytelen fájlnév-utótag.");
        Set("outputNaming", "duplicateSuffix", JsonValue.Create(suffix));
    }
    public static string Resource(string name)
    {
        var a = Assembly.GetExecutingAssembly();
        var key = a.GetManifestResourceNames().Single(x => x.EndsWith("." + name, StringComparison.Ordinal));
        using var reader = new StreamReader(a.GetManifestResourceStream(key)!);
        return reader.ReadToEnd();
    }
    // HU: A relatív XML-útvonalak az XML mappájához tartoznak; magát a fájlt sosem írjuk át feldolgozáskor.
    // EN: XML-relative paths belong to the XML directory; processing never rewrites the source configuration.
    public static Config FromXml(string path, string mode)
    {
        using var reader = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 });
        var xml = XDocument.Load(reader).Root ?? throw new ArgumentException("Üres XML.");
        if (xml.Name != "publicMasteringSettings")
            throw new ArgumentException("publicMasteringSettings XML szükséges.");
        if (xml.Elements().GroupBy(x => x.Name).Any(g => g.Count() > 1))
            throw new ArgumentException("Duplikált XML-beállítás.");
        var c = new Config(mode);
        foreach (var f in Fields)
        {
            XElement? parent = xml.Element(f.Group);
            var nodes = parent?.Elements(f.Key).ToArray() ?? [];
            if (nodes.Length > 1)
                throw new ArgumentException("Duplikált XML-mező: " + f.Key);
            if (nodes.Length == 0)
                continue;
            string text = nodes[0].Value.Trim();
            JsonNode node = f.Kind == "bool" ? JsonValue.Create(text switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ArgumentException("true/false szükséges: " + f.Key)
            })! : f.Kind == "text" ? JsonValue.Create(text)! : JsonValue.Create(double.Parse(text, CultureInfo.InvariantCulture))!;
            c.Set(f.Group, f.Key, node);
        }
        c.Validate();
        return c;
    }
    public static Config ReadPreset(string path, out string name)
    {
        if (new FileInfo(path).Length > 131072)
            throw new ArgumentException("Túl nagy preset.");
        var j = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (j["format"]?.GetValue<string>() != "mastering-public-preset" || j["schema"]?.GetValue<int>() != 1)
            throw new ArgumentException("Nem támogatott preset.");
        name = j["name"]?.GetValue<string>() ?? "";
        ValidateName(name);
        var values = j["values"]!.AsObject();
        foreach (string required in new[] { "finishing", "silenceControl", "noiseReduction", "outputNaming" })
            if (values[required] is not JsonObject)
                throw new ArgumentException("Hiányos preset: " + required);
        var result = new Config(j["profile"]!.GetValue<string>(), values);
        result.Set("finishing", "enabled", JsonValue.Create(result.Mode != "clean"));
        return result;
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new ArgumentException("A preset neve 1–80 látható karakter legyen.");
    }
}
public record FieldSpec(string Group, string Key, string Label, string Tab, string Kind, double Min, double Max, bool Hidden);
