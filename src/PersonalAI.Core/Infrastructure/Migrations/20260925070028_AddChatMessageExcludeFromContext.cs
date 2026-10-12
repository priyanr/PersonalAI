using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageExcludeFromContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFromContext",
                table: "ChatMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludeFromContext",
                table: "ChatMessages");
        }
    }
}
