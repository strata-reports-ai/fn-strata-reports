using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StrataReports.Functions.Migrations
{
    /// <inheritdoc />
    public partial class AddMissingAuditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "TaskRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "TaskRecords",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "ReviewRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "ReviewRecords",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "RevenueRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "RevenueRecords",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "InspectionRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "InspectionRecords",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Imports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "ExpenseRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "ExpenseRecords",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "TaskRecords");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "TaskRecords");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ReviewRecords");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ReviewRecords");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "RevenueRecords");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "RevenueRecords");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "InspectionRecords");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "InspectionRecords");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ExpenseRecords");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ExpenseRecords");
        }
    }
}
