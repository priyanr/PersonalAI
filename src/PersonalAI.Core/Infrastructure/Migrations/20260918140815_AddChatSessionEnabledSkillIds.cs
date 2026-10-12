using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSessionEnabledSkillIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnabledSkillIdsJson",
                table: "ChatSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnabledSkillIdsJson",
                table: "ChatSessions");
        }
    }
}
