using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StrataReports.Functions.Migrations
{
    public partial class AddTrialEndsAtDefaultAndBillingColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"Tenants\" SET \"TrialEndsAt\" = NOW() + INTERVAL '14 days' WHERE \"TrialEndsAt\" IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
