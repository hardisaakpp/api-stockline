using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ErpDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erp_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OriginWarehouse = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DestinationLocation = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    PurchaseOrderDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastFailureWasTransient = table.Column<bool>(type: "bit", nullable: false),
                    DocEntry = table.Column<long>(type: "bigint", nullable: true),
                    DocNum = table.Column<long>(type: "bigint", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erp_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "erp_document_lines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erp_document_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_erp_document_lines_erp_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "erp_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_erp_document_lines_DocumentId",
                table: "erp_document_lines",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_erp_documents_OrderId",
                table: "erp_documents",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erp_document_lines");

            migrationBuilder.DropTable(
                name: "erp_documents");
        }
    }
}
