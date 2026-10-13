using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobInstructions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Destination",
                table: "Jobs",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Instruction",
                table: "Jobs",
                type: "TEXT",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleInstruction",
                table: "Jobs",
                type: "TEXT",
                maxLength: 8192,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Destination",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "Instruction",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RuleInstruction",
                table: "Jobs");
        }
    }
}
