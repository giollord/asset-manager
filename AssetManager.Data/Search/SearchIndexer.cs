using System.Text;
using System.Text.RegularExpressions;
using AssetManager.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AssetManager.Data.Search;

/// <summary>
/// Maintains the FTS5 <c>asset_fts</c> table (rowid = asset id). Rows are rebuilt in C# so names like
/// "WoodenBarrel_01" are also indexed as separate words.
/// </summary>
public static partial class SearchIndexer
{
    public static async Task RefreshAsync(CatalogDbContext db, IEnumerable<long> assetIds, CancellationToken ct = default)
    {
        foreach (var chunk in assetIds.Distinct().Chunk(500))
        {
            var rows = await db.AllAssets.AsNoTracking()
                .Where(a => chunk.Contains(a.Id))
                .Select(a => new
                {
                    a.Id,
                    a.Name,
                    a.Description,
                    a.Notes,
                    Source = a.Source.DisplayName,
                    Files = a.Files.Select(f => new { f.FileName, f.RelativePath }).ToList(),
                    Tags = a.Tags.Select(t => t.Tag.Name).ToList(),
                })
                .ToListAsync(ct);

            var conn = (SqliteConnection)db.Database.GetDbConnection();
            var opened = conn.State != System.Data.ConnectionState.Open;
            if (opened) await db.Database.OpenConnectionAsync(ct);
            try
            {
                var outer = db.Database.CurrentTransaction?.GetDbTransaction();
                var tx = outer ?? await conn.BeginTransactionAsync(ct);
                await using (var del = conn.CreateCommand())
                {
                    del.Transaction = (SqliteTransaction)tx;
                    del.CommandText = $"DELETE FROM asset_fts WHERE rowid IN ({string.Join(',', chunk)})";
                    await del.ExecuteNonQueryAsync(ct);
                }
                await using var ins = conn.CreateCommand();
                ins.Transaction = (SqliteTransaction)tx;
                ins.CommandText = "INSERT INTO asset_fts(rowid, name, files, paths, description, tags, source, notes) VALUES ($id, $name, $files, $paths, $desc, $tags, $source, $notes)";
                var pId = ins.Parameters.Add("$id", SqliteType.Integer);
                var pName = ins.Parameters.Add("$name", SqliteType.Text);
                var pFiles = ins.Parameters.Add("$files", SqliteType.Text);
                var pPaths = ins.Parameters.Add("$paths", SqliteType.Text);
                var pDesc = ins.Parameters.Add("$desc", SqliteType.Text);
                var pTags = ins.Parameters.Add("$tags", SqliteType.Text);
                var pSource = ins.Parameters.Add("$source", SqliteType.Text);
                var pNotes = ins.Parameters.Add("$notes", SqliteType.Text);
                foreach (var r in rows)
                {
                    pId.Value = r.Id;
                    pName.Value = WithWords(r.Name);
                    pFiles.Value = string.Join(' ', r.Files.Select(f => WithWords(Path.GetFileNameWithoutExtension(f.FileName))));
                    pPaths.Value = string.Join(' ', r.Files.Select(f => WithWords(f.RelativePath)));
                    pDesc.Value = r.Description ?? "";
                    pTags.Value = string.Join(" ; ", r.Tags);
                    pSource.Value = WithWords(r.Source);
                    pNotes.Value = r.Notes ?? "";
                    await ins.ExecuteNonQueryAsync(ct);
                }
                if (outer is null)
                {
                    await tx.CommitAsync(ct);
                    await tx.DisposeAsync();
                }
            }
            finally
            {
                if (opened) await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>Rebuilds the whole index.</summary>
    public static async Task RebuildAsync(CatalogDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM asset_fts", ct);
        var ids = await db.AllAssets.Select(a => a.Id).ToListAsync(ct);
        await RefreshAsync(db, ids, ct);
    }

    /// <summary>The text plus its split words: "WoodenBarrel_01" → "WoodenBarrel_01 Wooden Barrel 01".</summary>
    public static string WithWords(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var split = CamelBoundary().Replace(text, " ");
        split = Separators().Replace(split, " ").Trim();
        return split == text ? text : $"{text} {split}";
    }

    /// <summary>
    /// Builds an FTS5 MATCH expression from user text: every word must match as a prefix
    /// ("wood barr" → <c>"wood"* AND "barr"*</c>). Returns null when there is nothing to search.
    /// </summary>
    public static string? BuildMatch(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return null;
        var words = Words().Matches(userText).Select(m => m.Value).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (sb.Length > 0) sb.Append(" AND ");
            sb.Append('"').Append(w.Replace("\"", "\"\"")).Append("\"*");
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"[_\-./\\]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
