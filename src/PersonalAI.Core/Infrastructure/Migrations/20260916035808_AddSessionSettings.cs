using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NumCtx",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ToolsEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrimHistoryEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NumCtx",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "ToolsEnabled",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "TrimHistoryEnabled",
                table: "ChatSessions");
        }
    }
}
