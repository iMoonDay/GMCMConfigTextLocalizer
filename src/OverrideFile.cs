using System.Text.Encodings.Web;
using System.Text.Json;

namespace GMCMConfigTextLocalizer;

internal static class OverrideFile
{
    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return values;

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The override file root must be an object.");

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new JsonException($"The translation for '{property.Name}' must be a string.");
            if (!values.TryAdd(property.Name, property.Value.GetString() ?? ""))
                throw new JsonException($"Duplicate source text '{property.Name}'.");
        }
        return values;
    }

    public static string GetPath(string modDirectory, string locale, string modId)
    {
        string fileName = string.Concat(modId.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.Combine(modDirectory, "overrides", locale, $"{fileName}.json");
    }
}
