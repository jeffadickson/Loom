using System.Text.Json;
using Loom.Core;
namespace Loom.Sqlite;
public sealed partial class SqliteStore : IReportDesignStore
{
    private void ReportTable()
    {
        using var command=connection.CreateCommand();
        command.CommandText="CREATE TABLE IF NOT EXISTS _report_designs(typeId TEXT PRIMARY KEY, version INTEGER NOT NULL, definition TEXT NOT NULL, actor TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }
    public ReportDesign? ReadReportDesign(string typeId)
    {
        lock(gate)
        {
            ReportTable();using var c=connection.CreateCommand();c.CommandText="SELECT definition FROM _report_designs WHERE typeId=$id";c.Parameters.AddWithValue("$id",typeId);
            return c.ExecuteScalar() is string json?JsonSerializer.Deserialize<ReportDesign>(json,Catalog.Json):null;
        }
    }
    public ReportDesign SaveReportDesign(string typeId,ReportDesign design,Actor actor)
    {
        if(actor.Role!="Admin")throw new LoomException(403,"Only administrators can save report layouts.");
        lock(gate)
        {
            ReportTable();using var tx=connection.BeginTransaction();
            var accepted=ReadCatalogCore(tx)??throw new LoomException(409,"Missing accepted schema.");
            if(typeId!=design.Definition?.TypeId)throw new LoomException(422,"Report model mismatch.");
            ReportDesigns.Validate(design.Definition,design.Layout,accepted);
            using var c=connection.CreateCommand();c.Transaction=tx;
            c.CommandText="SELECT version FROM _report_designs WHERE typeId=$id";c.Parameters.AddWithValue("$id",typeId);
            var old=c.ExecuteScalar();var version=old==null?0:Convert.ToInt32(old);
            if(design.Version!=version)throw new LoomException(409,"Report layout changed. Reload before saving.");
            var saved=design with {Version=checked(version+1)};
            c.CommandText="INSERT INTO _report_designs(typeId,version,definition,actor) VALUES($id,$v,$json,$actor) ON CONFLICT(typeId) DO UPDATE SET version=$v,definition=$json,actor=$actor";
            c.Parameters.AddWithValue("$v",saved.Version);c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(saved,Catalog.Json));c.Parameters.AddWithValue("$actor",actor.Id);c.ExecuteNonQuery();tx.Commit();return saved;
        }
    }
}
