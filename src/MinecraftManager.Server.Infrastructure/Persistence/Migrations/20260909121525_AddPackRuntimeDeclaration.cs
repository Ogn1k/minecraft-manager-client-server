using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinecraftManager.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPackRuntimeDeclaration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoaderType",
                table: "pack_versions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoaderVersion",
                table: "pack_versions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinecraftVersion",
                table: "pack_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoaderType",
                table: "pack_versions");

            migrationBuilder.DropColumn(
                name: "LoaderVersion",
                table: "pack_versions");

            migrationBuilder.DropColumn(
                name: "MinecraftVersion",
                table: "pack_versions");
        }
    }
}
