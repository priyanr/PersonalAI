using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalAI.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameTargetsToSshServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SshServerGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SshServerGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SshServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SecretRef = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    HostKeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SshServers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SshServers_SshServerGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "SshServerGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SshServerGroups_Name",
                table: "SshServerGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SshServers_GroupId_Name",
                table: "SshServers",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            // A rename, not a rebuild: the rows move to the new tables (EF would otherwise drop the old ones with their data).
            migrationBuilder.Sql("INSERT INTO SshServerGroups (Id, Name, Description, CreatedAt) SELECT Id, Name, Description, CreatedAt FROM TargetGroups");
            migrationBuilder.Sql("INSERT INTO SshServers (Id, GroupId, Name, Address, Port, Username, SecretRef, Enabled, HostKeyFingerprint, LastSeenAt, LastError, CreatedAt) SELECT Id, GroupId, Name, Address, Port, Username, SecretRef, Enabled, HostKeyFingerprint, LastSeenAt, LastError, CreatedAt FROM Targets");

            migrationBuilder.DropTable(
                name: "Targets");

            migrationBuilder.DropTable(
                name: "TargetGroups");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TargetGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Targets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    HostKeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    SecretRef = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Targets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Targets_TargetGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TargetGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TargetGroups_Name",
                table: "TargetGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Targets_GroupId_Name",
                table: "Targets",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.Sql("INSERT INTO TargetGroups (Id, Name, Description, CreatedAt) SELECT Id, Name, Description, CreatedAt FROM SshServerGroups");
            migrationBuilder.Sql("INSERT INTO Targets (Id, GroupId, Name, Address, Port, Username, SecretRef, Enabled, HostKeyFingerprint, LastSeenAt, LastError, CreatedAt) SELECT Id, GroupId, Name, Address, Port, Username, SecretRef, Enabled, HostKeyFingerprint, LastSeenAt, LastError, CreatedAt FROM SshServers");

            migrationBuilder.DropTable(
                name: "SshServers");

            migrationBuilder.DropTable(
                name: "SshServerGroups");
        }
    }
}
