using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string cachePath;
    private readonly string versionPath;
    private Snapshot snapshot = new(new(StringComparer.OrdinalIgnoreCase), 0);
    private EntityTagHeaderValue? version;

    public CommunityPacks(string directory)
    {
        cachePath = Path.Combine(directory, "community-packs.zip");
        versionPath = Path.Combine(directory, "community-packs.etag");
    }

    public int LoadCache()
    {
        if (!File.Exists(cachePath))
            return 0;
        if (new FileInfo(cachePath).Length > ArchiveLimit)
            throw new InvalidDataException("The community archive exceeds the download size limit.");

        byte[] bytes = File.ReadAllBytes(cachePath);
        snapshot = ReadArchive(bytes);
        if (File.Exists(versionPath))
        {
            string[] metadata = File.ReadAllLines(versionPath);
            // Only send a validator that belongs to the ZIP we successfully loaded.
            if (metadata.Length == 2 && metadata[0] == CacheHash(bytes)
                && EntityTagHeaderValue.TryParse(metadata[1], out var cachedVersion)
                && cachedVersion != EntityTagHeaderValue.Any)
                version = cachedVersion;
        }
        return snapshot.PackCount;
    }

    public async Task<Download?> Fetch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://codeload.github.com/{Repository}/zip/refs/heads/community-packs");
        if (version is not null && File.Exists(cachePath))
            request.Headers.IfNoneMatch.Add(version);
        using HttpResponseMessage response = await Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && request.Headers.IfNoneMatch.Count > 0)
            return null;
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
        Snapshot packs = ReadArchive(bytes);
        EntityTagHeaderValue? downloadedVersion = response.Headers.ETag;
        string metadata = downloadedVersion is null ? "" : $"{CacheHash(bytes)}\n{downloadedVersion}";
        WriteAtomically(versionPath, System.Text.Encoding.UTF8.GetBytes(metadata));
        WriteAtomically(cachePath, bytes);
        return new Download(packs, downloadedVersion);
    }

    public void Apply(Download result)
    {
        snapshot = result.Packs;
        version = result.Version;
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string CacheHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public string? Find(string locale, string modId, string source)
    {
        if (snapshot.Locales.TryGetValue(locale, out var mods)
            && mods.TryGetValue(modId, out var texts)
            && texts.TryGetValue(source, out string? translated))
            return translated;
        return null;
    }

    private static Snapshot ReadArchive(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false));
        var locales = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedSize = 0;
        int count = 0;
        foreach (ZipArchiveEntry file in archive.Entries)
        {
            if (file.Name.Length == 0)
                continue;
            Match match = PackPath.Match(file.FullName);
            if (!match.Success)
                throw new InvalidDataException($"Unexpected file in the community archive: {file.FullName}");
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

    internal sealed record Download(Snapshot Packs, EntityTagHeaderValue? Version);
    internal sealed record Snapshot(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> Locales, int PackCount);
}
