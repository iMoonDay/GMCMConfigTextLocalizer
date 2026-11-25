using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GMCMConfigTextLocalizer;

internal sealed class CommunityPacks
{
    public const string Repository = "iMoonDay/GMCMConfigTextLocalizer";
    private const int ArchiveLimit = 16 * 1024 * 1024;
    private const int PackLimit = 1024 * 1024;
    private const int ExpandedLimit = 32 * 1024 * 1024;
    private static readonly Regex PackPath = new(
        @"\A[^/]+/([A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)/([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*)\.json\z");
    private static readonly Regex LegacyPackPath = new(
        @"\A[^/]+/packs/([A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)/([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*)\.json\z");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string cachePath;
    private Snapshot snapshot = new(new(StringComparer.OrdinalIgnoreCase), 0);

    public CommunityPacks(string directory)
    {
        cachePath = Path.Combine(directory, "community-packs.zip");
    }

    public int LoadCache()
    {
        if (!File.Exists(cachePath))
            return 0;
        if (new FileInfo(cachePath).Length > ArchiveLimit)
            throw new InvalidDataException("The community archive exceeds the download size limit.");

        snapshot = ReadArchive(File.ReadAllBytes(cachePath), allowLegacyLayout: true);
        return snapshot.PackCount;
    }

    public static async Task<Download> Fetch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using HttpResponseMessage response = await Client.GetAsync(
            $"https://codeload.github.com/{Repository}/zip/refs/heads/community-packs",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ArchiveLimit)
            throw new InvalidDataException("The community archive exceeds the download size limit.");

        using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var archive = new MemoryStream();
        var buffer = new byte[81920];
        int length;
        while ((length = await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (archive.Length + length > ArchiveLimit)
                throw new InvalidDataException("The community archive exceeds the download size limit.");
            archive.Write(buffer, 0, length);
        }

        byte[] bytes = archive.ToArray();
        return new Download(bytes, ReadArchive(bytes));
    }

    public void Apply(Download result)
    {
        string temporary = cachePath + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, result.Archive);
            File.Move(temporary, cachePath, overwrite: true);
            snapshot = result.Packs;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public string? Find(string locale, string modId, string source)
    {
        if (snapshot.Locales.TryGetValue(locale, out var mods)
            && mods.TryGetValue(modId, out var texts)
            && texts.TryGetValue(source, out string? translated))
            return translated;
        return null;
    }

    private static Snapshot ReadArchive(byte[] bytes, bool allowLegacyLayout = false)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false));
        var locales = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedSize = 0;
        int count = 0;
        foreach (ZipArchiveEntry file in archive.Entries)
        {
            Match match = PackPath.Match(file.FullName);
            if (!match.Success && allowLegacyLayout)
                match = LegacyPackPath.Match(file.FullName);
            if (!match.Success)
                continue;
            expandedSize += file.Length;
            if (file.Length > PackLimit || expandedSize > ExpandedLimit || !paths.Add($"{match.Groups[1].Value}/{match.Groups[2].Value}"))
                throw new InvalidDataException($"Invalid community pack size or duplicate: {file.FullName}");

            using Stream content = file.Open();
            using JsonDocument json = JsonDocument.Parse(content);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Community pack must contain a JSON object: {file.FullName}");

            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty entry in json.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.String
                    || !texts.TryAdd(entry.Name, entry.Value.GetString()!))
                    throw new InvalidDataException($"Invalid or duplicate translation in {file.FullName}");
            }
            foreach (string empty in texts.Where(entry => string.IsNullOrWhiteSpace(entry.Value)).Select(entry => entry.Key).ToArray())
                texts.Remove(empty);

            string locale = match.Groups[1].Value;
            if (!locales.TryGetValue(locale, out var mods))
                locales.Add(locale, mods = new(StringComparer.OrdinalIgnoreCase));
            mods.Add(match.Groups[2].Value, texts);
            count++;
        }
        if (count == 0)
            throw new InvalidDataException("The archive contains no translation packs.");
        return new Snapshot(locales, count);
    }

    internal sealed record Download(byte[] Archive, Snapshot Packs);
    internal sealed record Snapshot(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> Locales, int PackCount);
}
