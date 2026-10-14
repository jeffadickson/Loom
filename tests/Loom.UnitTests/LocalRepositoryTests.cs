using Loom.Core;
using Loom.Sqlite;
namespace Loom.Tests;
public sealed class MemoryRepositoryTests:RepositoryContract
{
    protected override IDocumentStore Open()=>new MemoryStore();
}
public sealed class SqliteRepositoryTests:RepositoryContract
{
    protected override IDocumentStore Open()=>new SqliteStore("Data Source=:memory:");
}
