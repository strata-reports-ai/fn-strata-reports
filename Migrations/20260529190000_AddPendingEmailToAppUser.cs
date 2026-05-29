using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StrataReports.Functions.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingEmailToAppUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingEmail",
                table: "Users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingEmail",
                table: "Users");
        }
    }
}
