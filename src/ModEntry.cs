using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace GMCMConfigTextLocalizer;

public sealed class ModEntry : Mod
{
    private const string GmcmId = "spacechase0.GenericModConfigMenu";
    private static ModEntry instance = null!;
    private readonly ConditionalWeakTable<object, OptionText> options = new();
    private readonly ConditionalWeakTable<object, Func<string, string>> choiceFormatters = new();
    private readonly ConditionalWeakTable<object, Func<string>> pageTitles = new();
    private TranslationStore translations = null!;
    private CommunityPacks community = null!;
    private object gmcmApi = null!;
    private ISyncMenuApi? syncMenu;
    private Task<CommunityPacks.Download>? pendingSync;
    private Rectangle syncButton;
    private string syncMessage = "sync.ready";
    private int packCount;

    public override void Entry(IModHelper helper)
    {
        I18n.Initialize(helper.Translation);
        instance = this;
        community = new CommunityPacks(helper.DirectoryPath);
        try
        {
            packCount = community.LoadCache();
            if (packCount > 0)
                syncMessage = "sync.cached";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Monitor.Log(error.Message, LogLevel.Warn);
        }
        translations = new TranslationStore(
            helper.DirectoryPath,
            Directory.GetParent(helper.DirectoryPath)?.FullName
                ?? throw new InvalidOperationException("The mod directory has no Mods parent."),
            helper.Translation.Locale,
            Monitor,
            community
        );
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.Content.LocaleChanged += OnLocaleChanged;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        try
        {
            gmcmApi = Helper.ModRegistry.GetApi(GmcmId)
                ?? throw new InvalidOperationException("The GMCM API is unavailable.");
            InstallPatches();
        }
        catch (Exception error)
        {
            new Harmony(ModManifest.UniqueID).UnpatchAll(ModManifest.UniqueID);
            string version = Helper.ModRegistry.Get(GmcmId)?.Manifest.Version.ToString() ?? "?";
            Monitor.Log(I18n.Get("log.patch.version", new { version }) + Environment.NewLine + error.Message, LogLevel.Warn);
            return;
        }
        Helper.ConsoleCommands.Add("gmcmtext_export", I18n.Get("command.export.usage"), OnExportCommand);
        Helper.ConsoleCommands.Add("gmcmtext_sync", I18n.Get("sync.command"), (_, _) => StartPackSync());
        syncMenu = Helper.ModRegistry.GetApi<ISyncMenuApi>(GmcmId);
        if (syncMenu is not null)
        {
            syncMenu.Register(ModManifest, () => { }, () => { }, titleScreenOnly: false);
            syncMenu.AddComplexOption(ModManifest, () => "", DrawSyncButton, height: () => 96);
            Helper.Events.Input.ButtonPressed += OnSyncButtonPressed;
        }

        Monitor.Log(I18n.Get("log.patch.ready"), LogLevel.Info);
    }

    private void DrawSyncButton(SpriteBatch batch, Vector2 position)
    {
        string label = I18n.Get("sync.button");
        Point size = Game1.smallFont.MeasureString(label).ToPoint();
        syncButton = new Rectangle((int)position.X + 8, (int)position.Y + 4, Math.Max(200, size.X + 40), 44);
        bool hover = pendingSync is null && syncButton.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        IClickableMenu.drawTextureBox(batch, syncButton.X, syncButton.Y, syncButton.Width, syncButton.Height,
            hover ? Color.Wheat : Color.White);
        Utility.drawTextWithShadow(batch, label, Game1.smallFont,
            new Vector2(syncButton.Center.X - size.X / 2f, syncButton.Center.Y - size.Y / 2f), Game1.textColor);
        Utility.drawTextWithShadow(batch, I18n.Get(syncMessage, new { count = packCount }), Game1.smallFont,
            new Vector2(syncButton.X, syncButton.Bottom + 6), Game1.textColor);
    }

    private void OnSyncButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button != SButton.MouseLeft || pendingSync is not null
            || !syncButton.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true))
            || !syncMenu!.TryGetCurrentMenu(out IManifest? manifest, out _)
            || manifest?.UniqueID != ModManifest.UniqueID)
            return;

        Helper.Input.Suppress(e.Button);
        Game1.playSound("smallSelect");
        StartPackSync();
    }

    private void StartPackSync()
    {
        if (pendingSync is not null)
            return;

        syncMessage = "sync.running";
        Monitor.Log(I18n.Get(syncMessage), LogLevel.Info);
        pendingSync = CommunityPacks.Fetch();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (pendingSync is not { IsCompleted: true } completed)
            return;

        pendingSync = null;
        try
        {
            CommunityPacks.Download download = completed.GetAwaiter().GetResult();
            community.Apply(download);
            packCount = download.Packs.PackCount;
            syncMessage = "sync.complete";
            Monitor.Log(I18n.Get(syncMessage, new { count = packCount }), LogLevel.Info);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException
            or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            syncMessage = "sync.failed";
            Monitor.Log(I18n.Get(syncMessage) + Environment.NewLine + error.Message, LogLevel.Warn);
        }
    }

    private void InstallPatches()
    {
        Assembly gmcm = gmcmApi.GetType().Assembly;
        if (gmcm.GetName().Name != "GenericModConfigMenu")
            throw new InvalidOperationException("Unexpected GMCM API assembly.");

        Type baseOption = GetType(gmcm, "GenericModConfigMenu.Framework.ModOption.BaseModOption");
        Type api = GetType(gmcm, "GenericModConfigMenu.Framework.Api");
        Type page = GetType(gmcm, "GenericModConfigMenu.Framework.ModConfigPage");
        Type modConfig = GetType(gmcm, "GenericModConfigMenu.Framework.ModConfig");
        Type choiceDefinition = GetType(gmcm, "GenericModConfigMenu.Framework.ModOption.ChoiceModOption`1");
        Type choiceOption = choiceDefinition.MakeGenericType(typeof(string));

        MethodBase baseConstructor = AccessTools.Constructor(baseOption,
            [typeof(string), typeof(Func<string>), typeof(Func<string>), modConfig])
            ?? throw new MissingMethodException(baseOption.FullName, ".ctor");
        MethodInfo nameGetter = AccessTools.PropertyGetter(baseOption, "Name")
            ?? throw new MissingMethodException(baseOption.FullName, "get_Name");
        MethodInfo tooltipGetter = AccessTools.PropertyGetter(baseOption, "Tooltip")
            ?? throw new MissingMethodException(baseOption.FullName, "get_Tooltip");
        MethodBase choiceConstructor = AccessTools.Constructor(choiceOption,
            [typeof(string), typeof(Func<string>), typeof(Func<string>), modConfig,
                typeof(Func<string>), typeof(Action<string>), typeof(string[]), typeof(Func<string, string>)])
            ?? throw new MissingMethodException(choiceOption.FullName, ".ctor");
        MethodInfo formatChoiceGetter = AccessTools.PropertyGetter(choiceOption, "FormatChoice")
            ?? throw new MissingMethodException(choiceOption.FullName, "get_FormatChoice");
        MethodInfo setActivePage = AccessTools.Method(modConfig, "SetActiveRegisteringPage",
            [typeof(string), typeof(Func<string>)])
            ?? throw new MissingMethodException(modConfig.FullName, "SetActiveRegisteringPage");
        MethodInfo? overridePageName = AccessTools.Method(api, "OverridePageDisplayName",
            [typeof(IManifest), typeof(string), typeof(string)]);
        if (AccessTools.Field(api, "ConfigManager") is null
            || AccessTools.Property(page, "PageTitle") is null
            || AccessTools.Field(GetType(gmcm, "GenericModConfigMenu.Framework.ModConfigManager"), "Configs") is null)
            throw new MissingMemberException("GMCM configuration registry or page title");

        var harmony = new Harmony(ModManifest.UniqueID);
        harmony.Patch(baseConstructor, postfix: Hook(nameof(AfterOptionCreated)));
        harmony.Patch(nameGetter, postfix: Hook(nameof(AfterNameGetter)));
        harmony.Patch(tooltipGetter, postfix: Hook(nameof(AfterTooltipGetter)));
        harmony.Patch(choiceConstructor, postfix: Hook(nameof(AfterChoiceCreated)));
        harmony.Patch(formatChoiceGetter, postfix: Hook(nameof(AfterFormatChoiceGetter)));
        harmony.Patch(setActivePage, postfix: Hook(nameof(AfterSetActivePage)));
        if (overridePageName is not null)
            harmony.Patch(overridePageName, postfix: Hook(nameof(AfterOverridePageName)));

        CaptureExistingConfigurations();
    }

    private static Type GetType(Assembly assembly, string name)
    {
        return assembly.GetType(name, throwOnError: true)!;
    }

    private static HarmonyMethod Hook(string name)
    {
        return new HarmonyMethod(typeof(ModEntry), name);
    }

    private static void AfterOptionCreated(
        object __instance,
        Func<string> name,
        Func<string> tooltip,
        object mod)
    {
        instance.CaptureOption(__instance, name, tooltip, mod);
    }

    private static void AfterNameGetter(object __instance, ref Func<string> __result)
    {
        if (instance.options.TryGetValue(__instance, out OptionText? text) && text.Name is not null)
            __result = instance.Localize(text.ModId, text.Name);
    }

    private static void AfterTooltipGetter(object __instance, ref Func<string> __result)
    {
        if (instance.options.TryGetValue(__instance, out OptionText? text) && text.Tooltip is not null)
            __result = instance.Localize(text.ModId, text.Tooltip);
    }

    private static void AfterChoiceCreated(object __instance, Func<string>? name, Func<string>? tooltip,
        object mod, Func<string, string>? formatChoice)
    {
        instance.CaptureOption(__instance, name, tooltip, mod);
        instance.CaptureChoiceFormatter(__instance, formatChoice);
    }

    private static void AfterFormatChoiceGetter(object __instance, ref Func<string, string> __result)
    {
        if (instance.options.TryGetValue(__instance, out OptionText? text)
            && instance.choiceFormatters.TryGetValue(__instance, out Func<string, string>? formatter))
            __result = value => instance.translations.Resolve(text.ModId, formatter(value));
    }

    private static void AfterSetActivePage(object __instance, string pageId)
    {
        if (string.IsNullOrEmpty(pageId))
            return;

        IManifest mod = (IManifest)AccessTools.Property(__instance.GetType(), "ModManifest")!.GetValue(__instance)!;
        var configPages = (IDictionary)AccessTools.Property(__instance.GetType(), "Pages")!.GetValue(__instance)!;
        instance.CapturePage(configPages[pageId]!, mod.UniqueID, force: false);
    }

    private static void AfterOverridePageName(object __instance, IManifest mod, string pageName)
    {
        if (instance.TryGetPage(__instance, mod, pageName, out object? page)
            && page is not null)
            instance.CapturePage(page, mod.UniqueID, force: true);
    }

    private void CaptureOption(object option, Func<string>? name, Func<string>? tooltip, object config)
    {
        if (options.TryGetValue(option, out _))
            return;

        IManifest manifest = (IManifest)AccessTools.Property(config.GetType(), "ModManifest")!.GetValue(config)!;
        options.Add(option, new OptionText(manifest.UniqueID, name, tooltip));
    }

    private Func<string> Localize(string modId, Func<string> source)
    {
        return () => translations.Resolve(modId, source() ?? "");
    }

    private void CaptureChoiceFormatter(object option, Func<string, string>? formatter)
    {
        choiceFormatters.GetValue(option, _ => value => formatter?.Invoke(value) ?? value);
    }

    private void CapturePage(object page, string modId, bool force)
    {
        if (!force && pageTitles.TryGetValue(page, out _))
            return;
        if (force)
            pageTitles.Remove(page);

        PropertyInfo titleProperty = AccessTools.Property(page.GetType(), "PageTitle")
            ?? throw new MissingMemberException(page.GetType().FullName, "PageTitle");
        if (titleProperty.GetValue(page) is not Func<string> sourceTitle)
            return;

        pageTitles.Add(page, sourceTitle);
        titleProperty.SetValue(page, Localize(modId, sourceTitle));
    }

    private bool TryGetPage(object api, IManifest mod, string pageName, out object? page)
    {
        object manager = AccessTools.Field(api.GetType(), "ConfigManager")!.GetValue(api)!;
        object configMap = AccessTools.Field(manager.GetType(), "Configs")!.GetValue(manager)!;
        var configs = (IDictionary)configMap;
        if (configs[mod.UniqueID] is not object config)
        {
            page = null;
            return false;
        }

        object pagesValue = AccessTools.Property(config.GetType(), "Pages")!.GetValue(config)!;
        var configPages = (IDictionary)pagesValue;
        page = configPages[pageName];
        return page is not null;
    }

    private void CaptureExistingConfigurations()
    {
        foreach ((object config, string modId, string pageId, object page) in ReadPages())
        {
            if (pageId.Length > 0)
                CapturePage(page, modId, force: false);

            foreach (object option in ReadOptions(page))
            {
                Type optionType = option.GetType();
                var name = (Func<string>?)AccessTools.Property(optionType, "Name")!.GetValue(option);
                var tooltip = (Func<string>?)AccessTools.Property(optionType, "Tooltip")!.GetValue(option);
                CaptureOption(option, name, tooltip, config);
                if (AccessTools.Property(optionType, "Choices")?.PropertyType == typeof(string[]))
                    CaptureChoiceFormatter(option, (Func<string, string>?)AccessTools.Property(optionType, "FormatChoice")!.GetValue(option));
            }
        }
    }

    private IEnumerable<(object Config, string ModId, string PageId, object Page)> ReadPages()
    {
        object manager = AccessTools.Field(gmcmApi.GetType(), "ConfigManager")!.GetValue(gmcmApi)!;
        var configs = (IDictionary)AccessTools.Field(manager.GetType(), "Configs")!.GetValue(manager)!;
        foreach (DictionaryEntry configEntry in configs)
        {
            object config = configEntry.Value!;
            var pages = (IDictionary)AccessTools.Property(config.GetType(), "Pages")!.GetValue(config)!;
            foreach (DictionaryEntry page in pages)
                yield return (config, (string)configEntry.Key, (string)page.Key, page.Value!);
        }
    }

    private static IEnumerable ReadOptions(object page)
    {
        return (IEnumerable)AccessTools.Property(page.GetType(), "Options")!.GetValue(page)!;
    }

    private IEnumerable<(string ModId, string Source)> ReadSources(string? requestedMod)
    {
        foreach ((_, string modId, _, object page) in ReadPages())
        {
            if (modId.Equals("SMAPI", StringComparison.OrdinalIgnoreCase)
                || (requestedMod is not null && !modId.Equals(requestedMod, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (pageTitles.TryGetValue(page, out Func<string>? title))
                yield return (modId, title() ?? "");

            foreach (object option in ReadOptions(page))
            {
                if (!options.TryGetValue(option, out OptionText? text))
                    continue;

                yield return (modId, text.Name?.Invoke() ?? "");
                yield return (modId, text.Tooltip?.Invoke() ?? "");

                if (choiceFormatters.TryGetValue(option, out Func<string, string>? formatter))
                {
                    var choices = (string[])AccessTools.Property(option.GetType(), "Choices")!.GetValue(option)!;
                    foreach (string value in choices)
                        yield return (modId, formatter(value));
                }
            }
        }
    }

    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e)
    {
        translations.SetLocale(e.NewLocale);
    }

    private void OnExportCommand(string command, string[] args)
    {
        if (args.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(args[0]))
        {
            Monitor.Log(I18n.Get("command.export.usage"), LogLevel.Info);
            return;
        }

        string locale = args[0];
        string? modId = args.Length == 2 ? args[1] : null;
        try
        {
            translations.ClearCache();
            int count = TemplateExporter.Export(translations, locale, ReadSources(modId));
            if (count == 0)
                Monitor.Log(I18n.Get("command.export.none", new { locale }), LogLevel.Info);
            else
                Monitor.Log(I18n.Get("command.export.complete", new { count, locale }), LogLevel.Info);
            translations.ClearCache();
        }
        catch (Exception ex)
        {
            Monitor.Log(ex.ToString(), LogLevel.Error);
            Monitor.Log(I18n.Get("command.export.failed"), LogLevel.Error);
        }
    }

    private sealed record OptionText(string ModId, Func<string>? Name, Func<string>? Tooltip);
}
