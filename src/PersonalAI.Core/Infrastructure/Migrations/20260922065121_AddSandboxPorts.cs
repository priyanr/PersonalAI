using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSandboxPorts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PortsJson",
                table: "SandboxInstances",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PortsJson",
                table: "SandboxInstances");
        }
    }
}
