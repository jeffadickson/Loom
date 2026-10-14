using Loom.Core;
using Loom.Demo;
using System.Text.Json.Nodes;

static class ReportWriterTests
{
 public static int Run(Catalog catalog)
 {
  int count=0;
  void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;Console.WriteLine("PASS writer: "+message);}
  using var store=new MemoryStore();store.Migrate(catalog);
  var service=new DocumentService(catalog,store,[new WorkOrderHook()]);var actor=new Actor("writer","Admin");
  var asset=service.Create("Asset",new JsonObject{["name"]="<script>alert(1)</script>",["tag"]="A-1",["location"]="Lab",["category"]="Electronics",["active"]=true},actor);
  var type=catalog.Get("WorkOrder");
  string Id(string name)=>type.Fields.Single(f=>f.Name==name).Id!;
  var report=new SavedReport("Work",type.Id!,[new(Id("title")),new(Id("priority")),new(Id("estimate")),new(Id("asset"))],Id("title"));
  var layout=new ReportLayout("Work <orders>","Quarterly",Id("priority"),[Id("estimate")]);
  Check(ReportWriter.Write(report,layout,catalog,service,actor).Contains("No matching records"),"empty report");
  foreach(var item in new[]{("First <script>","High",2m),("Second","High",3m),("Third","Routine",1m)})
   service.Create("WorkOrder",new JsonObject{["title"]=item.Item1,["asset"]=asset.Id,["priority"]=item.Item2,["due"]="2027-01-01",["hours"]=item.Item3,["rate"]=10m},actor);
  var html=ReportWriter.Write(report,layout,catalog,service,actor);
  Check(html.Contains("Work &lt;orders&gt;") && html.Contains("First &lt;script&gt;") && !html.Contains("<script>"),"title and values HTML escaped");
  Check(html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;") && !html.Contains(asset.Id),"linked records use escaped readable titles");
  Check(html.Contains("Priority: High") && html.Contains("Priority: Routine"),"groups rendered");
  Check(html.Contains("50.00") && html.Contains("60.00") && html.Contains("Grand total · 3 records"),"group and grand sums exact");
  Check(html.Contains("@media print") && html.Contains("table-header-group"),"print stylesheet and repeated headings");
  try{ReportWriter.Write(report,layout,catalog,service,new Actor("x","Unknown"));throw new Exception("Expected denial");}catch(LoomException e){Check(e.Status==403,"permission enforced");}
  try{ReportWriter.Write(report,layout with{SumFieldIds=[Id("title")]},catalog,service,actor);throw new Exception("Expected numeric refusal");}catch(LoomException e){Check(e.Status==422,"text totals refused");}
  try{ReportWriter.Write(report,layout with{GroupFieldId=Id("hours")},catalog,service,actor);throw new Exception("Expected missing column refusal");}catch(LoomException e){Check(e.Status==422,"unselected group refused");}
  var block=new ReportTextBlock("dddddddddddddddddddddddddddddddd","Cost: ",Source:"sum",FieldId:Id("estimate"));
  var bound=layout with {HeaderBlocks=[block],FooterBlocks=[block with {Source="count",FieldId=null,Text="Jobs: "}]};
  string Render(SavedReport r,ReportLayout l)=>ReportWriter.Write(r,l,catalog,service,actor);
  void Reject(ReportTextBlock b,string message){try{Render(report,bound with{HeaderBlocks=[b]});throw new Exception("Expected refusal");}catch(LoomException e){Check(e.Status==422,message);}}
  Check(Render(report,bound).Contains("Cost: 60.00</div>")&&Render(report,bound).Contains("Jobs: 3</div>"),"bound blocks calculate full report sum and count");
  var filtered=report with{Filter=new ReportFilter(Id("priority"),"High")};
  Check(Render(filtered,bound).Contains("Cost: 50.00</div>")&&Render(filtered,bound).Contains("Jobs: 2</div>"),"bound blocks respect report filter");
  Check(Render(report with{Filter=new ReportFilter(Id("priority"),"None")},bound).Contains("Cost: 0.00</div>"),"empty sum is zero");
  Check(Render(report,bound with{HeaderBlocks=[block with{Text="<b>Cost:</b> "}]}).Contains("&lt;b&gt;Cost:&lt;/b&gt; 60.00"),"bound block prefix escaped");
  Reject(block with{Source="execute"},"unknown block source refused");
  Reject(block with{FieldId=Id("title")},"nonnumeric block field refused");
  Reject(block with{FieldId=Id("hours")},"unselected block field refused");
  Reject(block with{FieldId=null},"missing sum field refused");
  Reject(block with{Source="count"},"unexpected count field refused");
  var relabeled=new Catalog(catalog.Types.Values.Select(t=>t.Id!=type.Id?t:t with{Fields=t.Fields.Select(f=>f.Id==Id("estimate")?f with{Label="Revised cost"}:f).ToArray()}));
  Check(ReportWriter.Write(report,bound,relabeled,new DocumentService(relabeled,store,[]),actor).Contains("Cost: 60.00</div>"),"bound total resolves stable field identity");
  var renamed=new Catalog(catalog.Types.Values.Select(t=>t.Id!=type.Id?t:t with{Version=2,Fields=t.Fields.Select(f=>f.Name=="priority"?f with{Label="Urgency"}:f).ToArray()}));
  Check(ReportWriter.Write(report,layout,renamed,new DocumentService(renamed,store,[]),actor).Contains("Urgency: High"),"captions resolve through identity");
  var restricted = new Catalog(catalog.Types.Values.Select(t=>t.Name!="Asset"?t:t with {Permissions=new Dictionary<string,string[]>{{"Admin",t.Permissions["Admin"]}}}));
  Check(ReportWriter.Write(report,layout,restricted,new DocumentService(restricted,store,[]),new Actor("v","Viewer")).Contains("[Restricted]"),"linked titles respect target permissions");
  return count;
 }
}
