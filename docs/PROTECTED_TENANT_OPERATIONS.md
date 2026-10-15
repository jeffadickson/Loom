# Protected tenant operations

A tenant is an organization with its own database and membership list. A tenant administrator can
inspect and control that organization's deliveries and recurring schedules. A global account role
alone does not grant tenant access. Encryption protects queued message contents; retention limits
how long failed contents remain available for retry.

## Run the example

```
dotnet run --project src/Loom.Demo -- --tenant-operations-demo
```

Open `/jobs.html`. The demo password is `Loom-jobs-demo!`:

| Account | Access |
| --- | --- |
| jobs-admin | north administration |
| jobs-south | south administration |
| jobs-both | north and south; organization selector |
| jobs-global | global Admin role but no tenant membership; denied |
| jobs-viewer | no tenant administration; denied |

The flag supplies local demonstration accounts only and refuses to replace configured authentication.
The existing `--jobs-demo` remains the single-workspace example. Tenant jobs use separate databases
under `data/tenant-jobs` by default. Only local receipt/failure handlers are registered; no network
messages are sent. Receipts contain a generic acknowledgement, not a copy of the protected message.

`GET /api/jobs/contexts` lists only organizations the signed-in subject may administer. Operations use
`/api/jobs/tenants/{tenant}/...` for delivery lists/creation/retry/cancel and schedule creation/pause/resume.
The server resolves current configured membership for each operation, independent of global roles and
role headers. Payloads and lease tokens are omitted from list and create responses. Existing custom-header
and same-origin request protections still apply. Changing organizations clears displayed rows, closes
confirmation dialogs and rejects stale list responses. Logout clears the organization selector and data.

## Configure an authenticated host

Set `Jobs:TenantsEnabled=true`, `Jobs:TenantDataDirectory`, an explicit `Jobs:Tenants` array and
`Jobs:Memberships` entries with `UserId`, `TenantId`, `Role`. Operators need tenant `Admin`; the reserved
host service identity `jobs-worker` needs `Worker` membership for each tenant it processes. Do not grant
that service identity to an interactive account. Memberships are read from the live configuration
provider; changes take effect when that provider reloads. Route lists and key rings are startup snapshots.

Set `Jobs:FailedPayloadRetentionHours` (default 168, range 1–87600) and `Jobs:KeyRingFile` to an
operator-provisioned JSON key ring outside the web root. Production mode refuses to create missing keys.
Example structure (replace the placeholder with base64 encoding of 32 cryptographically random bytes):

```json
{"CurrentKey":"key-2026-10","Keys":{"key-2026-10":"BASE64_32_RANDOM_BYTES"}}
```

Use a secret store or a restricted file provisioned by deployment tooling. Never commit real keys.
The demo creates a random key file once and reuses it across restarts; Unix creation permissions are
0600. On Windows, restrict the directory/file ACL to the service identity. The demo default stores keys
beside the tenant directories for convenience; production should separate key storage and database
backups. Possession of both keys and ciphertext permits decryption. Disk encryption, filesystem ACLs,
TLS, backup controls and a production deployment review remain necessary deployment concerns.

To rotate, add a new named 256-bit key, select it as `CurrentKey`, and restart. Retain old keys while
queued deliveries or recurring schedules still need them. Old envelopes name their key. Rotation does
not re-encrypt existing rows. Losing keys prevents recovery; archive them securely with a separately
controlled recovery process. Do not remove a key just to expire one message: other messages may use it.

## Core APIs and guarantees

`IDeliveryPayloadProtection` is a host-configurable seam. `AesDeliveryPayloadProtection` implements
AES-256-GCM using .NET's cryptographic implementation, random 96-bit nonces and 128-bit tags. The
versioned envelope contains the key ID. Authenticated associated data binds the configured tenant/application
scope and the type, key (schedule name for recurring definitions), and handler. Ciphertext moved to another
tenant, altered bytes or a changed queue identity fail authentication. There is no plaintext fallback
when protection is configured. Invalid/unreadable delivery envelopes enter Failed without invoking handlers;
operators can restore the necessary key and retry before retention expires. Invalid schedules report a
preparation failure without stopping existing deliveries; repair or pause them through trusted operations.

Pass the same configured protection policy to `DeliveryOutbox` used for enqueue, schedules and workers.
Recurring definitions are encrypted too; each occurrence is decrypted and encrypted for its own identity.
The stored Binary limit remains 64 KiB, including envelope overhead (30 bytes plus the ASCII key-ID length).
Hosts must use the same scope/key ring across restarts. Existing unprotected applications keep their current
behavior unless protection is explicitly configured. Do not point a protected host at an unreviewed plaintext
queue; conversion requires an explicit migration and recovery plan.

`AuditRedactionPolicy` identifies fields by stable field ID. Pass it to every `DocumentService` or
`TenantWorkspace` writing the protected catalog. It copies and removes configured values from new before/after
audit snapshots, including writes inside Atomic callbacks. Live documents are unchanged. The tenant host
redacts Payload and LeaseToken while retaining actor, action, version and status metadata. This policy is
application configuration, not protection against privileged direct database writes or an unconfigured writer.

Completed and cancelled deliveries immediately clear their current payload. `ExpireFailedPayloads` scans
at most 25 old failures per pass by default, rechecks status/version in a transaction, clears their payload,
and marks them Cancelled with `Payload retention expired.` The unique key and metadata remain; retry is
then denied. Failed DueAt records the terminal failure instant for new failures. Retention additionally
checks ModifiedAt so legacy old due times cannot expire a recently edited failure early. Pending, leased
and recurring definition payloads are not expired by this method. Pausing a schedule retains its encrypted
payload for resume. Backlogs and repeated edits can delay cleanup; this is bounded background maintenance,
not a hard-time deletion guarantee. Pausing the worker pauses cleanup as well.

This is logical data minimization, **not secure erasure**. Existing audit rows are not rewritten. SQLite
WAL/free pages, filesystem snapshots, replicas and backups can retain historical bytes. Configure backup
retention and review any historical cleanup separately. The current change does not purge metadata or
receiver deduplication keys, and it does not claim cryptographic per-message erasure.

## Verification

Unit tests cover authentication failure, scope/purpose binding, key rotation, encrypted reopen, raw
stored audit minimization, protected recurrence, bounded failed-payload retention and non-retryability.
The real cookie/browser test covers scoped organization selection, stale-row clearing, pause/logout,
mobile layout, cross-tenant API denial and denial of a global Admin without membership. A restart drill
reuses persisted keys and databases and verifies delivery stays in its original tenant. Both single-workspace
and tenant operations browser tests run in cross-platform CI. Production key management, retrospective
audit cleanup and large-scale qualification remain open in issue #24.

## Offline key-management commands

Run these before starting the web host. Provision a private parent directory first; commands do not
create directories or overwrite existing destination files.

```
dotnet run --project src/Loom.Demo -- --jobs-key-create /private/loom/keys-v1.json
dotnet run --project src/Loom.Demo -- --jobs-key-check /private/loom/keys-v1.json
dotnet run --project src/Loom.Demo -- --jobs-key-rotate /private/loom/keys-v1.json /private/loom/keys-v2.json
```

Use equivalent absolute paths on Windows. Commands print only the active key identifier and retained-key
count, never the key bytes. Errors return a nonzero exit status. The core `DeliveryPayloadKeyRingFile`
API provides the same create/inspect/open/rotate operations for deployment tooling. File parsing is strict:
64 KiB maximum, 256 keys maximum, unique properties/IDs, an existing active ID, and 32-byte decoded keys.
The existing `CurrentKey`/`Keys` JSON format is retained. Symbolic-link key files are rejected; parent
paths must still be trusted and protected from replacement by other users.

Creation and rotation write a random-named temporary file in the destination directory, flush it, then
rename without overwrite. An exclusively created destination `.lock` file serializes cooperating
publishers because no-overwrite rename alone can race on some filesystems. Readers see a complete file.
If a process crashes, a lock or temporary file may remain; the command fails closed. Stop all key
operations, inspect the destination with --jobs-key-check, and remove stale files only after confirming
there is no active publisher. Never use other tools to write the destination concurrently.
Unix files are created with mode 0600; Windows inherits the parent ACL, which the operator must restrict.
This protects against partial writes during ordinary interruption; it is not a claim of guaranteed
filesystem durability through every power-loss scenario. Backups remain necessary.

Use this rotation sequence:

1. Pause/stop all producers and workers sharing the ring. A running process holds a startup snapshot.
2. Rotate to a **new** destination file. Every old key is retained and a fresh active key is added.
3. Validate and securely back up the new ring, separately from the database backup.
4. Set `Jobs:KeyRingFile` to the new file and restart all participating processes together.
5. Verify pending deliveries and recurring definitions still decode before resuming normal operation.

Do not switch only producers: older workers cannot read messages encrypted with the new key. Once
new-key messages exist, reverting to the old ring alone is not a safe rollback. Keep the new ring even
if application code is rolled back. The commands deliberately do not remove keys or rewrite ciphertext;
key retirement requires an inventory of queued deliveries, recurring definitions and retained backups.

The CI restart drill now rotates while the host is stopped, copies the rotated ring to a separate
recovery file, validates it, checks command output for secret leakage, and restarts against that copy.
A delivery encrypted under the original key must complete. Core tests additionally verify newly encrypted
messages survive recovery and cannot be opened by an old-only ring, plus duplicate/malformed rejection,
non-overwrite, concurrent publication and Unix file permissions. This qualifies the local file workflow;
cloud KMS integration and production disaster-recovery exercises remain separate work.
