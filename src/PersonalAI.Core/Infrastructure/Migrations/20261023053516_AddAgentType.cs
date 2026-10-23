using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Agents",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "General");

            migrationBuilder.AddColumn<string>(
                name: "TypeConfigJson",
                table: "Agents",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "TypeConfigJson",
                table: "Agents");
        }
    }
}
