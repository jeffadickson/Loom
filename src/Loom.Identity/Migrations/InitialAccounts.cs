using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Loom.Identity.Migrations;

[DbContext(typeof(IdentityDatabase))]
[Migration("20261014210000_InitialAccounts")]
public sealed class InitialAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if(ActiveProvider!="Microsoft.EntityFrameworkCore.Sqlite")throw new NotSupportedException("This Identity migration supports SQLite only.");
        using var stream=typeof(InitialAccounts).Assembly.GetManifestResourceStream("Loom.Identity.Migrations.InitialAccounts.sql")!;
        using var reader=new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>throw new NotSupportedException("Account database deletion requires explicit backup and recovery procedures.");
}
