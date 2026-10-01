using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplierFlowAndRedistribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentOrderId",
                table: "orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RedistributionVersion",
                table: "orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ConfirmedQuantity",
                table: "order_lines",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RequestedQuantity",
                table: "order_lines",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SupplyingWarehouseCode",
                table: "locations",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_locations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SupplyingWarehouseCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_locations_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_redistribution_draft",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    LocationCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_redistribution_draft", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_redistribution_draft_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "ST-001",
                column: "SupplyingWarehouseCode",
                value: "WH-MAIN");

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "ST-002",
                column: "SupplyingWarehouseCode",
                value: "WH-MAIN");

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "ST-003",
                column: "SupplyingWarehouseCode",
                value: "WH-MAIN");

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "ST-004",
                column: "SupplyingWarehouseCode",
                value: "WH-OUTLET");

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "ST-005",
                column: "SupplyingWarehouseCode",
                value: "WH-OUTLET");

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "ST-006",
                column: "SupplyingWarehouseCode",
                value: "WH-REGIONAL");

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "ST-099",
                column: "SupplyingWarehouseCode",
                value: "WH-MAIN");

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "WH-MAIN",
                column: "SupplyingWarehouseCode",
                value: null);

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "WH-OUTLET",
                column: "SupplyingWarehouseCode",
                value: null);

            migrationBuilder.UpdateData(
                table: "locations",
                keyColumn: "Code",
                keyValue: "WH-REGIONAL",
                column: "SupplyingWarehouseCode",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_orders_ParentOrderId",
                table: "orders",
                column: "ParentOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_locations_OrderId",
                table: "order_locations",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_redistribution_draft_OrderId",
                table: "order_redistribution_draft",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_locations");

            migrationBuilder.DropTable(
                name: "order_redistribution_draft");

            migrationBuilder.DropIndex(
                name: "IX_orders_ParentOrderId",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ParentOrderId",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "RedistributionVersion",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ConfirmedQuantity",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "RequestedQuantity",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "SupplyingWarehouseCode",
                table: "locations");
        }
    }
}
