using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Loom.Core;

/// <summary>Fixed UTC interval schedules. Occurrence enqueue and cursor advance commit together.</summary>
public sealed class RecurringSchedules(DeliveryOutbox outbox,TimeProvider? clock=null)
{
    private readonly TimeProvider clock=clock??TimeProvider.System;
    public static DocType Definition(string name,Dictionary<string,string[]> permissions)=>new(name,"Recurring schedules",1,"Name",false,
        [new("Name","Name","Text",true,true,MaxLength:120),new("Handler","Handler","Text",true,MaxLength:120),
         new("Payload","Payload","Binary",true,MaxBytes:65536),new("IntervalSeconds","Interval seconds","Integer",true,Minimum:1,Maximum:31536000),
         new("NextDueAt","Next due (Unix ms)","Integer",true),new("Enabled","Enabled","Boolean",true),
         new("MissedRuns","Missed runs","Select",true,Options:["Skip","CatchUp"])],permissions);
    private static long Number(Document d,string field)=>long.Parse(d.Values[field]!.ToString(),CultureInfo.InvariantCulture);
    /// <summary>Schedules are immutable except for pause/resume and the worker-owned cursor. Replace to change cadence.</summary>
    public Document Create(IApplicationSession session,string type,string name,string handler,JsonObject payload,
        int intervalSeconds,DateTimeOffset firstDue,string missedRuns="Skip")
    {
        if(intervalSeconds is <1 or >31536000||missedRuns is not ("Skip" or "CatchUp")||string.IsNullOrWhiteSpace(handler))
            throw new LoomException(422,"Specify a valid interval, handler and missed-run policy.");
        var bytes=Encoding.UTF8.GetBytes(payload.ToJsonString());
        if(bytes.Length>65536)throw new LoomException(422,"Schedule payload exceeds 64 KiB.");
        return session.Create(type,new(){["Name"]=name,["Handler"]=handler,["Payload"]=Convert.ToBase64String(bytes),
            ["IntervalSeconds"]=intervalSeconds,["NextDueAt"]=firstDue.ToUnixTimeMilliseconds(),["Enabled"]=true,["MissedRuns"]=missedRuns});
    }
    /// <summary>Pause prevents future enqueue; already queued deliveries must be cancelled separately.</summary>
    public Document SetEnabled(IApplicationSession session,string type,string id,int version,bool enabled)=>session.Atomic(s=>
    {
        var schedule=s.Get(type,id);if(schedule.Version!=version)throw new LoomException(409,"Schedule changed; reload before continuing.");
        var values=(JsonObject)schedule.Values.DeepClone();values["Enabled"]=enabled;
        return s.Update(type,id,version,values);
    });
    /// <summary>Bound total occurrences per tick. Skip emits only the most recent due occurrence; CatchUp retains older ones.</summary>
    public int Tick(IApplicationSession session,string scheduleType,string deliveryType,int limit=25)
    {
        if(limit is <1 or >100)throw new ArgumentOutOfRangeException(nameof(limit));
        var schema=session.Describe(scheduleType);string Id(string name)=>schema.Fields.Single(f=>f.Name==name).Id!;
        var now=clock.GetUtcNow().ToUnixTimeMilliseconds();
        var candidates=session.Query(scheduleType,new(Id("NextDueAt"),Take:limit,Filters:[
            new(Id("Enabled"),FilterOperator.Equal,JsonValue.Create(true)),
            new(Id("NextDueAt"),FilterOperator.LessThan,JsonValue.Create(checked(now+1)))],SelectFieldIds:[]));
        var count=0;
        foreach(var candidate in candidates.Items)
        {
            var remaining=limit-count;if(remaining==0)break;
            count+=session.Atomic(s=>
            {
                var schedule=s.Get(scheduleType,candidate.Id);
                if(schedule.Version!=candidate.Version||schedule.Values["Enabled"]?.GetValue<bool>()!=true)return 0;
                var due=Number(schedule,"NextDueAt");if(due>now)return 0;
                var interval=checked(Number(schedule,"IntervalSeconds")*1000);
                if(interval<=0)throw new LoomException(422,"Invalid recurring interval.");
                var skip=schedule.Values["MissedRuns"]!.ToString()=="Skip";
                if(skip)due=checked(due+(now-due)/interval*interval);
                var payload=JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(schedule.Values["Payload"]!.ToString())))!.AsObject();
                var emitted=0;
                while(due<=now&&emitted<remaining)
                {
                    // Schedule ID + scheduled instant is stable across process retries and competing ticks.
                    outbox.Enqueue(s,deliveryType,schedule.Values["Handler"]!.ToString(),payload,
                        DateTimeOffset.FromUnixTimeMilliseconds(due),$"schedule:{schedule.Id}:{due}");
                    due=checked(due+interval);emitted++;
                }
                var values=(JsonObject)schedule.Values.DeepClone();values["NextDueAt"]=due;
                s.Update(scheduleType,schedule.Id,schedule.Version,values);return emitted;
            });
        }
        return count;
    }
}
