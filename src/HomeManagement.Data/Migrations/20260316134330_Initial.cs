using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    ActorIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    TargetMachineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetMachineName = table.Column<string>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousHash = table.Column<string>(type: "TEXT", nullable: true),
                    EventHash = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalTargets = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedTargets = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedTargets = table.Column<int>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Machines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", nullable: false),
                    Fqdn = table.Column<string>(type: "TEXT", nullable: true),
                    IpAddresses = table.Column<string>(type: "TEXT", nullable: false),
                    OsType = table.Column<string>(type: "TEXT", nullable: false),
                    OsVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionMode = table.Column<string>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CpuCores = table.Column<int>(type: "INTEGER", nullable: true),
                    RamBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Architecture = table.Column<string>(type: "TEXT", nullable: true),
                    DisksJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastContactUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Machines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextFireUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFireUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobMachineResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PatchId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    RequiresReboot = table.Column<bool>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    StartupType = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    CapturedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Machines");
        }
    }
}
