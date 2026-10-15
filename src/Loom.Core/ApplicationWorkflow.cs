using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Loom.Core;

/// <summary>Trusted C# guard/effect context. All database effects must use this scoped session.</summary>
public sealed record WorkflowContext(IApplicationSession Session,Document Document);
public sealed record WorkflowAction(string Name,string[] From,string To,string[] Roles,
    Action<WorkflowContext>? Guard=null,Action<WorkflowContext,JsonObject>? Effect=null);

/// <summary>A state graph keyed by a stable field ID; application states are separate from Draft/Submitted.</summary>
public sealed class ApplicationWorkflow
{
    private readonly string stateFieldId;
    private readonly Dictionary<string,WorkflowAction> actions;
    public ApplicationWorkflow(string stateFieldId,IEnumerable<WorkflowAction> actions)
    {
        this.stateFieldId=stateFieldId;
        this.actions=new(StringComparer.Ordinal);
        foreach(var action in actions)
        {
            if(string.IsNullOrWhiteSpace(action.Name)||string.IsNullOrWhiteSpace(action.To)||action.From.Length==0||
                action.Roles.Length==0||action.From.Any(string.IsNullOrWhiteSpace)||action.Roles.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Workflow actions require names, source/target states and roles.");
            Synchronous(action.Guard);Synchronous(action.Effect);
            if(!this.actions.TryAdd(action.Name,action with{From=[..action.From],Roles=[..action.Roles]}))
                throw new ArgumentException("Duplicate workflow action.");
        }
    }
    internal static void Synchronous(Delegate? callback)
    {
        if(callback?.GetInvocationList().Any(d=>d.Method.IsDefined(typeof(AsyncStateMachineAttribute),false))==true)
            throw new ArgumentException("Transactional callbacks must be synchronous; async void is not supported.");
    }
    /// <summary>Check role, version, state and guard, then persist effects and state together or roll back all.</summary>
    public Document Execute(IApplicationSession session,string type,string id,int version,string action)
    {
        if(!actions.TryGetValue(action,out var rule))throw new LoomException(400,"Unknown workflow action.");
        return session.Atomic(s=>
        {
            if(!rule.Roles.Contains(s.Identity.Role))throw new LoomException(403,"Role cannot perform this workflow action.");
            var field=s.Describe(type).Fields.SingleOrDefault(f=>f.Id==stateFieldId)
                ??throw new LoomException(403,"Workflow state field is not visible.");
            if(field.ReadOnly)throw new LoomException(403,"Workflow state field is read-only.");
            var document=s.Get(type,id);
            if(document.Version!=version)throw new LoomException(409,"Document changed; reload before continuing.");
            if(!rule.From.Contains(document.Values[field.Name]?.ToString()??""))
                throw new LoomException(409,"Action is unavailable in the current state.");
            // Guards receive a copy so they cannot silently rewrite the persisted document.
            rule.Guard?.Invoke(new(s,document.Copy()));
            var values=(JsonObject)document.Values.DeepClone();
            rule.Effect?.Invoke(new(s,document.Copy()),values);
            values[field.Name]=rule.To;
            return s.Update(type,id,version,values);
        });
    }
}
