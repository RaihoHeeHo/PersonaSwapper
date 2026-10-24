using System;
using System.Collections.Generic;
using System.IO;

namespace PersonaSwapper.Core.Tables
{
    /// <summary>
    /// BATTLE/TABLE/VISUAL.TBL. Segment 0: enemy visual variables (200 bytes per enemy);
    /// segment 2: persona visual variables (148 bytes per persona). Other segments are preserved.
    /// Offsets from p5r_tbl.bt (TVisual_EnemyVisualVariablesA / TVisual_PersonaVisualVariablesA).
    /// </summary>
    public sealed class VisualTable
    {
        public const int EnemySegment = 0;
        public const int PersonaSegment = 2;
        public const int EnemySize = 200;
        public const int PersonaSize = 148;

        public VisualTable( SegmentedTable table )
        {
            Table = table;
            if ( table.Segments.Count <= PersonaSegment )
                throw new InvalidDataException( "VISUAL.TBL has fewer segments than expected." );

            var enemies = table.Segments[EnemySegment];
            var personas = table.Segments[PersonaSegment];
            if ( enemies.Length % EnemySize != 0 || personas.Length % PersonaSize != 0 )
                throw new InvalidDataException( "VISUAL.TBL segment sizes don't match the P5R layout." );

            for ( var i = 0; i < enemies.Length / EnemySize; i++ )
                Enemies.Add( new EnemyVisualEntry( enemies, i ) );
            for ( var i = 0; i < personas.Length / PersonaSize; i++ )
                Personas.Add( new PersonaVisualEntry( personas, i ) );
        }

        public SegmentedTable Table { get; }

        /// <summary>Indexed by enemy (unit) ID.</summary>
        public List<EnemyVisualEntry> Enemies { get; } = new();

        /// <summary>Indexed by persona ID.</summary>
        public List<PersonaVisualEntry> Personas { get; } = new();

        public static VisualTable Parse( byte[] data ) => new( SegmentedTable.Parse( data ) );

        public byte[] Serialize() => Table.Serialize();
    }

    public sealed class PersonaVisualEntry : TableRecord
    {
        public PersonaVisualEntry( byte[] segment, int index ) : base( segment, index, VisualTable.PersonaSize ) { }

        // u32 flags, 4 collision cylinders (20 bytes each), 2 offset positions (6 bytes each), then the scale.
        /// <summary>Battle model scale in percent (100 = as modelled).</summary>
        public short BattleModelScale { get => S16( 96 ); set => S16( 96, value ); }
    }

    public sealed class EnemyVisualEntry : TableRecord
    {
        public EnemyVisualEntry( byte[] segment, int index ) : base( segment, index, VisualTable.EnemySize ) { }

        // u32 flags, 5 collision cylinders (20 bytes each), then model scale.
        /// <summary>Enemy model scale in percent.</summary>
        public ushort ModelScale => U16( 104 );
    }
}
