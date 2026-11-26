namespace AssetManager.Shared.Components;

/// <summary>
/// One section of the Settings page (design D8): its anchor id, title, the setting names search looks at, and the
/// component that renders it. Each section component declares its own <c>Title</c> and <c>SearchTerms</c>.
/// </summary>
public sealed record SettingsSection(string Id, string Title, IReadOnlyList<string> SearchTerms, Type Component)
{
    /// <summary>Case-insensitive substring match on the title and setting names; an empty query matches.</summary>
    public bool Matches(string? query)
    {
        var q = query?.Trim();
        return string.IsNullOrEmpty(q)
            || Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || SearchTerms.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The sections in page order. A new section is added here with its component's title and terms.</summary>
    public static IReadOnlyList<SettingsSection> All { get; } =
    [
        new("appearance", AppearanceSettings.Title, AppearanceSettings.SearchTerms, typeof(AppearanceSettings)),
        new("catalog", CatalogSettings.Title, CatalogSettings.SearchTerms, typeof(CatalogSettings)),
        new("backups", BackupSettings.Title, BackupSettings.SearchTerms, typeof(BackupSettings)),
        new("ai-model", AiModelSettings.Title, AiModelSettings.SearchTerms, typeof(AiModelSettings)),
        new("import", ImportSettings.Title, ImportSettings.SearchTerms, typeof(ImportSettings)),
        new("library", LibrarySettings.Title, LibrarySettings.SearchTerms, typeof(LibrarySettings)),
        new("previews", PreviewSettingsForm.Title, PreviewSettingsForm.SearchTerms, typeof(PreviewSettingsForm)),
        new("maintenance", MaintenanceSettings.Title, MaintenanceSettings.SearchTerms, typeof(MaintenanceSettings)),
        new("about", AboutSettings.Title, AboutSettings.SearchTerms, typeof(AboutSettings)),
    ];
}
