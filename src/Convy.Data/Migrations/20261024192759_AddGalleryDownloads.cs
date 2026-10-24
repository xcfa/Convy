using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGalleryDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GalleryDownloads",
                columns: table => new
                {
                    ItemRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ContentId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SavePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PagesTotal = table.Column<int>(type: "INTEGER", nullable: true),
                    PagesDone = table.Column<int>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GalleryDownloads", x => x.ItemRef);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GalleryDownloads_State",
                table: "GalleryDownloads",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GalleryDownloads");
        }
    }
}
