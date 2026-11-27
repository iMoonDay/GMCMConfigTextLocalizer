using System.Text.RegularExpressions;

namespace GMCMConfigTextLocalizer;

internal sealed class NativeTextIndex
{
    private static readonly Regex Token = new(@"\{\{\s*([\w.-]+)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+");
    private static readonly Regex ChineseLineBreak = new(@"(?<=[\p{IsCJKUnifiedIdeographs}，。；：、！？（）《》「」])[ \t]*\r?\n[ \t]*(?=[\p{IsCJKUnifiedIdeographs}，。；：、！？（）《》「」])");
    private static readonly Regex Decoration = new(@"\A\s*(?:(?:>+|\.{3,}|…+)\s*)?(?<body>.*?)(?:\s*(?:[➔→]|->)\s*|\s*)\z", RegexOptions.Singleline);
    private const string MissingTranslationPrefix = "(no translation:";
    private readonly HashSet<string> exact = new(StringComparer.Ordinal);
    private readonly List<Regex> templates = new();

    public NativeTextIndex(Dictionary<string, string> texts, bool includeMissingMarkers = true)
    {
        foreach ((string key, string value) in texts)
        {
            string original = Normalize(value);
            if (original.Length == 0)
                continue;

            exact.Add(original);
            if (includeMissingMarkers)
            {
                exact.Add($"{MissingTranslationPrefix}{key})");
                if (key.EndsWith(".name", StringComparison.Ordinal))
                    exact.Add($"{MissingTranslationPrefix}{key[..^5]})");
            }

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
            templates.Add(new Regex(pattern));
        }
    }

    public bool Contains(string source)
    {
        string normalized = Normalize(source);
        string body = Normalize(Decoration.Match(source).Groups["body"].Value);
        return exact.Contains(normalized) || exact.Contains(body)
            || templates.Any(template => template.IsMatch(normalized) || template.IsMatch(body));
    }

    private static string Normalize(string value)
    {
        return Whitespace.Replace(ChineseLineBreak.Replace(value, ""), " ").Trim();
    }
}
