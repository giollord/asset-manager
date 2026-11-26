using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Favorite",
                table: "Assets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "GridUncertain",
                table: "Assets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Assets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Assets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SpriteSheet",
                table: "Assets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssetTagOptOuts",
                columns: table => new
                {
                    AssetId = table.Column<long>(type: "INTEGER", nullable: false),
                    TagId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTagOptOuts", x => new { x.AssetId, x.TagId });
                    table.ForeignKey(
                        name: "FK_AssetTagOptOuts_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetTagOptOuts_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceTags",
                columns: table => new
                {
                    SourceId = table.Column<long>(type: "INTEGER", nullable: false),
                    TagId = table.Column<long>(type: "INTEGER", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceTags", x => new { x.SourceId, x.TagId });
                    table.ForeignKey(
                        name: "FK_SourceTags_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Favorite",
                table: "Assets",
                column: "Favorite");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_GridUncertain",
                table: "Assets",
                column: "GridUncertain");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Rating",
                table: "Assets",
                column: "Rating");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTagOptOuts_TagId",
                table: "AssetTagOptOuts",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceTags_TagId",
                table: "SourceTags",
                column: "TagId");

            // asset_fts gains a "notes" column (design D10). FTS5 cannot add columns, so the table is recreated and
            // the existing rows are copied over (notes start empty, as the column is new).
            RecreateFts(migrationBuilder, withNotes: true);
        }

        private static void RecreateFts(MigrationBuilder migrationBuilder, bool withNotes)
        {
            var columns = "name, files, paths, description, tags, source";
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS asset_fts_after_delete;");
            migrationBuilder.Sql($"""
                CREATE VIRTUAL TABLE asset_fts_new USING fts5(
                    {columns}{(withNotes ? ", notes" : "")},
                    tokenize = 'unicode61 remove_diacritics 2',
                    prefix = '2 3');
                """);
            migrationBuilder.Sql($"INSERT INTO asset_fts_new(rowid, {columns}) SELECT rowid, {columns} FROM asset_fts;");
            migrationBuilder.Sql("DROP TABLE asset_fts;");
            migrationBuilder.Sql("ALTER TABLE asset_fts_new RENAME TO asset_fts;");
            migrationBuilder.Sql("""
                CREATE TRIGGER asset_fts_after_delete AFTER DELETE ON Assets BEGIN
                    DELETE FROM asset_fts WHERE rowid = old.Id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RecreateFts(migrationBuilder, withNotes: false);

            migrationBuilder.DropTable(
                name: "AssetTagOptOuts");

            migrationBuilder.DropTable(
                name: "SourceTags");

            migrationBuilder.DropIndex(
                name: "IX_Assets_Favorite",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Assets_GridUncertain",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Assets_Rating",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "Favorite",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "GridUncertain",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "SpriteSheet",
                table: "Assets");
        }
    }
}
