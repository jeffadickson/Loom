using Loom.Core;
using System.Text.Json.Nodes;
namespace Loom.Demo;

// Runs are private to their assigned operator, except for administrators.
public sealed class LaboratoryAccessPolicy : IDocumentAccessPolicy
{
    public FieldFilter[] Scope(DocType schema, Actor actor) => schema.Name == "LabRun" && actor.Role != "Admin"
        ? [new(schema.Fields.Single(f => f.Name == "Operator").Id!, FilterOperator.Equal, JsonValue.Create(actor.Id))] : [];
    public bool CanRead(Field field, Actor actor) => field.Id != "cc000000000000000000000000000005" || actor.Role == "Admin";
    public bool CanWrite(Field field, Actor actor) => CanRead(field, actor);
}
