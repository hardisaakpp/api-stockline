using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ErpAttemptsAndPrinting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erp_document_attempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Transient = table.Column<bool>(type: "bit", nullable: false),
                    DocNum = table.Column<long>(type: "bigint", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erp_document_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_erp_document_attempts_erp_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "erp_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_erp_document_attempts_DocumentId",
                table: "erp_document_attempts",
                column: "DocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erp_document_attempts");
        }
    }
}
