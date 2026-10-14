using System.Text.Json.Nodes;
namespace Loom.Core;

// Persist identity references, resolve human-readable names at execution time.
public sealed record ReportColumn(string FieldId, string? Heading = null, int Width = 20, string Alignment = "auto");
public sealed record ReportFilter(string FieldId, string Value);
public sealed record SavedReport(string Name, string TypeId, ReportColumn[] Columns,
    string? SortFieldId = null, bool Descending = false, ReportFilter? Filter = null);
public sealed record ReportHeading(string FieldId, string Name, string Label);
public sealed record ReportResult(int Total, ReportHeading[] Columns, JsonArray Rows);
public static class SavedReports
{
    public static ReportResult Run(SavedReport report, Catalog catalog, DocumentService service, Actor actor)
    {
        var type = catalog.Types.Values.SingleOrDefault(t => t.Id == report.TypeId)
            ?? throw new LoomException(422, "Report model no longer exists.");
        if (!service.Can(type.Name, actor, "read")) throw new LoomException(403, "Report access denied.");
        Field Resolve(string id) => type.Fields.SingleOrDefault(f => f.Id == id)
            ?? throw new LoomException(422, "Report field no longer exists: " + id);
        if (report.Columns.Length == 0 || report.Columns.Select(c => c.FieldId).Distinct().Count() != report.Columns.Length)
            throw new LoomException(422, "Reports need distinct columns.");
        var fields = report.Columns.Select(c => Resolve(c.FieldId)).ToArray();
        var page = service.List(type.Name, actor, sort: report.SortFieldId == null ? null : Resolve(report.SortFieldId).Name,
            descending: report.Descending, take: 200, field: report.Filter == null ? null : Resolve(report.Filter.FieldId).Name,
            equals: report.Filter?.Value);
        if (page.Total > 200) throw new LoomException(422, "Report exceeds 200 rows; refine its filter.");
        var rows = new JsonArray();
        foreach (var document in page.Items)
        {
            var row = new JsonArray();
            foreach (var field in fields) row.Add(field.Type=="Binary" && document.Values[field.Name]!=null ? JsonValue.Create("[Binary: "+Convert.FromBase64String(document.Values[field.Name]!.ToString()).Length+" bytes]") : document.Values[field.Name]?.DeepClone());
            rows.Add(row);
        }
        return new(page.Total, fields.Select((f, i) => new ReportHeading(f.Id!, f.Name, report.Columns[i].Heading ?? f.Label)).ToArray(), rows);
    }
}
