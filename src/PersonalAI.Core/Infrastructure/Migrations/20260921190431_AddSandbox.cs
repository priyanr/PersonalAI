using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSandbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SandboxEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SandboxInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Image = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContainerId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    HostPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DestroyedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxInstances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxInstances_LastUsedAt",
                table: "SandboxInstances",
                column: "LastUsedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SandboxInstances_OwnerId",
                table: "SandboxInstances",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SandboxInstances_Status",
                table: "SandboxInstances",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SandboxInstances");

            migrationBuilder.DropColumn(
                name: "SandboxEnabled",
                table: "ChatSessions");
        }
    }
}
