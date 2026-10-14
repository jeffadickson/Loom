using Loom.Core;
namespace Loom.Demo.Models;
// Handwritten-model example. Keep IDs when renaming properties.
[Document("ba793989d79aa8559ad3acab1bb4dd3d", nameof(Name), Name = "Asset", Label = "Equipment", Submittable = false)]
[Grant("Admin", "read", "create", "write", "delete", "audit")]
[Grant("Editor", "read", "create", "write", "audit")]
[Grant("Viewer", "read")]
public sealed class AssetModel
{
    [Field("e5bb583c88a666ec7d9a75add42cabc4", Name = "name", Label = "Equipment name", Required = true)]
    public string? Name { get; set; }
    [Field("94907ed369f912b035b0b9d47161023a", Name = "tag", Label = "Asset tag", Required = true, Unique = true)]
    public string? Tag { get; set; }
    [Field("ae7ec89aadf7a1c5ec72b2520784269f", Name = "category", Label = "Category", Required = true, Options = new[] { "Electronics", "Mechanical", "Facilities" })]
    public string? Category { get; set; }
    [Field("c4104c406c67567456427e88a129fdc5", Name = "location", Label = "Location", Required = true)]
    public string? Location { get; set; }
    [Field("a315132afdd948f5f17bbe6da3677f60", Name = "active", Label = "In service", Required = true)]
    public bool? Active { get; set; }
}
[Document("cb389ca1b459b17689f79b1ae5630900", nameof(Title), Name = "WorkOrder", Label = "Work orders", Submittable = true)]
[Grant("Admin", "read", "create", "write", "delete", "submit", "cancel", "audit")]
[Grant("Editor", "read", "create", "write", "submit", "audit")]
[Grant("Viewer", "read")]
public sealed class WorkOrderModel
{
    [Field("785211e68cf9d8b61ab52d72246c2263", Name = "title", Label = "Work description", Required = true)]
    public string? Title { get; set; }
    [Field("063b0295f0faf70799b02cb29a75ceef", Name = "asset", Label = "Equipment", Required = true, Target = "Asset")]
    public string? Asset { get; set; }
    [Field("89fe833e9acb0279c3718d0645f4e769", Name = "priority", Label = "Priority", Required = true, Options = new[] { "Routine", "High", "Urgent" })]
    public string? Priority { get; set; }
    [Field("25d6648213c2e65d73f71e1e97d235ea", Name = "due", Label = "Due date", Required = true)]
    public DateOnly? Due { get; set; }
    [Field("d90072863e835577519993d5d0453047", Name = "hours", Label = "Estimated hours", Required = true)]
    public decimal? Hours { get; set; }
    [Field("64adee66db9c168a6748760af3bdb6d9", Name = "rate", Label = "Hourly rate ($)", Required = true)]
    public decimal? Rate { get; set; }
    [Field("b6492ce67c54b3ba50b018b94c12d692", Name = "estimate", Label = "Estimate ($)", Required = true, ReadOnly = true)]
    public decimal? Estimate { get; set; }
}
