using System.Buffers.Binary;
using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Loom.Core;

public static class BlobCodec
{
    // Versioned envelope: magic(4), codec(1), decoded length(4), SHA256(32), payload.
    public static byte[] Encode(byte[] bytes,string mode="auto")
    {
        if(mode is not ("auto" or "none" or "brotli"))throw new ArgumentException("Unknown codec policy.");
        byte[] payload=bytes;byte codec=0;
        if(mode=="brotli" || mode=="auto" && bytes.Length>=1024)
        {
            var buffer=new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
            if(!BrotliEncoder.TryCompress(bytes,buffer,out var written,quality:4,window:22))throw new InvalidOperationException("Brotli encoding failed.");
            var compressed=buffer[..written];if(mode=="brotli" || compressed.Length<bytes.Length){payload=compressed;codec=1;}
        }
        var envelope=new byte[41+payload.Length];"LMB1"u8.CopyTo(envelope);envelope[4]=codec;
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(5),bytes.Length);SHA256.HashData(bytes).CopyTo(envelope,9);payload.CopyTo(envelope,41);return envelope;
    }
    public static byte[] Decode(byte[] envelope,int maxBytes)
    {
        try
        {
            if(envelope.Length<41 || !envelope.AsSpan(0,4).SequenceEqual("LMB1"u8) || envelope[4]>1)throw new InvalidDataException();
            var length=BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(5,4));
            if(length<0 || length>maxBytes)throw new InvalidDataException();
            byte[] result;
            if(envelope[4]==0){if(envelope.Length-41!=length)throw new InvalidDataException();result=envelope[41..];}
            else
            {
                result=new byte[Math.Max(1,length)];using var decoder=new BrotliDecoder();
                var status=decoder.Decompress(envelope.AsSpan(41),result,out var consumed,out var written);
                if(status!=OperationStatus.Done || written!=length || consumed!=envelope.Length-41)throw new InvalidDataException();
                if(length==0)result=[];
            }
            if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(result),envelope.AsSpan(9,32)))throw new InvalidDataException();
            return result;
        }
        catch(Exception e)when(e is InvalidDataException or IOException){throw new LoomException(422,"Corrupt or oversized binary envelope.");}
    }
}
