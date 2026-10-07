using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileListings",
                columns: table => new
                {
                    ResultId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    TorrentFile = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileListings", x => x.ResultId);
                });

            migrationBuilder.CreateTable(
                name: "SearchResults",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SearchId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: true),
                    AvailabilityJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourcesJson = table.Column<string>(type: "TEXT", nullable: false),
                    MatchedQueriesJson = table.Column<string>(type: "TEXT", nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ContentId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    QueriesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourcesJson = table.Column<string>(type: "TEXT", nullable: false),
                    NextSourceIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileListings_ExpiresAt",
                table: "FileListings",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SearchResults_ExpiresAt",
                table: "SearchResults",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SearchResults_SearchId_DedupKey",
                table: "SearchResults",
                columns: new[] { "SearchId", "DedupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_SearchSessions_ExpiresAt",
                table: "SearchSessions",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileListings");

            migrationBuilder.DropTable(
                name: "SearchResults");

            migrationBuilder.DropTable(
                name: "SearchSessions");
        }
    }
}
