using System.Text.Json.Nodes;
namespace Loom.Core;

/// <summary>Trusted application policy, composed with the catalog's role grants.</summary>
public interface IDocumentAccessPolicy
{
    FieldFilter[] Scope(DocType schema, Actor actor) => [];
    bool CanRead(Field field, Actor actor) => true;
    bool CanWrite(Field field, Actor actor) => true;
}
