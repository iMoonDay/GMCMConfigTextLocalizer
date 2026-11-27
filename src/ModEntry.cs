using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
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
    private GmcmPageEditor? pageEditor;
    private readonly PerScreen<TranslationManagementList> management = new(() => new());
    private HashSet<string> disabledMods = new(StringComparer.OrdinalIgnoreCase);
    private bool translationsEnabled;
    private object gmcmApi = null!;
    private ISyncMenuApi? syncMenu;
    private Task<IReadOnlyList<CommunityPacks.TranslationFile>>? pendingSync;
    private Rectangle globalCheckbox;
    private Rectangle syncButton;
    private Rectangle exportButton;
    private Rectangle folderButton;
    private bool folderOpenFailed;
    private string syncMessage = "sync.ready";
    private int syncedCount;

    public override void Entry(IModHelper helper)
    {
        I18n.Initialize(helper.Translation);
        instance = this;
        TranslationSettings settings = helper.ReadConfig<TranslationSettings>();
        translationsEnabled = settings.Enabled;
        disabledMods.UnionWith(settings.DisabledMods ?? []);
        translations = new TranslationStore(
            helper.DirectoryPath,
            Directory.GetParent(helper.DirectoryPath)?.FullName
                ?? throw new InvalidOperationException("The mod directory has no Mods parent."),
            helper.Translation.Locale,
            Monitor,
            modId => translationsEnabled && IsModTranslationEnabled(modId)
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
        var editorPatches = new Harmony(ModManifest.UniqueID + ".Editor");
        try
        {
            pageEditor = new GmcmPageEditor(gmcmApi.GetType().Assembly, editorPatches, translations,
                page => pageTitles.TryGetValue(page, out Func<string>? title) ? title() : null,
                ReadOptionSources, CreateModExportMenu, Helper, Monitor);
        }
        catch (Exception error)
        {
            editorPatches.UnpatchAll(editorPatches.Id);
            string version = Helper.ModRegistry.Get(GmcmId)?.Manifest.Version.ToString() ?? "?";
            Monitor.Log(I18n.Get("log.editor.version", new { version }) + Environment.NewLine + error, LogLevel.Warn);
        }

        Helper.ConsoleCommands.Add("gmcmtext_export", I18n.Get("command.export.usage"), OnExportCommand);
        Helper.ConsoleCommands.Add("gmcmtext_sync", I18n.Get("sync.command"), (_, _) => StartPackSync());
        syncMenu = Helper.ModRegistry.GetApi<ISyncMenuApi>(GmcmId);
        if (syncMenu is not null)
        {
            syncMenu.Register(ModManifest, () => SaveTranslationSettings(true, new(StringComparer.OrdinalIgnoreCase)),
                () => { }, titleScreenOnly: false);
            syncMenu.AddComplexOption(ModManifest, () => "", DrawActions, height: () => 269);
            if (pageEditor is not null)
                syncMenu.AddComplexOption(ModManifest, () => "", DrawManagement,
                    beforeMenuOpened: RefreshManagement, height: () => management.Value.Height);
            Helper.Events.Input.ButtonPressed += OnActionPressed;
        }

        Monitor.Log(I18n.Get("log.patch.ready"), LogLevel.Info);
        StartPackSync();
    }

    private void DrawActions(SpriteBatch batch, Vector2 position)
    {
        int top = (int)position.Y + 4;
        int left = (int)position.X + 8;
        Rectangle? clip = null;
        if (pageEditor is not null && GmcmPageEditor.CurrentMenu() is { } menu)
        {
            Rectangle table = pageEditor.GetTableBounds(menu);
            left = table.Left + 10;
            table.Inflate(20, 20);
            clip = table;
        }
        DrawGlobalCheckbox(batch, left, top, clip);
        top += 44;
        syncButton = DrawAction(batch, I18n.Get("sync.button"), left, top, pendingSync is null);
        Utility.drawTextWithShadow(batch, I18n.Get(syncMessage, new { count = syncedCount }), Game1.smallFont,
            new Vector2(syncButton.X, syncButton.Bottom + 6), Game1.textColor);
        exportButton = DrawAction(batch, I18n.Get("export.button"), left, top + 94, enabled: true);
        folderButton = DrawAction(batch, I18n.Get("folder.open"), left, top + 145, enabled: true);
        if (folderOpenFailed)
            Utility.drawTextWithShadow(batch, I18n.Get("folder.open-failed"), Game1.smallFont,
                new Vector2(folderButton.X, folderButton.Bottom + 6), Game1.textColor);
        if (clip is { } visible)
        {
            syncButton = Rectangle.Intersect(syncButton, visible);
            exportButton = Rectangle.Intersect(exportButton, visible);
            folderButton = Rectangle.Intersect(folderButton, visible);
        }
    }

    private void DrawGlobalCheckbox(SpriteBatch batch, int left, int top, Rectangle? clip)
    {
        const int size = TranslationManagementList.CheckboxSize;
        string label = I18n.Get("management.global-enabled");
        int labelX = left + size + TranslationManagementList.CheckboxGap;
        var checkbox = new Rectangle(left, top, size, size);
        var target = new Rectangle(left, top, labelX - left + (int)Math.Ceiling(Game1.smallFont.MeasureString(label).X), size);
        globalCheckbox = clip is { } visible ? Rectangle.Intersect(target, visible) : target;
        bool hover = globalCheckbox.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        TranslationManagementList.DrawCheckbox(batch, checkbox, translationsEnabled, hover);
        Utility.drawTextWithShadow(batch, label, Game1.smallFont, new Vector2(labelX, top + 5), Game1.textColor);
    }

    private static Rectangle DrawAction(SpriteBatch batch, string label, int x, int y, bool enabled)
    {
        Vector2 size = Game1.smallFont.MeasureString(label);
        var bounds = new Rectangle(x, y, Math.Max(200, (int)size.X + 40), 44);
        bool hover = enabled && bounds.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        IClickableMenu.drawTextureBox(batch, x, y, bounds.Width, bounds.Height, hover ? Color.Wheat : Color.White);
        Utility.drawTextWithShadow(batch, label, Game1.smallFont,
            new Vector2(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f), Game1.textColor);
        return bounds;
    }

    private void OnActionPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button != SButton.MouseLeft || !syncMenu!.TryGetCurrentMenu(out IManifest? manifest, out _)
            || manifest?.UniqueID != ModManifest.UniqueID)
            return;

        var mouse = new Point(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        bool global = globalCheckbox.Contains(mouse);
        bool sync = pendingSync is null && syncButton.Contains(mouse);
        bool export = exportButton.Contains(mouse);
        bool folder = folderButton.Contains(mouse);
        Action? manage = management.Value.FindAction(mouse);
        if (!global && !sync && !export && !folder && manage is null)
            return;

        Helper.Input.Suppress(e.Button);
        Game1.playSound("smallSelect");
        if (global)
            SaveTranslationSettings(!translationsEnabled, disabledMods);
        else if (manage is not null)
            manage();
        else if (export)
            OpenExportMenu();
        else if (folder)
            OpenOverridesFolder();
        else
            StartPackSync();
    }

    private bool IsModTranslationEnabled(string modId) => !disabledMods.Contains(modId);

    private void SaveTranslationSettings(bool enabled, HashSet<string> updated)
    {
        try
        {
            Helper.WriteConfig(new TranslationSettings
            {
                Enabled = enabled,
                DisabledMods = updated.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
            });
            translationsEnabled = enabled;
            disabledMods = updated;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Monitor.Log(I18n.Get("management.save-failed") + Environment.NewLine + error.Message, LogLevel.Warn);
        }
    }

    private void ToggleTranslation(TranslationManagementList.Row row)
    {
        var updated = new HashSet<string>(disabledMods, StringComparer.OrdinalIgnoreCase);
        if (!updated.Add(row.Mod.Id))
            updated.Remove(row.Mod.Id);
        SaveTranslationSettings(translationsEnabled, updated);
    }

    private void SetModTranslations(bool enabled, IEnumerable<string> modIds)
    {
        var updated = new HashSet<string>(disabledMods, StringComparer.OrdinalIgnoreCase);
        if (enabled)
            updated.ExceptWith(modIds);
        else
            updated.UnionWith(modIds);
        SaveTranslationSettings(translationsEnabled, updated);
    }

    private void RefreshManagement()
    {
        var overrides = translations.ReadOverrideCatalog();
        var sources = ReadSources(null, includeInternalMods: true, reportReadError: (id, error) =>
            Monitor.Log($"{id}: {error.Message}", LogLevel.Trace))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Source))
            .ToLookup(entry => entry.ModId, entry => entry.Source, StringComparer.OrdinalIgnoreCase);
        string locale = translations.GetExportLocale();
        var rows = Helper.ModRegistry.GetAll().Select(mod => mod.Manifest.UniqueID)
            .Concat(ReadPages().Select(page => page.ModId)).Concat(overrides.Keys).Concat(translations.ReadIgnoredModIds())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(modId =>
            {
                IManifest? manifest = Helper.ModRegistry.Get(modId)?.Manifest;
                var values = overrides.GetValueOrDefault(modId);
                var ignored = translations.GetIgnoredSources(modId);
                string name = manifest?.Name ?? (values is not null || ignored.Count > 0 ? $"{modId}.json" : modId);
                int count = values?.Count(entry => !string.IsNullOrWhiteSpace(entry.Key)
                    && !string.IsNullOrWhiteSpace(entry.Value) && !ignored.Contains(entry.Key)) ?? 0;
                var candidates = sources[modId].Concat(values?.Keys.AsEnumerable() ?? [])
                    .Where(source => !string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.Ordinal).ToArray();
                NativeTextIndex? localized = candidates.Length > 0 ? translations.ReadLocalizedTextIndex(modId) : null;
                int pending = candidates.Count(source => TranslationStore.GetStatus(source, values?.GetValueOrDefault(source),
                    localized, locale, ignored.Contains(source)) == TranslationStatus.Pending);
                return new TranslationManagementList.Row(new ExportMod(modId, name), manifest is not null, count, pending);
            })
            .OrderByDescending(row => row.Installed)
            .ThenBy(row => row.Mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        management.Value.SetRows(rows);
    }

    private void DrawManagement(SpriteBatch batch, Vector2 position)
    {
        if (GmcmPageEditor.CurrentMenu() is not { } menu)
            return;

        Rectangle table = pageEditor!.GetTableBounds(menu);
        management.Value.Draw(batch, position, table, IsModTranslationEnabled, SetModTranslations, ToggleTranslation, row =>
        {
            try
            {
                pageEditor.OpenForMod(menu, row.Mod, ReadModEditorSources(row.Mod.Id), () =>
                {
                    int previousHeight = management.Value.Height;
                    RefreshManagement();
                    if (management.Value.Height < previousHeight)
                        pageEditor.ClampTableScroll(menu);
                });
            }
            catch (Exception error)
            {
                Monitor.Log(I18n.Get("editor.open-failed") + Environment.NewLine + error, LogLevel.Error);
            }
        }, () => pageEditor.ClampTableScroll(menu));
    }

    private IEnumerable<string> ReadModEditorSources(string modId)
    {
        if (Helper.ModRegistry.Get(modId) is { } mod)
            yield return mod.Manifest.Name;
        foreach (ExportText entry in ReadSources(modId, includeInternalMods: true))
            yield return entry.Source;
        if (translations.ReadOverrideCatalog().TryGetValue(modId, out var overrides))
        {
            foreach (string source in overrides.Keys)
                yield return source;
        }
        foreach (string source in translations.GetIgnoredSources(modId))
            yield return source;
    }

    private void OpenOverridesFolder()
    {
        folderOpenFailed = false;
        try
        {
            string path = Directory.CreateDirectory(Path.Combine(translations.ModDirectory, "overrides")).FullName;
            using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            folderOpenFailed = true;
            Monitor.Log(I18n.Get("folder.open-failed") + Environment.NewLine + error.Message, LogLevel.Warn);
        }
    }

    private void OpenExportMenu()
    {
        IClickableMenu? parent = Game1.activeClickableMenu;
        var title = parent as TitleMenu;
        if (title is not null)
            parent = TitleMenu.subMenu;
        if (parent is null)
            return;

        var mods = ReadPages()
            .Select(page => page.ModId)
            .Where(IsExportable)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new ExportMod(id, Helper.ModRegistry.Get(id)?.Manifest.Name ?? id))
            .OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string locale = translations.GetExportLocale();

        var menu = CreateExportMenu(mods, locale, ids => ReadSources(null, ids),
            () =>
            {
                if (title is not null)
                    TitleMenu.subMenu = parent;
                else
                    Game1.activeClickableMenu = parent;
            }, singleMod: false);
        try
        {
            menu.Load();
        }
        catch (Exception error)
        {
            Monitor.Log(I18n.Get("command.export.failed") + Environment.NewLine + error, LogLevel.Error);
            return;
        }
        if (title is not null)
            title.ForceSubmenu(menu);
        else
            Game1.activeClickableMenu = menu;
    }

    private ExportMenu CreateModExportMenu(ExportMod mod, string locale, Action close)
    {
        var menu = CreateExportMenu([mod], locale,
            _ => ReadSources(mod.Id, includeInternalMods: true), close, singleMod: true);
        menu.Load();
        return menu;
    }

    private ExportMenu CreateExportMenu(IReadOnlyList<ExportMod> mods, string locale,
        Func<IReadOnlySet<string>, IEnumerable<ExportText>> readSources, Action close, bool singleMod) =>
        new(mods, locale, readSources, translations, entries => ExportFromMenu(locale, entries), close,
            error => Monitor.Log(error.ToString(), LogLevel.Error), singleMod);

    private bool IsExportable(string id)
        => !id.Equals("SMAPI", StringComparison.OrdinalIgnoreCase)
            && !id.Equals(ModManifest.UniqueID, StringComparison.OrdinalIgnoreCase);

    private int ExportFromMenu(string locale, IReadOnlyList<ExportText> entries)
    {
        translations.ClearCache();
        return TemplateExporter.ExportSelected(translations.ModDirectory, locale,
            entries.Select(entry => (entry.ModId, entry.Source)));
    }

    private void StartPackSync()
    {
        if (pendingSync is not null)
            return;

        syncMessage = "sync.running";
        pendingSync = CommunityPacks.Fetch();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (pendingSync is not { IsCompleted: true } completed)
            return;

        pendingSync = null;
        try
        {
            ApplyCommunityTranslations(completed.GetAwaiter().GetResult());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException
            or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            syncMessage = "sync.failed";
            Monitor.Log(I18n.Get(syncMessage) + Environment.NewLine + error.Message, LogLevel.Warn);
        }
    }

    private void ApplyCommunityTranslations(IReadOnlyList<CommunityPacks.TranslationFile> files)
    {
        syncedCount = 0;
        bool failed = false;
        foreach (CommunityPacks.TranslationFile file in files)
        {
            try
            {
                syncedCount += translations.MergeCommunityTranslations(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                failed = true;
                Monitor.Log(I18n.Get("sync.file-failed", new { locale = file.Locale, mod = file.ModId })
                    + Environment.NewLine + error.Message, LogLevel.Warn);
            }
        }

        syncMessage = failed ? "sync.partial" : syncedCount > 0 ? "sync.complete" : "sync.current";
        Monitor.Log(I18n.Get(syncMessage, new { count = syncedCount }), failed ? LogLevel.Warn : LogLevel.Info);
        if (syncedCount > 0 && syncMenu?.TryGetCurrentMenu(out IManifest? manifest, out _) == true && manifest?.UniqueID == ModManifest.UniqueID)
            RefreshManagement();
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
        if (!instance.options.TryGetValue(__instance, out OptionText? text))
            return;

        Func<string, string> formatter = instance.CaptureChoiceFormatter(__instance, __result);
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

    private Func<string, string> CaptureChoiceFormatter(object option, Func<string, string>? formatter)
    {
        return choiceFormatters.GetValue(option, _ => value => formatter?.Invoke(value) ?? value);
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

    private (string? Name, string? Tooltip, string[] Choices) ReadOptionSources(object option)
    {
        if (!options.TryGetValue(option, out OptionText? text))
            return (null, null, []);

        string? name = text.Name?.Invoke();
        string? tooltip = text.Tooltip?.Invoke();
        Type type = option.GetType();
        if (AccessTools.Property(type, "Choices")?.GetValue(option) is not string[] choices)
            return (name, tooltip, []);

        if (!choiceFormatters.TryGetValue(option, out Func<string, string>? formatter))
        {
            var format = (Func<string, string>?)AccessTools.Property(type, "FormatChoice")!.GetValue(option);
            formatter = CaptureChoiceFormatter(option, format);
        }
        return (name, tooltip, choices.Select(formatter).ToArray());
    }

    private IEnumerable<ExportText> ReadSources(string? requestedMod, IReadOnlySet<string>? selectedMods = null,
        bool includeInternalMods = false, Action<string, Exception>? reportReadError = null)
    {
        foreach ((_, string modId, _, object page) in ReadPages())
        {
            if ((!includeInternalMods && !IsExportable(modId)) || (selectedMods is not null && !selectedMods.Contains(modId))
                || (requestedMod is not null && !modId.Equals(requestedMod, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (pageTitles.TryGetValue(page, out Func<string>? title))
            {
                string? source;
                try
                {
                    source = title();
                }
                catch (Exception error) when (reportReadError is not null)
                {
                    reportReadError(modId, error);
                    source = null;
                }
                yield return new ExportText(modId, source ?? "");
            }

            foreach (object option in ReadOptions(page))
            {
                (string? Name, string? Tooltip, string[] Choices) text;
                try
                {
                    text = ReadOptionSources(option);
                }
                catch (Exception error) when (reportReadError is not null)
                {
                    reportReadError(modId, error);
                    continue;
                }
                yield return new ExportText(modId, text.Name ?? "");
                yield return new ExportText(modId, text.Tooltip ?? "");
                foreach (string choice in text.Choices)
                    yield return new ExportText(modId, choice);
            }
        }
    }

    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e)
    {
        translations.SetLocale(e.NewLocale);
        if (syncMenu?.TryGetCurrentMenu(out IManifest? manifest, out _) == true && manifest?.UniqueID == ModManifest.UniqueID)
            RefreshManagement();
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
            int count = TemplateExporter.Export(translations, locale,
                ReadSources(modId).Select(entry => (entry.ModId, entry.Source)));
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
