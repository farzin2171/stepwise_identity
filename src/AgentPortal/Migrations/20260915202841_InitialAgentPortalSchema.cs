using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPortal.Migrations
{
    /// <inheritdoc />
    public partial class InitialAgentPortalSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PolicyChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentSubjectId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ResourceName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OldCondition = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewCondition = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FailureDetail = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyChangeRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyChangeRequests_TenantKey_RequestedAtUtc",
                table: "PolicyChangeRequests",
                columns: new[] { "TenantKey", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyChangeRequests");
        }
    }
}
