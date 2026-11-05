using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeManagement.Data.SqlServer.Migrations
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
                    MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AgentSuccessUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastKnownConnected = table.Column<bool>(type: "bit", nullable: true),
                    AgentFailure = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ManagedObservationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetricsAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MetricsSuccessUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HealthJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetricsFailure = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
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
