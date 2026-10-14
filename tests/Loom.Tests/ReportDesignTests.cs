using Loom.Core;
using Loom.Sqlite;
static class ReportDesignTests
{
 public static int Run(Catalog catalog,string temp)
 {
  int count=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;Console.WriteLine("PASS layout: "+message);}
  void Reject(int status,Action action,string message){try{action();throw new Exception("Expected refusal");}catch(LoomException ex){Check(ex.Status==status,message);}}
  var type=catalog.Get("Asset");var field=type.Fields.Single(f=>f.Name=="tag");var admin=new Actor("a","Admin");
  var design=new ReportDesign(0,new SavedReport("Equipment",type.Id!,[new(field.Id!,"Serial",40,"center")]),new ReportLayout("Equipment",HeaderBlocks:[new ReportTextBlock("abababababababababababababababab","Count: ",Source:"count")],FontSize:16,Font:"serif",Footer:"Internal",Orientation:"portrait"));
  var path=Path.Combine(temp,"layouts.db");
  using(var store=new SqliteStore("Data Source="+path))
  {
   store.Migrate(catalog);Check(store.ReadReportDesign(type.Id!)==null,"no saved layout uses default");
   Reject(403,()=>store.SaveReportDesign(type.Id!,design,new Actor("v","Viewer")),"save authorization");
   var saved=store.SaveReportDesign(type.Id!,design,admin);Check(saved.Version==1,"first save version");
   Reject(409,()=>store.SaveReportDesign(type.Id!,design,admin),"stale overwrite refused");
   Reject(422,()=>store.SaveReportDesign(type.Id!,saved with{Layout=saved.Layout with{Font="bad;url(x)"}},admin),"CSS injection refused");
   Reject(422,()=>store.SaveReportDesign(type.Id!,saved with{Definition=saved.Definition with{Columns=[new(field.Id!,Width:0)]}},admin),"invalid column width refused");
   Reject(422,()=>store.SaveReportDesign(type.Id!,saved with{Definition=saved.Definition with{Columns=[]}},admin),"empty columns refused");
   var block=new ReportTextBlock("cccccccccccccccccccccccccccccccc","<b>Note</b>",10,20,50,40,18);
   var decorated=saved with {Layout=saved.Layout with{HeaderBlocks=[block],FooterBlocks=[block with{Text="End note"}],PageHeader="</style><script>bad</script>",PageFooter="Controlled",PageNumbers=true}};
   var printed=ReportWriter.Write(decorated.Definition,decorated.Layout,catalog,new DocumentService(catalog,store,[]),admin);
   Check(printed.Contains("&lt;b&gt;Note&lt;/b&gt;")&&printed.Contains("left:10%;top:20px;width:50%"),"positioned text escaped and placed");
   Check(!printed.Contains("</style><script>")&&printed.Contains("counter(pages)"),"page text escaped and page counters emitted");
   Reject(422,()=>ReportDesigns.Validate(saved.Definition,saved.Layout with{HeaderBlocks=[block with{X=90}]},catalog),"horizontal band overflow refused");
   Reject(422,()=>ReportDesigns.Validate(saved.Definition,saved.Layout with{HeaderBlocks=[block with{Y=110}]},catalog),"vertical band overflow refused");
   Reject(422,()=>ReportDesigns.Validate(saved.Definition,saved.Layout with{HeaderBlocks=[block,block]},catalog),"duplicate block identities refused");
   Reject(422,()=>ReportDesigns.Validate(saved.Definition,saved.Layout with{PageHeader=new string('x',121)},catalog),"oversized page header refused");
   var next=new Catalog(catalog.Types.Values.Select(t=>t.Id!=type.Id?t:t with{Version=2,Fields=t.Fields.Select(f=>f.Id==field.Id?f with{Name="serialNumber"}:f).ToArray()}));
   store.Apply(store.Plan(next).Id,admin);
   var persisted=store.ReadReportDesign(type.Id!)!;ReportDesigns.Validate(persisted.Definition,persisted.Layout,next);Check(true,"saved IDs survive field rename");
   var html=ReportWriter.Write(persisted.Definition,persisted.Layout,next,new DocumentService(next,store,[]),admin);
   Check(html.Contains("Georgia,serif")&&html.Contains("size:A4 portrait")&&html.Contains("Internal"),"saved typography orientation footer rendered");
  }
  using(var store=new SqliteStore("Data Source="+path))
  {var loaded=store.ReadReportDesign(type.Id!)!;Check(loaded.Layout.HeaderBlocks![0].Source=="count"&&loaded.Version==1&&loaded.Definition.Columns[0].Width==40&&loaded.Definition.Columns[0].Alignment=="center","saved layout survives reopen");}
  return count;
 }
}
