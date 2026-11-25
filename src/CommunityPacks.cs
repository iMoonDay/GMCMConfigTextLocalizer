using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GMCMConfigTextLocalizer;

internal sealed class CommunityPacks
{
    public const string Repository = "iMoonDay/GMCMConfigTextLocalizer";
    private const string Branch = "community-packs";
    private const string VersionFile = "version.txt";
    private const int VersionLimit = 64;
    private const int ArchiveLimit = 16 * 1024 * 1024;
    private const int PackLimit = 1024 * 1024;
    private const int ExpandedLimit = 32 * 1024 * 1024;
    private static readonly Regex PackPath = new(
        @"\A[^/]+/([A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)/([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*)\.json\z");
    private static readonly Regex VersionFormat = new(@"\A[0-9a-f]{40}\z");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string cachePath;
    private Snapshot? snapshot;

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

        snapshot = ReadArchive(File.ReadAllBytes(cachePath));
        return snapshot.PackCount;
    }

    public async Task<Snapshot?> Fetch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        byte[] versionBytes = await DownloadFile(
            $"https://raw.githubusercontent.com/{Repository}/{Branch}/{VersionFile}", VersionLimit, timeout.Token)
            .ConfigureAwait(false);
        string remoteVersion = ParseVersion(versionBytes);
        if (snapshot?.Version == remoteVersion && File.Exists(cachePath))
            return null;

        byte[] bytes = await DownloadFile(
            $"https://codeload.github.com/{Repository}/zip/refs/heads/{Branch}", ArchiveLimit, timeout.Token)
            .ConfigureAwait(false);
        Snapshot packs = ReadArchive(bytes);
        if (packs.Version != remoteVersion)
            throw new InvalidDataException("The community archive version differs from version.txt. Retry after GitHub finishes updating.");

        WriteAtomically(cachePath, bytes);
        return packs;
    }

    private static async Task<byte[]> DownloadFile(string url, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using HttpResponseMessage response = await Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLimited(stream, limit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLimited(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var contents = new MemoryStream();
        var buffer = new byte[Math.Min(limit + 1, 81920)];
        int length;
        while ((length = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (contents.Length + length > limit)
                throw new InvalidDataException("A community translation file exceeds its size limit.");
            contents.Write(buffer, 0, length);
        }
        return contents.ToArray();
    }

    public void Apply(Snapshot packs) => snapshot = packs;

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

    public string? Find(string locale, string modId, string source)
    {
        if (snapshot is not null && snapshot.Locales.TryGetValue(locale, out var mods)
            && mods.TryGetValue(modId, out var texts)
            && texts.TryGetValue(source, out string? translated))
            return translated;
        return null;
    }

    private static string ParseVersion(byte[] bytes)
    {
        string version = Encoding.UTF8.GetString(bytes).Trim();
        if (!VersionFormat.IsMatch(version))
            throw new InvalidDataException("The community pack version.txt does not contain a valid version.");
        return version;
    }

    private static Snapshot ReadArchive(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false));
        ZipArchiveEntry[] versionEntries = archive.Entries
            .Where(file => file.Name == VersionFile && file.FullName.Count(character => character == '/') == 1)
            .ToArray();
        if (versionEntries.Length != 1)
            throw new InvalidDataException("The community archive must contain one root version.txt.");

        string version;
        using (var reader = new BinaryReader(versionEntries[0].Open()))
        {
            byte[] versionBytes = reader.ReadBytes(VersionLimit + 1);
            if (versionBytes.Length > VersionLimit)
                throw new InvalidDataException("The community archive version.txt exceeds its size limit.");
            version = ParseVersion(versionBytes);
        }

        string root = versionEntries[0].FullName[..^VersionFile.Length];
        var locales = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedSize = 0;
        int count = 0;
        foreach (ZipArchiveEntry file in archive.Entries)
        {
            if (file.Name.Length == 0 || file == versionEntries[0])
                continue;
            Match match = PackPath.Match(file.FullName);
            if (!match.Success || !file.FullName.StartsWith(root, StringComparison.Ordinal))
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
        return new Snapshot(locales, count, version);
    }

    internal sealed record Snapshot(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> Locales, int PackCount, string Version);
}
