using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace GMCMConfigTextLocalizer;

internal sealed class TranslationEditorMenu(
    string modName,
    string locale,
    IReadOnlyList<TranslationEditorMenu.Entry> entries,
    Func<string, string> inheritedTranslation,
    Action<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, bool>> apply,
    Action<TranslationEditorMenu> openExport,
    Action openExternal,
    Action<TranslationEditorMenu> close,
    Action<Exception> reportError) : IClickableMenu, IKeyboardSubscriber
{
    private const int RowHeight = 52;
    private readonly List<(Rectangle Bounds, Action Activate)> buttons = new();
    private readonly Dictionary<string, string> changes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> marks = new(StringComparer.Ordinal);
    private int pendingCount = entries.Count(entry => !entry.Ignored && entry.Status == TranslationStatus.Pending);
    private List<Entry> filtered = entries.Where(entry => !entry.Ignored && entry.Status == TranslationStatus.Pending).ToList();
    private EntryFilter filter = EntryFilter.Pending;
    private string query = "";
    private bool searchFocused;
    private int selection;
    private int firstRow;
    private int visibleRows = 1;
    private int caret;
    private bool allSelected;
    private string? status;

    public bool Selected { get; set; }
    public bool ViewportChanged { get; private set; }

    private Entry Current => filtered[selection];
    private string Draft => GetDraft(Current);
    private string InputText => searchFocused ? query : Draft;
    private string GetDraft(Entry entry) => changes.GetValueOrDefault(entry.Source, entry.Translation);
    private bool IsIgnored(Entry entry) => marks.GetValueOrDefault(entry.Source, entry.Ignored);

    public void Focus()
    {
        FocusInput(search: filtered.Count == 0);
    }

    public void RestoreFocus()
    {
        if (entries.Count > 0)
            Game1.keyboardDispatcher.Subscriber = this;
    }

    public void ReleaseFocus()
    {
        if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, this))
            Game1.keyboardDispatcher.Subscriber = null;
    }

    public override bool readyToClose() => false;

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        ViewportChanged = true;
    }

    public override void draw(SpriteBatch batch)
    {
        int width = Math.Min(1080, Game1.uiViewport.Width - 24);
        int height = Math.Min(680, Game1.uiViewport.Height - 24);
        var box = new Rectangle((Game1.uiViewport.Width - width) / 2, (Game1.uiViewport.Height - height) / 2, width, height);
        var mouse = new Point(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        buttons.Clear();
        batch.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.86f);
        drawTextureBox(batch, box.X, box.Y, box.Width, box.Height, Color.White);

        int left = box.Left + 28;
        int top = box.Top + 68;
        int bottom = box.Bottom - 62;
        int listWidth = Math.Clamp(width / 3, 160, 340);
        int detailsX = left + listWidth + 20;
        int detailsWidth = box.Right - 28 - detailsX;
        string exportLabel = I18n.Get("editor.export");
        string externalLabel = I18n.Get("editor.external");
        int externalWidth = (int)Math.Ceiling(Game1.smallFont.MeasureString(externalLabel).X) + 28;
        int exportWidth = (int)Math.Ceiling(Game1.smallFont.MeasureString(exportLabel).X) + 28;
        int externalX = box.Right - 28 - externalWidth;
        int exportX = externalX - 10 - exportWidth;
        Text(batch, Fit(I18n.Get("editor.title", new { mod = modName, locale }), exportX - left - 12), left, box.Top + 26);
        AddButton(new Rectangle(exportX, box.Top + 19, exportWidth, 38), exportLabel,
            () => TryAction(() => openExport(this), "command.export.failed"));
        AddButton(new Rectangle(externalX, box.Top + 19, externalWidth, 38), externalLabel,
            () => TryAction(openExternal, "editor.external-failed"));
        if (entries.Count == 0)
        {
            Text(batch, I18n.Get("editor.no-sources"), left, top);
            AddButton(new Rectangle(left, bottom, 130, 40), I18n.Get("export.close"), Cancel);
            if (status is not null)
                Text(batch, Fit(status, width - 200), left + 146, bottom + 9);
            drawMouse(batch);
            return;
        }
        string[] tabLabels = [
            I18n.Get("editor.pending-count", new { count = pendingCount }),
            I18n.Get("editor.unnecessary"),
            I18n.Get("editor.all-count", new { count = entries.Count })
        ];
        int[] tabWidths = tabLabels.Select(label => (int)Math.Ceiling(Game1.smallFont.MeasureString(label).X) + 18).ToArray();
        int tabX = left;
        int tabY = top;
        for (int index = 0; index < tabLabels.Length; index++)
        {
            int tabWidth = Math.Min(listWidth, tabWidths[index]);
            if (tabX + tabWidth > left + listWidth)
            {
                tabX = left;
                tabY += 40;
            }
            DrawFilter(new Rectangle(tabX, tabY, tabWidth, 34), tabLabels[index], (EntryFilter)index);
            tabX += tabWidth + 5;
        }
        var search = new Rectangle(left, tabY + 42, listWidth, 36);
        drawTextureBox(batch, search.X, search.Y, search.Width, search.Height, searchFocused ? Color.Wheat : Color.White);
        if (searchFocused || query.Length > 0)
            DrawInput(batch, search, query, searchFocused);
        else
            Text(batch, Fit(I18n.Get("editor.search"), search.Width - 24), search.X + 12, search.Y + 6, 0.65f);
        buttons.Add((search, () => FocusInput(search: true)));

        int listTop = search.Bottom + 8;
        visibleRows = Math.Max(1, (bottom - listTop - 44) / RowHeight);
        firstRow = Math.Clamp(firstRow, 0, Math.Max(0, filtered.Count - visibleRows));

        for (int index = firstRow; index < Math.Min(filtered.Count, firstRow + visibleRows); index++)
        {
            int row = index;
            Entry entry = filtered[row];
            var bounds = new Rectangle(left, listTop + (row - firstRow) * RowHeight, listWidth, RowHeight - 2);
            if (row == selection || bounds.Contains(mouse))
                batch.Draw(Game1.staminaRect, bounds, Color.Wheat * 0.72f);
            Text(batch, Fit(entry.Source, listWidth - 16), left + 8, bounds.Top + 2);
            DrawEntryStatus(entry, left + 8, bounds.Top + 25);
            buttons.Add((bounds, () => Select(row)));
        }

        if (filtered.Count == 0)
        {
            string message = query.Trim().Length > 0 ? "editor.no-results"
                : filter == EntryFilter.Unnecessary ? "editor.no-unnecessary" : "editor.no-pending";
            DrawWrapped(batch, I18n.Get(message), left + 8, listTop + 8, listWidth - 16, 3);
        }
        else
        {
            int listBottom = listTop + visibleRows * RowHeight;
            AddButton(new Rectangle(left, listBottom + 2, 42, 34), "<", () => Scroll(-visibleRows), firstRow > 0);
            AddButton(new Rectangle(left + 49, listBottom + 2, 42, 34), ">", () => Scroll(visibleRows), firstRow + visibleRows < filtered.Count);
        }

        if (filtered.Count > 0)
            DrawDetails();

        int buttonWidth = Math.Max(120, (int)Game1.smallFont.MeasureString(I18n.Get("editor.apply")).X + 28);
        AddButton(new Rectangle(box.Right - 28 - buttonWidth, bottom, buttonWidth, 40), I18n.Get("editor.apply"), Apply,
            changes.Count > 0 || marks.Count > 0);
        AddButton(new Rectangle(left, bottom, 130, 40), I18n.Get("editor.cancel"), Cancel);
        if (status is not null)
            Text(batch, Fit(status, width - buttonWidth - 230), left + 146, bottom + 9);
        drawMouse(batch);

        void DrawDetails()
        {
            int lineHeight = Game1.smallFont.LineSpacing;
            int sourceLines = Math.Clamp((bottom - top - 182) / Math.Max(1, 2 * lineHeight), 1, 5);
            int sourceY = top + 38;
            string copyLabel = I18n.Get("editor.copy-source");
            int copyWidth = (int)Math.Ceiling(Game1.smallFont.MeasureString(copyLabel).X) + 28;
            Text(batch, I18n.Get("editor.source"), detailsX, top);
            AddButton(new Rectangle(detailsX + detailsWidth - copyWidth, top, copyWidth, 32), copyLabel, CopySource);
            DrawWrapped(batch, Current.Source, detailsX, sourceY, detailsWidth, sourceLines);
            int editLabelY = sourceY + sourceLines * lineHeight + 10;
            Text(batch, I18n.Get("editor.translation"), detailsX, editLabelY);
            string markLabel = I18n.Get(IsIgnored(Current) ? "editor.unmark" : "editor.mark");
            int markWidth = (int)Math.Ceiling(Game1.smallFont.MeasureString(markLabel).X) + 24;
            AddButton(new Rectangle(detailsX + detailsWidth - markWidth, editLabelY - 4, markWidth, 30), markLabel, ToggleMark);
            var input = new Rectangle(detailsX, editLabelY + 30, detailsWidth, 42);
            drawTextureBox(batch, input.X, input.Y, input.Width, input.Height, Color.White);
            DrawInput(batch, input, Draft, !searchFocused);
            buttons.Add((input, FocusAtEnd));

            int previewY = input.Bottom + 10;
            Text(batch, I18n.Get("editor.preview"), detailsX, previewY);
            string preview = IsIgnored(Current) ? Current.Source
                : string.IsNullOrWhiteSpace(Draft) ? inheritedTranslation(Current.Source) : Draft;
            DrawWrapped(batch, preview, detailsX, previewY + 25, detailsWidth, Math.Max(0, (bottom - previewY - 35) / lineHeight));

            if (new Rectangle(detailsX, sourceY, detailsWidth, sourceLines * lineHeight).Contains(mouse))
                drawHoverText(batch, Game1.parseText(Current.Source, Game1.smallFont, detailsWidth), Game1.smallFont);
        }

        void DrawFilter(Rectangle bounds, string label, EntryFilter category)
        {
            bool active = filter == category;
            DrawButton(batch, bounds, Fit(label, bounds.Width - 16), active || bounds.Contains(mouse), enabled: true);
            buttons.Add((bounds, () =>
            {
                filter = category;
                FilterEntries();
            }));
        }

        void DrawEntryStatus(Entry entry, int x, int y)
        {
            string detail = IsIgnored(entry) ? I18n.Get("editor.unnecessary")
                : changes.TryGetValue(entry.Source, out string? draft)
                ? I18n.Get("editor.unsaved") + (string.IsNullOrWhiteSpace(draft) ? "" : " · " + draft)
                : entry.Status switch
                {
                    TranslationStatus.Translated => entry.Translation,
                    TranslationStatus.Native => I18n.Get("editor.native"),
                    TranslationStatus.Localized => I18n.Get("editor.localized"),
                    TranslationStatus.Unnecessary => I18n.Get("editor.unnecessary"),
                    _ => I18n.Get("editor.pending")
                };
            if (marks.ContainsKey(entry.Source))
                detail = I18n.Get("editor.unsaved") + " · " + detail;
            Text(batch, Fit(detail, listWidth - 16), x, y, entry.Status == TranslationStatus.Pending ? 0.95f : 0.7f);
        }

        void AddButton(Rectangle bounds, string label, Action activate, bool enabled = true)
        {
            DrawButton(batch, bounds, label, bounds.Contains(mouse), enabled);
            if (enabled)
                buttons.Add((bounds, activate));
        }
    }

    private void DrawInput(SpriteBatch batch, Rectangle bounds, string text, bool hasFocus)
    {
        // Keep the caret visible in the single-line input area.
        int positionInText = hasFocus ? caret : 0;
        int start = positionInText == 0 ? 0 : text.LastIndexOf('\n', positionInText - 1) + 1;
        int end = text.IndexOf('\n', positionInText);
        if (end < 0)
            end = text.Length;
        int available = bounds.Width - 32;
        while (start < positionInText && Game1.smallFont.MeasureString(text[start..positionInText]).X > available)
            start = NextCharacter(text, start);
        string visible = Fit(text[start..end], available);
        var position = new Vector2(bounds.X + 12, bounds.Y + 8);
        float textWidth = Game1.smallFont.MeasureString(visible).X;
        if (hasFocus && allSelected)
            batch.Draw(Game1.staminaRect, new Rectangle((int)position.X, (int)position.Y, (int)textWidth, Game1.smallFont.LineSpacing), Color.Wheat);
        Utility.drawTextWithShadow(batch, visible, Game1.smallFont, position, Game1.textColor);
        if (hasFocus && Selected && !allSelected && Environment.TickCount64 / 500 % 2 == 0)
        {
            int cursorX = (int)(position.X + Game1.smallFont.MeasureString(text[start..caret]).X);
            batch.Draw(Game1.staminaRect, new Rectangle(cursorX, (int)position.Y, 2, Game1.smallFont.LineSpacing), Game1.textColor);
        }
    }

    internal static void DrawButton(SpriteBatch batch, Rectangle bounds, string label, bool hover, bool enabled)
    {
        drawTextureBox(batch, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            enabled ? (hover ? Color.Wheat : Color.White) : Color.White * 0.55f);
        Vector2 size = Game1.smallFont.MeasureString(label);
        Text(batch, label, (int)(bounds.Center.X - size.X / 2), (int)(bounds.Center.Y - size.Y / 2), enabled ? 1f : 0.55f);
    }

    private static void Text(SpriteBatch batch, string text, int x, int y, float opacity = 1f) =>
        Utility.drawTextWithShadow(batch, text, Game1.smallFont, new Vector2(x, y), Game1.textColor * opacity);

    private static void DrawWrapped(SpriteBatch batch, string text, int x, int y, int width, int lineCount)
    {
        string[] lines = Game1.parseText(text, Game1.smallFont, width).Split('\n');
        for (int line = 0; line < Math.Min(lineCount, lines.Length); line++)
        {
            string value = lines[line] + (line == lineCount - 1 && lines.Length > lineCount ? "…" : "");
            Text(batch, Fit(value, width), x, y + line * Game1.smallFont.LineSpacing);
        }
    }

    internal static string Fit(string text, int width)
    {
        string line = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (Game1.smallFont.MeasureString(line).X <= width)
            return line;
        int end = line.Length;
        while (end > 0 && Game1.smallFont.MeasureString(line[..end] + "…").X > width)
            end = PreviousCharacter(line, end);
        return line[..end] + "…";
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        foreach ((Rectangle bounds, Action activate) in buttons)
        {
            if (!bounds.Contains(x, y))
                continue;
            if (playSound)
                Game1.playSound("smallSelect");
            activate();
            return;
        }
    }

    public override void receiveScrollWheelAction(int direction) => Scroll(-Math.Sign(direction) * 3);

    public override void receiveKeyPress(Keys key)
    {
        switch (key)
        {
            case Keys.Escape:
                Cancel();
                break;
            case Keys.PageUp:
            case Keys.PageDown:
                if (filtered.Count > 0)
                    Select(Math.Clamp(selection + (key == Keys.PageUp ? -visibleRows : visibleRows), 0, filtered.Count - 1));
                break;
            default:
                if (entries.Count > 0)
                    EditKey(key);
                break;
        }
    }

    private void FilterEntries()
    {
        Entry? previous = filtered.Count > 0 ? Current : null;
        string term = query.Trim();
        filtered = entries.Where(entry => MatchesFilter(entry)
            && (term.Length == 0 || entry.Source.Contains(term, StringComparison.OrdinalIgnoreCase)
                || GetDraft(entry).Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        selection = previous is null ? 0 : Math.Max(0, filtered.IndexOf(previous));
        firstRow = Math.Max(0, selection - visibleRows + 1);
        if (!searchFocused)
            Focus();
    }

    private bool MatchesFilter(Entry entry) => filter switch
    {
        EntryFilter.Pending => !IsIgnored(entry) && entry.Status == TranslationStatus.Pending,
        EntryFilter.Unnecessary => IsIgnored(entry) || entry.Status == TranslationStatus.Unnecessary,
        _ => true
    };

    private void ToggleMark()
    {
        Entry entry = Current;
        bool marked = !IsIgnored(entry);
        if (marked == entry.Ignored)
            marks.Remove(entry.Source);
        else
            marks[entry.Source] = marked;

        pendingCount = entries.Count(candidate => !IsIgnored(candidate) && candidate.Status == TranslationStatus.Pending);
        int oldSelection = selection;
        FilterEntries();
        if (!MatchesFilter(entry) && filtered.Count > 0)
            selection = Math.Min(oldSelection, filtered.Count - 1);
        firstRow = Math.Max(0, selection - visibleRows + 1);
        status = null;
        Focus();
    }

    private void Scroll(int amount) => firstRow = Math.Clamp(firstRow + amount, 0, Math.Max(0, filtered.Count - visibleRows));

    private void Select(int index)
    {
        selection = index;
        firstRow = Math.Clamp(firstRow, Math.Max(0, selection - visibleRows + 1), selection);
        FocusAtEnd();
    }

    private void FocusAtEnd() => FocusInput(search: false);

    private void FocusInput(bool search)
    {
        searchFocused = search;
        allSelected = false;
        caret = entries.Count > 0 ? InputText.Length : 0;
        RestoreFocus();
    }

    private void Apply()
    {
        if (TryAction(() => apply(changes, marks), "editor.save-failed"))
            Cancel();
    }

    private void CopySource()
    {
        if (TryAction(() =>
        {
            if (!DesktopClipboard.SetText(Current.Source))
                throw new InvalidOperationException("The system clipboard is unavailable.");
        }, "editor.copy-source-failed"))
            status = I18n.Get("editor.source-copied");
    }

    private bool TryAction(Action action, string errorKey)
    {
        status = null;
        try
        {
            action();
            return true;
        }
        catch (Exception error)
        {
            reportError(error);
            status = I18n.Get(errorKey);
            return false;
        }
    }

    public void Cancel()
    {
        ReleaseFocus();
        close(this);
    }

    public void RecieveTextInput(char inputChar) => RecieveTextInput(inputChar.ToString());

    public void RecieveTextInput(string text)
    {
        if (Selected)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
            Replace(searchFocused ? normalized.Replace('\n', ' ') : normalized);
        }
    }

    public void RecieveCommandInput(char command)
    {
        if (!Selected)
            return;
        if (command == '\r')
        {
            if (searchFocused && filtered.Count > 0)
                FocusAtEnd();
            else if (!searchFocused)
                Replace("\n");
        }
        else if (command == '\b' && (allSelected || caret > 0))
        {
            string text = InputText;
            int start = allSelected ? 0 : PreviousCharacter(text, caret);
            SetInput(text.Remove(start, (allSelected ? text.Length : caret) - start), start);
        }
    }

    public void RecieveSpecialInput(Keys key) { }

    private void EditKey(Keys key)
    {
        if (!Selected)
            return;
        KeyboardState keyboard = Game1.input.GetKeyboardState();
        if (key == Keys.A && (keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl)
            || keyboard.IsKeyDown(Keys.LeftWindows) || keyboard.IsKeyDown(Keys.RightWindows)))
        {
            allSelected = true;
            return;
        }
        string text = InputText;
        if (key == Keys.Delete && (allSelected || caret < text.Length))
        {
            int start = allSelected ? 0 : caret;
            SetInput(text.Remove(start, (allSelected ? text.Length : NextCharacter(text, caret)) - start), start);
            return;
        }

        switch (key)
        {
            case Keys.Left:
                caret = allSelected ? 0 : PreviousCharacter(text, caret);
                break;
            case Keys.Right:
                caret = allSelected ? text.Length : NextCharacter(text, caret);
                break;
            case Keys.Home:
                caret = 0;
                break;
            case Keys.End:
                caret = text.Length;
                break;
            default:
                return;
        }
        allSelected = false;
    }

    private void Replace(string text)
    {
        int start = allSelected ? 0 : caret;
        string value = allSelected ? text : InputText.Insert(start, text);
        SetInput(value, start + text.Length);
    }

    private void SetInput(string value, int position)
    {
        caret = position;
        allSelected = false;
        status = null;
        if (searchFocused)
        {
            query = value;
            FilterEntries();
            return;
        }
        if (value == Current.Translation)
            changes.Remove(Current.Source);
        else
            changes[Current.Source] = value;
    }

    private static int PreviousCharacter(string text, int index) =>
        Math.Max(0, index - (index > 1 && char.IsSurrogatePair(text, index - 2) ? 2 : 1));

    private static int NextCharacter(string text, int index) =>
        Math.Min(text.Length, index + (index + 1 < text.Length && char.IsSurrogatePair(text, index) ? 2 : 1));

    private enum EntryFilter { Pending, Unnecessary, All }
    internal sealed record Entry(string Source, string Translation, TranslationStatus Status, bool Ignored);
}
