using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Loom.Core;

public sealed record ReportTextBlock(string Id, string Text, int X = 0, int Y = 0, int Width = 40, int Height = 40, int FontSize = 16, string Source = "text", string? FieldId = null);

public sealed record ReportLayout(string Title, string? Subtitle = null, string? GroupFieldId = null,
    string[]? SumFieldIds = null, int FontSize = 14, string Font = "sans", string? Footer = null, string Orientation = "landscape", ReportTextBlock[]? HeaderBlocks = null, ReportTextBlock[]? FooterBlocks = null, string? PageHeader = null, string? PageFooter = null, bool PageNumbers = false);

public static class ReportWriter
{
    public static string Write(SavedReport definition, ReportLayout layout, Catalog catalog, DocumentService service, Actor actor)
    {
        ReportDesigns.Validate(definition, layout, catalog);
        var result = SavedReports.Run(definition, catalog, service, actor);
        var schema = catalog.Types.Values.Single(t => t.Id == definition.TypeId);
        int Index(string id)
        {
            var index = Array.FindIndex(result.Columns, c => c.FieldId == id);
            if (index < 0) throw new LoomException(422, "Grouping and totals must reference selected report columns.");
            return index;
        }
        var groupIndex = layout.GroupFieldId == null ? -1 : Index(layout.GroupFieldId);
        var sums = (layout.SumFieldIds ?? []).Distinct().Select(Index).ToArray();
        var fields = result.Columns.Select(c => schema.Fields.Single(f => f.Id == c.FieldId)).ToArray();
        if (sums.Any(i => fields[i].Type is not ("Integer" or "Decimal" or "Money"))) throw new LoomException(422, "Totals require numeric columns.");
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var linkTitles = new Dictionary<(string Type, string Id), string>();
        string LinkTitle(Field field, string id)
        {
            var key = (field.Target!, id);
            if (linkTitles.TryGetValue(key, out var cached)) return cached;
            string title;
            try
            {
                var target = catalog.Get(field.Target!);
                var linked = service.Get(target.Name, id, actor);
                title = linked.Values[target.TitleField]?.ToString() ?? id;
            }
            catch (LoomException ex) when (ex.Status == 403) { title = "[Restricted]"; }
            catch (LoomException ex) when (ex.Status == 404) { title = "[Unavailable]"; }
            linkTitles[key] = title;
            return title;
        }
        string Format(JsonNode? value, int i) => value == null ? "—" : fields[i].Type switch
        {
            "Link" => LinkTitle(fields[i], value.ToString()),
            "Boolean" => value.GetValue<bool>() ? "Yes" : "No",
            "Measurement" => Science.MeasurementJson.Format(value),
            "Money" => fields[i].Currency + " " + value.GetValue<decimal>().ToString("F"+(fields[i].Scale??2), CultureInfo.InvariantCulture),
            "Decimal" => value.GetValue<decimal>().ToString("#,0.00##########################", CultureInfo.InvariantCulture),
            "Integer" => value.GetValue<long>().ToString("N0", CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        var rows = result.Rows.Select(n => (JsonArray)n!).ToArray();
        var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>");
        b.Append(E(layout.Title)).Append("</title><style>").Append(Css).Append("body{font-size:").Append(layout.FontSize).Append("px;font-family:").Append(layout.Font switch { "serif"=>"Georgia,serif", "mono"=>"monospace", _=>"system-ui,sans-serif" }).Append("}@media print{body{font-size:").Append(layout.FontSize).Append("px}}@page{size:A4 ").Append(layout.Orientation).Append("}").Append(PageCss(layout)).Append("</style></head><body><main><header><p class=\"eyebrow\">LOOM · REPORT</p><h1>")
            .Append(E(layout.Title)).Append("</h1><p>").Append(E(layout.Subtitle)).Append("</p><p class=\"meta\">")
            .Append(result.Total).Append(" records · Generated ").Append(E(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'")))
            .Append("</p></header><p class=\"screen\">Use your browser’s Print command to print or save as PDF.</p>");
        decimal Sum(JsonArray[] data, int i)
        {
            return FieldValues.SumExact(data.Where(row=>row[i]!=null).Select(row=>decimal.Parse(row[i]!.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture)));
        }
        string Amount(decimal value,int i) => fields[i].Type=="Money" ? fields[i].Currency+" "+value.ToString("F"+(fields[i].Scale??2),CultureInfo.InvariantCulture) : value.ToString("#,0.00##########################",CultureInfo.InvariantCulture);
        string BlockText(ReportTextBlock block) => block.Text + (block.Source switch
        {
            "count" => rows.Length.ToString("N0", CultureInfo.InvariantCulture),
            "sum" => Amount(Sum(rows, Index(block.FieldId!)),Index(block.FieldId!)),
            _ => ""
        });
        void Blocks(ReportTextBlock[]? blocks)
        {
            if(blocks == null || blocks.Length == 0)return;
            b.Append("<div class=\"text-band\">");
            foreach(var block in blocks)
                b.Append("<div class=\"text-block\" style=\"left:").Append(block.X).Append("%;top:").Append(block.Y).Append("px;width:").Append(block.Width).Append("%;height:").Append(block.Height).Append("px;font-size:").Append(block.FontSize).Append("px\">").Append(E(BlockText(block))).Append("</div>");
            b.Append("</div>");
        }
        Blocks(layout.HeaderBlocks);
        void Totals(JsonArray[] data, string caption)
        {
            b.Append("<div class=\"totals\"><strong>").Append(E(caption)).Append(" · ").Append(data.Length).Append(" records</strong>");
            foreach (var i in sums)
            {
                var total = Sum(data, i);
                b.Append("<span>").Append(E(result.Columns[i].Label)).Append(": <b>").Append(E(Amount(total,i))).Append("</b></span>");
            }
            b.Append("</div>");
        }
        void Table(JsonArray[] data, string? heading)
        {
            b.Append("<section>");
            if (heading != null) b.Append("<h2>").Append(E(heading)).Append("</h2>");
            b.Append("<table><colgroup>");
            var totalWidth = definition.Columns.Sum(c=>c.Width);
            foreach(var column in definition.Columns) b.Append("<col style=\"width:").Append((100m*column.Width/totalWidth).ToString("0.###",CultureInfo.InvariantCulture)).Append("%\">");
            b.Append("</colgroup><thead><tr>");
            for(var i=0;i<result.Columns.Length;i++) b.Append("<th scope=\"col\" style=\"text-align:").Append(definition.Columns[i].Alignment=="auto"?"left":definition.Columns[i].Alignment).Append("\">").Append(E(result.Columns[i].Label)).Append("</th>");
            b.Append("</tr></thead><tbody>");
            foreach (var row in data)
            {
                b.Append("<tr>");
                for (var i = 0; i < result.Columns.Length; i++) b.Append("<td style=\"text-align:").Append(definition.Columns[i].Alignment=="auto"?(fields[i].Type is "Integer" or "Decimal" or "Money"?"right":"left"):definition.Columns[i].Alignment).Append("\">").Append(E(Format(row[i], i))).Append("</td>");
                b.Append("</tr>");
            }
            b.Append("</tbody></table>");
            Totals(data, heading == null ? "Total" : "Group total");
            b.Append("</section>");
        }
        if (rows.Length == 0) b.Append("<p>No matching records.</p>");
        else if (groupIndex < 0) Table(rows, null);
        else
        {
            foreach (var group in rows.GroupBy(row => row[groupIndex]?.ToJsonString()))
                Table(group.ToArray(), result.Columns[groupIndex].Label + ": " + Format(group.First()[groupIndex], groupIndex));
            Totals(rows, "Grand total");
        }
        Blocks(layout.FooterBlocks);
        b.Append("<footer>End of report · ").Append(E(layout.Footer ?? definition.Name)).Append("</footer></main></body></html>");
        return b.ToString();
    }
    private static string PageCss(ReportLayout layout)
    {
        // Hex escapes preserve Unicode and prevent quotes, braces or style tags escaping CSS strings.
        static string Quote(string? value) => "\"" + string.Concat((value ?? "").EnumerateRunes().Select(r => "\\" + r.Value.ToString("X", CultureInfo.InvariantCulture) + " ")) + "\"";
        return "@page{margin:22mm 15mm;@top-center{content:" + Quote(layout.PageHeader) + ";font:10pt sans-serif;color:#425b66}@bottom-left{content:" + Quote(layout.PageFooter) + ";font:9pt sans-serif;color:#425b66}@bottom-right{content:" + (layout.PageNumbers ? "\"Page \" counter(page) \" of \" counter(pages)" : "none") + ";font:9pt sans-serif;color:#425b66}}";
    }
    private const string Css = """
        *{box-sizing:border-box}.text-band{position:relative;height:120px;margin:12px 0;break-inside:avoid}.text-block{position:absolute;white-space:pre-wrap;overflow:hidden;overflow-wrap:anywhere}body{margin:0;background:#edf1f5;color:#172b3a;font:14px/1.5 system-ui,sans-serif}main{max-width:1100px;margin:32px auto;padding:40px;background:white}header{border-bottom:3px solid #176c70;margin-bottom:24px}h1{font-size:32px;margin:8px 0}h2{font-size:19px;margin:28px 0 12px}.eyebrow{color:#176c70;font-size:11px;letter-spacing:2px;font-weight:700}.meta,footer{color:#576b79;font-size:12px}.screen{background:#eef7f6;padding:12px}table{width:100%;border-collapse:collapse;table-layout:fixed}th{text-align:left;background:#eaf0f4;font-size:12px}th,td{padding:10px;border-bottom:1px solid #dbe3e8;overflow-wrap:anywhere}.number{text-align:right;font-variant-numeric:tabular-nums}.totals{display:flex;flex-wrap:wrap;gap:12px 24px;padding:14px 10px;background:#f3f7f8}.totals span{margin-left:auto}footer{margin-top:32px;border-top:1px solid #dbe3e8;padding-top:12px}@page{size:A4 landscape;margin:15mm}@media print{body{background:white;font-size:10pt}main{margin:0;padding:0;max-width:none}.screen{display:none}thead{display:table-header-group}tr,.totals{break-inside:avoid}h2{break-after:avoid}th,td{padding:6px}header{margin-bottom:12px}}@media(max-width:600px){main{margin:0;padding:16px}th,td{padding:5px;font-size:11px}h1{font-size:26px}}
        """;
}
