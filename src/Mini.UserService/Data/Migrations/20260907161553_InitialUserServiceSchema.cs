using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Mini.UserService.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialUserServiceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "UserIdentityRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserIdentityRoles", x => x.UserId);
                });

            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "TenantId", "CreatedDate", "Description", "IsActive", "Key", "Name", "UpdatedDate" },
                values: new object[,]
                {
                    { new Guid("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Phase 3's first tenant. Has local users and file-configured external providers.", true, "acme", "Acme Corporation", null },
                    { new Guid("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Phase 9's tenant. No local user and no file-configured provider — a database-backed provider is its only way in.", true, "initech", "Initech", null },
                    { new Guid("c9f0f895-fb98-4d75-8d81-7d7c7f4a6b1e"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Phase 3's second tenant. Local users only, no external providers.", true, "globex", "Globex Corporation", null }
                });

            migrationBuilder.InsertData(
                table: "UserIdentityRoles",
                columns: new[] { "UserId", "Role" },
                values: new object[] { "1", "Admin" });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Key",
                table: "Tenants",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "UserIdentityRoles");
        }
    }
}
