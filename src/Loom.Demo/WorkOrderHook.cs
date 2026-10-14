using System.Text.Json.Nodes;
using System.Globalization;
using Loom.Core;

namespace Loom.Demo;

public sealed class WorkOrderHook : IDocumentHook
{
    public string Type => "WorkOrder";
    public IReadOnlyCollection<string> RequiredFields => ["hours", "rate", "estimate"];
    public void BeforeSave(JsonObject values)
    {
        if (!decimal.TryParse(values["hours"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) || hours <= 0 || hours > 10000)
            throw new LoomException(422, "Estimated hours must be greater than zero and no more than 10,000.");
        if (!decimal.TryParse(values["rate"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || rate < 0 || rate > 100000)
            throw new LoomException(422, "Hourly rate must be between 0 and 100,000.");
        values["estimate"] = decimal.Round(hours * rate, 2, MidpointRounding.AwayFromZero);
    }
}
