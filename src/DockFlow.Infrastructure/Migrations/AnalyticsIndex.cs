using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DockFlow.Infrastructure;

[DbContext(typeof(SchemaContext))]
[Migration("20261127000300_AnalyticsIndex")]
public sealed class AnalyticsIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE INDEX audit_completed_by_day ON public.audit_log(tenant_id,occurred_at)
        WHERE kind='UnloadingCompleted';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP INDEX public.audit_completed_by_day;");
}
