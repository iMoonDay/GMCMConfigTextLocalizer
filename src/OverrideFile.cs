using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GMCMConfigTextLocalizer;

internal static class OverrideFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static Dictionary<string, string?> Read(string path)
    {
        if (!File.Exists(path))
            return new(StringComparer.Ordinal);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
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
            if (!values.TryAdd(property.Name, property.Value.GetString()))
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

    public static void WriteAtomically<T>(string path, T values, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(values, WriteOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
