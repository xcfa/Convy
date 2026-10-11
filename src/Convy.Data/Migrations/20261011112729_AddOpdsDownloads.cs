using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpdsDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpdsDownloads",
                columns: table => new
                {
                    ItemRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CatalogId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SavePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Fallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpdsDownloads", x => x.ItemRef);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpdsDownloads_State",
                table: "OpdsDownloads",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpdsDownloads");
        }
    }
}
