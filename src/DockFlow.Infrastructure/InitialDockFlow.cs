using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DockFlow.Infrastructure;

[DbContext(typeof(SchemaContext))]
[Migration("20261127000100_InitialDockFlow")]
public sealed class InitialDockFlow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        CREATE TABLE public.tenants (id uuid PRIMARY KEY);
        CREATE TABLE public.carriers (
            tenant_id uuid NOT NULL REFERENCES public.tenants(id), id uuid NOT NULL,
            PRIMARY KEY (tenant_id,id)
        );
        CREATE TABLE public.docks (
            id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES public.tenants(id),
            code text NOT NULL, cargo text NOT NULL CHECK (cargo IN ('General','Refrigerated')),
            max_weight_kg integer NOT NULL CHECK (max_weight_kg > 0), operational boolean NOT NULL DEFAULT true,
            UNIQUE (tenant_id,id), UNIQUE (tenant_id,code)
        );
        CREATE TABLE public.visits (
            id uuid PRIMARY KEY, tenant_id uuid NOT NULL, carrier_id uuid NULL, dock_id uuid NOT NULL,
            vehicle text NOT NULL, cargo text NOT NULL CHECK (cargo IN ('General','Refrigerated')),
            weight_kg integer NOT NULL CHECK (weight_kg > 0), starts_at timestamptz NOT NULL, ends_at timestamptz NOT NULL,
            status text NOT NULL CHECK (status IN ('Scheduled','CheckedIn','Assigned','InProgress','Completed','Cancelled','NoShow')),
            version bigint NOT NULL CHECK (version >= 1),
            CHECK (starts_at < ends_at AND extract(second FROM starts_at) = 0 AND extract(second FROM ends_at) = 0),
            UNIQUE (tenant_id,id), FOREIGN KEY (tenant_id,dock_id) REFERENCES public.docks(tenant_id,id),
            FOREIGN KEY (tenant_id,carrier_id) REFERENCES public.carriers(tenant_id,id),
            CONSTRAINT visits_no_overlapping_reservations EXCLUDE USING gist (
                tenant_id WITH =, dock_id WITH =, tstzrange(starts_at,ends_at,'[)') WITH &&
            ) WHERE (status IN ('Scheduled','CheckedIn','Assigned','InProgress'))
        );
        CREATE UNIQUE INDEX visits_one_physical_occupant ON public.visits(tenant_id,dock_id)
            WHERE status IN ('Assigned','InProgress');
        CREATE INDEX visits_by_actor ON public.visits (tenant_id,carrier_id,starts_at,id);
        CREATE TABLE public.booking_requests (
            tenant_id uuid NOT NULL REFERENCES public.tenants(id), carrier_id uuid NULL,
            subject text NOT NULL, request_key text NOT NULL, fingerprint text NOT NULL, response jsonb NOT NULL,
            FOREIGN KEY (tenant_id,carrier_id) REFERENCES public.carriers(tenant_id,id),
            CONSTRAINT booking_requests_actor_key UNIQUE NULLS NOT DISTINCT (tenant_id,carrier_id,subject,request_key)
        );
        CREATE TABLE public.audit_log (
            sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            id uuid NOT NULL UNIQUE, tenant_id uuid NOT NULL REFERENCES public.tenants(id),
            aggregate_id uuid NOT NULL, subject text NOT NULL, carrier_id uuid NULL, kind text NOT NULL,
            occurred_at timestamptz NOT NULL, detail jsonb NOT NULL,
            FOREIGN KEY (tenant_id,carrier_id) REFERENCES public.carriers(tenant_id,id)
        );
        CREATE INDEX audit_by_aggregate ON public.audit_log (tenant_id,aggregate_id,sequence);
        CREATE TABLE public.outbox (
            id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES public.tenants(id),
            kind text NOT NULL, aggregate_id uuid NOT NULL, occurred_at timestamptz NOT NULL, published_at timestamptz NULL
        );
        CREATE INDEX outbox_unpublished ON public.outbox (occurred_at,id) WHERE published_at IS NULL;
        CREATE TABLE public.inbox (id uuid PRIMARY KEY, processed_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE public.incidents (
            id uuid PRIMARY KEY, tenant_id uuid NOT NULL, dock_id uuid NOT NULL, visit_id uuid NULL,
            kind text NOT NULL, detected_at timestamptz NOT NULL, status text NOT NULL,
            FOREIGN KEY (tenant_id,dock_id) REFERENCES public.docks(tenant_id,id),
            FOREIGN KEY (tenant_id,visit_id) REFERENCES public.visits(tenant_id,id),
            CONSTRAINT incidents_identity UNIQUE NULLS NOT DISTINCT (tenant_id,dock_id,visit_id,kind)
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE public.incidents, public.inbox, public.outbox, public.audit_log, public.booking_requests,
            public.visits, public.docks, public.carriers, public.tenants;
        """);
}
