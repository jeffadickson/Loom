using System.Text.Json.Nodes;

namespace Loom.Facility;

/// <summary>Synthetic, repeatable local fixture. Never adds demo accounts to an ordinary authenticated host.</summary>
public static class FacilitySeed
{
    public const string Password="Loom-facility-demo!";
    public static readonly FacilityMember[] Members =
        (from tenant in new[]{"north","south"} from role in new[]{"Manager","Operator","Reviewer"}
         select new FacilityMember(tenant+"-"+role.ToLowerInvariant(),tenant,role)).ToArray();
    public static void Populate(FacilityApplication app)
    {
        foreach(var tenant in new[]{"north","south"})
        {
            var manager=tenant+"-manager";
            if(app.Overview(manager,tenant).Records["Campaign"].Count>0)continue;
            var campaign=app.Register(manager,tenant,"Campaign",new(){["Name"]="Avionics environmental campaign",["Project"]="Engineering demonstrator",["Budget"]="25000.00"});
            var instrument=app.Register(manager,tenant,"Instrument",new(){["Name"]="Thermal chamber TC-04",["Serial"]=tenant.ToUpperInvariant()+"-TC-04",["Location"]="Environmental laboratory / Bay 2"});
            var now=DateTime.UtcNow;
            var calibration=app.Register(manager,tenant,"Calibration",new(){["Name"]="TC-04 / calibration 1",["Instrument"]=instrument.Id,
                ["ValidFrom"]=now.AddYears(-1).ToString("yyyy-MM-dd"),["ValidUntil"]=now.AddYears(1).ToString("yyyy-MM-dd"),["Certificate"]="SYNTHETIC-CAL-1042"});
            var specimen=app.Register(manager,tenant,"Specimen",new(){["Name"]="Controller assembly EM-02",["Serial"]="EM-02",["Configuration"]="Engineering model / revision C",["Material"]="Aluminium enclosure / electronic assembly"});
            var procedure=app.Register(manager,tenant,"Procedure",new(){["Name"]="Ambient stability check",["Revision"]="C",
                ["Instructions"]="Allow the reference probe to equilibrate. Record the plateau temperature and attach the acquisition file.",
                ["Unit"]="degC",["Lower"]="19.5",["Upper"]="20.5"});
            app.Schedule(manager,tenant,new("TR-001 · Controller thermal stability",campaign.Id,instrument.Id,calibration.Id,specimen.Id,procedure.Id,
                tenant+"-operator",now.AddHours(-1).ToString("O"),now.AddHours(8).ToString("O"),"1250.00"));
        }
    }
}
