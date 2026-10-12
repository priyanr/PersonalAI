using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentsAndAgentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "ollama"),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NumCtx = table.Column<int>(type: "INTEGER", nullable: true),
                    EnabledSkillIdsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    ScheduleKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ScheduleOnceAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_AgentId",
                table: "AgentRuns",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_StartedAt",
                table: "AgentRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_CreatedAt",
                table: "Agents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_NextRunAt",
                table: "Agents",
                column: "NextRunAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentRuns");

            migrationBuilder.DropTable(
                name: "Agents");
        }
    }
}
