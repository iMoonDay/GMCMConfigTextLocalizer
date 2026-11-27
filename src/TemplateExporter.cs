using System.Text.RegularExpressions;

namespace GMCMConfigTextLocalizer;

internal static class TemplateExporter
{
    public static int Export(TranslationStore translations, string locale,
        IEnumerable<(string ModId, string Source)> sources)
    {
        if (!Regex.IsMatch(locale, @"\A[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*\z"))
            throw new ArgumentException("The locale must be a language code.", nameof(locale));

        int exported = 0;
        foreach (var group in sources.GroupBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase))
        {
            var ignored = translations.GetIgnoredSources(group.Key, locale);
            string path = OverrideFile.GetPath(translations.ModDirectory, locale, group.Key);
            Dictionary<string, string?> existing = OverrideFile.Read(path);
            var merged = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            foreach ((string source, string? value) in existing)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    merged.Add(source, value);
            }

            foreach (string source in group.Select(entry => entry.Source).Distinct(StringComparer.Ordinal))
            {
                if (!ignored.Contains(source) && source.Any(char.IsLetter) && !IsChineseText(source, locale)
                    && translations.FindTranslation(group.Key, source, locale) is null
                    && merged.TryAdd(source, ""))
                    exported++;
            }

            if (merged.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            else if (merged.Count != existing.Count
                || merged.Any(entry => !existing.TryGetValue(entry.Key, out string? value) || value != entry.Value))
                OverrideFile.WriteAtomically(path, merged);
        }

        return exported;
    }

    public static int ExportSelected(string directory, string locale,
        IEnumerable<(string ModId, string Source)> sources)
    {
        if (!Regex.IsMatch(locale, @"\A[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*\z"))
            throw new ArgumentException("The locale must be a language code.", nameof(locale));

        var files = sources.GroupBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            string path = OverrideFile.GetPath(directory, locale, group.Key);
            var values = new SortedDictionary<string, string?>(OverrideFile.Read(path), StringComparer.Ordinal);
            int before = values.Count;
            foreach (string source in group.Select(entry => entry.Source))
                values.TryAdd(source, "");
            return (Path: path, Values: values, Added: values.Count - before);
        }).ToArray();

        foreach (var file in files)
        {
            if (file.Added > 0)
                OverrideFile.WriteAtomically(file.Path, file.Values);
        }
        return files.Sum(file => file.Added);
    }

    internal static bool IsChineseText(string source, string locale)
    {
        if (!locale.Equals("zh", StringComparison.OrdinalIgnoreCase)
            && !locale.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            return false;

        int hanCharacters = Regex.Matches(source, @"[\u3400-\u4DBF\u4E00-\u9FFF]").Count;
        return hanCharacters > 0
            && (!Regex.IsMatch(source, @"[A-Za-z]{2,}(?:[ \t]+[A-Za-z]{2,}){2,}")
                || hanCharacters >= Regex.Matches(source, "[A-Za-z]{2,}").Count * 2);
    }

}
