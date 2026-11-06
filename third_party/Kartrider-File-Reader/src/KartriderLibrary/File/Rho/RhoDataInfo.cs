using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using KartLibrary.Encrypt;
using KartLibrary.IO;
using System.Diagnostics;
using System.IO.Compression;

namespace KartLibrary.File
{
    public class RhoDataInfo : IComparable<RhoDataInfo>
    {
        public uint Index { get; set; }
        public long Offset { get; set; }
        public int DataSize { get; set; }
        public int UncompressedSize { get; set; }
        public RhoBlockProperty BlockProperty { get; set; }
        public uint Checksum { get; set; }

        public int CompareTo(RhoDataInfo? other)
        {
            return Index.CompareTo(other?.Index);
        }

        public override int GetHashCode()
        {
            return (int)Index;
        }
    }
    //Extension
    public static class RhoBlockReader
    {
        public static RhoDataInfo ReadBlockInfo(this BinaryReader reader, uint Key)
        {
            RhoDataInfo output = new RhoDataInfo();
            byte[] blockInfoData = reader.ReadBytes(0x20);
            //Debug.Print($"adler_raw: {Adler.Adler32(0, blockInfoData, 0, blockInfoData.Length):x8}");
            blockInfoData = RhoEncrypt.DecryptHeaderInfo(blockInfoData, Key);
            uint hash = Adler.Adler32(0, blockInfoData, 0, blockInfoData.Length);
            using (MemoryStream ms = new MemoryStream(blockInfoData))
            {
                BinaryReader msReader = new BinaryReader(ms);
                output.Index = msReader.ReadUInt32();
                output.Offset = msReader.ReadUInt32() << 8;
                output.DataSize = msReader.ReadInt32();
                output.UncompressedSize = msReader.ReadInt32();
                output.BlockProperty = (RhoBlockProperty)msReader.ReadInt32();
                output.Checksum = msReader.ReadUInt32();
            }
            return output;
        }

        // For Rho layer 1.0
        public static RhoDataInfo ReadBlockInfo10(this BinaryReader reader, byte[] Key)
        {
            RhoDataInfo output = new RhoDataInfo();
            byte[] blockInfoData = reader.ReadBytes(0x20);
            blockInfoData = RhoEncrypt.DecryptBlockInfoOld(blockInfoData, Key);
            using (MemoryStream ms = new MemoryStream(blockInfoData))
            {
                BinaryReader msReader = new BinaryReader(ms);
                output.Index = msReader.ReadUInt32();
                output.Offset = msReader.ReadUInt32() << 8;
                output.DataSize = msReader.ReadInt32();
                output.UncompressedSize = msReader.ReadInt32();
                output.BlockProperty = (RhoBlockProperty)msReader.ReadInt32();
                output.Checksum = msReader.ReadUInt32();
            }

            return output;
        }

    }

    public enum RhoBlockProperty
    {
        None,
        Compressed = 2,
        PartialEncrypted = 4,
        FullEncrypted = 5,
        CompressedEncrypted = FullEncrypted | Compressed
    }
}
