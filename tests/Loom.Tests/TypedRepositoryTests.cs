using Loom.Core;
using Loom.Demo;
using Loom.Demo.Models;
using Loom.Sqlite;

static class TypedRepositoryTests
{
    public static int Run(Catalog catalog, string directory)
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS typed: " + message); }
        void Deny(int status, Action action, string message)
        {
            try { action(); throw new Exception("Expected refusal: " + message); }
            catch (LoomException e) { Check(e.Status == status, message); }
        }
        var admin = new Actor("typed-admin", "Admin");
        foreach (var provider in new[] {"Memory", "Sqlite"})
        {
            using IDocumentStore store = provider == "Memory" ? new MemoryStore() : new SqliteStore("Data Source="+Path.Combine(directory,"typed.db"));
            store.Migrate(catalog);
            var service = new DocumentService(catalog,store,[new WorkOrderHook()]);
            var assets = service.Repository<AssetModel>(admin);
            var asset = assets.Create(new() {Name="Scope",Tag="TYP-001",Location="Lab A",Category="Electronics",Active=true});
            Check(asset.Value.Tag=="TYP-001" && asset.Version==1,provider+" typed create");
            var second = assets.Create(new() {Name="Pump",Tag="TYP-002",Location="Lab B",Category="Mechanical",Active=false});
            var ordered = assets.List(sortBy:x=>x.Tag,descending:true,take:1);
            Check(ordered.Total==2 && ordered.Items[0].Id==second.Id,provider+" typed sort and paging");
            Check(assets.Where(x=>x.Active,(bool?)true).Items.Single().Id==asset.Id,provider+" typed boolean filter");
            Check(assets.Where(x=>x.Location,"Lab A",sortBy:x=>x.Name).Items.Single().Id==asset.Id,provider+" typed string filter");
            try { assets.List(sortBy:x=>x.Tag!.Length); throw new Exception("Expected selector refusal"); }
            catch (ArgumentException) { Check(true,provider+" non-property expressions rejected"); }
            asset.Value.Location="Lab C";
            var saved = assets.Save(asset);
            Check(saved.Version==2 && assets.Get(asset.Id).Value.Location=="Lab C",provider+" typed update");
            Deny(409,()=>assets.Save(asset),provider+" stale typed save refused");
            saved.Value.Location=null;
            Deny(422,()=>assets.Save(saved),provider+" typed validation retained");
            Check(assets.Get(asset.Id).Value.Location=="Lab C",provider+" failed update preserves values");
            var generated = service.Repository<Loom.Generated.Asset>(admin);
            Check(generated.Get(asset.Id).Value.tag == "TYP-001", provider+" generated JSON model supports typed repository");
            var generatedRecord = generated.Get(asset.Id);
            generatedRecord = generatedRecord with { Value = generatedRecord.Value with { location = "Lab D" } };
            generated.Save(generatedRecord);
            Check(assets.Get(asset.Id).Value.Location == "Lab D", provider+" generated init properties round-trip");
            var viewer = service.Repository<AssetModel>(new Actor("viewer","Viewer"));
            Deny(403,()=>viewer.Create(new()),provider+" typed role permission retained");
            Deny(403,()=>viewer.History(asset.Id),provider+" typed history permission retained");
            var orders = service.Repository<WorkOrderModel>(admin);
            var order = orders.Create(new() {Title="Calibrate",Asset=asset.Id,Priority="Routine",Due=new DateOnly(2027,1,2),Hours=2.5m,Rate=120m,Estimate=-1});
            Check(order.Value.Estimate==300m && order.Value.Due==new DateOnly(2027,1,2),provider+" decimal/date and server calculation");
            order.Value.Hours=3m; order.Value.Estimate=-500;
            order=orders.Save(order);
            Check(order.Value.Estimate==360m,provider+" calculated property ignored on save and recomputed");
            Deny(409,()=>assets.Delete(assets.Get(asset.Id)),provider+" relationship deletion guard retained");
            var submitted=orders.Submit(order);
            Check(submitted.State=="Submitted",provider+" typed submit");
            Deny(409,()=>orders.Save(submitted),provider+" submitted edit locked");
            Check(orders.Cancel(submitted).State=="Cancelled",provider+" typed cancel");
            Check(assets.History(asset.Id).Count==3,provider+" typed history");
            assets.Delete(second);Deny(404,()=>assets.Get(second.Id),provider+" typed delete");
            if(store is IMigrationStore migrations)
            {
                var schema=catalog.Get("Asset");
                var next=new Catalog(catalog.Types.Values.Select(t=>t.Id!=schema.Id?t:t with {Version=2,Fields=t.Fields.Select(f=>f.Name=="tag"?f with {Name="serialNumber"}:f).ToArray()}));
                migrations.Apply(migrations.Plan(next).Id,admin);
                var rebound=new DocumentService(next,store,[new WorkOrderHook()]).Repository<AssetModel>(admin);
                Check(rebound.Get(asset.Id).Value.Tag=="TYP-001", "C# property binds by ID after column rename");
                Check(rebound.Where(x=>x.Tag,"TYP-001").Total==1,"typed selector follows renamed column");
                var updated=rebound.Get(asset.Id);updated.Value.Tag="TYP-003";rebound.Save(updated);
                Check(rebound.Get(asset.Id).Value.Tag=="TYP-003","typed write follows renamed column");
                var added=new Catalog(next.Types.Values.Select(t=>t.Id!=schema.Id?t:t with {Version=3,Fields=[..t.Fields,new Field("notes","Notes","Text")]}));
                migrations.Apply(migrations.Plan(added).Id,admin);
                try {new DocumentService(added,store,[]).Repository<AssetModel>(admin);throw new Exception("Expected incomplete binding refusal");}
                catch(InvalidOperationException){Check(true,"incomplete typed model refused instead of dropping unknown fields");}
            }
        }
        return count;
    }
}
