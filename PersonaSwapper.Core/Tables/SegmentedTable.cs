using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace PersonaSwapper.Core.Tables
{
    /// <summary>
    /// An Atlus .TBL file: a sequence of segments, each stored as a big-endian u32 byte size
    /// followed by the data, with every segment start aligned to 16 bytes from the start of the file.
    /// Segments are kept as raw bytes so fields we don't understand round-trip untouched.
    /// </summary>
    public sealed class SegmentedTable
    {
        public const int Alignment = 16;

        public List<byte[]> Segments { get; } = new();

        public static SegmentedTable Parse( byte[] data )
        {
            var table = new SegmentedTable();
            var offset = 0;
            while ( offset + 4 <= data.Length )
            {
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian( data.AsSpan( offset, 4 ) );
                if ( size < 0 || offset + 4 + size > data.Length )
                    throw new InvalidDataException( $"Segment {table.Segments.Count} at 0x{offset:X} claims {size} bytes, past the end of the file." );

                table.Segments.Add( data.AsSpan( offset + 4, size ).ToArray() );
                offset = Align( offset + 4 + size );
            }
            return table;
        }

        public byte[] Serialize()
        {
            using var stream = new MemoryStream();
            Span<byte> sizeBytes = stackalloc byte[4];
            foreach ( var segment in Segments )
            {
                BinaryPrimitives.WriteUInt32BigEndian( sizeBytes, (uint)segment.Length );
                stream.Write( sizeBytes );
                stream.Write( segment );
                var padding = Align( (int)stream.Length ) - (int)stream.Length;
                for ( var i = 0; i < padding; i++ )
                    stream.WriteByte( 0 );
            }
            return stream.ToArray();
        }

        private static int Align( int value ) => ( value + ( Alignment - 1 ) ) & ~( Alignment - 1 );
    }

    /// <summary>A fixed-size record inside a segment, with big-endian field accessors.</summary>
    public abstract class TableRecord
    {
        protected TableRecord( byte[] segment, int index, int size )
        {
            Segment = segment;
            Index = index;
            Offset = index * size;
        }

        protected byte[] Segment { get; }

        public int Index { get; }

        protected int Offset { get; }

        protected byte U8( int at ) => Segment[Offset + at];
        protected void U8( int at, byte value ) => Segment[Offset + at] = value;

        protected ushort U16( int at ) => BinaryPrimitives.ReadUInt16BigEndian( Segment.AsSpan( Offset + at, 2 ) );
        protected void U16( int at, ushort value ) => BinaryPrimitives.WriteUInt16BigEndian( Segment.AsSpan( Offset + at, 2 ), value );

        protected short S16( int at ) => BinaryPrimitives.ReadInt16BigEndian( Segment.AsSpan( Offset + at, 2 ) );
        protected void S16( int at, short value ) => BinaryPrimitives.WriteInt16BigEndian( Segment.AsSpan( Offset + at, 2 ), value );
    }
}
