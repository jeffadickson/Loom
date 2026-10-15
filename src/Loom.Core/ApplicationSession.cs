using System.Text.Json.Nodes;

namespace Loom.Core;

/// <summary>Authorized operations shared by application workflows and transactional workers.</summary>
public interface IApplicationSession
{
    Actor Identity { get; }
    T Atomic<T>(Func<IApplicationSession,T> action);
    DocumentForm Describe(string type);
    Document Get(string type,string id);
    Document Create(string type,JsonObject values);
    Document Update(string type,string id,int version,JsonObject values);
    Page Query(string type,DocumentQuery query);
}

/// <summary>Binds a trusted host actor to a document service; scoped instances expire after Atomic.</summary>
public sealed class ApplicationSession(DocumentService service,Actor identity):IApplicationSession
{
    public Actor Identity=>identity;
    public T Atomic<T>(Func<IApplicationSession,T> action)=>service.Atomic(s=>action(new ApplicationSession(s,identity)));
    public DocumentForm Describe(string type)=>service.Describe(type,identity);
    public Document Get(string type,string id)=>service.Get(type,id,identity);
    public Document Create(string type,JsonObject values)=>service.Create(type,values,identity);
    public Document Update(string type,string id,int version,JsonObject values)=>service.Update(type,id,version,values,identity);
    public Page Query(string type,DocumentQuery query)=>service.Query(type,identity,query);
}
