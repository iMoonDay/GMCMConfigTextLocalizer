using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GMCMConfigTextLocalizer;

internal static class CommunityPacks
{
    public const string Repository = "iMoonDay/GMCMConfigTextLocalizer";
    private const string Branch = "community-packs";
    private const string VersionFile = "version.txt";
    private const int ArchiveLimit = 16 * 1024 * 1024;
    private const int PackLimit = 1024 * 1024;
    private const int ExpandedLimit = 32 * 1024 * 1024;
    private static readonly Regex PackPath = new(
        @"\A[^/]+/([A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)/([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*)\.json\z");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };

    public static async Task<IReadOnlyList<TranslationFile>> Fetch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        byte[] bytes = await DownloadFile(
            $"https://codeload.github.com/{Repository}/zip/refs/heads/{Branch}", ArchiveLimit, timeout.Token)
            .ConfigureAwait(false);
        return ReadArchive(bytes);
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

    private static IReadOnlyList<TranslationFile> ReadArchive(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false));
        ZipArchiveEntry[] versionEntries = archive.Entries
            .Where(file => file.Name == VersionFile && file.FullName.Count(character => character == '/') == 1)
            .ToArray();
        if (versionEntries.Length != 1)
            throw new InvalidDataException("The community archive must contain one root version.txt.");

        string root = versionEntries[0].FullName[..^VersionFile.Length];
        var files = new List<TranslationFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedSize = 0;
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

            files.Add(new TranslationFile(match.Groups[1].Value, match.Groups[2].Value, texts));
        }
        if (files.Count == 0)
            throw new InvalidDataException("The archive contains no translation packs.");
        return files;
    }

    internal sealed record TranslationFile(string Locale, string ModId, IReadOnlyDictionary<string, string> Texts);
}
