using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE asset_fts USING fts5(
                    name, files, paths, description, tags, source,
                    tokenize = 'unicode61 remove_diacritics 2',
                    prefix = '2 3');
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER asset_fts_after_delete AFTER DELETE ON Assets BEGIN
                    DELETE FROM asset_fts WHERE rowid = old.Id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS asset_fts_after_delete;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS asset_fts;");
        }
    }
}
