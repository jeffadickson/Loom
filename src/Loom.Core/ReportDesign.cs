namespace Loom.Core;

public sealed record ReportDesign(int Version, SavedReport Definition, ReportLayout Layout);
public interface IReportDesignStore
{
    ReportDesign? ReadReportDesign(string typeId);
    ReportDesign SaveReportDesign(string typeId, ReportDesign design, Actor actor);
}
public static class ReportDesigns
{
    public static void Validate(SavedReport definition, ReportLayout layout, Catalog catalog)
    {
        if(definition==null || layout==null)throw new LoomException(422,"Report definition and layout are required.");
        var type = catalog.Types.Values.SingleOrDefault(t => t.Id == definition.TypeId) ?? throw new LoomException(422,"Unknown report model.");
        if (definition.Columns == null || definition.Columns.Length is < 1 or > 40 || definition.Columns.Any(c=>c==null) || definition.Columns.Select(c=>c.FieldId).Distinct().Count()!=definition.Columns.Length)
            throw new LoomException(422,"Choose 1–40 distinct columns.");
        foreach(var column in definition.Columns)
            if (!type.Fields.Any(f=>f.Id==column.FieldId) || column.Width is < 1 or > 100 || column.Alignment is not ("auto" or "left" or "center" or "right") || column.Heading?.Length>120)
                throw new LoomException(422,"Invalid column, width, alignment or heading.");
        if(string.IsNullOrWhiteSpace(layout.Title) || layout.Title.Length>200 || layout.Subtitle?.Length>500 || layout.Footer?.Length>300 || layout.FontSize is < 9 or > 20 || layout.Font is not ("sans" or "serif" or "mono") || layout.Orientation is not ("portrait" or "landscape"))
            throw new LoomException(422,"Invalid report typography or text.");
        if(layout.PageHeader?.Length>120 || layout.PageFooter?.Length>120)throw new LoomException(422,"Page header/footer text is limited to 120 characters.");
        foreach(var blocks in new[]{layout.HeaderBlocks,layout.FooterBlocks})
        {
            if(blocks==null)continue;
            if(blocks.Length>12 || blocks.Any(b=>b==null) || blocks.Select(b=>b.Id).Distinct().Count()!=blocks.Length)throw new LoomException(422,"Use up to 12 distinct text blocks per band.");
            foreach(var block in blocks)
            {
                if(!Guid.TryParseExact(block.Id,"N",out _) || block.Text==null || block.Text.Length>500 || block.X<0 || block.Y<0 || block.Width is < 5 or > 100 || block.Height is < 20 or > 120 || block.X>100-block.Width || block.Y>120-block.Height || block.FontSize is < 9 or > 40)
                    throw new LoomException(422,"Text block exceeds its band or has invalid text/style.");
                if(block.Source is not ("text" or "count" or "sum"))throw new LoomException(422,"Unknown block data source.");
                if(block.Source=="sum")
                {
                    if(!definition.Columns.Any(c=>c.FieldId==block.FieldId) || !type.Fields.Any(f=>f.Id==block.FieldId && f.Type is "Integer" or "Decimal" or "Money"))
                        throw new LoomException(422,"Block totals require a selected numeric field.");
                }
                else if(block.FieldId!=null)throw new LoomException(422,"Only sum blocks accept a field binding.");
            }
        }
        bool Selected(string id)=>definition.Columns.Any(c=>c.FieldId==id);
        if(layout.GroupFieldId!=null && !Selected(layout.GroupFieldId))throw new LoomException(422,"Group field must be selected.");
        foreach(var id in layout.SumFieldIds??[])
            if(!Selected(id) || !type.Fields.Any(f=>f.Id==id && f.Type is "Decimal" or "Integer" or "Money"))throw new LoomException(422,"Totals require selected numeric columns.");
        if(definition.SortFieldId!=null && !type.Fields.Any(f=>f.Id==definition.SortFieldId))throw new LoomException(422,"Invalid sort field.");
        if(definition.Filter!=null && !type.Fields.Any(f=>f.Id==definition.Filter.FieldId))throw new LoomException(422,"Invalid filter field.");
    }
}
