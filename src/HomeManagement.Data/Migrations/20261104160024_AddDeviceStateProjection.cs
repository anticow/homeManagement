using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeManagement.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceStateProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceStates",
                columns: table => new
                {
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AgentSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastKnownConnected = table.Column<bool>(type: "INTEGER", nullable: true),
                    AgentFailure = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ManagedObservationJson = table.Column<string>(type: "TEXT", nullable: true),
                    MetricsAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MetricsSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HealthJson = table.Column<string>(type: "TEXT", nullable: true),
                    MetricsFailure = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceStates", x => x.MachineId);
                    table.ForeignKey(
                        name: "FK_DeviceStates_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceStates");
        }
    }
}
