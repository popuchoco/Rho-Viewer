using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using KartLibrary.Encrypt;
using System.Xml.Linq;
using KartLibrary.IO;
using System.IO.Compression;

namespace KartLibrary.Data
{
    // KR is means "KartRider and Raycity".
    public static class DataProcessor
    {
        internal const int MaxKRDataBytes = 256 * 1024 * 1024;

        public static byte[] EncodeKRData(byte[] Data, bool Encrypted, bool Compressed, uint EncryptKey = 0)
        {
            using (MemoryStream ms = new MemoryStream(Data.Length))
            {
                BinaryWriter bw = new BinaryWriter(ms);
                long initialPos = bw.BaseStream.Position;
                const byte checkCode = 0x53;
                byte ProcessMode = (byte)((Encrypted ? 2 : 0) | (Compressed ? 1 : 0));
                uint Hash = Adler.Adler32(0, Data, 0, Data.Length); ;
                int DecompressSize = Data.Length;
                byte[] processedData = Data;
                if (Compressed)
                {
                    using (MemoryStream mss = new MemoryStream())
                    {
                        ZLibStream zs = new ZLibStream(mss, CompressionMode.Compress);
                        zs.Write(processedData, 0, processedData.Length);
                        processedData = ms.ToArray();
                    }
                }
                if (Encrypted)
                {
                    processedData = RhoEncrypt.DecryptData(EncryptKey, processedData);
                }
                bw.Write(checkCode);
                bw.Write(ProcessMode);
                bw.Write(Hash);
                if (Encrypted)
                    bw.Write(EncryptKey);
                if (Compressed)
                    bw.Write(DecompressSize);
                bw.Write(processedData);
                return ms.ToArray();
            }
        }

        public static byte[] DecodeKRData(byte[] OriginalData)
        {
            if (OriginalData.Length > MaxKRDataBytes)
                throw new InvalidDataException("KRData is outside the supported size range.");
            using (MemoryStream ms = new MemoryStream(OriginalData))
            {
                BinaryReader br = new BinaryReader(ms);
                byte checkCode = br.ReadByte();
                if (checkCode != 0x53)
                    throw new Exception("It is not KRData Format.");
                byte ProcessMode = br.ReadByte();
                uint Hash = br.ReadUInt32();
                bool Encrypted = (ProcessMode & 2) == 2;
                bool Compressed = (ProcessMode & 1) == 1;
                uint EncryptKey = Encrypted ? br.ReadUInt32() : 0;
                int DecompressSize = Compressed ? br.ReadInt32() : 0;
                if (Compressed && (DecompressSize < 0 || DecompressSize > MaxKRDataBytes))
                    throw new InvalidDataException("KRData decompressed size is outside the supported range.");
                byte[] originalData = br.ReadBytes((int)(OriginalData.Length - br.BaseStream.Position));
                byte[] processedData = originalData;
                if (Encrypted)
                {
                    processedData = RhoEncrypt.DecryptData(EncryptKey, processedData);
                }
                if (Compressed)
                {
                    using (MemoryStream mss = new MemoryStream(processedData))
                    {
                        processedData = new byte[DecompressSize];
                        ZLibStream zs = new ZLibStream(mss, CompressionMode.Decompress);
                        zs.ReadExactly(processedData);
                        if (zs.ReadByte() != -1)
                            throw new InvalidDataException("KRData expands beyond its declared size.");
                    }
                }
                uint CheckHash = Adler.Adler32(0, processedData, 0, processedData.Length);
                if (CheckHash != Hash)
                    throw new Exception("Exception: KRData hash is not qualified.");
                return processedData;
            }
        }

        // Extensions
        public static byte[] ReadKRData(this BinaryReader br, int TotalLength)
        {
            long initialPos = br.BaseStream.Position;
            if (TotalLength < 6 || TotalLength > MaxKRDataBytes ||
                (br.BaseStream.CanSeek && TotalLength > br.BaseStream.Length - initialPos))
                throw new InvalidDataException("KSV file size is outside the supported range or truncated.");
            byte checkCode = br.ReadByte();
            if (checkCode != 0x53)
                throw new Exception("It is not KRData Format.");
            byte ProcessMode = br.ReadByte();
            uint Hash = br.ReadUInt32();
            bool Encrypted = (ProcessMode & 2) == 2;
            bool Compressed = (ProcessMode & 1) == 1;
            uint EncryptKey = Encrypted ? br.ReadUInt32() : 0;
            int DecompressSize = Compressed ? br.ReadInt32() : 0;
            if (Compressed && (DecompressSize < 0 || DecompressSize > MaxKRDataBytes))
                throw new InvalidDataException("KRData decompressed size is outside the supported range.");
            var payloadLength = TotalLength - (br.BaseStream.Position - initialPos);
            if (payloadLength < 0)
                throw new InvalidDataException("KRData header exceeds its declared file size.");
            byte[] originalData = new byte[checked((int)payloadLength)];
            br.BaseStream.ReadExactly(originalData);
            byte[] processedData = originalData;
            if (Encrypted)
            {
                processedData = RhoEncrypt.DecryptData(EncryptKey, processedData);
            }
            if (Compressed)
            {
                using (MemoryStream ms = new MemoryStream(processedData))
                {
                    processedData = new byte[DecompressSize];
                    using ZLibStream zs = new ZLibStream(ms, CompressionMode.Decompress);
                    zs.ReadExactly(processedData);
                    if (zs.ReadByte() != -1)
                        throw new InvalidDataException("KRData expands beyond its declared size.");
                }
            }
            uint CheckHash = Adler.Adler32(0, processedData, 0, processedData.Length);
            if (CheckHash != Hash)
                throw new Exception("Exception: KRData hash is not qualified.");
            return processedData;
        }

        public static int WriteKRData(this BinaryWriter bw, byte[] Data, bool Encrypted, bool Compressed, uint EncryptKey = 0)
        {
            long initialPos = bw.BaseStream.Position;
            const byte checkCode = 0x53;
            byte ProcessMode = (byte)((Encrypted ? 2 : 0) | (Compressed ? 1 : 0));
            uint Hash = Adler.Adler32(0, Data, 0, Data.Length); ;
            int DecompressSize = Data.Length;
            byte[] processedData = Data;
            if (Compressed)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    ZLibStream zs = new ZLibStream(ms, CompressionMode.Compress);
                    zs.Write(processedData, 0, processedData.Length);
                    processedData = ms.ToArray();
                }
            }
            if (Encrypted)
            {
                processedData = RhoEncrypt.DecryptData(EncryptKey, processedData);
            }
            bw.Write(checkCode);
            bw.Write(ProcessMode);
            bw.Write(Hash);
            if (Encrypted)
                bw.Write(EncryptKey);
            if (Compressed)
                bw.Write(DecompressSize);
            bw.Write(processedData);
            return (int)(bw.BaseStream.Position - initialPos);
        }

    }
}
