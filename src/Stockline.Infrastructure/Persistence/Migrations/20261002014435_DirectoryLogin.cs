using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DirectoryLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "users",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalProvider",
                table: "users",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_ExternalProvider_ExternalId",
                table: "users",
                columns: new[] { "ExternalProvider", "ExternalId" },
                unique: true,
                filter: "[ExternalProvider] IS NOT NULL AND [ExternalId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_ExternalProvider_ExternalId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ExternalProvider",
                table: "users");
        }
    }
}
