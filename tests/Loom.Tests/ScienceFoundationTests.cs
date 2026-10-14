using Loom.Core;
using Loom.Core.Science;
using Loom.Sqlite;
using System.Text.Json.Nodes;

static class ScienceFoundationTests
{
    public static int Run(string temp)
    {
        int count=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS foundations: "+name);}
        void Reject(Action action,string name){try{action();throw new Exception("Accepted: "+name);}catch(LoomException){Check(true,name);}}
        var descriptor=new ArrayDescriptor(ScientificDType.Int32,ByteOrder.LittleEndian,[10000],[new("time","s")],"mV");
        var data=new byte[40000];for(int i=0;i<data.Length;i++)data[i]=(byte)(i%251);
        var array=new ScientificArray(descriptor,data,1024);
        Check(array.ReadElements(255,12).SequenceEqual(data.AsSpan(1020,48).ToArray()),"range crosses compressed chunk boundary exactly");
        Check(array.ReadElements(10000,0).Length==0,"empty range at end");
        Reject(()=>array.ReadElements(9999,2),"out-of-bounds range");Reject(()=>array.ReadElements(0,100,399),"read allocation budget");
        data[0]=99;descriptor.Shape[0]=1;var exposed=array.Descriptor;exposed.Shape[0]=3;
        Check(array.ReadElements(0,1)[0]==0 && array.Descriptor.Shape[0]==10000,"array detaches payload and descriptor");
        Reject(()=>new ScientificArray(descriptor,new byte[5]),"shape length mismatch");
        Reject(()=>new ArrayDescriptor(ScientificDType.Float64,ByteOrder.LittleEndian,[int.MaxValue,int.MaxValue],[new("x","m"),new("y","m")],"V").ByteLength(1024),"shape overflow refused before allocation");
        var floats=new ArrayDescriptor(ScientificDType.Float64,ByteOrder.LittleEndian,[1],[new("time","s")],"V");
        Reject(()=>new ScientificArray(floats,BitConverter.GetBytes(double.NaN)),"finite-only float policy");
        Check(new ScientificArray(floats with{FloatPolicy=FloatPolicy.AllowNonFinite},BitConverter.GetBytes(double.NaN)).ReadElements(0,1).SequenceEqual(BitConverter.GetBytes(double.NaN)),"explicit NaN policy preserves bits");
        var clock=new SampleClock("2026-01-01T00:00:00Z",TimeScale.Utc,.000001m,"instrument-clock",.001m);
        var timing=SampleTiming.Uniform(clock,4,0,.1m,[SampleQuality.Good,SampleQuality.Missing,SampleQuality.Saturated,SampleQuality.Good]);
        Check(timing.Select(SampleQuality.Missing|SampleQuality.Saturated).SequenceEqual(new[]{0,3}),"quality selection preserves source indexes");
        Check(timing.WithClockOffset(.25m).Coordinate(3)==.55m && timing.Coordinate(3)==.3m,"clock correction leaves raw timing unchanged");
        var irregular=new SampleTiming(clock,new decimal[]{0,.1m,2},new SampleQuality[3]);Check(irregular.Coordinate(2)==2,"irregular coordinate gap retained");
        Reject(()=>new SampleTiming(clock,new decimal[]{1,1},new SampleQuality[2]),"nonincreasing coordinates rejected");
        Reject(()=>SampleTiming.Uniform(clock,int.MaxValue,0,1),"sample allocation budget");
        var reference=new DatasetReference(1,"test","runs/1","rev1",["voltage"]);int calls=0;
        var gateway=new DatasetGateway([new TestAdapter(()=>{calls++;return array;})],(actor,_)=>actor.Role=="Admin");
        Reject(()=>gateway.Read(new("no","Viewer"),reference,new()),"dataset authorization");Check(calls==0,"unauthorized request never invokes adapter");
        Check(gateway.Read(new("yes","Admin"),reference,new()).ChunkCount==array.ChunkCount,"configured adapter read");
        Reject(()=>gateway.Read(new("yes","Admin"),reference,new(100,10)),"oversized adapter response rejected");
        Reject(()=>(reference with{Dataset="https://user:password@host/data"}).Validate(),"credentials cannot enter dataset locator");
        Reject(()=>(reference with{Dataset="../file"}).Validate(),"dataset traversal rejected");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        try{gateway.Read(new("yes","Admin"),reference,new(),cancellation.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){Check(true,"dataset cancellation");}
        foreach(var provider in new[]{"memory","sqlite"})
        {
            var model=new DocType("RawRun","Raw run",1,"Name",false,[new("Name","Name","Text",Required:true,MaxLength:12,Default:"Untitled"),new("Count","Count","Integer",Minimum:0,Maximum:10)],new(){["Admin"]=["read","create","write","delete","audit"]},Immutable:true);
            var catalog=new Catalog([model]);using IDocumentStore store=provider=="memory"?new MemoryStore():new SqliteStore("Data Source="+Path.Combine(temp,"raw-"+provider+".db"));store.Migrate(catalog);
            var service=new DocumentService(catalog,store,[]);var actor=new Actor("scientist","Admin");
            var raw=service.Create("RawRun",new(){["Count"]=5},actor);Check(raw.Values["Name"]!.ToString()=="Untitled",provider+" future create default");
            Reject(()=>service.Update("RawRun",raw.Id,1,new(){["Name"]="changed"},actor),provider+" immutable update refused");
            Reject(()=>service.Delete("RawRun",raw.Id,1,actor),provider+" immutable deletion refused");
            Check(service.History("RawRun",raw.Id,actor).Count==1,provider+" rejected modifications leave no write or audit");
            Reject(()=>service.Create("RawRun",new(){["Name"]="too long for field"},actor),provider+" text length enforced");
            Reject(()=>service.Create("RawRun",new(){["Count"]=11},actor),provider+" numeric bounds enforced");
            Reject(()=>service.Create("RawRun",new(){["Name"]=null},actor),provider+" explicit null does not silently become default");
            var revision=DocumentRevision.Capture(service,actor,"RawRun",raw.Id);Check(revision.Version==1 && revision.Sha256.Length==64,provider+" exact revision captured");
            new RunProvenance(revision,revision,revision,revision,"operator","1.0",[]).Validate();
            Reject(()=>new RunProvenance(revision,revision,revision,revision,"operator","1.0",[revision]).Validate(),provider+" derived run needs algorithm version");
            Reject(()=>DocumentRevision.Capture(service,new("outsider","Viewer"),"RawRun",raw.Id),provider+" provenance capture respects authorization");
        }
        return count;
    }
    private sealed class TestAdapter(Func<ScientificArray> read):IDatasetAdapter
    {
        public string Provider=>"test";
        public ScientificArray Read(DatasetReference reference,DatasetReadBudget budget,CancellationToken cancellationToken)=>read();
    }
}
