using System.Runtime.CompilerServices;
using System.Text.Json;
using StardewModdingAPI;

namespace GMCMConfigTextLocalizer;

internal sealed class TranslationStore
{
    private const string MissingTranslationPrefix = "(no translation:";
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly Dictionary<string, string> modDirectories;
    private readonly Dictionary<string, CachedJson> jsonCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedOverlay> overlayCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<Dictionary<string, string>, NativeTextIndex> nativeTexts = new();
    private readonly HashSet<string> warnedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMonitor monitor;
    private readonly CommunityPacks community;
    private string locale;

    public string ModDirectory { get; }

    public TranslationStore(string modDirectory, string modsDirectory, string locale, IMonitor monitor, CommunityPacks community)
    {
        ModDirectory = modDirectory;
        this.locale = locale;
        this.monitor = monitor;
        this.community = community;
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
        return FindTranslation(modId, source, locale) ?? source;
    }

    public string? FindTranslation(string modId, string source, string targetLocale)
    {
        if (modDirectories.TryGetValue(modId, out string? targetDirectory))
        {
            string directory = Path.Combine(targetDirectory, "i18n");
            Dictionary<string, string> requested = LoadLocaleFiles(directory, targetLocale, useSimplifiedChineseAlias: true);
            if (source.StartsWith(MissingTranslationPrefix, StringComparison.Ordinal) && source.EndsWith(')'))
            {
                string missingKey = source[MissingTranslationPrefix.Length..^1].Trim();
                if (requested.TryGetValue(missingKey, out string? recovered)
                    || requested.TryGetValue($"{missingKey}.name", out recovered))
                    return recovered;
            }

            Dictionary<string, string> defaults = LoadFile(Path.Combine(directory, "default.json"));
            if (requested.Count > 0
                && nativeTexts.GetValue(defaults, values => new NativeTextIndex(values)).Find(source, requested) is string matched)
                return matched;

            if (requested.Count > 0 && !targetLocale.Equals(locale, StringComparison.OrdinalIgnoreCase)
                && new NativeTextIndex(LoadLocaleFiles(directory, locale, useSimplifiedChineseAlias: true))
                    .Find(source, requested) is string fromActiveLocale)
                return fromActiveLocale;

            if (requested.Any(entry => entry.Value == source
                && (!defaults.TryGetValue(entry.Key, out string? original) || original != source)))
                return source;
        }

        return GetOverlayTranslation(modId, source, targetLocale)
            ?? GetLocaleCandidates(targetLocale).Reverse()
                .Select(candidate => community.Find(candidate, modId, source))
                .FirstOrDefault(translation => translation is not null);
    }

    private string? GetOverlayTranslation(string modId, string source, string targetLocale)
    {
        foreach (string candidate in GetLocaleCandidates(targetLocale).Reverse())
        {
            string path = OverrideFile.GetPath(ModDirectory, candidate, modId);
            Dictionary<string, string>? values = LoadOverride(path);
            if (values?.TryGetValue(source, out string? translation) == true
                && !string.IsNullOrWhiteSpace(translation))
                return translation;
        }

        return null;
    }

    private Dictionary<string, string>? LoadOverride(string path)
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

    private Dictionary<string, string> LoadLocaleFiles(string directory, string localeCode, bool useSimplifiedChineseAlias = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        IEnumerable<string> candidates = GetLocaleCandidates(localeCode);
        if (useSimplifiedChineseAlias && localeCode.Equals("zh", StringComparison.OrdinalIgnoreCase))
            candidates = candidates.Append("zh-CN");
        foreach (string localeCandidate in candidates)
        {
            string path = Path.Combine(directory, $"{localeCandidate}.json");
            foreach ((string key, string value) in LoadFile(path))
            {
                if (!string.IsNullOrWhiteSpace(value))
                    result[key] = value;
            }
        }
        return result;
    }

    private Dictionary<string, string> LoadFile(string path)
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

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The translation file root must be an object.");

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    values[property.Name] = property.Value.GetString() ?? "";
            }

            jsonCache[path] = new CachedJson(info.LastWriteTimeUtc, info.Length, values);
            return values;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
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
    private sealed record CachedOverlay(DateTime LastWriteTimeUtc, long Length, Dictionary<string, string> Values);
}
