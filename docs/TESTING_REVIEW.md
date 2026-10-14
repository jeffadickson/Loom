# Repository review and test layers

## Findings and fixes

1. **High: post-hook field validation differed across providers.** `DocumentService.Validate` rejected unknown input keys before hooks, but never checked keys introduced by a hook. Memory/server JSON retained undeclared keys while SQLite wrote only schema columns. The service now rejects undeclared post-hook keys before any insert. The regression test uses a substituted hook and real Memory storage.
2. **High: server schema agreement was checked only at startup.** A running server store could continue writing after the schema ledger changed. Each server transaction now compares the accepted model inside the same serializable transaction, before invoking business logic. Both Testcontainers dialects test refusal and confirm the callback was never executed. This detects ledger drift, not every possible manual physical-DDL change.
3. **Coverage gap: the original server executable had eight broad checks and no concurrent independent-connection test.** The new shared xUnit contract runs the same scenarios on Memory, SQLite, PostgreSQL and SQL Server. Server-specific tests cover schema drift and two independent connections competing for the same version.
4. **Remaining: scans and application-level uniqueness still limit server providers.** The new tests exercise correctness at small scale; they do not establish performance, a collation policy or database-native unique constraints. These remain under #9–#11. General physical-schema drift and migration coverage also remain open.

## What belongs in each test layer

| Layer | Implementation | What it establishes |
|---|---|---|
| Business behavior | xUnit with the real MemoryStore | Validation, exact values, snapshots, optimistic writes and transaction rollback without Docker |
| Interactions and failures | NSubstitute at IDocumentHook / IStoreSession boundaries | Authorization precedes storage; hook precedes insert; audit follows insert; injected audit failure rolls back a real insert |
| SQLite compatibility | Same inherited xUnit contract with SQLite `:memory:` | Actual SQLite SQL, storage types and exact numeric query behavior |
| Server compatibility | Same inherited contract with Testcontainers PostgreSQL and SQL Server | Actual drivers, dialects, transactions and serialization; additional drift and competing-writer tests |

MemoryStore is an in-memory implementation. SQLite `:memory:` is a real SQL engine using volatile storage. Neither proves PostgreSQL or SQL Server compatibility. Substitute calls do not prove transaction semantics; the real-store tests do that separately.

Sequence assertions are used only where ordering is part of correctness. The NSubstitute test combines `Received.InOrder` with exact received counts. Hook-output assertions and audit identity checks establish meaningful outcomes rather than checking every internal call.

## Running tests

```sh
# Fast, no Docker required: Memory, real SQLite, NSubstitute interaction tests
dotnet test tests/Loom.UnitTests

# Requires a running Docker-compatible daemon
dotnet test tests/Loom.ContainerTests --filter 'Database=PostgreSQL'
dotnet test tests/Loom.ContainerTests --filter 'Database=SQLServer'

# Retained legacy coverage; it has not been silently replaced
dotnet run --project tests/Loom.Tests -- .
```

Testcontainers owns startup/readiness, dynamic host ports and cleanup through xUnit's `IAsyncLifetime`. Each fixture shares one container across its class, while every test gets a newly named database. Database names are generated internally from GUIDs. No production connection strings are used. Missing Docker fails the container suite; it never turns compatibility tests into passing skips. CI runs fast tests first, then the two databases as separate matrix jobs and uploads TRX results even on failure. Image versions are pinned in the fixture; updating them is an explicit maintenance step.

The existing service-container smoke workflow remains as a separate gate while the broader legacy console suite is incrementally moved to named xUnit cases. There is no requirement to mock all repositories or replace production interfaces just to make tests possible.

## Upstream references

- [xUnit shared context and async fixture lifetime](https://xunit.net/docs/shared-context)
- [NSubstitute ordered calls](https://nsubstitute.github.io/help/received-in-order/)
- [NSubstitute callbacks](https://nsubstitute.github.io/help/callbacks/)
- [Testcontainers PostgreSQL](https://dotnet.testcontainers.org/modules/postgres/)
- [Testcontainers SQL Server](https://dotnet.testcontainers.org/modules/mssql/)
