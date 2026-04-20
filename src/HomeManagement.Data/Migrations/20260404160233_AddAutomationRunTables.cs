using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationRunTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowType = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalMachines = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedMachines = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedMachines = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    OutputJson = table.Column<string>(type: "TEXT", nullable: true),
                    OutputMarkdown = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationMachineResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ResultDataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationMachineResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationMachineResults_AutomationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AutomationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRunSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StepName = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRunSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRunSteps_AutomationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AutomationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationMachineResults_MachineId",
                table: "AutomationMachineResults",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationMachineResults_RunId",
                table: "AutomationMachineResults",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationMachineResults_RunId_MachineId",
                table: "AutomationMachineResults",
                columns: new[] { "RunId", "MachineId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationMachineResults_RunId_Success",
                table: "AutomationMachineResults",
                columns: new[] { "RunId", "Success" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_CorrelationId",
                table: "AutomationRuns",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_StartedUtc",
                table: "AutomationRuns",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_State",
                table: "AutomationRuns",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_WorkflowType",
                table: "AutomationRuns",
                column: "WorkflowType");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRunSteps_RunId",
                table: "AutomationRunSteps",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRunSteps_RunId_StepName",
                table: "AutomationRunSteps",
                columns: new[] { "RunId", "StepName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationMachineResults");

            migrationBuilder.DropTable(
                name: "AutomationRunSteps");

            migrationBuilder.DropTable(
                name: "AutomationRuns");
        }
    }
}
