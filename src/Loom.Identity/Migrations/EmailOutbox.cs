using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Loom.Identity.Migrations;

/// <summary>Additive upgrade: existing users, roles and audit records remain untouched.</summary>
[DbContext(typeof(IdentityDatabase))]
[Migration("20261014230000_EmailOutbox")]
public sealed class EmailOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "LoomIdentityEmailOutbox" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_LoomIdentityEmailOutbox" PRIMARY KEY,
            "ProtectedPayload" TEXT NOT NULL,
            "Status" TEXT NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "ExpiresAt" INTEGER NOT NULL,
            "DueAt" INTEGER NOT NULL,
            "LeaseUntil" INTEGER NOT NULL,
            "LeaseToken" TEXT NULL,
            "Attempts" INTEGER NOT NULL,
            "FailureCode" TEXT NULL,
            "CompletedAt" INTEGER NULL
        );
        CREATE INDEX "IX_LoomIdentityEmailOutbox_Status_DueAt"
            ON "LoomIdentityEmailOutbox" ("Status", "DueAt");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Pending account mail must be resolved before an explicit database recovery.");
}
