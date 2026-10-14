using Loom.Core;
if(args.Length!=2){Console.Error.WriteLine("Usage: Loom.ModelTool <metadata-directory> <generated-directory>");return 2;}
try
{
    var catalog=Catalog.Load(args[0]);TypeGenerator.Generate(catalog,args[1]);
    Console.WriteLine($"Validated {catalog.Types.Count} models and generated bindings.");return 0;
}
catch(Exception e)when(e is InvalidOperationException or System.Text.Json.JsonException or LoomException)
{
    Console.Error.WriteLine("error LOOM001: "+e.Message);return 1;
}
