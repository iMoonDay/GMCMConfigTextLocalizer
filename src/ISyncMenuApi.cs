using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace GMCMConfigTextLocalizer;

public interface ISyncMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly);

    void AddComplexOption(IManifest mod, Func<string> name, Action<SpriteBatch, Vector2> draw,
        Func<string>? tooltip = null, Action? beforeMenuOpened = null, Action? beforeSave = null,
        Action? afterSave = null, Action? beforeReset = null, Action? afterReset = null,
        Action? beforeMenuClosed = null, Func<int>? height = null, string? fieldId = null);

    bool TryGetCurrentMenu(out IManifest? mod, out string? page);
}
