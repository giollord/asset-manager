namespace AssetManager.Core.Domain;

/// <summary>
/// A set of field values to apply to an asset. User edits mark the fields in <see cref="Asset.UserEdited"/>;
/// AI updates skip those fields unless the user explicitly asks to overwrite (spec: Manual metadata editing).
/// Null members are left unchanged.
/// </summary>
public sealed record AssetUpdate
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public AssetType? Type { get; init; }
    public IReadOnlyList<string>? Style { get; init; }
    public IReadOnlyList<ColorInfo>? Colors { get; init; }
    public LicenseInfo? License { get; init; }
    public IReadOnlyList<LinkInfo>? Links { get; init; }

    /// <summary>Applies a user edit: sets the values and marks them as user-edited.</summary>
    public void ApplyAsUser(Asset a)
    {
        foreach (var field in Apply(a, guard: EditedFields.None)) a.UserEdited |= field;
        a.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Applies an AI result; user-edited fields are kept unless <paramref name="overwriteUserEdits"/>.</summary>
    public void ApplyAsAi(Asset a, bool overwriteUserEdits = false)
    {
        var applied = Apply(a, guard: overwriteUserEdits ? EditedFields.None : a.UserEdited);
        if (overwriteUserEdits) a.UserEdited &= ~applied.Aggregate(EditedFields.None, (x, f) => x | f);
        a.UpdatedAt = DateTime.UtcNow;
    }

    private List<EditedFields> Apply(Asset a, EditedFields guard)
    {
        var applied = new List<EditedFields>();
        void Set(EditedFields f, bool has, Action set)
        {
            if (!has || guard.HasFlag(f)) return;
            set();
            applied.Add(f);
        }
        Set(EditedFields.Name, Name is not null, () => a.Name = Name!);
        Set(EditedFields.Description, Description is not null, () => a.Description = Description);
        Set(EditedFields.Type, Type is not null, () => a.Type = Type!.Value);
        Set(EditedFields.Style, Style is not null, () => a.Style = [.. Style!]);
        Set(EditedFields.Colors, Colors is not null, () => a.Colors = [.. Colors!]);
        Set(EditedFields.License, License is not null, () => { a.HasLicenseOverride = true; a.LicenseOverride = License!.Clone(); });
        Set(EditedFields.Links, Links is not null, () => a.LinksOverride = [.. Links!]);
        return applied;
    }
}

/// <summary>Field values for a source; same user-edit protection as <see cref="AssetUpdate"/>.</summary>
public sealed record SourceUpdate
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Notes { get; init; }
    public LicenseInfo? License { get; init; }
    public IReadOnlyList<LinkInfo>? Links { get; init; }

    public void ApplyAsUser(Source s)
    {
        foreach (var f in Apply(s, EditedFields.None)) s.UserEdited |= f;
    }

    public void ApplyAsAi(Source s, bool overwriteUserEdits = false)
    {
        var applied = Apply(s, overwriteUserEdits ? EditedFields.None : s.UserEdited);
        if (overwriteUserEdits) s.UserEdited &= ~applied.Aggregate(EditedFields.None, (x, f) => x | f);
    }

    private List<EditedFields> Apply(Source s, EditedFields guard)
    {
        var applied = new List<EditedFields>();
        void Set(EditedFields f, bool has, Action set)
        {
            if (!has || guard.HasFlag(f)) return;
            set();
            applied.Add(f);
        }
        Set(EditedFields.Name, DisplayName is not null, () => s.DisplayName = DisplayName!);
        Set(EditedFields.Description, Description is not null, () => s.Description = Description);
        Set(EditedFields.Notes, Notes is not null, () => s.Notes = Notes);
        Set(EditedFields.License, License is not null, () => s.License = License!.Clone());
        Set(EditedFields.Links, Links is not null, () => s.Links = [.. Links!]);
        return applied;
    }
}

/// <summary>A tag as shown on an asset, with where it came from (user, import, AI).</summary>
public sealed record TagView(string Name, TagOrigin Origin);
