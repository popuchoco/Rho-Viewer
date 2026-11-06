using System;
using System.IO;

namespace KartLibrary.File
{
    internal static class ArchiveReadLimits
    {
        internal const int MaxEntryCount = 100_000;

        internal static void ValidateEntryCount(int count, string description)
        {
            if (count <= 0 || count > MaxEntryCount)
                throw new InvalidDataException($"{description} count is outside the supported range.");
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
