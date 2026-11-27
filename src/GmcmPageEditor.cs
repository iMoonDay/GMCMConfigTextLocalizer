using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
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

internal sealed class GmcmPageEditor
{
    private static GmcmPageEditor instance = null!;
    private readonly TranslationStore translations;
    private readonly Func<object, string?> readPageTitle;
    private readonly Func<object, (string? Name, string? Tooltip, string[] Choices)> readOption;
    private readonly Func<ExportMod, string, Action, ExportMenu> createExportMenu;
    private readonly IMonitor monitor;
    private readonly IModHelper helper;
    private readonly ConditionalWeakTable<IClickableMenu, Page> pages = new();
    private readonly PerScreen<TranslationEditorMenu?> activeEditor = new();
    private readonly PerScreen<ExportMenu?> activeExport = new();
    private readonly PropertyInfo labelText, labelPosition, labelWidth, elementData, children, tableSize, tablePosition, dropdownLabels, configPages;
    private readonly FieldInfo titleWidth, ui, table, hovers, keybindOverlay, activeDropdown, modConfig, currentPage;
    private readonly Type labelType, dropdownType, paragraphType;
    private readonly MethodInfo beforeOpen, addRow;

    public GmcmPageEditor(Assembly gmcm, Harmony harmony, TranslationStore translations,
        Func<object, string?> readPageTitle,
        Func<object, (string? Name, string? Tooltip, string[] Choices)> readOption,
        Func<ExportMod, string, Action, ExportMenu> createExportMenu,
        IModHelper helper, IMonitor monitor)
    {
        this.translations = translations;
        this.readPageTitle = readPageTitle;
        this.readOption = readOption;
        this.createExportMenu = createExportMenu;
        this.helper = helper;
        this.monitor = monitor;
        Type gmcmMenu = gmcm.GetType("GenericModConfigMenu.Framework.SpecificModConfigMenu", throwOnError: true)!;
        Type config = gmcm.GetType("GenericModConfigMenu.Framework.ModConfig", throwOnError: true)!;
        Type option = gmcm.GetType("GenericModConfigMenu.Framework.ModOption.BaseModOption", throwOnError: true)!;
        paragraphType = gmcm.GetType("GenericModConfigMenu.Framework.ModOption.ParagraphModOption", throwOnError: true)!;
        ConstructorInfo constructor = AccessTools.Constructor(gmcmMenu,
            [config, typeof(int), typeof(string), typeof(Action<string>), typeof(Action)])
            ?? throw new MissingMethodException(gmcmMenu.FullName, ".ctor");
        titleWidth = Field(gmcmMenu, "TitleLabelWidth");
        ui = Field(gmcmMenu, "Ui");
        table = Field(gmcmMenu, "Table");
        hovers = Field(gmcmMenu, "OptHovers");
        keybindOverlay = Field(gmcmMenu, "ActiveKeybindOverlay");
        Type elementType = gmcm.GetType("SpaceShared.UI.Element", throwOnError: true)!;
        labelType = gmcm.GetType("SpaceShared.UI.Label", throwOnError: true)!;
        dropdownType = gmcm.GetType("SpaceShared.UI.Dropdown", throwOnError: true)!;
        labelText = Property(labelType, "String");
        labelPosition = Property(labelType, "LocalPosition");
        labelWidth = Property(labelType, "Width");
        elementData = Property(elementType, "UserData");
        children = Property(ui.FieldType, "Children");
        tableSize = Property(table.FieldType, "Size");
        tablePosition = Property(table.FieldType, "Position");
        dropdownLabels = Property(dropdownType, "Labels");
        activeDropdown = Field(dropdownType, "ActiveDropdown");
        modConfig = Field(gmcmMenu, "ModConfig");
        currentPage = Field(gmcmMenu, "CurrPage");
        configPages = Property(config, "Pages");
        beforeOpen = AccessTools.Method(option, "BeforeMenuOpened") ?? throw new MissingMethodException(option.FullName, "BeforeMenuOpened");
        addRow = AccessTools.Method(table.FieldType, "AddRow", [elementType.MakeArrayType()])
            ?? throw new MissingMethodException(table.FieldType.FullName, "AddRow");
        MethodInfo draw = AccessTools.DeclaredMethod(gmcmMenu, "draw", [typeof(SpriteBatch)])
            ?? throw new MissingMethodException(gmcmMenu.FullName, "draw");
        MethodInfo addLabels = AccessTools.DeclaredMethod(gmcmMenu, "AddDefaultLabels", [typeof(IManifest)])
            ?? throw new MissingMethodException(gmcmMenu.FullName, "AddDefaultLabels");

        instance = this;
        harmony.Patch(constructor, transpiler: Hook(nameof(CaptureRows)));
        harmony.Patch(addLabels, postfix: Hook(nameof(AfterAddLabels)));
        harmony.Patch(draw, postfix: Hook(nameof(DrawButton)));
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.GameLoop.UpdateTicked += OnUpdate;
    }

    private static FieldInfo Field(Type type, string name) =>
        AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);

    private static PropertyInfo Property(Type type, string name) =>
        AccessTools.Property(type, name) ?? throw new MissingMemberException(type.FullName, name);

    private static HarmonyMethod Hook(string name) => new(typeof(GmcmPageEditor), name);

    private static IEnumerable<CodeInstruction> CaptureRows(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        if (code.Count(instruction => instruction.Calls(instance.beforeOpen)) != 1
            || code.Count(instruction => instruction.Calls(instance.addRow)) != 1)
            throw new InvalidOperationException("Could not identify the GMCM option-to-row construction loop.");

        // Pass the option and the actual row array to the hooks without changing the original calls.
        foreach (CodeInstruction instruction in code)
        {
            MethodInfo? capture = instruction.Calls(instance.beforeOpen)
                ? AccessTools.Method(typeof(GmcmPageEditor), nameof(RememberOption))
                : instruction.Calls(instance.addRow) ? AccessTools.Method(typeof(GmcmPageEditor), nameof(RememberRow)) : null;
            if (capture is not null)
            {
                yield return new CodeInstruction(OpCodes.Dup).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, capture);
            }
            yield return instruction;
        }
    }

    private static void RememberOption(object option, IClickableMenu menu) =>
        instance.pages.GetOrCreateValue(menu).PendingOption = option;

    private static void RememberRow(Array elements, IClickableMenu menu)
    {
        Page page = instance.pages.GetOrCreateValue(menu);
        page.Rows.Add((page.PendingOption!, elements));
        page.PendingOption = null;
    }

    private static void AfterAddLabels(IClickableMenu __instance, IManifest? __0)
    {
        if (__0 is null)
            return;

        Page page = instance.pages.GetOrCreateValue(__instance);
        page.Mod = __0;
        if (instance.GetPageTitle(__instance) is not { Length: > 0 })
            instance.SetTitle(__instance, instance.translations.Resolve(__0.UniqueID, __0.Name));
    }

    private string? GetPageTitle(IClickableMenu menu)
    {
        var registeredPages = (IDictionary)configPages.GetValue(modConfig.GetValue(menu))!;
        return readPageTitle(registeredPages[currentPage.GetValue(menu)!]!);
    }

    private static Rectangle ButtonBounds()
    {
        const int gap = 12;
        int width = (int)Game1.smallFont.MeasureString(I18n.Get("editor.button")).X + 32;
        int right = Game1.uiViewport.Width - gap;
        if (Game1.activeClickableMenu is TitleMenu { windowedButton: { } windowedButton })
            right = Math.Min(right, windowedButton.bounds.Left - gap);
        return new Rectangle(right - width, 4, width, 36);
    }

    private static bool CanOpen(IClickableMenu menu, out Page page)
    {
        return instance.pages.TryGetValue(menu, out page!) && page.Mod is not null
            && instance.keybindOverlay.GetValue(menu) is null && instance.activeDropdown.GetValue(null) is null;
    }

    private static void DrawButton(IClickableMenu __instance, SpriteBatch __0)
    {
        if (!CanOpen(__instance, out _))
            return;

        Rectangle bounds = ButtonBounds();
        bool hover = bounds.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        TranslationEditorMenu.DrawButton(__0, bounds, I18n.Get("editor.button"), hover, enabled: true);
        if (hover)
            __instance.drawMouse(__0);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        IClickableMenu? current = CurrentMenu();
        if (e.Button == SButton.Escape)
        {
            if (activeExport.Value is { } export && ReferenceEquals(export, current))
            {
                helper.Input.Suppress(e.Button);
                export.Back();
            }
            else if (activeEditor.Value is { } editor && ReferenceEquals(editor, current))
            {
                helper.Input.Suppress(e.Button);
                editor.Cancel();
            }
            return;
        }
        if (e.Button != SButton.MouseLeft || current is not { } menu || !CanOpen(menu, out Page page)
            || !ButtonBounds().Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true)))
            return;

        helper.Input.Suppress(e.Button);
        try
        {
            Open(menu, page);
            Game1.playSound("smallSelect");
        }
        catch (Exception error)
        {
            monitor.Log(I18n.Get("editor.open-failed") + Environment.NewLine + error, LogLevel.Error);
        }
    }

    private void Open(IClickableMenu menu, Page page)
    {
        IManifest mod = page.Mod!;
        OpenEditor(menu, new ExportMod(mod.UniqueID, mod.Name),
            ReadBindings(menu, page).ToLookup(binding => binding.Source, StringComparer.Ordinal));
    }

    public void OpenForMod(IClickableMenu menu, ExportMod mod, IEnumerable<string> sources, Action refresh)
    {
        OpenEditor(menu, mod, sources.Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal).OrderBy(source => source, StringComparer.CurrentCulture).ToArray(),
            _ => { }, refresh);
    }

    private void OpenEditor(IClickableMenu menu, ExportMod mod, ILookup<string, Binding> bindings)
    {
        OpenEditor(menu, mod, bindings.Select(group => group.Key).ToArray(), sources =>
        {
            foreach (string source in sources)
            {
                string translation = translations.Resolve(mod.Id, source);
                foreach (Binding binding in bindings[source])
                    binding.Update(translation);
            }
        }, () => { });
    }

    private void OpenEditor(IClickableMenu menu, ExportMod mod, IReadOnlyList<string> originalTexts,
        Action<IEnumerable<string>> refreshBindings, Action afterClose)
    {
        string locale = translations.GetOverrideLocale(mod.Id);
        string markerLocale = translations.GetExportLocale();
        var ignored = translations.GetIgnoredSources(mod.Id);
        NativeTextIndex? localized = translations.ReadLocalizedTextIndex(mod.Id);
        var sources = originalTexts.Select(source =>
        {
            string? translation = translations.GetLocalTranslation(mod.Id, source);
            var status = TranslationStore.GetStatus(source, translation, localized, locale, ignored: false);
            return new TranslationEditorMenu.Entry(source, translation ?? source, status, ignored.Contains(source));
        }).ToArray();
        var parent = menu.GetParentMenu();
        bool titleScreen = Game1.activeClickableMenu is TitleMenu;
        IKeyboardSubscriber? previousSubscriber = Game1.keyboardDispatcher.Subscriber;
        IClickableMenu.onExit? previousExit = menu.exitFunction;
        int originalWidth = Game1.uiViewport.Width;
        int originalHeight = Game1.uiViewport.Height;
        bool externalOpened = false;
        var editor = new TranslationEditorMenu(mod.Name, locale, sources,
            source => translations.ResolveWithoutLocalOverride(mod.Id, source, locale),
            (changes, marks) =>
            {
                if (changes.Count > 0)
                    translations.SaveOverrides(mod.Id, locale, changes);
                if (marks.Count > 0)
                    translations.SaveIgnoredSources(mod.Id, markerLocale, marks);
                refreshBindings(changes.Keys.Union(marks.Keys, StringComparer.Ordinal));
            },
            edited =>
            {
                ExportMenu export = createExportMenu(mod, locale, () =>
                {
                    Show(edited);
                    activeExport.Value = null;
                    edited.RestoreFocus();
                });
                edited.ReleaseFocus();
                Show(export);
                activeExport.Value = export;
            },
            () =>
            {
                OpenExternal(mod.Id, locale);
                externalOpened = true;
            },
            edited =>
            {
                if (externalOpened)
                {
                    translations.ClearCache();
                    refreshBindings(originalTexts);
                }
                if (edited.ViewportChanged || originalWidth != Game1.uiViewport.Width || originalHeight != Game1.uiViewport.Height)
                {
                    Rectangle viewport = new(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height);
                    menu.gameWindowSizeChanged(viewport, viewport);
                }
                Show(menu);
                if (titleScreen)
                    menu.exitFunction = previousExit;
                Game1.keyboardDispatcher.Subscriber = previousSubscriber;
                activeEditor.Value = null;
                afterClose();
            },
            error => monitor.Log(error.ToString(), LogLevel.Error));

        Show(editor);
        activeEditor.Value = editor;
        editor.Focus();

        void Show(IClickableMenu next)
        {
            if (titleScreen)
                TitleMenu.subMenu = next;
            else if (parent is not null)
                parent.SetChildMenu(next);
            else
                Game1.activeClickableMenu = next;
        }

    }

    public Rectangle GetTableBounds(IClickableMenu menu)
    {
        object value = table.GetValue(menu)!;
        var position = (Vector2)tablePosition.GetValue(value)!;
        var size = (Vector2)tableSize.GetValue(value)!;
        return new Rectangle((int)position.X, (int)position.Y, (int)size.X, (int)size.Y);
    }

    public void ClampTableScroll(IClickableMenu menu)
    {
        object value = table.GetValue(menu)!;
        Type type = value.GetType();
        // Refresh GMCM's scroll range after the list's height changes.
        AccessTools.Method(type, "Update", [typeof(bool)])!.Invoke(value, [false]);
        object scrollbar = Property(type, "Scrollbar").GetValue(value)!;
        AccessTools.Method(scrollbar.GetType(), "ScrollBy", [typeof(int)])!.Invoke(scrollbar, [0]);
    }

    private void OpenExternal(string modId, string locale)
    {
        string path = OverrideFile.GetPath(translations.ModDirectory, locale, modId);
        if (!File.Exists(path))
            OverrideFile.WriteAtomically(path, new Dictionary<string, string?>(), overwrite: false);

        using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnUpdate(object? sender, UpdateTickedEventArgs e)
    {
        IClickableMenu? current = CurrentMenu();
        if (activeEditor.Value is { } editor && !ReferenceEquals(editor, current)
            && (activeExport.Value is not { } export || !ReferenceEquals(export, current)))
        {
            editor.ReleaseFocus();
            activeEditor.Value = null;
            activeExport.Value = null;
        }
    }

    internal static IClickableMenu? CurrentMenu()
    {
        IClickableMenu? menu = Game1.activeClickableMenu;
        if (menu is TitleMenu)
            return TitleMenu.subMenu;
        while (menu?.GetChildMenu() is { } child)
            menu = child;
        return menu;
    }

    private IEnumerable<Binding> ReadBindings(IClickableMenu menu, Page page)
    {
        IManifest mod = page.Mod!;
        if (GetPageTitle(menu) is { Length: > 0 } title)
            yield return new(title, value => SetTitle(menu, mod.Name + " > " + value));
        else
            yield return new(mod.Name, value => SetTitle(menu, value));

        foreach ((object option, Array elements) in page.Rows)
        {
            if (elements.Length == 0)
                continue;
            var text = readOption(option);
            object first = elements.GetValue(0)!;
            bool paragraph = paragraphType.IsInstanceOfType(option);
            if (labelType.IsInstanceOfType(first))
            {
                if (!string.IsNullOrWhiteSpace(text.Name))
                    yield return new(text.Name, value => labelText.SetValue(first, paragraph ? WrapParagraph(menu, value) : value));
                if (!string.IsNullOrWhiteSpace(text.Tooltip))
                    yield return new(text.Tooltip, value => SetTooltip(menu, first, value));
            }
            if (text.Choices.Length > 0 && elements.Cast<object>().FirstOrDefault(dropdownType.IsInstanceOfType) is { } dropdown)
            {
                var labels = (string[])dropdownLabels.GetValue(dropdown)!;
                for (int index = 0; index < labels.Length; index++)
                {
                    int choice = index;
                    if (!string.IsNullOrWhiteSpace(text.Choices[choice]))
                        yield return new(text.Choices[choice], value => labels[choice] = value);
                }
            }
        }
    }

    private void SetTitle(IClickableMenu menu, string value)
    {
        var elements = (IEnumerable)children.GetValue(ui.GetValue(menu))!;
        object title = elements.Cast<object>().First(labelType.IsInstanceOfType);
        labelText.SetValue(title, value);
        int width = (int)labelWidth.GetValue(title)!;
        var position = (Vector2)labelPosition.GetValue(title)!;
        labelPosition.SetValue(title, new Vector2((Game1.uiViewport.Width - width) / 2f, position.Y));
        titleWidth.SetValue(menu, width);
    }

    private void SetTooltip(IClickableMenu menu, object label, string value)
    {
        elementData.SetValue(label, value);
        if (AccessTools.Property(labelType, "ScreenReaderDescription") is { } description)
            description.SetValue(label, value);
        var labels = (IList)hovers.GetValue(menu)!;
        if (!labels.Contains(label))
            labels.Add(label);
    }

    private string WrapParagraph(IClickableMenu menu, string text)
    {
        var size = (Vector2)tableSize.GetValue(table.GetValue(menu))!;
        return Game1.parseText(text, Game1.smallFont, (int)size.X);
    }

    private sealed class Page
    {
        public IManifest? Mod { get; set; }
        public object? PendingOption { get; set; }
        public List<(object Option, Array Elements)> Rows { get; } = new();
    }

    private sealed record Binding(string Source, Action<string> Update);
}
