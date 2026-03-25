using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeManagement.Data.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlServerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ActorIdentity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetMachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetMachineName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PreviousHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EventHash = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "AuthRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PermissionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalTargets = table.Column<int>(type: "int", nullable: false),
                    CompletedTargets = table.Column<int>(type: "int", nullable: false),
                    FailedTargets = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Machines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hostname = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Fqdn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IpAddresses = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OsType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OsVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConnectionMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Protocol = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CpuCores = table.Column<int>(type: "int", nullable: true),
                    RamBytes = table.Column<long>(type: "bigint", nullable: true),
                    Architecture = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastContactUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Machines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CronExpression = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    NextFireUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFireUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    IssuedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthRefreshTokens_AuthUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AuthUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuthUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AuthUserRoles_AuthRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AuthRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AuthUserRoles_AuthUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AuthUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobMachineResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobMachineResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobMachineResults_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MachineTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineTags_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatchHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatchId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    RequiresReboot = table.Column<bool>(type: "bit", nullable: true),
                    State = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatchHistory_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PatchHistory_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartupType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProcessId = table.Column<int>(type: "int", nullable: true),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceSnapshots_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action",
                table: "AuditEvents",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_Outcome",
                table: "AuditEvents",
                columns: new[] { "Action", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CorrelationId",
                table: "AuditEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TargetMachineId_TimestampUtc",
                table: "AuditEvents",
                columns: new[] { "TargetMachineId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TimestampUtc",
                table: "AuditEvents",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuthRefreshTokens_TokenHash",
                table: "AuthRefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthRefreshTokens_UserId_ExpiresUtc",
                table: "AuthRefreshTokens",
                columns: new[] { "UserId", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthRoles_Name",
                table: "AuthRoles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUserRoles_RoleId",
                table: "AuthUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_Email",
                table: "AuthUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_Provider",
                table: "AuthUsers",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_Username",
                table: "AuthUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobMachineResults_JobId",
                table: "JobMachineResults",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobMachineResults_MachineId",
                table: "JobMachineResults",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_JobMachineResults_MachineId_Success",
                table: "JobMachineResults",
                columns: new[] { "MachineId", "Success" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SubmittedUtc",
                table: "Jobs",
                column: "SubmittedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Type_State",
                table: "Jobs",
                columns: new[] { "Type", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Machines_CreatedUtc",
                table: "Machines",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Machines_Hostname",
                table: "Machines",
                column: "Hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineTags_Key",
                table: "MachineTags",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_MachineTags_Key_Value",
                table: "MachineTags",
                columns: new[] { "Key", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_MachineTags_MachineId_Key",
                table: "MachineTags",
                columns: new[] { "MachineId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatchHistory_JobId",
                table: "PatchHistory",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_PatchHistory_MachineId",
                table: "PatchHistory",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_PatchHistory_MachineId_PatchId",
                table: "PatchHistory",
                columns: new[] { "MachineId", "PatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchHistory_MachineId_TimestampUtc",
                table: "PatchHistory",
                columns: new[] { "MachineId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PatchHistory_State",
                table: "PatchHistory",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_PatchHistory_TimestampUtc",
                table: "PatchHistory",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceSnapshots_CapturedUtc",
                table: "ServiceSnapshots",
                column: "CapturedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceSnapshots_MachineId",
                table: "ServiceSnapshots",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceSnapshots_MachineId_ServiceName",
                table: "ServiceSnapshots",
                columns: new[] { "MachineId", "ServiceName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "AuthRefreshTokens");

            migrationBuilder.DropTable(
                name: "AuthUserRoles");

            migrationBuilder.DropTable(
                name: "JobMachineResults");

            migrationBuilder.DropTable(
                name: "MachineTags");

            migrationBuilder.DropTable(
                name: "PatchHistory");

            migrationBuilder.DropTable(
                name: "ScheduledJobs");

            migrationBuilder.DropTable(
                name: "ServiceSnapshots");

            migrationBuilder.DropTable(
                name: "AuthRoles");

            migrationBuilder.DropTable(
                name: "AuthUsers");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Machines");
        }
    }
}
