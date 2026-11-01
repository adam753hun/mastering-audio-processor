// SPDX-License-Identifier: GPL-3.0-only
// Copyright (c) 2026 Ádám Sarkament
using System.Text.Json;
using System.Text.RegularExpressions;
namespace MasteringCs;

// HU: Csak felületi szöveget fordítunk. A presetkulcsok, mappanevek és a DSP számai nyelvfüggetlenek.
// EN: Translate presentation text only. Preset keys, folder names and DSP numbers stay language independent.
public static class L
{
    public sealed record TextPair(string hu, string en);
    private static readonly Dictionary<string, TextPair> Texts = Load();
    private static string language = "hu";
    public static string Language
    {
        get => language;
        set => language = value is "hu" or "en" ? value : throw new ArgumentException("Unsupported interface language.");
    }
    public static IReadOnlyDictionary<string, TextPair> Catalog => Texts;
    private static Dictionary<string, TextPair> Load()
    {
        var assembly = typeof(L).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".ui-text.json", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return JsonSerializer.Deserialize<Dictionary<string, TextPair>>(reader.ReadToEnd())!;
    }
    public static string T(string key) => Texts.TryGetValue(key, out var pair) ? Select(pair) : key;
    private static string Select(TextPair pair) => Language == "en" ? pair.en : pair.hu;
    private static readonly Regex Slots = new(@"\{[^{}]+\}", RegexOptions.Compiled);

    // HU: A formázott státuszüzenetekben csak a sablont fordítjuk, a behelyettesített fájlneveket nem.
    // EN: Translate formatted status templates, never substituted user filenames or preset names.
    public static string Render(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
            return Render(text[..^2]) + "\r\n";
        if (text.EndsWith('\n'))
            return Render(text[..^1]) + "\n";
        if (Texts.TryGetValue(text, out var direct))
            return Select(direct);
        foreach (var entry in Texts)
        {
            if (text == entry.Value.hu || text == entry.Value.en)
                return Select(entry.Value);
            foreach (string source in new[] { entry.Key, entry.Value.hu, entry.Value.en }.Distinct())
            {
                var slots = Slots.Matches(source);
                if (slots.Count == 0)
                    continue;
                string pattern = @"\A";
                int start = 0;
                foreach (Match slot in slots)
                {
                    pattern += Regex.Escape(source[start..slot.Index]) + "(.*?)";
                    start = slot.Index + slot.Length;
                }
                pattern += Regex.Escape(source[start..]) + @"\z";
                var match = Regex.Match(text, pattern, RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
                if (!match.Success)
                    continue;
                int index = 0;
                return Slots.Replace(Select(entry.Value), _ => match.Groups[++index].Value);
            }
        }
        // HU: Technikai hibaüzenetben az ismert előtag fordul; a rendszer részleteit megőrizzük.
        // EN: Translate known error prefixes while preserving operating-system diagnostic details.
        foreach (var entry in Texts)
            foreach (string source in new[] { entry.Key, entry.Value.hu, entry.Value.en }.Distinct())
            {
                if (Slots.IsMatch(source))
                    continue;
                string prefix = source.EndsWith(": ", StringComparison.Ordinal) ? source : source + ": ";
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                    return (source.EndsWith(": ", StringComparison.Ordinal) ? Select(entry.Value) : Select(entry.Value) + ": ") + Render(text[prefix.Length..]);
            }
        if (text.Contains('\n'))
            return string.Join('\n', text.Split('\n').Select(Render));
        return text;
    }
    public static void TranslateTree(Control root)
    {
        // HU: Az adatszerkesztőket kihagyjuk: a felhasználó szövege nem fordítandó felirat.
        // EN: Skip data editors: user text is not a translatable interface label.
        if (root is not (TextBoxBase or ComboBox or NumericUpDown))
            root.Text = Render(root.Text);
        foreach (Control child in root.Controls)
            TranslateTree(child);
    }
    public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        => MessageBox.Show(owner, Render(text), Render(caption), buttons, icon);
}
