using System.Text.Json;
using Loom.Core;
using Loom.Demo.Models;

namespace Loom.Demo;

public static class TypedExample
{
    // Disposable example: never writes the application's configured database.
    public static void Run(Catalog catalog)
    {
        using var store = new MemoryStore();
        store.Migrate(catalog);
        var service = new DocumentService(catalog, store, [new WorkOrderHook()]);
        var actor = new Actor("example", "Admin");
        var equipment = service.Repository<AssetModel>(actor);
        var orders = service.Repository<WorkOrderModel>(actor);
        var scope = equipment.Create(new AssetModel
        {
            Name = "Oscilloscope", Tag = "LAB-101", Category = "Electronics",
            Location = "Lab A", Active = true
        });
        var job = orders.Create(new WorkOrderModel
        {
            Title = "Calibrate scope", Asset = scope.Id, Priority = "Routine",
            Due = new DateOnly(2027, 1, 15), Hours = 2.5m, Rate = 120m
        });
        Console.WriteLine($"Created {scope.Value.Name}; estimate = {job.Value.Estimate}");
        job.Value.Hours = 3m;
        job = orders.Save(job); // Uses the record's version to detect conflicting edits.
        Console.WriteLine($"Updated estimate = {job.Value.Estimate}; version = {job.Version}");
        var active = equipment.Where(x => x.Active, (bool?)true, sortBy: x => x.Name);
        Console.WriteLine(JsonSerializer.Serialize(active, Catalog.Json));
        Console.WriteLine($"Submitted state = {orders.Submit(job).State}");
    }
}
