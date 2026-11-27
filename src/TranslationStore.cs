using System.Runtime.CompilerServices;
using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Toolkit.Serialization;

namespace GMCMConfigTextLocalizer;

internal enum TranslationStatus { Pending, Translated, Native, Localized, Unnecessary }

internal sealed class TranslationStore
{
    private static readonly JsonHelper TranslationJson = new();
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly Dictionary<string, string> modDirectories;
    private readonly Dictionary<string, CachedJson> jsonCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedOverlay> overlayCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> ignoredByLocale = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> NoIgnoredSources = new HashSet<string>();
    private readonly ConditionalWeakTable<Dictionary<string, string>, NativeTextIndex> nativeTexts = new();
    private readonly HashSet<string> warnedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMonitor monitor;
    private readonly Func<string, bool> isEnabled;
    private string locale;

    public string ModDirectory { get; }
    public string CurrentLocale => locale;

    public TranslationStore(string modDirectory, string modsDirectory, string locale, IMonitor monitor, Func<string, bool> isEnabled)
    {
        ModDirectory = modDirectory;
        this.locale = locale;
        this.monitor = monitor;
        this.isEnabled = isEnabled;
        modDirectories = FindModDirectories(modsDirectory);
    }

    public void SetLocale(string newLocale)
    {
        locale = newLocale;
    }

    public void ClearCache()
    {
        jsonCache.Clear();
        overlayCache.Clear();
        nativeTexts.Clear();
    }

    public string Resolve(string modId, string source)
    {
        return isEnabled(modId) && !GetIgnoredSources(modId).Contains(source) ? ResolveForEditing(modId, source) : source;
    }

    public string ResolveForEditing(string modId, string source) => GetLocalTranslation(modId, source) ?? source;

    public string? GetLocalTranslation(string modId, string source) => GetOverlayTranslation(modId, source, locale);

    public static TranslationStatus GetStatus(string source, string? translation, NativeTextIndex? localized, string targetLocale, bool ignored)
    {
        if (ignored)
            return TranslationStatus.Unnecessary;
        if (translation is not null)
            return TranslationStatus.Translated;
        if (localized?.Contains(source) == true)
            return TranslationStatus.Native;
        if (TemplateExporter.IsChineseText(source, targetLocale))
            return TranslationStatus.Localized;
        return source.Any(char.IsLetter) ? TranslationStatus.Pending : TranslationStatus.Unnecessary;
    }

    public NativeTextIndex? ReadLocalizedTextIndex(string modId)
    {
        if (!modDirectories.TryGetValue(modId, out string? directory))
            return null;

        string i18n = Path.Combine(directory, "i18n");
        Dictionary<string, string> original = LoadFile(Path.Combine(i18n, "default.json"));
        var localized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in GetNativeLocaleCandidates(locale))
        {
            foreach ((string key, string value) in LoadFile(Path.Combine(i18n, $"{candidate}.json")))
            {
                if (!string.IsNullOrWhiteSpace(value))
                    localized[key] = value;
            }
        }

        var translated = localized.Where(entry => !original.TryGetValue(entry.Key, out string? value) || entry.Value != value)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return translated.Count > 0 ? new NativeTextIndex(translated, includeMissingMarkers: false) : null;
    }

    public Dictionary<string, Dictionary<string, string?>> ReadOverrideCatalog()
    {
        var catalog = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in GetLocaleCandidates(locale))
        {
            string directory = Path.Combine(ModDirectory, "overrides", candidate);
            if (!Directory.Exists(directory))
                continue;

            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                string modId = Path.GetFileNameWithoutExtension(path);
                if (!catalog.TryGetValue(modId, out Dictionary<string, string?>? entries))
                    catalog.Add(modId, entries = new(StringComparer.Ordinal));

                if (LoadOverride(path) is not { } values)
                    continue;
                foreach ((string source, string? translation) in values)
                {
                    if (!string.IsNullOrWhiteSpace(translation))
                        entries[source] = translation;
                    else
                        entries.TryAdd(source, null);
                }
            }
        }
        return catalog;
    }

    public string GetExportLocale()
        => GetTranslationLocale(locale);

    private static string GetTranslationLocale(string locale)
    {
        string current = GetLocaleCandidates(locale).Last();
        return current.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh" : current;
    }

    public IReadOnlySet<string> GetIgnoredSources(string modId, string? targetLocale = null)
    {
        var ignored = ReadIgnoredTranslations(GetTranslationLocale(targetLocale ?? locale));
        return ignored.TryGetValue(modId, out var sources) ? sources : NoIgnoredSources;
    }

    public IEnumerable<string> ReadIgnoredModIds() => ReadIgnoredTranslations(GetExportLocale()).Keys;

    private Dictionary<string, HashSet<string>> ReadIgnoredTranslations(string targetLocale)
    {
        if (ignoredByLocale.TryGetValue(targetLocale, out var ignored))
            return ignored;

        string path = GetIgnoredPath(targetLocale);
        ignored = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            try
            {
                ignored = LoadIgnoredFile(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                WarnInvalidFile(path, error);
            }
        }
        ignoredByLocale.Add(targetLocale, ignored);
        return ignored;
    }

    public void SaveIgnoredSources(string modId, string targetLocale, IReadOnlyDictionary<string, bool> changes)
    {
        if (!string.Equals(targetLocale, GetExportLocale(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game language changed while the translation editor was open.");

        string path = GetIgnoredPath(targetLocale);
        var ignored = File.Exists(path) ? LoadIgnoredFile(path) : new(StringComparer.OrdinalIgnoreCase);
        if (!ignored.TryGetValue(modId, out var sources))
            sources = new(StringComparer.Ordinal);

        foreach ((string source, bool marked) in changes)
        {
            if (marked)
                sources.Add(source);
            else
                sources.Remove(source);
        }
        if (sources.Count == 0)
            ignored.Remove(modId);
        else
            ignored[modId] = sources;

        OverrideFile.WriteAtomically(path, ignored);
        ignoredByLocale[targetLocale] = ignored;
    }

    private static Dictionary<string, HashSet<string>> LoadIgnoredFile(string path)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(path));
        if (values is null || values.Values.Any(sources => sources is null))
            throw new JsonException("Expected mod IDs and sets of ignored source texts.");
        return new(values, StringComparer.OrdinalIgnoreCase);
    }

    private string GetIgnoredPath(string targetLocale) => OverrideFile.GetPath(ModDirectory, ".ignored", targetLocale);

    public string GetOverrideLocale(string modId)
    {
        string current = GetLocaleCandidates(locale).Last();
        // Existing region-specific overrides take precedence over the export locale.
        return File.Exists(OverrideFile.GetPath(ModDirectory, current, modId)) ? current : GetExportLocale();
    }

    public string ResolveWithoutLocalOverride(string modId, string source, string overrideLocale)
    {
        return GetOverlayTranslation(modId, source, locale, overrideLocale) ?? source;
    }

    public void SaveOverrides(string modId, string overrideLocale, IReadOnlyDictionary<string, string> changes)
    {
        if (!string.Equals(overrideLocale, GetOverrideLocale(modId), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The override locale changed while the translation editor was open.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(overrideLocale, @"\A[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*\z"))
            throw new ArgumentException("The locale must be a language code.", nameof(overrideLocale));

        string path = OverrideFile.GetPath(ModDirectory, overrideLocale, modId);
        var merged = new SortedDictionary<string, string?>(OverrideFile.Read(path), StringComparer.Ordinal);
        foreach ((string source, string translation) in changes)
        {
            if (string.IsNullOrWhiteSpace(translation))
                merged.Remove(source);
            else
                merged[source] = translation;
        }
        OverrideFile.WriteAtomically(path, merged);
        overlayCache.Remove(path);
    }

    public int MergeCommunityTranslations(CommunityPacks.TranslationFile file)
    {
        string path = OverrideFile.GetPath(ModDirectory, file.Locale, file.ModId);
        bool exists = File.Exists(path);
        Dictionary<string, string?> values = OverrideFile.Read(path);
        int previousCount = values.Count;
        foreach ((string source, string translation) in file.Texts)
            values.TryAdd(source, translation);

        int added = values.Count - previousCount;
        if (added > 0)
        {
            OverrideFile.WriteAtomically(path, values, overwrite: exists);
            overlayCache.Remove(path);
        }
        return added;
    }

    public IReadOnlyList<NativeTextIndex> ReadNativeTextIndices(string modId, bool strict = false)
    {
        if (!modDirectories.TryGetValue(modId, out string? directory))
            return [];

        string i18n = Path.Combine(directory, "i18n");
        return GetNativeLocaleCandidates(locale).Prepend("default").Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(candidate => LoadFile(Path.Combine(i18n, $"{candidate}.json"), strict))
            .Where(values => values.Count > 0)
            .Select(values => nativeTexts.GetValue(values, texts => new NativeTextIndex(texts)))
            .ToArray();
    }

    public string? FindTranslation(string modId, string source, string targetLocale)
    {
        return GetOverlayTranslation(modId, source, targetLocale)
            ?? (ReadNativeTextIndices(modId).Any(index => index.Contains(source)) ? source : null);
    }

    private string? GetOverlayTranslation(string modId, string source, string targetLocale, string? skipLocalLocale = null)
    {
        foreach (string candidate in GetLocaleCandidates(targetLocale).Reverse())
        {
            if (string.Equals(candidate, skipLocalLocale, StringComparison.OrdinalIgnoreCase))
                continue;

            string path = OverrideFile.GetPath(ModDirectory, candidate, modId);
            Dictionary<string, string?>? values = LoadOverride(path);
            if (values?.TryGetValue(source, out string? translation) == true
                && !string.IsNullOrWhiteSpace(translation))
                return translation;
        }

        return null;
    }

    private Dictionary<string, string?>? LoadOverride(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;

        try
        {
            if (!overlayCache.TryGetValue(path, out CachedOverlay? cached)
                || cached.LastWriteTimeUtc != info.LastWriteTimeUtc
                || cached.Length != info.Length)
            {
                cached = new CachedOverlay(info.LastWriteTimeUtc, info.Length, OverrideFile.Read(path));
                overlayCache[path] = cached;
            }
            return cached.Values;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            WarnInvalidFile(path, ex);
            return null;
        }
    }

    private static IEnumerable<string> GetNativeLocaleCandidates(string localeCode)
    {
        IEnumerable<string> candidates = GetLocaleCandidates(localeCode);
        return localeCode.Equals("zh", StringComparison.OrdinalIgnoreCase) ? candidates.Append("zh-CN") : candidates;
    }

    private Dictionary<string, string> LoadFile(string path, bool strict = false)
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var info = new FileInfo(path);
            if (jsonCache.TryGetValue(path, out CachedJson? cached)
                && cached.LastWriteTimeUtc == info.LastWriteTimeUtc
                && cached.Length == info.Length)
                return cached.Values;

            if (!TranslationJson.ReadJsonFileIfExists(path, out Dictionary<string, string>? values))
                throw new InvalidDataException($"Translation file is empty or unavailable: {path}");
            foreach (string key in values.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
                values.Remove(key);

            jsonCache[path] = new CachedJson(info.LastWriteTimeUtc, info.Length, values);
            return values;
        }
        catch (Exception ex) when (!strict
            && ex is (IOException or InvalidDataException or UnauthorizedAccessException or Newtonsoft.Json.JsonException))
        {
            WarnInvalidFile(path, ex);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void WarnInvalidFile(string path, Exception exception)
    {
        if (warnedPaths.Add(path))
            monitor.Log(I18n.Get("log.translation-file.invalid", new { path }) + Environment.NewLine + exception.Message,
                LogLevel.Warn);
    }

    private Dictionary<string, string> FindModDirectories(string modsDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifestPath in Directory.EnumerateFiles(modsDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath), JsonOptions);
                if (!document.RootElement.TryGetProperty("UniqueID", out JsonElement idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                    continue;

                string? modId = idElement.GetString();
                string? directory = Path.GetDirectoryName(manifestPath);
                if (!string.IsNullOrWhiteSpace(modId) && directory is not null)
                    result.TryAdd(modId, directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (warnedPaths.Add(manifestPath))
                    monitor.Log(I18n.Get("log.mod-manifest.invalid", new { path = manifestPath })
                        + Environment.NewLine + ex.Message, LogLevel.Warn);
            }
        }

        return result;
    }

    private static IEnumerable<string> GetLocaleCandidates(string localeCode)
    {
        if (string.IsNullOrWhiteSpace(localeCode))
        {
            yield return "default";
            yield break;
        }

        string normalized = localeCode.Replace('_', '-');
        string[] parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (int count = 1; count <= parts.Length; count++)
            yield return string.Join('-', parts.Take(count));
    }

    private sealed record CachedJson(DateTime LastWriteTimeUtc, long Length, Dictionary<string, string> Values);
    private sealed record CachedOverlay(DateTime LastWriteTimeUtc, long Length, Dictionary<string, string?> Values);
}
