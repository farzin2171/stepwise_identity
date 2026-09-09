using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Mini.UserService.Connectors.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialConnectorSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Connectors",
                columns: table => new
                {
                    ConnectorId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connectors", x => x.ConnectorId);
                });

            migrationBuilder.CreateTable(
                name: "Handlers",
                columns: table => new
                {
                    HandlerId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Handlers", x => x.HandlerId);
                });

            migrationBuilder.CreateTable(
                name: "WebApiConnectorConfigurations",
                columns: table => new
                {
                    WebApiConnectorConfigurationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebApiConnectorConfigurations", x => x.WebApiConnectorConfigurationId);
                });

            migrationBuilder.CreateTable(
                name: "ClaimConnectorConfigurations",
                columns: table => new
                {
                    ClaimConnectorConfigurationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HandlerId = table.Column<int>(type: "int", nullable: false),
                    ClaimName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimConnectorConfigurations", x => x.ClaimConnectorConfigurationId);
                    table.ForeignKey(
                        name: "FK_ClaimConnectorConfigurations_Handlers_HandlerId",
                        column: x => x.HandlerId,
                        principalTable: "Handlers",
                        principalColumn: "HandlerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorHandlers",
                columns: table => new
                {
                    ConnectorHandlerId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConnectorId = table.Column<int>(type: "int", nullable: false),
                    HandlerId = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorHandlers", x => x.ConnectorHandlerId);
                    table.ForeignKey(
                        name: "FK_ConnectorHandlers_Connectors_ConnectorId",
                        column: x => x.ConnectorId,
                        principalTable: "Connectors",
                        principalColumn: "ConnectorId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectorHandlers_Handlers_HandlerId",
                        column: x => x.HandlerId,
                        principalTable: "Handlers",
                        principalColumn: "HandlerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebApiConnectorConfigurationRoutes",
                columns: table => new
                {
                    WebApiConnectorConfigurationRouteId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WebApiConnectorConfigurationId = table.Column<int>(type: "int", nullable: false),
                    HandlerId = table.Column<int>(type: "int", nullable: false),
                    Route = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebApiConnectorConfigurationRoutes", x => x.WebApiConnectorConfigurationRouteId);
                    table.ForeignKey(
                        name: "FK_WebApiConnectorConfigurationRoutes_Handlers_HandlerId",
                        column: x => x.HandlerId,
                        principalTable: "Handlers",
                        principalColumn: "HandlerId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebApiConnectorConfigurationRoutes_WebApiConnectorConfigurations_WebApiConnectorConfigurationId",
                        column: x => x.WebApiConnectorConfigurationId,
                        principalTable: "WebApiConnectorConfigurations",
                        principalColumn: "WebApiConnectorConfigurationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorHandlerCascadingTenants",
                columns: table => new
                {
                    ConnectorHandlerCascadingTenantId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectorHandlerId = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<byte>(type: "tinyint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorHandlerCascadingTenants", x => x.ConnectorHandlerCascadingTenantId);
                    table.ForeignKey(
                        name: "FK_ConnectorHandlerCascadingTenants_ConnectorHandlers_ConnectorHandlerId",
                        column: x => x.ConnectorHandlerId,
                        principalTable: "ConnectorHandlers",
                        principalColumn: "ConnectorHandlerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorHandlerTenants",
                columns: table => new
                {
                    ConnectorHandlerTenantId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectorHandlerId = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorHandlerTenants", x => x.ConnectorHandlerTenantId);
                    table.ForeignKey(
                        name: "FK_ConnectorHandlerTenants_ConnectorHandlers_ConnectorHandlerId",
                        column: x => x.ConnectorHandlerId,
                        principalTable: "ConnectorHandlers",
                        principalColumn: "ConnectorHandlerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Connectors",
                columns: new[] { "ConnectorId", "Type" },
                values: new object[,]
                {
                    { 1, 1 },
                    { 2, 2 },
                    { 3, 3 }
                });

            migrationBuilder.InsertData(
                table: "Handlers",
                columns: new[] { "HandlerId", "Name" },
                values: new object[,]
                {
                    { 1, "GetUserRole" },
                    { 2, "GetUserByEmail" }
                });

            migrationBuilder.InsertData(
                table: "WebApiConnectorConfigurations",
                columns: new[] { "WebApiConnectorConfigurationId", "Host", "TenantId" },
                values: new object[,]
                {
                    { 1, "https://localhost:5014", new Guid("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0") },
                    { 2, "https://localhost:5014", new Guid("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93") }
                });

            migrationBuilder.InsertData(
                table: "ClaimConnectorConfigurations",
                columns: new[] { "ClaimConnectorConfigurationId", "ClaimName", "HandlerId", "TenantId" },
                values: new object[,]
                {
                    { 1, "role", 1, new Guid("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0") },
                    { 2, "role", 1, new Guid("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93") }
                });

            migrationBuilder.InsertData(
                table: "ConnectorHandlers",
                columns: new[] { "ConnectorHandlerId", "ConnectorId", "HandlerId", "IsEnabled" },
                values: new object[,]
                {
                    { 1, 1, 1, true },
                    { 2, 3, 1, true },
                    { 3, 1, 2, true },
                    { 4, 2, 1, false }
                });

            migrationBuilder.InsertData(
                table: "WebApiConnectorConfigurationRoutes",
                columns: new[] { "WebApiConnectorConfigurationRouteId", "HandlerId", "Route", "WebApiConnectorConfigurationId" },
                values: new object[,]
                {
                    { 1, 1, "users/{id}", 1 },
                    { 2, 2, "users/by-email/{email}", 1 },
                    { 3, 1, "users/{id}", 2 },
                    { 4, 2, "users/by-email/{email}", 2 }
                });

            migrationBuilder.InsertData(
                table: "ConnectorHandlerCascadingTenants",
                columns: new[] { "ConnectorHandlerCascadingTenantId", "ConnectorHandlerId", "IsEnabled", "Order", "TenantId" },
                values: new object[,]
                {
                    { 1, 1, true, (byte)1, new Guid("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0") },
                    { 2, 2, true, (byte)2, new Guid("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0") },
                    { 3, 1, true, (byte)1, new Guid("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93") },
                    { 4, 2, true, (byte)2, new Guid("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93") }
                });

            migrationBuilder.InsertData(
                table: "ConnectorHandlerTenants",
                columns: new[] { "ConnectorHandlerTenantId", "ConnectorHandlerId", "IsEnabled", "TenantId" },
                values: new object[,]
                {
                    { 1, 4, true, new Guid("c9f0f895-fb98-4d75-8d81-7d7c7f4a6b1e") },
                    { 2, 3, true, new Guid("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0") },
                    { 3, 3, true, new Guid("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93") }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimConnectorConfigurations_HandlerId",
                table: "ClaimConnectorConfigurations",
                column: "HandlerId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimConnectorConfigurations_TenantId_HandlerId",
                table: "ClaimConnectorConfigurations",
                columns: new[] { "TenantId", "HandlerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorHandlerCascadingTenants_ConnectorHandlerId",
                table: "ConnectorHandlerCascadingTenants",
                column: "ConnectorHandlerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorHandlerCascadingTenants_TenantId_ConnectorHandlerId",
                table: "ConnectorHandlerCascadingTenants",
                columns: new[] { "TenantId", "ConnectorHandlerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorHandlers_ConnectorId_HandlerId",
                table: "ConnectorHandlers",
                columns: new[] { "ConnectorId", "HandlerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorHandlers_HandlerId",
                table: "ConnectorHandlers",
                column: "HandlerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorHandlerTenants_ConnectorHandlerId",
                table: "ConnectorHandlerTenants",
                column: "ConnectorHandlerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorHandlerTenants_TenantId_ConnectorHandlerId",
                table: "ConnectorHandlerTenants",
                columns: new[] { "TenantId", "ConnectorHandlerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Connectors_Type",
                table: "Connectors",
                column: "Type",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Handlers_Name",
                table: "Handlers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebApiConnectorConfigurationRoutes_HandlerId",
                table: "WebApiConnectorConfigurationRoutes",
                column: "HandlerId");

            migrationBuilder.CreateIndex(
                name: "IX_WebApiConnectorConfigurationRoutes_WebApiConnectorConfigurationId_HandlerId",
                table: "WebApiConnectorConfigurationRoutes",
                columns: new[] { "WebApiConnectorConfigurationId", "HandlerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebApiConnectorConfigurations_TenantId",
                table: "WebApiConnectorConfigurations",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClaimConnectorConfigurations");

            migrationBuilder.DropTable(
                name: "ConnectorHandlerCascadingTenants");

            migrationBuilder.DropTable(
                name: "ConnectorHandlerTenants");

            migrationBuilder.DropTable(
                name: "WebApiConnectorConfigurationRoutes");

            migrationBuilder.DropTable(
                name: "ConnectorHandlers");

            migrationBuilder.DropTable(
                name: "WebApiConnectorConfigurations");

            migrationBuilder.DropTable(
                name: "Connectors");

            migrationBuilder.DropTable(
                name: "Handlers");
        }
    }
}
