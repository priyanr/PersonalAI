using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPerToolSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EditFileEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ListFilesEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReadFileEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SearchFilesEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WebSearchEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WriteFileEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditFileEnabled",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "ListFilesEnabled",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "ReadFileEnabled",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "SearchFilesEnabled",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "WebSearchEnabled",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "WriteFileEnabled",
                table: "ChatSessions");
        }
    }
}
