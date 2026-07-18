using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationPlatform.Api.Migrations
{
    /// <inheritdoc />
    public partial class HybridDirectorySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DirectorySyncEnabled",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DirectorySynced",
                table: "ScannedUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TargetDirectoryMode",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectorySyncEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DirectorySynced",
                table: "ScannedUsers");

            migrationBuilder.DropColumn(
                name: "TargetDirectoryMode",
                table: "Projects");
        }
    }
}
