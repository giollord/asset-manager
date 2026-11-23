using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "StagedSkips",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "StagedSkips",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CommittedFromSessionId",
                table: "Sources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "ImportSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StagedThrough",
                table: "ImportSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "StagedSkips");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "StagedSkips");

            migrationBuilder.DropColumn(
                name: "CommittedFromSessionId",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "ImportSessions");

            migrationBuilder.DropColumn(
                name: "StagedThrough",
                table: "ImportSessions");
        }
    }
}
