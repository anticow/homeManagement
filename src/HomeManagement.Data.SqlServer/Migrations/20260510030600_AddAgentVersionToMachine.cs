using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeManagement.Data.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentVersionToMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentVersion",
                table: "Machines",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentVersion",
                table: "Machines");
        }
    }
}
