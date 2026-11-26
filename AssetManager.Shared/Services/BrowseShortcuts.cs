namespace AssetManager.Shared.Services;

/// <summary>One Browse keyboard shortcut.</summary>
/// <param name="Keys">
/// Key combinations as browse.js reports them: <c>KeyboardEvent.key</c> (single characters lower-cased), prefixed with
/// "Control+" and, for named keys, "Shift+" (e.g. "Shift+ArrowLeft", "Control+a", "?").
/// </param>
/// <param name="Label">How the keys are written in the shortcut list and the README.</param>
/// <param name="Description">What the shortcut does.</param>
public sealed record Shortcut(string[] Keys, string Label, string Description);

/// <summary>
/// The Browse keyboard shortcuts (spec "Keyboard navigation", "Action shortcuts", design D10). This one table drives
/// both the keys browse.js intercepts and the "?" shortcut list, so the two cannot drift apart.
/// </summary>
public static class BrowseShortcuts
{
    public static readonly IReadOnlyList<Shortcut> All =
    [
        new(["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"], "← → ↑ ↓", "Move between assets"),
        new(["Shift+ArrowLeft", "Shift+ArrowRight", "Shift+ArrowUp", "Shift+ArrowDown"], "Shift + arrows", "Extend the selection"),
        new(["Control+a"], "Ctrl + A", "Select all results"),
        new(["Enter"], "Enter", "Open the details of the focused asset"),
        new(["Escape"], "Esc", "Close the details, or clear the selection"),
        new([" "], "Space", "Play or stop a sound, or turn a 3D model's preview"),
        new(["/"], "/", "Focus the search box"),
        new(["f"], "F", "Toggle favourite"),
        new(["0", "1", "2", "3", "4", "5"], "0 – 5", "Set the rating (0 clears it)"),
        new(["Delete"], "Delete", "Delete the selection from the catalog (asks first)"),
        new(["ContextMenu", "Shift+F10"], "Menu key, Shift + F10", "Open the menu of the focused asset (also a right click)"),
        new(["?"], "?", "Show this list"),
    ];

    /// <summary>Every key combination browse.js should hand to .NET.</summary>
    public static IReadOnlyList<string> Keys => All.SelectMany(s => s.Keys).Distinct().ToList();
}
