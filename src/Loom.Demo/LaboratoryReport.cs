using System.Net;
using System.Text;
using Loom.Core;
namespace Loom.Demo;

public static class LaboratoryReport
{
    public static string Write(string id, Catalog catalog, DocumentService service, Actor actor)
    {
        var run=service.Get("LabRun",id,actor);
        var documents=new List<Document>{run};
        foreach(var field in catalog.Get("LabRun").Fields.Where(f=>f.Type=="Link"))
            if(run.Values[field.Name] is { } value) documents.Add(service.Get(field.Target!,value.ToString(),actor));
        string E(string value)=>WebUtility.HtmlEncode(value);
        var html=new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Laboratory run report</title><style>body{font:15px system-ui;max-width:900px;margin:40px auto;color:#172c31}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:9px;border-bottom:1px solid #ccc;overflow-wrap:anywhere}th{width:30%}section{break-inside:avoid}h1{font-size:28px}@media print{body{margin:0}}</style><h1>Laboratory run report</h1>");
        foreach(var document in documents)
        {
            var schema=catalog.Get(document.Type);html.Append("<section><h2>").Append(E(schema.Label)).Append("</h2><table>");
            foreach(var field in schema.Fields.Where(f=>service.CanReadField(f,actor)))
            {
                var value=document.Values[field.Name];
                var display=field.Type=="Binary" && value!=null?$"Attachment: {Convert.FromBase64String(value.ToString()).Length} bytes":value?.ToString()??"—";
                if(field.Type=="Link" && value!=null)
                {
                    var linked=documents.SingleOrDefault(d=>d.Id==value.ToString());
                    if(linked!=null) display=linked.Values[catalog.Get(linked.Type).TitleField]?.ToString()??"Linked record";
                }
                if(field.Type=="Measurement" && value!=null) display=$"{value["value"]} {value["unit"]} · standard uncertainty {value["standardUncertainty"]} {value["unit"]}";
                html.Append("<tr><th>").Append(E(field.Label)).Append("</th><td>").Append(E(display)).Append("</td></tr>");
            }
            html.Append("</table><p>Record ").Append(E(document.Id)).Append(" · version ").Append(document.Version).Append("</p></section>");
        }
        return html.Append("</html>").ToString();
    }
}
