using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentEnabledTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnabledToolsJson",
                table: "Agents",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnabledToolsJson",
                table: "Agents");
        }
    }
}
