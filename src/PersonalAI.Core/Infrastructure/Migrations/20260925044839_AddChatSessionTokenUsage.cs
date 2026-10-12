using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSessionTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TotalInputTokens",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TotalOutputTokens",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotalInputTokens",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "TotalOutputTokens",
                table: "ChatSessions");
        }
    }
}
