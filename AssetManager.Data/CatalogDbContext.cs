using System.Text.Json;
using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AssetManager.Data;

/// <summary>The single-file SQLite catalog (design D2/D3).</summary>
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetFile> AssetFiles => Set<AssetFile>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AssetTag> AssetTags => Set<AssetTag>();
    public DbSet<SourceTag> SourceTags => Set<SourceTag>();
    public DbSet<AssetTagOptOut> AssetTagOptOuts => Set<AssetTagOptOut>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<PreviewBlob> Previews => Set<PreviewBlob>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<StoredFileChunk> StoredFileChunks => Set<StoredFileChunk>();
    public DbSet<AssetEmbedding> Embeddings => Set<AssetEmbedding>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobItemError> JobItemErrors => Set<JobItemError>();
    public DbSet<ImportSession> ImportSessions => Set<ImportSession>();
    public DbSet<StagedSkip> StagedSkips => Set<StagedSkip>();
    public DbSet<Setting> Settings => Set<Setting>();

    /// <summary>Assets including staged (uncommitted) ones.</summary>
    public IQueryable<Asset> AllAssets => Assets.IgnoreQueryFilters();

    /// <summary>Sources including staged (uncommitted) ones.</summary>
    public IQueryable<Source> AllSources => Sources.IgnoreQueryFilters();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Volume>(e => e.HasKey(v => v.Id));

        b.Entity<Source>(e =>
        {
            e.Property(s => s.Location).HasConversion(LocationConverter);
            e.HasIndex(s => s.VolumeId);
            e.HasIndex(s => s.ImportSessionId);
            e.HasOne(s => s.ParentSource).WithMany().HasForeignKey(s => s.ParentSourceId).OnDelete(DeleteBehavior.SetNull);
            e.ComplexProperty(s => s.Store);
            e.ComplexProperty(s => s.License);
            e.Property(s => s.Links).HasConversion(Json<List<LinkInfo>>()).Metadata.SetValueComparer(JsonComparer<List<LinkInfo>>());
            e.HasQueryFilter(s => s.ImportSessionId == null);
        });

        b.Entity<Asset>(e =>
        {
            e.HasOne(a => a.Source).WithMany(s => s.Assets).HasForeignKey(a => a.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => a.Type);
            e.HasIndex(a => a.SourceId);
            e.HasIndex(a => a.ImportSessionId);
            e.HasIndex(a => a.AiStatus);
            e.HasIndex(a => a.IsStored);
            e.HasIndex(a => a.EmbeddingDirty);
            e.HasIndex(a => a.ScaleAnomaly);
            e.HasIndex(a => a.Favorite);
            e.HasIndex(a => a.Rating);
            e.HasIndex(a => a.GridUncertain);
            e.ComplexProperty(a => a.LicenseOverride);
            JsonColumn(e.Property(a => a.Colors));
            JsonColumn(e.Property(a => a.LinksOverride));
            JsonColumn(e.Property(a => a.Image));
            JsonColumn(e.Property(a => a.SpriteSheet));
            JsonColumn(e.Property(a => a.Audio));
            JsonColumn(e.Property(a => a.Model));
            JsonColumn(e.Property(a => a.Font));
            JsonColumn(e.Property(a => a.Video));
            JsonColumn(e.Property(a => a.Document));
            e.Ignore(a => a.EffectiveLicense);
            e.Ignore(a => a.EffectiveLinks);
            e.Ignore(a => a.PrimaryFile);
            e.HasQueryFilter(a => a.ImportSessionId == null);
        });

        b.Entity<AssetFile>(e =>
        {
            e.Property(f => f.Location).HasConversion(LocationConverter);
            e.HasOne(f => f.Asset).WithMany(a => a.Files).HasForeignKey(f => f.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => f.AssetId);
            e.HasIndex(f => f.FastHash);
            e.HasIndex(f => f.UnityGuid);
            e.HasIndex(f => f.Location);
        });

        b.Entity<Tag>(e =>
        {
            e.Property(t => t.Name).UseCollation("NOCASE");
            e.HasIndex(t => t.Name).IsUnique();
        });

        b.Entity<AssetTag>(e =>
        {
            e.HasKey(t => new { t.AssetId, t.TagId });
            e.HasIndex(t => t.TagId);
            e.HasOne(t => t.Asset).WithMany(a => a.Tags).HasForeignKey(t => t.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Tag).WithMany(t => t.Assets).HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SourceTag>(e =>
        {
            e.HasKey(t => new { t.SourceId, t.TagId });
            e.HasIndex(t => t.TagId);
            e.HasOne<Source>().WithMany().HasForeignKey(t => t.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Tag).WithMany().HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AssetTagOptOut>(e =>
        {
            e.HasKey(t => new { t.AssetId, t.TagId });
            e.HasOne<Asset>().WithMany().HasForeignKey(t => t.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Tag>().WithMany().HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Collection>(e => e.HasIndex(c => c.Name));

        b.Entity<PreviewBlob>(e =>
        {
            e.HasOne<Asset>().WithMany().HasForeignKey(p => p.AssetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.AssetId, p.Kind, p.Index });
        });

        b.Entity<StoredFile>(e =>
        {
            e.HasOne(s => s.AssetFile).WithOne(f => f.Stored).HasForeignKey<StoredFile>(s => s.AssetFileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.AssetFileId).IsUnique();
        });

        b.Entity<StoredFileChunk>(e =>
        {
            e.HasKey(c => new { c.StoredFileId, c.Seq });
            e.HasOne<StoredFile>().WithMany().HasForeignKey(c => c.StoredFileId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AssetEmbedding>(e =>
        {
            e.HasKey(x => x.AssetId);
            e.HasOne<Asset>().WithOne().HasForeignKey<AssetEmbedding>(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Job>(e =>
        {
            e.HasIndex(j => j.State);
            e.HasIndex(j => j.Type);
        });

        b.Entity<JobItemError>(e =>
        {
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.JobId);
        });

        b.Entity<StagedSkip>(e =>
        {
            e.HasOne<ImportSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SessionId);
            e.Property(x => x.Location).HasConversion(LocationConverter!);
        });

        b.Entity<Setting>(e => e.HasKey(s => s.Key));
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // Store enums as text for readability of the single-file catalog.
        c.Properties<AssetType>().HaveConversion<string>();
        c.Properties<SkipKind>().HaveConversion<string>();
        c.Properties<SourceKind>().HaveConversion<string>();
        c.Properties<FileRole>().HaveConversion<string>();
        c.Properties<TagOrigin>().HaveConversion<string>();
        c.Properties<AiStatus>().HaveConversion<string>();
        c.Properties<AiInputMode>().HaveConversion<string>();
        c.Properties<TextOrigin>().HaveConversion<string>();
        c.Properties<PreviewKind>().HaveConversion<string>();
        c.Properties<ScaleAnomaly>().HaveConversion<string>();
        c.Properties<JobState>().HaveConversion<string>();
        c.Properties<TriState>().HaveConversion<string>();
        c.Properties<ImportSessionState>().HaveConversion<string>();
        c.Properties<AiDepth>().HaveConversion<string>();
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ValueConverter<Location, string> LocationConverter =
        new(l => l.ToString(), s => Location.Parse(s));

    private static ValueConverter<T, string> Json<T>() =>
        new(v => JsonSerializer.Serialize(v, JsonOptions), s => JsonSerializer.Deserialize<T>(s, JsonOptions)!);

    private static ValueComparer<T> JsonComparer<T>() =>
        new((a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
            v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!);

    private static void JsonColumn<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> p)
    {
        p.HasConversion(Json<T>());
        p.Metadata.SetValueComparer(JsonComparer<T>());
    }
}
