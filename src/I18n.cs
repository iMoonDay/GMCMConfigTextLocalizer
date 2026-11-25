using StardewModdingAPI;

namespace GMCMConfigTextLocalizer;

internal static class I18n
{
    private static ITranslationHelper translations = null!;

    public static void Initialize(ITranslationHelper helper)
    {
        translations = helper;
    }

    public static string Get(string key)
    {
        return translations.Get(key);
    }

    public static string Get(string key, object tokens)
    {
        return translations.Get(key, tokens);
    }
}
