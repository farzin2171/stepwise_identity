using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mini.AuthorizationService.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SubjectKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ResourceName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ContextHash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Authorized = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedDecisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedDecisions_TenantKey_SubjectKey_ResourceName_ContextHash",
                table: "CachedDecisions",
                columns: new[] { "TenantKey", "SubjectKey", "ResourceName", "ContextHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedDecisions");
        }
    }
}
