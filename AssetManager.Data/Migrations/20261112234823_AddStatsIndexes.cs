using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetManager.Data.Migrations
{
    /// <summary>
    /// Narrow covering indexes for the dashboard counts (design: Stats, under 1 s at 200k assets). Every catalog query
    /// filters <c>ImportSessionId IS NULL</c>, so it leads each index. The license columns belong to a complex property,
    /// so the indexes are plain SQL.
    /// </summary>
    public partial class AddStatsIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE INDEX IX_Assets_Stats_Type ON Assets (ImportSessionId, Type);");
            migrationBuilder.Sql("CREATE INDEX IX_Assets_Stats_AiStatus ON Assets (ImportSessionId, AiStatus);");
            migrationBuilder.Sql("CREATE INDEX IX_Assets_Stats_Source ON Assets (ImportSessionId, SourceId, HasLicenseOverride, " +
                "LicenseOverride_Commercial, LicenseOverride_Attribution, IsStored);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Assets_Stats_Type;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Assets_Stats_AiStatus;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Assets_Stats_Source;");
        }
    }
}
