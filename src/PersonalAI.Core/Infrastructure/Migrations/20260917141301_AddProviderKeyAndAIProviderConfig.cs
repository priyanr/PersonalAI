using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderKeyAndAIProviderConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "ChatSessions",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "ollama");

            migrationBuilder.CreateTable(
                name: "AIProviderConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    FieldName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIProviderConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AIProviderConfigs_ProviderKey_FieldName",
                table: "AIProviderConfigs",
                columns: new[] { "ProviderKey", "FieldName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AIProviderConfigs");

            migrationBuilder.DropColumn(
                name: "ProviderKey",
                table: "ChatSessions");
        }
    }
}
