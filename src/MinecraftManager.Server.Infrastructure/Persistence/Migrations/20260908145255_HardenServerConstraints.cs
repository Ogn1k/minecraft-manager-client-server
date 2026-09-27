using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinecraftManager.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenServerConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_pack_files_PackVersionId_Path",
                table: "pack_files");

            migrationBuilder.DropIndex(
                name: "IX_pack_assignments_InstanceId_PackId_IsActive",
                table: "pack_assignments");

            migrationBuilder.DropIndex(
                name: "IX_managed_paths_PackVersionId_Path",
                table: "managed_paths");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedPath",
                table: "pack_files",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedPath",
                table: "managed_paths",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "device_credentials",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE pack_files SET \"NormalizedPath\" = upper(\"Path\");");
            migrationBuilder.Sql("UPDATE managed_paths SET \"NormalizedPath\" = upper(\"Path\");");
            migrationBuilder.Sql("UPDATE device_credentials SET \"ConcurrencyToken\" = \"Id\";");

            migrationBuilder.AlterColumn<string>(name: "NormalizedPath", table: "pack_files", type: "character varying(1024)", maxLength: 1024, nullable: false, oldClrType: typeof(string), oldType: "character varying(1024)", oldMaxLength: 1024, oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "NormalizedPath", table: "managed_paths", type: "character varying(1024)", maxLength: 1024, nullable: false, oldClrType: typeof(string), oldType: "character varying(1024)", oldMaxLength: 1024, oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "ConcurrencyToken", table: "device_credentials", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_pack_files_PackVersionId_NormalizedPath",
                table: "pack_files",
                columns: new[] { "PackVersionId", "NormalizedPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pack_assignments_InstanceId_IsActive",
                table: "pack_assignments",
                columns: new[] { "InstanceId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_pack_assignments_InstanceId_PackId",
                table: "pack_assignments",
                columns: new[] { "InstanceId", "PackId" },
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_managed_paths_PackVersionId_NormalizedPath",
                table: "managed_paths",
                columns: new[] { "PackVersionId", "NormalizedPath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_pack_files_PackVersionId_NormalizedPath",
                table: "pack_files");

            migrationBuilder.DropIndex(
                name: "IX_pack_assignments_InstanceId_IsActive",
                table: "pack_assignments");

            migrationBuilder.DropIndex(
                name: "IX_pack_assignments_InstanceId_PackId",
                table: "pack_assignments");

            migrationBuilder.DropIndex(
                name: "IX_managed_paths_PackVersionId_NormalizedPath",
                table: "managed_paths");

            migrationBuilder.DropColumn(
                name: "NormalizedPath",
                table: "pack_files");

            migrationBuilder.DropColumn(
                name: "NormalizedPath",
                table: "managed_paths");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "device_credentials");

            migrationBuilder.CreateIndex(
                name: "IX_pack_files_PackVersionId_Path",
                table: "pack_files",
                columns: new[] { "PackVersionId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pack_assignments_InstanceId_PackId_IsActive",
                table: "pack_assignments",
                columns: new[] { "InstanceId", "PackId", "IsActive" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_paths_PackVersionId_Path",
                table: "managed_paths",
                columns: new[] { "PackVersionId", "Path" },
                unique: true);
        }
    }
}
