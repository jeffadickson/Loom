# Tenant workspaces and recovery

This increment provides opt-in, database-per-tenant **SQLite** workspaces. Existing
single-workspace behavior remains available. It does not retrofit shared records with
tenant ownership or migrate those records automatically.

## Configure the demo host

Enable Identity or configured Accounts mode first, then set environment variables:

    Tenancy__Enabled=true
    Tenancy__DataDirectory=<absolute-private-directory>
    Tenancy__Tenants__0=north
    Tenancy__Tenants__1=south
    Tenancy__Memberships__0__UserId=<stable-account-id>
    Tenancy__Memberships__0__TenantId=north
    Tenancy__Memberships__0__Role=Admin

Use actual stable Identity IDs, not email addresses. Configured Accounts mode uses its
configured account IDs. Memberships are operator configuration in this initial host:
restart to apply configuration changes. Custom hosts can supply a live resolver; sessions
recheck it on each operation. Global Admin does not override tenant membership.

Startup provisions explicitly listed tenants. It refuses changed catalogs and incorrectly
bound files. Directory/database symlinks are rejected below the trusted configured root.
Do not allow untrusted users to write that root.

The tenant API supports document list/query/get/create/update/delete, audit and saved-report
execution under /api/tenants/{tenant}. It uses the same authenticated session and
X-Loom-Demo: local request-header CSRF boundary as the existing demo API. The existing
/api/documents routes remain a separate single workspace and are **not** tenant routes.

Use TenantWorkspace.Open(userId, tenantId) in C# and dispose the returned session after
each request. Its methods do not accept an actor/role from the caller. Underlying stores and
DocumentService are intentionally private. Existing field/row policies still apply.

## Backup and restore

From src/Loom.Demo, with the built DLL:

    dotnet bin/Release/net8.0/Loom.Demo.dll --tenant-backup <private-root> north <new-backup.zip>
    dotnet bin/Release/net8.0/Loom.Demo.dll --tenant-restore <new-private-root> north <backup.zip>

These are local operator commands; they do not start the host or require account login.
Restrict access through OS permissions. Backup can snapshot an active SQLite document
database. Restore must target a new absent database; do not restore while a host is
provisioning or serving that destination. The source archive and existing databases are
never overwritten. No archive-provided path is extracted.

A backup preserves IDs, exact values, binary bytes, links, catalog, audit, migration receipts
and report layouts. The default restore database-size limit is 512 MiB; trusted C# callers
can explicitly choose another bound. A tenant mismatch, checksum failure or failed integrity
check refuses restore without publishing a destination database.

Run a drill: restore to a new root, start a separate host with matching tenant configuration,
verify document counts, representative links, binary hashes, report output and audit, then
plan a maintenance-window cutover. Keep the original database until the drill is accepted.

**Coverage limits:** this archive excludes the shared Identity database, pending identity
email, Data Protection keys, external blobs/time-series databases and application configuration.
Back those up separately using their supported procedures. It does not transfer data between
providers. Archives are not encrypted or signed; store them privately with appropriate backup
encryption and access controls.
