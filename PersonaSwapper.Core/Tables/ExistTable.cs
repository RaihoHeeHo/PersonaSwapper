using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PersonaSwapper.Core.Tables
{
    /// <summary>
    /// BATTLE/TABLE/EXIST.TBL: one segment with a 16-byte header of four (count, offset) pairs, laid out as
    /// count1, count2, offset1, offset2, count3, offset3, count4, offset4 (big-endian u16, offsets from the segment
    /// start), then the four sections:
    ///   1: 680 x 6 bytes, 2: 1425 x 2 bytes, 3: 366 x 4 bytes {enemy id, flags}, 4: 356 x 4 bytes {persona id, flags}.
    /// Section 4 lists every persona that exists (sorted, flags 0, last ID repeated as an end marker). It contains all
    /// 264 compendium personas but none of the unused slots except 23; a new persona missing from it crashed the
    /// fusion screen and the compendium.
    /// </summary>
    public sealed class ExistTable
    {
        public const string Path = "BATTLE/TABLE/EXIST.TBL";
        private const int HeaderSize = 16;

        private readonly SegmentedTable mTable;
        private readonly List<(ushort Id, ushort Flags)> mPersonas;
        private readonly int mPersonaOffset;

        private ExistTable( SegmentedTable table, List<(ushort, ushort)> personas, int personaOffset )
        {
            mTable = table;
            mPersonas = personas;
            mPersonaOffset = personaOffset;
        }

        public static ExistTable Parse( byte[] data )
        {
            var table = SegmentedTable.Parse( data );
            if ( table.Segments.Count != 1 || table.Segments[0].Length < HeaderSize )
                throw new InvalidDataException( "EXIST.TBL doesn't have the expected single segment." );
            var seg = table.Segments[0];
            ushort U16( int at ) => BinaryPrimitives.ReadUInt16BigEndian( seg.AsSpan( at, 2 ) );
            int count4 = U16( 12 ), offset4 = U16( 14 );
            if ( offset4 + count4 * 4 != seg.Length )
                throw new InvalidDataException( $"EXIST.TBL persona section ({count4} at 0x{offset4:X}) doesn't end the segment ({seg.Length} bytes)." );
            var personas = new List<(ushort, ushort)>();
            for ( var i = 0; i < count4; i++ )
                personas.Add( (U16( offset4 + i * 4 ), U16( offset4 + i * 4 + 2 )) );
            return new ExistTable( table, personas, offset4 );
        }

        public bool PersonaExists( int id ) => mPersonas.Any( p => p.Id == id );

        public IReadOnlyList<int> Personas => mPersonas.Select( p => (int)p.Id ).Distinct().ToList();

        /// <summary>Adds a persona to the list (sorted; the repeated last entry stays last as the end marker).</summary>
        public bool AddPersona( int id )
        {
            if ( PersonaExists( id ) ) return false;
            var index = mPersonas.FindIndex( p => p.Id > id );
            if ( index < 0 )
            {
                // Past the current end: becomes the new last entry, repeated as the end marker.
                mPersonas[^1] = ((ushort)id, 0);
                mPersonas.Add( ((ushort)id, 0) );
            }
            else
                mPersonas.Insert( index, ((ushort)id, 0) );
            return true;
        }

        public byte[] Serialize()
        {
            var old = mTable.Segments[0];
            var seg = new byte[mPersonaOffset + mPersonas.Count * 4];
            Buffer.BlockCopy( old, 0, seg, 0, mPersonaOffset );
            BinaryPrimitives.WriteUInt16BigEndian( seg.AsSpan( 12, 2 ), (ushort)mPersonas.Count );
            for ( var i = 0; i < mPersonas.Count; i++ )
            {
                BinaryPrimitives.WriteUInt16BigEndian( seg.AsSpan( mPersonaOffset + i * 4, 2 ), mPersonas[i].Id );
                BinaryPrimitives.WriteUInt16BigEndian( seg.AsSpan( mPersonaOffset + i * 4 + 2, 2 ), mPersonas[i].Flags );
            }
            var table = new SegmentedTable();
            table.Segments.Add( seg );
            return table.Serialize();
        }
    }
}
