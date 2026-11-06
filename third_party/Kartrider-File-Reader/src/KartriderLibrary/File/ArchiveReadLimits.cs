using System;
using System.Collections.Generic;
using System.IO;

namespace KartLibrary.File
{
    internal static class ArchiveReadLimits
    {
        internal const int MaxEntryCount = 100_000;
        internal const int MaxFolderNodes = 100_000;
        internal const int MaxResourceBytes = 256 * 1024 * 1024;
        internal const int MaxNullTerminatedTextLength = 1_048_576;

        internal static void ValidateNullTerminatedTextLength(int currentLength, string description)
        {
            if (currentLength >= MaxNullTerminatedTextLength)
                throw new InvalidDataException($"{description} exceeds the supported length.");
        }

        internal static void ValidateEntryCount(int count, string description)
        {
            if (count <= 0 || count > MaxEntryCount)
                throw new InvalidDataException($"{description} count is outside the supported range.");
        }

        internal static void ValidateFolderChildCount(int count, string description)
        {
            if (count < 0 || count > MaxEntryCount)
                throw new InvalidDataException($"{description} count is outside the supported range.");
        }

        internal static int ValidateFolderNodeCount(long currentCount, int addedCount)
        {
            if (currentCount < 0 || addedCount < 0 || addedCount > MaxFolderNodes || currentCount > MaxFolderNodes - addedCount)
                throw new InvalidDataException("Rho folder tree exceeds the supported node count.");
            return (int)(currentCount + addedCount);
        }

        internal static void RegisterFolderDataIndex(HashSet<uint> visitedFolderDataIndices, uint folderDataIndex)
        {
            if (!visitedFolderDataIndices.Add(folderDataIndex))
                throw new InvalidDataException($"Rho folder data index 0x{folderDataIndex:X8} is repeated.");
        }

        internal static void ValidateResourceSizes(long storedSize, long decodedSize, string description)
        {
            if (storedSize < 0 || storedSize > MaxResourceBytes || decodedSize < 0 || decodedSize > MaxResourceBytes)
                throw new InvalidDataException($"{description} resource size is outside the supported range.");
        }

        internal static void ValidateDataRange(Stream stream, long offset, long size, string description)
        {
            ValidateDataRange(stream.Length, offset, size, description);
        }

        internal static void ValidateDataRange(long fileLength, long offset, long size, string description)
        {
            if (fileLength < 0 || offset < 0 || size < 0 || offset > fileLength || size > fileLength - offset)
                throw new InvalidDataException($"{description} data is outside the archive's declared range.");
        }

        internal static long AlignRho5DataBegin(long position)
        {
            if (position < 0)
                throw new InvalidDataException("Rho5 data begin position is outside the supported range.");

            long remainder = position % 0x400L;
            if (remainder == 0)
                return position;

            try
            {
                return checked(position + (0x400L - remainder));
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("Rho5 data begin position is outside the supported range.", ex);
            }
        }

        internal static long ValidateRho5DataRange(long dataBegin, long blockOffset, long dataSize, long fileLength, string description)
        {
            if (dataBegin < 0 || blockOffset < 0)
                throw new InvalidDataException($"{description} data is outside the archive's declared range.");

            long offset;
            try
            {
                offset = checked(dataBegin + checked(blockOffset * 0x400L));
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"{description} data is outside the archive's declared range.", ex);
            }

            ValidateDataRange(fileLength, offset, dataSize, description);
            return offset;
        }

        internal static void ValidateTableFitsFile(long offset, int count, int minimumEntrySize, long fileLength, string description)
        {
            if (offset < 0 || count < 0 || minimumEntrySize <= 0)
                throw new InvalidDataException($"{description} table range is invalid.");

            long tableEnd;
            try
            {
                tableEnd = checked(offset + checked((long)count * minimumEntrySize));
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"{description} table range is invalid.", ex);
            }

            if (tableEnd > fileLength)
                throw new InvalidDataException($"{description} table is truncated.");
        }
    }
}
