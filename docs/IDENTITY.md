# Loom account management

`Loom.Identity` supplies ASP.NET Core Identity-backed local accounts and its
account-management UI. This is separate from the older `Accounts` mode, which
still exists only for compatibility with the configured-account demonstration.

## Run locally

Build first with `python tools/dev.py build`. Configure the host:

```text
ASPNETCORE_ENVIRONMENT=Development
Authentication__Mode=Identity
Identity__PublicOrigin=http://127.0.0.1:5080
Identity__DataDirectory=<absolute-private-data-directory>
Identity__Email__PickupDirectory=<absolute-private-mail-directory>
Identity__AllowRegistration=true
```

Set environment variables using your shell's syntax (PowerShell `$env:NAME=...`,
Bash `export NAME=...`). These paths must be outside the web root and source control.
The pickup directory is a local test mailbox: files contain actual confirmation
and recovery links. Treat them as credentials; it is forbidden outside Development.
No email is sent in pickup mode.

Create the first administrator by running the built demo DLL with
`--identity-admin=you@example.com`; enter a strong password on standard input.
For example, from `src/Loom.Demo`:

```text
dotnet bin/Release/net8.0/Loom.Demo.dll --identity-admin=you@example.com
dotnet bin/Release/net8.0/Loom.Demo.dll --memory-demo
```

The bootstrap operation refuses to run once an Admin exists. It records the action
in the identity audit and marks this locally provisioned administrator verified.
Do not put passwords in command arguments, scripts or repository configuration.
Business data can be in memory while accounts remain persistent.

Open `/Identity/Account/Login`. Account settings are at
`/Identity/Account/Manage`, including email, password, authenticator setup and
recovery codes. Administrators use `/accounts.html` to create users, assign
Viewer/Editor/Admin roles, disable/re-enable users and send password-reset email.
New admin-created users receive confirmation and password setup links; administrators
do not choose or see their passwords. The last enabled Admin is protected.

Self-registration defaults to **off**. When enabled, new users must confirm email
and receive only the Viewer effective role. Registration has no role input.
Users retain stable IDs when their email changes. Use that ID for application row
ownership; the laboratory's Operator field expects an account ID in Identity mode.

## Email and deployment

Replace pickup with these settings for SMTP:

```text
Identity__PublicOrigin=https://localhost:5080
Identity__Email__SmtpHost=<mail-host>
Identity__Email__Port=587
Identity__Email__From=<verified-sender>
Identity__Email__Username=<secret-configuration>
Identity__Email__Password=<secret-configuration>
```

SMTP requires TLS. There is no silent sender fallback. Applications can replace
`IIdentityEmailTransport` through dependency injection while retaining the durable queue. The fixed PublicOrigin prevents
request-host poisoning of confirmation and recovery links.

Outside Development, HTTPS is required and the session cookie is Secure.
Configure an ASP.NET HTTPS certificate for the demo host; this host still only
accepts loopback requests. A separately hosted application can use `AddLoomIdentity`,
`InitializeLoomIdentityAsync` and `UseLoomIdentity`, with authentication/rate-limiting
middleware and static files enabled. Production reverse-proxy trust and certificate
management are deployment responsibilities, not automatically configured here.

The dedicated SQLite schema is initialized by a committed EF migration and records
its version in `__EFMigrationsHistory`. Future upgrades require new reviewed migrations;
never edit the initial SQL after release. Coordinate account-schema upgrades before
starting multiple host instances. `--identity-schema` exports the current model DDL
for review without creating accounts.

The private data directory contains the account SQLite database and Data Protection
keys. Back up both. Restrict filesystem access and encrypt the storage according to
your deployment. Persisting keys enables confirmation links and sessions to survive
restarts; it does not encrypt the directory at rest. Use a distinct
`Identity__ApplicationName` for each application security boundary.

## Security behavior

- Identity handles password hashing, reset/confirmation tokens, TOTP and recovery codes.
- Passwords require at least 12 characters with the Identity complexity rules.
- Five failed password attempts lock the account for 15 minutes; UI endpoints are rate-limited.
- Confirmation/reset tokens expire after one hour. Password reset invalidates prior sessions and reset-token reuse.
- Security stamps are validated on each authenticated request. Role changes and disablement revoke existing sessions.
- Razor account forms use antiforgery validation. Administrative JSON operations require an antiforgery cookie and `X-CSRF-TOKEN` obtained from the authenticated token endpoint.
- Administrative operations are audited without passwords, tokens or authenticator secrets.
- Account roles map to the existing Loom document permissions. Account authentication does not establish tenant/team membership.

## Optional organization sign-in

Configure `Identity__Oidc__Authority`, `ClientId`, `ClientSecret` and optional
`DisplayName`. The standard OpenID Connect middleware uses authorization code +
PKCE, HTTPS metadata and the Identity external-login cookie. Register the callback
`/signin-oidc` with the provider. Secrets belong in deployment configuration.
External users receive no automatic Admin role; account linking uses Identity's
explicit external-login flow. A live organization/provider integration must be
verified with that provider's credentials before deployment; local lifecycle tests
do not claim external-provider interoperability.

## Verification

`tools/test_identity.py` exercises the real Razor UI with Playwright, a temporary
persistent account database and captured email. It checks registration/confirmation,
password change/recovery, reset replay, MFA, administration, disabled-session rejection,
role spoofing, CSRF and restart durability. Run it through the normal browser target.

This increment does not implement tenant membership administration, external-provider
provisioning, SMTP infrastructure, passwordless login or automatic migration of the
legacy configured-account list. Those are separate tracked capabilities.

## Durable email delivery

Account email now queues durably. Registration, admin creation and password recovery
return once the account/queue transaction commits; an unavailable SMTP service is
retried in the background. The worker starts with the host. No extra service is
required for a single host.

Administrators can open **Account email delivery** on `/accounts.html` to refresh
the latest 100 delivery states and cancel pending, unclaimed messages. Cancellation
cannot recall an email already handed to SMTP. The API is
`GET /identity-admin/api/email-deliveries` and antiforgery-protected
`POST /identity-admin/api/email-deliveries/{id}/cancel`.
Diagnostics intentionally omit addresses, subjects and token-bearing message bodies.

- **Pending:** waiting for a worker or retry; at most five attempts.
- **Sent:** the configured transport accepted the message, not proof it reached an inbox.
- **Dead:** retries exhausted or the protected payload could not be read.
- **Expired:** the 55-minute queue lifetime elapsed before delivery.
- **Cancelled:** an administrator cancelled unclaimed work.

Retries wait 1, 2, 4 and 8 minutes. A crashed worker's lease becomes reclaimable after
120 seconds. SMTP delivery may duplicate a message if a crash occurs after SMTP
acceptance but before acknowledgement. The stable envelope ID is available to custom
transports for deduplication. Local pickup deduplicates by that ID automatically.

The database stores encrypted envelopes using the same persisted Data Protection
keys as Identity. Back up the database **and keys**. Terminal messages erase their
payload; delivery metadata is removed after seven days by the running worker.
Development pickup files remain your responsibility to remove after testing.

For a different provider, register an `IIdentityEmailTransport` implementation
after `AddLoomIdentity`. Honor its cancellation token; do not log the envelope.
Keep the durable `IEmailSender` registration. The current host still validates SMTP
or explicit development-pickup configuration at startup.
