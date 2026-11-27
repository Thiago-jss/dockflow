using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DockFlow.Infrastructure;

[DbContext(typeof(SchemaContext))]
[Migration("20261127000200_ProtectAuditHistory")]
public sealed class ProtectAuditHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        REVOKE UPDATE, DELETE, TRUNCATE ON public.audit_log FROM PUBLIC;
        DO $permissions$
        BEGIN
            IF EXISTS (SELECT FROM pg_roles WHERE rolname='dockflow' AND rolname<>current_user) THEN
                REVOKE UPDATE, DELETE, TRUNCATE ON public.audit_log FROM dockflow;
            END IF;
        END;
        $permissions$;
        """);

    // Do not restore unsafe write privileges when rolling back; the schema owner can grant them explicitly.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
