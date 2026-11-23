using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportIgnoredFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IgnoredFolders",
                table: "ImportSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "UseDefaultIgnores",
                table: "ImportSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IgnoredFolders",
                table: "ImportSessions");

            migrationBuilder.DropColumn(
                name: "UseDefaultIgnores",
                table: "ImportSessions");
        }
    }
}
