using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace GMCMConfigTextLocalizer;

internal sealed record ExportMod(string Id, string Name);
internal sealed record ExportText(string ModId, string Source);

internal sealed class ExportMenu(
    IReadOnlyList<ExportMod> mods,
    string locale,
    Func<IReadOnlySet<string>, IEnumerable<ExportText>> readSources,
    TranslationStore translations,
    Func<IReadOnlyList<ExportText>, int> export,
    Action close,
    Action<Exception> reportError,
    bool singleMod) : IClickableMenu
{
    private const int RowHeight = 62;
    private const int ModRowHeight = 47;
    private readonly HashSet<string> selectedMods = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ExportText> selectedTexts = new();
    private readonly Dictionary<string, ExportMod> modNames = mods.ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
    private readonly List<MenuButton> buttons = new();
    private List<ExportText> entries = new();
    private IReadOnlyList<ExportText> pending = [];
    private IReadOnlyList<ExportText> scopedEntries = [];
    private IReadOnlyList<ExportText> visibleEntries = [];
    private bool pendingOnly = true;
    private int page;
    private int pageSize = 1;
    private int modPage;
    private int modPageSize = 1;
    private Rectangle sidebar;
    private string status = "";

    public void Load()
    {
        selectedMods.UnionWith(modNames.Keys);
        LoadEntries();
    }

    public override bool readyToClose() => !singleMod;

    public override void draw(SpriteBatch batch)
    {
        int w = Math.Min(1160, Game1.uiViewport.Width - 32);
        int h = Math.Min(780, Game1.uiViewport.Height - 32);
        var box = new Rectangle((Game1.uiViewport.Width - w) / 2, (Game1.uiViewport.Height - h) / 2, w, h);
        var mouse = new Point(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        buttons.Clear();
        batch.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.55f);
        drawTextureBox(batch, box.X, box.Y, box.Width, box.Height, Color.White);
        int left = box.Left + 26;
        int right = box.Right - 26;
        int top = box.Top + 90;
        int bottom = box.Bottom - 68;
        int sidebarWidth = Math.Min(308, (right - left) * 32 / 100);
        sidebar = new Rectangle(left, top, sidebarWidth, bottom - top + 42);
        int x = sidebar.Right + 26;
        int innerWidth = right - x;
        int selectedCount = scopedEntries.Count(selectedTexts.Contains);
        string heading = I18n.Get("editor.title", new { mod = I18n.Get("export.custom"), locale });
        DrawText(batch, Preview(heading, right - left), left, box.Top + 24, Game1.textColor);
        DrawText(batch, Preview(GetSelectionSummary(selectedCount), innerWidth), x, top - 34, Game1.textColor);
        batch.Draw(Game1.staminaRect, new Rectangle(sidebar.Right + 12, top - 29, 2, bottom - top + 66), Game1.textColor * 0.24f);

        string? hoverText = null;
        DrawModFilters();
        DrawSelectionRows();

        void DrawModFilters()
        {
            string title = I18n.Get("export.mod-filter");
            DrawText(batch, Preview(title, sidebar.Width), left, top - 34, Game1.textColor);
            var all = new Rectangle(left, top, sidebar.Width, 38);
            string label = I18n.Get("export.mod-filter-all", new { count = selectedMods.Count, total = mods.Count });
            DrawCheckboxRow(all, label, null, mods.Count > 0 && selectedMods.Count == mods.Count, SelectAllMods, mods.Count > 0);

            int rowsTop = all.Bottom + 8;
            modPageSize = Math.Max(1, (bottom - rowsTop - 6) / ModRowHeight);
            modPage = Math.Min(modPage, LastModPage);
            int end = Math.Min(mods.Count, (modPage + 1) * modPageSize);
            for (int index = modPage * modPageSize; index < end; index++)
            {
                ExportMod mod = mods[index];
                var row = new Rectangle(left, rowsTop + (index % modPageSize) * ModRowHeight, sidebar.Width, ModRowHeight - 2);
                DrawCheckboxRow(row, mod.Name, null, selectedMods.Contains(mod.Id), () => ToggleMod(mod.Id));
                if (row.Contains(mouse))
                    hoverText = mod.Name + Environment.NewLine + mod.Id;
            }
            DrawPager(sidebar.Center.X, modPage, LastModPage, MoveModPage, bottom);
        }

        void DrawSelectionRows()
        {
            int smallWidth = Math.Min(156, (innerWidth - 14) / 2);
            DrawButton(batch, mouse, new Rectangle(x, top, smallWidth, 38), I18n.Get("export.select-all"),
                () => selectedTexts.UnionWith(visibleEntries), visibleEntries.Count > 0);
            DrawButton(batch, mouse, new Rectangle(x + smallWidth + 12, top, smallWidth, 38), I18n.Get("export.clear"),
                () => selectedTexts.ExceptWith(scopedEntries), selectedCount > 0);
            DrawPendingFilter(batch, mouse, x, top + 48, innerWidth);
            int rowsTop = top + 90;
            if (visibleEntries.Count == 0)
            {
                string empty = selectedMods.Count == 0 ? "export.no-mods-selected"
                    : pendingOnly ? "editor.no-pending" : "editor.no-sources";
                DrawText(batch, Preview(I18n.Get(empty), innerWidth), x, rowsTop + 5, Game1.textColor);
            }
            pageSize = Math.Max(1, (bottom - rowsTop - 33) / RowHeight);
            page = Math.Min(page, LastPage);
            int end = Math.Min(visibleEntries.Count, (page + 1) * pageSize);
            for (int index = page * pageSize; index < end; index++)
            {
                var row = new Rectangle(x, rowsTop + (index % pageSize) * RowHeight, innerWidth, RowHeight - 3);
                ExportText text = visibleEntries[index];
                DrawCheckboxRow(row, text.Source, modNames[text.ModId].Name, selectedTexts.Contains(text),
                    () => Toggle(selectedTexts, text));
                if (row.Contains(mouse))
                    hoverText = text.Source;
            }
            DrawPager(x + innerWidth / 2, page, LastPage, MovePage, bottom);
        }

        int footerWidth = Math.Min(144, Math.Max(64, (innerWidth - 192) / 2));
        DrawButton(batch, mouse, new Rectangle(x, bottom, footerWidth, 42), I18n.Get("export.close"), Back);
        DrawButton(batch, mouse, new Rectangle(right - footerWidth, bottom, footerWidth, 42),
            I18n.Get("export.button"), RunExport, selectedCount > 0);

        if (status.Length > 0)
            DrawText(batch, Preview(status, innerWidth), x, bottom - 33, Game1.textColor);
        if (hoverText is not null)
            drawHoverText(batch, Game1.parseText(hoverText, Game1.smallFont, Math.Min(innerWidth, 540)), Game1.smallFont);
        drawMouse(batch);

        void DrawCheckboxRow(Rectangle row, string label, string? detail, bool isChecked, Action toggle, bool enabled = true)
        {
            bool hover = enabled && row.Contains(mouse);
            if (hover)
                batch.Draw(Game1.staminaRect, row, Color.Wheat * 0.62f);
            const int size = TranslationManagementList.CheckboxSize;
            var checkbox = new Rectangle(row.X + 2, row.Center.Y - size / 2, size, size);
            TranslationManagementList.DrawCheckbox(batch, checkbox, isChecked, hover);
            int labelX = checkbox.Right + 8;
            int labelWidth = row.Right - labelX - 4;
            int labelY = detail is null ? row.Center.Y - Game1.smallFont.LineSpacing / 2 : row.Y + 2;
            DrawText(batch, Preview(label, labelWidth), labelX, labelY, Game1.textColor);
            if (detail is not null)
                DrawText(batch, Preview(detail, labelWidth), labelX, row.Y + 29, Game1.textColor * 0.72f);
            if (enabled)
                buttons.Add(new MenuButton(row, toggle));
        }

        void DrawPager(int center, int current, int last, Action<int> move, int y)
        {
            if (last == 0)
                return;
            DrawButton(batch, mouse, new Rectangle(center - 92, y, 40, 42), "<", () => move(-1), current > 0);
            DrawCentered(batch, $"{current + 1} / {last + 1}", new Rectangle(center - 51, y, 102, 42), Game1.textColor);
            DrawButton(batch, mouse, new Rectangle(center + 52, y, 40, 42), ">", () => move(1), current < last);
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        MenuButton? button = buttons.FirstOrDefault(button => button.Bounds.Contains(x, y));
        if (button is null)
            return;
        if (playSound)
            Game1.playSound("smallSelect");
        button.Activate();
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (direction == 0)
            return;
        int delta = -Math.Sign(direction);
        if (sidebar.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true)))
            MoveModPage(delta);
        else
            MovePage(delta);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
            Back();
        else if (key is (Keys.PageUp or Keys.PageDown))
            MovePage(key == Keys.PageUp ? -1 : 1);
    }

    private int LastPage => Math.Max(0, (visibleEntries.Count - 1) / pageSize);
    private int LastModPage => Math.Max(0, (mods.Count - 1) / modPageSize);
    private void MovePage(int delta) => page = Math.Clamp(page + delta, 0, LastPage);
    private void MoveModPage(int delta) => modPage = Math.Clamp(modPage + delta, 0, LastModPage);

    private void LoadEntries()
    {
        var ignored = selectedMods.ToDictionary(id => id, id => translations.GetIgnoredSources(id, locale), StringComparer.OrdinalIgnoreCase);
        entries = readSources(selectedMods).Where(entry => !string.IsNullOrWhiteSpace(entry.Source))
            .Where(entry => !ignored[entry.ModId].Contains(entry.Source))
            .Distinct()
            .OrderBy(entry => modNames[entry.ModId].Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Source, StringComparer.CurrentCulture)
            .ToList();
        LoadPendingEntries();
        pendingOnly = true;
        selectedTexts.Clear();
        selectedTexts.UnionWith(pending);
        RefreshFilters();
    }

    private void DrawPendingFilter(SpriteBatch batch, Point mouse, int x, int y, int width)
    {
        var box = new Rectangle(x + 2, y + 2, TranslationManagementList.CheckboxSize, TranslationManagementList.CheckboxSize);
        string label = I18n.Get("export.pending-only");
        int labelX = box.Right + TranslationManagementList.CheckboxGap;
        label = Preview(label, width - (labelX - x));
        var target = new Rectangle(x, y, labelX - x + (int)Math.Ceiling(Game1.smallFont.MeasureString(label).X), 36);
        TranslationManagementList.DrawCheckbox(batch, box, pendingOnly, target.Contains(mouse));
        DrawText(batch, label, labelX, y + 6, Game1.textColor);
        buttons.Add(new MenuButton(target, TogglePendingFilter));
    }

    private void TogglePendingFilter()
    {
        if (!pendingOnly)
            LoadPendingEntries();
        pendingOnly = !pendingOnly;
        RefreshFilters();
    }

    private void LoadPendingEntries()
    {
        var localized = entries.Select(entry => entry.ModId).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, translations.ReadLocalizedTextIndex, StringComparer.OrdinalIgnoreCase);
        pending = entries.Where(entry =>
            TranslationStore.GetStatus(entry.Source, translations.GetLocalTranslation(entry.ModId, entry.Source),
                    localized[entry.ModId], locale, ignored: false) == TranslationStatus.Pending).ToArray();
    }

    private string GetSelectionSummary(int selectedCount)
    {
        string summary = I18n.Get("export.selected-texts", new { count = selectedCount, total = scopedEntries.Count });
        if (!pendingOnly)
            return summary;

        int hidden = selectedCount - visibleEntries.Count(selectedTexts.Contains);
        return summary + " · " + I18n.Get(hidden > 0 ? "export.pending-with-hidden" : "editor.pending-count",
            new { count = visibleEntries.Count, hidden });
    }

    private void RunExport()
    {
        try
        {
            int count = export(scopedEntries.Where(selectedTexts.Contains).ToArray());
            status = count == 0 ? I18n.Get("export.no-new") : I18n.Get("export.done", new { count, locale });
        }
        catch (Exception error)
        {
            ReportFailure(error);
        }
    }

    private void ReportFailure(Exception error)
    {
        reportError(error);
        status = I18n.Get("command.export.failed");
    }

    public void Back() => close();

    private void RefreshFilters()
    {
        scopedEntries = entries.Where(entry => selectedMods.Contains(entry.ModId)).ToArray();
        visibleEntries = pendingOnly ? pending.Where(entry => selectedMods.Contains(entry.ModId)).ToArray() : scopedEntries;
        page = 0;
        buttons.Clear();
    }

    private void SelectAllMods()
    {
        if (selectedMods.Count == mods.Count)
            selectedMods.Clear();
        else
            selectedMods.UnionWith(modNames.Keys);
        RefreshFilters();
    }

    private void ToggleMod(string id)
    {
        Toggle(selectedMods, id);
        RefreshFilters();
    }

    private static void Toggle<T>(HashSet<T> selected, T value)
    {
        if (!selected.Add(value))
            selected.Remove(value);
    }

    private void DrawButton(SpriteBatch batch, Point mouse, Rectangle box, string text, Action activate, bool enabled = true)
    {
        drawTextureBox(batch, box.X, box.Y, box.Width, box.Height,
            enabled ? (box.Contains(mouse) ? Color.Wheat : Color.White) : Color.White * 0.55f);
        DrawCentered(batch, Preview(text, box.Width - 14), box, Game1.textColor * (enabled ? 1f : 0.55f));
        if (enabled)
            buttons.Add(new MenuButton(box, activate));
    }

    private static void DrawCentered(SpriteBatch batch, string text, Rectangle box, Color color)
    {
        Vector2 size = Game1.smallFont.MeasureString(text);
        DrawText(batch, text, box.Center.X - size.X / 2, box.Center.Y - size.Y / 2, color);
    }

    private static void DrawText(SpriteBatch batch, string text, float x, float y, Color color)
    {
        Utility.drawTextWithShadow(batch, text, Game1.smallFont, new Vector2(x, y), color);
    }

    private static string Preview(string text, int maxWidth)
    {
        string line = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (Game1.smallFont.MeasureString(line).X <= maxWidth)
            return line;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(line);
        int end = 0;
        while (elements.MoveNext())
        {
            int next = elements.ElementIndex + elements.GetTextElement().Length;
            if (Game1.smallFont.MeasureString(line[..next] + "…").X > maxWidth)
                break;
            end = next;
        }
        return line[..end] + "…";
    }

    private sealed record MenuButton(Rectangle Bounds, Action Activate);
}
