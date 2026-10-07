using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemRef = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientCategory = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Subpath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    SelectedFilesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ResultId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Rule = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    PlacementAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDownloadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastProgressAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Provider_ItemRef",
                table: "Jobs",
                columns: new[] { "Provider", "ItemRef" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
