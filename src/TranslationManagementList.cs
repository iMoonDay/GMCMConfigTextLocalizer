using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace GMCMConfigTextLocalizer;

internal sealed class TranslationManagementList
{
    private const int RowHeight = 45;
    private const int HeaderHeight = 76;
    internal const int CheckboxSize = 32;
    internal const int CheckboxGap = 12;
    private readonly List<(Rectangle Bounds, Action Activate)> buttons = new();
    private IReadOnlyList<Row> rows = [];
    private IReadOnlyList<Row> visibleRows = [];
    private bool showAll;

    public int Height => HeaderHeight + Math.Max(1, visibleRows.Count) * RowHeight;

    public void SetRows(IReadOnlyList<Row> newRows)
    {
        rows = newRows;
        UpdateVisibleRows();
    }

    private void UpdateVisibleRows()
    {
        visibleRows = showAll ? rows : rows.Where(row => row.Count > 0).ToArray();
        buttons.Clear();
    }

    public Action? FindAction(Point mouse) => buttons.FirstOrDefault(button => button.Bounds.Contains(mouse)).Activate;

    public void Draw(SpriteBatch batch, Vector2 position, Rectangle table, Func<string, bool> isEnabled,
        Action<bool, IEnumerable<string>> selectAll, Action<Row> toggle, Action<Row> edit, Action clampScroll)
    {
        buttons.Clear();
        var mouse = new Point(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        int left = table.Left + 10;
        int right = table.Right - 10;
        int top = (int)position.Y;
        string editLabel = I18n.Get("management.edit");
        int editWidth = ButtonWidth(editLabel);
        int editX = right - editWidth;
        int nameX = left + CheckboxSize + CheckboxGap;
        const int countWidth = 108;
        int pendingX = editX - countWidth - 8;
        int countX = pendingX - countWidth;
        Rectangle clip = table;
        clip.Inflate(20, 20);

        if (top + HeaderHeight >= clip.Top && top < clip.Bottom)
        {
            string title = I18n.Get("management.title");
            Text(title, left, top + 9, Game1.textColor);
            CenterText(I18n.Get("management.valid-count"), countX, countWidth, top + 47, Game1.textColor);
            CenterText(I18n.Get("management.pending-count"), pendingX, countWidth, top + 47, Game1.textColor);
            int filterX = left + (int)Math.Ceiling(Game1.smallFont.MeasureString(title).X) + 20;
            string filterLabel = I18n.Get("management.show-all");
            var filterBox = new Rectangle(filterX, top + 3, CheckboxSize, CheckboxSize);
            int filterLabelX = filterBox.Right + CheckboxGap;
            int filterRight = filterLabelX + (int)Math.Ceiling(Game1.smallFont.MeasureString(filterLabel).X);
            var filterTarget = new Rectangle(filterX, filterBox.Y, filterRight - filterX, CheckboxSize);
            Checkbox(filterBox, filterTarget, showAll, () =>
            {
                showAll = !showAll;
                UpdateVisibleRows();
                if (!showAll)
                    clampScroll();
            });
            Text(filterLabel, filterLabelX, top + 9, Game1.textColor);
            if (visibleRows.Count > 0)
            {
                bool allSelected = visibleRows.All(row => isEnabled(row.Mod.Id));
                string label = I18n.Get(allSelected ? "management.select-none" : "export.select-all");
                int width = ButtonWidth(label);
                var bounds = new Rectangle(right - width, top + 2, width, 36);
                Button(bounds, label, () => selectAll(!allSelected, visibleRows.Select(row => row.Mod.Id)));
            }
        }
        int rowsTop = top + HeaderHeight;
        int firstVisible = Math.Max(0, (clip.Top - rowsTop) / RowHeight);
        for (int index = firstVisible; index < visibleRows.Count; index++)
        {
            int y = rowsTop + index * RowHeight;
            if (y >= clip.Bottom)
                break;

            Row row = visibleRows[index];
            Color color = row.Installed ? Game1.textColor : new Color(104, 104, 104);
            var checkbox = new Rectangle(left, y + 2, CheckboxSize, CheckboxSize);
            Checkbox(checkbox, checkbox, isEnabled(row.Mod.Id), () => toggle(row));

            Text(TranslationEditorMenu.Fit(row.Mod.Name, countX - nameX - 12), nameX, y + 7, color);
            CenterText(row.Count.ToString(), countX, countWidth, y + 7, color);
            CenterText(row.Pending.ToString(), pendingX, countWidth, y + 7, color);
            Button(new Rectangle(editX, y, editWidth, 36), editLabel, () => edit(row));
        }

        if (visibleRows.Count == 0)
            Text(I18n.Get("management.empty"), left, top + HeaderHeight + 7, Game1.textColor);

        void Checkbox(Rectangle box, Rectangle target, bool isChecked, Action activate)
        {
            Rectangle visible = Rectangle.Intersect(target, clip);
            DrawCheckbox(batch, box, isChecked, visible.Contains(mouse));
            if (!visible.IsEmpty)
                buttons.Add((visible, activate));
        }

        void Button(Rectangle bounds, string label, Action activate)
        {
            Rectangle visible = Rectangle.Intersect(bounds, clip);
            if (visible.IsEmpty)
                return;

            TranslationEditorMenu.DrawButton(batch, bounds, label, visible.Contains(mouse), enabled: true);
            buttons.Add((visible, activate));
        }

        void CenterText(string text, int x, int width, int y, Color color) =>
            Text(text, x + (width - (int)Game1.smallFont.MeasureString(text).X) / 2, y, color);

        void Text(string text, int x, int y, Color color) =>
            Utility.drawTextWithShadow(batch, text, Game1.smallFont, new Vector2(x, y), color);
    }

    internal static void DrawCheckbox(SpriteBatch batch, Rectangle bounds, bool isChecked, bool hover)
    {
        Rectangle source = isChecked ? OptionsCheckbox.sourceRectChecked : OptionsCheckbox.sourceRectUnchecked;
        batch.Draw(Game1.mouseCursors, bounds, source, hover ? Color.Wheat : Color.White);
    }

    private static int ButtonWidth(string label) => Math.Max(66, (int)Math.Ceiling(Game1.smallFont.MeasureString(label).X) + 30);

    internal sealed record Row(ExportMod Mod, bool Installed, int Count, int Pending);
}
