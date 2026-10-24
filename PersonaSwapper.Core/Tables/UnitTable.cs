using System;
using System.IO;
using System.Collections.Generic;

namespace PersonaSwapper.Core.Tables
{
    /// <summary>
    /// BATTLE/TABLE/UNIT.TBL. We only edit segment 2 (persona elemental affinities, 40 bytes per persona)
    /// and read segment 4 (enemy visual index: persona ID / model ID). Everything else is preserved.
    /// </summary>
    public sealed class UnitTable
    {
        public const int PersonaAffinitySegment = 2;
        public const int VisualIndexSegment = 4;
        public const int AffinitiesSize = 40;
        public const int VisualIndexSize = 6;

        public UnitTable( SegmentedTable table )
        {
            Table = table;
            if ( table.Segments.Count <= VisualIndexSegment )
                throw new InvalidDataException( "UNIT.TBL has fewer segments than expected." );

            var affinities = table.Segments[PersonaAffinitySegment];
            if ( affinities.Length % AffinitiesSize != 0 )
                throw new InvalidDataException( "UNIT.TBL persona affinity segment size doesn't match the P5R layout." );
            for ( var i = 0; i < affinities.Length / AffinitiesSize; i++ )
                PersonaAffinities.Add( new AffinitiesEntry( affinities, i ) );

            var visual = table.Segments[VisualIndexSegment];
            for ( var i = 0; i < visual.Length / VisualIndexSize; i++ )
                EnemyVisuals.Add( new EnemyVisualIndexEntry( visual, i ) );
        }

        public SegmentedTable Table { get; }

        /// <summary>Indexed by persona ID.</summary>
        public List<AffinitiesEntry> PersonaAffinities { get; } = new();

        /// <summary>Indexed by enemy (unit) ID.</summary>
        public List<EnemyVisualIndexEntry> EnemyVisuals { get; } = new();

        public static UnitTable Parse( byte[] data ) => new( SegmentedTable.Parse( data ) );

        public byte[] Serialize() => Table.Serialize();
    }

    public enum Element
    {
        Physical, Gun, Fire, Ice, Electric, Wind, Psy, Nuke, Bless, Curse, Almighty,
        Dizzy, Confuse, Fear, Forget, Hunger, Sleep, Rage, Despair, Brainwash,
    }

    /// <summary>The headline affinity, as shown in game.</summary>
    public enum Affinity
    {
        Neutral, Weak, Resist, Block, Repel, Drain,
    }

    public sealed class AffinitiesEntry : TableRecord
    {
        public const int ElementCount = 20;

        // Flag byte bits (p5r_structs.bt AffinityBitfield, MSB first):
        // 0x80 double ailment chance, 0x40 guarantee ailment, 0x20 ailment immune,
        // 0x10 resist, 0x08 weak, 0x04 drain, 0x02 repel, 0x01 block. Second byte: multiplier in 5% steps.
        private const byte Block = 0x01, Repel = 0x02, Drain = 0x04, Weak = 0x08, Resist = 0x10;
        private const byte AffinityMask = Block | Repel | Drain | Weak | Resist;

        public AffinitiesEntry( byte[] segment, int index ) : base( segment, index, UnitTable.AffinitiesSize ) { }

        public byte GetFlags( Element element ) => U8( (int)element * 2 );

        public byte GetMultiplier( Element element ) => U8( (int)element * 2 + 1 );

        public void SetMultiplier( Element element, byte value ) => U8( (int)element * 2 + 1, value );

        public Affinity GetAffinity( Element element )
        {
            var flags = GetFlags( element );
            if ( ( flags & Block ) != 0 ) return Affinity.Block;
            if ( ( flags & Repel ) != 0 ) return Affinity.Repel;
            if ( ( flags & Drain ) != 0 ) return Affinity.Drain;
            if ( ( flags & Resist ) != 0 ) return Affinity.Resist;
            if ( ( flags & Weak ) != 0 ) return Affinity.Weak;
            return Affinity.Neutral;
        }

        /// <summary>Changes the headline affinity, keeping the ailment bits and multiplier.</summary>
        public void SetAffinity( Element element, Affinity affinity )
        {
            if ( GetAffinity( element ) == affinity )
                return;

            var flags = (byte)( GetFlags( element ) & ~AffinityMask );
            flags |= affinity switch
            {
                Affinity.Weak => Weak,
                Affinity.Resist => Resist,
                Affinity.Block => Block,
                Affinity.Repel => Repel,
                Affinity.Drain => Drain,
                _ => 0,
            };
            U8( (int)element * 2, flags );
        }
    }

    public sealed class EnemyVisualIndexEntry : TableRecord
    {
        public EnemyVisualIndexEntry( byte[] segment, int index ) : base( segment, index, UnitTable.VisualIndexSize ) { }

        /// <summary>Persona obtained when this Shadow is recruited.</summary>
        public ushort PersonaId => U16( 0 );

        /// <summary>The enemy model folder (MODEL/CHARACTER/ENEMY/&lt;id&gt;).</summary>
        public ushort ModelId => U16( 2 );
    }
}
