using System;
using System.Collections.Generic;
using System.IO;

namespace PersonaSwapper.Core.Tables
{
    /// <summary>
    /// BATTLE/TABLE/PERSONA.TBL. Layouts from p5r_tbl.bt (tge-was-taken/010-Editor-Templates).
    /// Segment 0: 14-byte stats per persona. Segment 1: 70-byte skills and stat growth per persona.
    /// Segments 2-3 (party level thresholds, party personas) are preserved untouched.
    /// </summary>
    public sealed class PersonaTable
    {
        public const int StatsSize = 14;
        public const int SkillsSize = 70;

        public PersonaTable( SegmentedTable table )
        {
            Table = table;
            if ( table.Segments.Count < 2 )
                throw new InvalidDataException( "PERSONA.TBL has fewer than 2 segments." );

            var statsSegment = table.Segments[0];
            var skillsSegment = table.Segments[1];
            if ( statsSegment.Length % StatsSize != 0 || skillsSegment.Length % SkillsSize != 0 )
                throw new InvalidDataException( "PERSONA.TBL segment sizes don't match the expected P5R layout." );

            var count = Math.Min( statsSegment.Length / StatsSize, skillsSegment.Length / SkillsSize );
            for ( var i = 0; i < count; i++ )
            {
                Stats.Add( new PersonaStatsEntry( statsSegment, i ) );
                Skills.Add( new PersonaSkillsEntry( skillsSegment, i ) );
            }
        }

        public SegmentedTable Table { get; }

        public List<PersonaStatsEntry> Stats { get; } = new();

        public List<PersonaSkillsEntry> Skills { get; } = new();

        public int Count => Stats.Count;

        public static PersonaTable Parse( byte[] data ) => new( SegmentedTable.Parse( data ) );

        public byte[] Serialize() => Table.Serialize();
    }

    [Flags]
    public enum PersonaFlags : ushort
    {
        None = 0,
        Evolved = 0x0001,
        NoNormalFusion = 0x0002,
        NotRegisterable = 0x0008,
        StoryPersona = 0x0010,
        PartyPersona = 0x0020,
        TreasureDemon = 0x0100,
        Dlc = 0x0200,
    }

    public sealed class PersonaStatsEntry : TableRecord
    {
        public PersonaStatsEntry( byte[] segment, int index ) : base( segment, index, PersonaTable.StatsSize ) { }

        public PersonaFlags Flags { get => (PersonaFlags)U16( 0 ); set => U16( 0, (ushort)value ); }
        public byte Arcana { get => U8( 2 ); set => U8( 2, value ); }
        public byte Level { get => U8( 3 ); set => U8( 3, value ); }
        public byte Strength { get => U8( 4 ); set => U8( 4, value ); }
        public byte Magic { get => U8( 5 ); set => U8( 5, value ); }
        public byte Endurance { get => U8( 6 ); set => U8( 6, value ); }
        public byte Agility { get => U8( 7 ); set => U8( 7, value ); }
        public byte Luck { get => U8( 8 ); set => U8( 8, value ); }
        public ushort Inheritance { get => U16( 10 ); set => U16( 10, value ); }
    }

    public enum LearnableKind : byte
    {
        Nothing = 0,
        Skill = 1,
        Trait = 3,
    }

    public sealed class PersonaSkillsEntry : TableRecord
    {
        public const int SkillSlotCount = 16;

        public PersonaSkillsEntry( byte[] segment, int index ) : base( segment, index, PersonaTable.SkillsSize ) { }

        // Weighted stat growth: chance of +1 on level-up = weight / sum of weights (3 rolls per level).
        public byte GrowthStrength { get => U8( 0 ); set => U8( 0, value ); }
        public byte GrowthMagic { get => U8( 1 ); set => U8( 1, value ); }
        public byte GrowthEndurance { get => U8( 2 ); set => U8( 2, value ); }
        public byte GrowthAgility { get => U8( 3 ); set => U8( 3, value ); }
        public byte GrowthLuck { get => U8( 4 ); set => U8( 4, value ); }

        public PersonaSkillSlot GetSlot( int slot )
        {
            if ( slot < 0 || slot >= SkillSlotCount )
                throw new ArgumentOutOfRangeException( nameof( slot ) );
            var at = 6 + slot * 4;
            return new PersonaSkillSlot( U8( at ), (LearnableKind)U8( at + 1 ), S16( at + 2 ) );
        }

        public void SetSlot( int slot, PersonaSkillSlot value )
        {
            if ( slot < 0 || slot >= SkillSlotCount )
                throw new ArgumentOutOfRangeException( nameof( slot ) );
            var at = 6 + slot * 4;
            U8( at, value.LevelsAfterBase );
            U8( at + 1, (byte)value.Kind );
            S16( at + 2, value.Id );
        }

        public IEnumerable<PersonaSkillSlot> Slots
        {
            get
            {
                for ( var i = 0; i < SkillSlotCount; i++ )
                    yield return GetSlot( i );
            }
        }
    }

    /// <param name="LevelsAfterBase">0 = known from the start, otherwise learned this many levels after the base level.</param>
    public readonly record struct PersonaSkillSlot( byte LevelsAfterBase, LearnableKind Kind, short Id )
    {
        public bool IsEmpty => Kind == LearnableKind.Nothing;
    }
}
