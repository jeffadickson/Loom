using System.Buffers.Binary;

namespace Loom.Core.Science;

public enum ScientificDType { Int16, Int32, Int64, Float32, Float64 }
public enum ByteOrder { LittleEndian, BigEndian }
public enum FloatPolicy { FiniteOnly, AllowNonFinite }
public sealed record ArrayAxis(string Name,string Unit);
public sealed record ArrayDescriptor(ScientificDType DType,ByteOrder ByteOrder,int[] Shape,ArrayAxis[] Axes,
    string Unit,double Scale=1,double Offset=0,FloatPolicy FloatPolicy=FloatPolicy.FiniteOnly)
{
    public int ElementBytes=>DType switch {ScientificDType.Int16=>2,ScientificDType.Int32 or ScientificDType.Float32=>4,ScientificDType.Int64 or ScientificDType.Float64=>8,_=>throw new LoomException(422,"Unknown dtype.")};
    public int ByteLength(int maxBytes)
    {
        if(maxBytes<0 || Shape is not {Length:>0 and <=8} || Axes==null || Axes.Length!=Shape.Length ||
            Shape.Any(n=>n<0) || Axes.Any(a=>a==null || string.IsNullOrWhiteSpace(a.Name) || a.Unit==null) ||
            Axes.Select(a=>a.Name).Distinct().Count()!=Axes.Length || Unit==null || !Enum.IsDefined(ByteOrder) || !Enum.IsDefined(FloatPolicy) ||
            !double.IsFinite(Scale) || Scale==0 || !double.IsFinite(Offset))throw new LoomException(422,"Invalid array metadata.");
        long length=ElementBytes;
        foreach(var dimension in Shape){if(dimension!=0 && length>maxBytes/(long)dimension)throw new LoomException(422,"Array exceeds byte budget.");length*=dimension;}
        if(length>maxBytes)throw new LoomException(422,"Array exceeds byte budget.");
        return (int)length;
    }
}

/// <summary>Immutable compressed chunks; reads decompress only intersecting chunks.</summary>
public sealed class ScientificArray
{
    private readonly ArrayDescriptor descriptor;
    private readonly byte[][] chunks;
    private readonly int length;
    private readonly int chunkBytes;
    public ArrayDescriptor Descriptor=>Copy(descriptor);
    public int ChunkCount=>chunks.Length;
    public int StoredBytes=>chunks.Sum(c=>c.Length);
    private static ArrayDescriptor Copy(ArrayDescriptor d)=>d with{Shape=(int[])d.Shape.Clone(),Axes=(ArrayAxis[])d.Axes.Clone()};
    public ScientificArray(ArrayDescriptor descriptor,byte[] bytes,int chunkBytes=65536,int maxBytes=64*1024*1024)
    {
        length=descriptor.ByteLength(maxBytes);
        if(bytes.Length!=length || chunkBytes<descriptor.ElementBytes || chunkBytes>1024*1024 || chunkBytes%descriptor.ElementBytes!=0)
            throw new LoomException(422,"Array payload or chunk size does not match metadata.");
        this.descriptor=Copy(descriptor);this.chunkBytes=chunkBytes;
        if(descriptor.FloatPolicy==FloatPolicy.FiniteOnly && descriptor.DType is ScientificDType.Float32 or ScientificDType.Float64)
        {
            for(int i=0;i<bytes.Length;i+=descriptor.ElementBytes)
            {
                var span=bytes.AsSpan(i,descriptor.ElementBytes);
                double value=descriptor.DType==ScientificDType.Float32
                    ? BitConverter.Int32BitsToSingle(descriptor.ByteOrder==ByteOrder.LittleEndian?BinaryPrimitives.ReadInt32LittleEndian(span):BinaryPrimitives.ReadInt32BigEndian(span))
                    : BitConverter.Int64BitsToDouble(descriptor.ByteOrder==ByteOrder.LittleEndian?BinaryPrimitives.ReadInt64LittleEndian(span):BinaryPrimitives.ReadInt64BigEndian(span));
                if(!double.IsFinite(value))throw new LoomException(422,"Array contains a forbidden NaN or infinity.");
            }
        }
        chunks=new byte[(length+chunkBytes-1)/chunkBytes][];
        for(int i=0;i<chunks.Length;i++)chunks[i]=BlobCodec.Encode(bytes.AsSpan(i*chunkBytes,Math.Min(chunkBytes,length-i*chunkBytes)).ToArray());
    }
    public byte[] ReadElements(int offset,int count,int maxReadBytes=8*1024*1024)
    {
        int width=descriptor.ElementBytes;
        if(offset<0 || count<0 || offset>length/width || count>length/width-offset || maxReadBytes<0 || count>maxReadBytes/width)
            throw new LoomException(422,"Array range exceeds bounds or read budget.");
        var result=new byte[count*width];int start=offset*width,end=start+result.Length;
        for(int position=start;position<end;)
        {
            int index=position/chunkBytes,within=position%chunkBytes;
            var decoded=BlobCodec.Decode(chunks[index],chunkBytes);
            int take=Math.Min(decoded.Length-within,end-position);decoded.AsSpan(within,take).CopyTo(result.AsSpan(position-start));position+=take;
        }
        return result;
    }
}
