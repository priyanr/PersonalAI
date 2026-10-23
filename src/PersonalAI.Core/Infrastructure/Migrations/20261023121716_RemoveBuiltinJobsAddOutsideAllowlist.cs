using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBuiltinJobsAddOutsideAllowlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OutsideAllowlist",
                table: "CollectionJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // The built-in "Linux host health" jobs are gone: the model designs its own collection now. Remove the jobs and
            // what only they produced (their five datasets, and alert rules reading those datasets), for the agents that
            // had them. Dashboards are left alone; the agent rebuilds them against whatever it collects next.
            const string builtinAgents = "SELECT AgentId FROM CollectionJobs WHERE Source = 'builtin'";
            const string builtinDatasets = "('system', 'disk', 'temperature', 'container', 'discovery')";
            migrationBuilder.Sql($"DELETE FROM AlertEvents WHERE RuleId IN (SELECT Id FROM AlertRules WHERE Dataset IN {builtinDatasets} AND AgentId IN ({builtinAgents}))");
            migrationBuilder.Sql($"DELETE FROM AlertRules WHERE Dataset IN {builtinDatasets} AND AgentId IN ({builtinAgents})");
            migrationBuilder.Sql($"DELETE FROM DataPoints WHERE DatasetId IN (SELECT Id FROM Datasets WHERE Name IN {builtinDatasets} AND AgentId IN ({builtinAgents}))");
            migrationBuilder.Sql($"DELETE FROM Datasets WHERE Name IN {builtinDatasets} AND AgentId IN ({builtinAgents})");
            migrationBuilder.Sql("DELETE FROM CollectionJobs WHERE Source = 'builtin'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutsideAllowlist",
                table: "CollectionJobs");
        }
    }
}
