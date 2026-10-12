using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRunTokensAndGmailFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GmailEnabled",
                table: "ChatSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "TotalInputTokens",
                table: "AgentRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TotalOutputTokens",
                table: "AgentRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GmailEnabled",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "TotalInputTokens",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "TotalOutputTokens",
                table: "AgentRuns");
        }
    }
}
