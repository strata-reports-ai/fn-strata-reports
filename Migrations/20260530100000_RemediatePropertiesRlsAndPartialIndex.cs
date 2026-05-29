using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StrataReports.Functions.Migrations
{
    public partial class RemediatePropertiesRlsAndPartialIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_properties_tenant_active;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON \"Properties\";");
            migrationBuilder.Sql("ALTER TABLE \"Properties\" DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS idx_properties_tenant_active ON \"Properties\" (\"TenantId\", \"DeletedAt\") WHERE \"DeletedAt\" IS NULL;");

            migrationBuilder.Sql("ALTER TABLE \"Properties\" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE \"Properties\" FORCE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'Properties' AND policyname = 'tenant_isolation'
                    ) THEN
                        CREATE POLICY tenant_isolation ON ""Properties""
                            USING (""TenantId""::text = current_setting('app.current_tenant_id', true));
                    END IF;
                END
                $$;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON \"Properties\";");
            migrationBuilder.Sql("ALTER TABLE \"Properties\" DISABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_properties_tenant_active;");
        }
    }
}
