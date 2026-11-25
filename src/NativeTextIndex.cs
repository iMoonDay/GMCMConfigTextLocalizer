using System.Text.RegularExpressions;

namespace GMCMConfigTextLocalizer;

internal sealed class NativeTextIndex
{
    private static readonly Regex Token = new(@"\{\{\s*([\w.-]+)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+");
    private static readonly Regex Decoration = new(@"\A(?<prefix>\s*(?:(?:>+|\.{3})\s*)?)(?<body>.*?)(?<suffix>\s*(?:[➔→]|->)\s*|\s*)\z", RegexOptions.Singleline);
    private readonly Dictionary<string, List<string>> exact = new(StringComparer.Ordinal);
    private readonly List<Template> templates = new();

    public NativeTextIndex(Dictionary<string, string> texts)
    {
        foreach ((string key, string value) in texts)
        {
            string original = Normalize(value);
            if (original.Length == 0)
                continue;

            if (!exact.TryGetValue(original, out List<string>? keys))
                exact.Add(original, keys = new List<string>());
            keys.Add(key);

            MatchCollection tokens = Token.Matches(original);
            if (tokens.Count == 0 || Token.Replace(original, "").Count(char.IsLetter) < 4)
                continue;

            string pattern = "\\A";
            int offset = 0;
            var names = new List<string>();
            foreach (Match token in tokens)
            {
                pattern += Regex.Escape(original[offset..token.Index]);
                string name = token.Groups[1].Value.ToUpperInvariant();
                int index = names.IndexOf(name);
                if (index < 0)
                {
                    names.Add(name);
                    pattern += $"(?<v{names.Count - 1}>.+?)";
                }
                else
                    pattern += $"\\k<v{index}>";
                offset = token.Index + token.Length;
            }
            pattern += Regex.Escape(original[offset..]) + "\\z";
            templates.Add(new Template(key, new Regex(pattern), names));
        }
    }

    public string? Find(string source, Dictionary<string, string> requested)
    {
        string normalized = Normalize(source);
        if (exact.ContainsKey(normalized))
            return FindBody(normalized, requested);

        Match pieces = Decoration.Match(source);
        string body = Normalize(pieces.Groups["body"].Value);
        string? translation = FindBody(body, requested);
        return translation is null
            ? null
            : pieces.Groups["prefix"].Value + translation.Trim() + pieces.Groups["suffix"].Value;
    }

    private string? FindBody(string body, Dictionary<string, string> requested)
    {
        if (body.Length == 0)
            return null;
        string? translation = null;
        if (exact.TryGetValue(body, out List<string>? keys))
        {
            foreach (string key in keys)
            {
                if (!requested.TryGetValue(key, out string? value) || Normalize(value) == body)
                    continue;
                if (translation is not null && translation != value)
                    return null;
                translation = value;
            }
        }
        else
        {
            foreach (Template template in templates)
            {
                if (!requested.TryGetValue(template.Key, out string? value))
                    continue;
                Match match = template.Pattern.Match(body);
                if (!match.Success)
                    continue;

                bool hasMissingToken = false;
                string rendered = Token.Replace(value, token =>
                {
                    int index = template.Names.IndexOf(token.Groups[1].Value.ToUpperInvariant());
                    if (index >= 0)
                        return match.Groups[$"v{index}"].Value;
                    hasMissingToken = true;
                    return token.Value;
                });
                if (hasMissingToken || Normalize(rendered) == body)
                    continue;
                if (translation is not null && translation != rendered)
                    return null;
                translation = rendered;
            }
        }

        return translation;
    }

    public static string Normalize(string value)
    {
        return Whitespace.Replace(value, " ").Trim();
    }

    private sealed record Template(string Key, Regex Pattern, List<string> Names);
}
