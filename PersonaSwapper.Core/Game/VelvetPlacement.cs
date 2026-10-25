using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PersonaSwapper.Core.Game
{
    /// <summary>
    /// FACILITY/COMBINE/TABLE/FCLCMBMODELPOS.DAT: where the Velvet Room (fusion and compendium) places each persona's
    /// model. A 12-byte header ("FPMC") then 465 entries of 48 bytes (12 little-endian floats: position xyz, pad,
    /// scale xyz, pad, two rotations, pad, pad); entry <c>id + 1</c> belongs to persona <c>id</c>.
    /// Every compendium persona has a non-zero entry (scale ~0.7-1.6); unused slots are all zeros, and the game crashed
    /// opening the fusion screen or the compendium with a new persona whose entry was empty.
    /// </summary>
    public sealed class VelvetPlacement
    {
        public const string Path = "FACILITY/COMBINE/TABLE/FCLCMBMODELPOS.DAT";
        private const int HeaderSize = 12;
        private const int EntrySize = 48;

        private readonly byte[] mData;

        private VelvetPlacement( byte[] data ) => mData = data;

        public static VelvetPlacement Parse( byte[] data )
        {
            if ( data.Length < HeaderSize + EntrySize || data[0] != (byte)'F' || data[1] != (byte)'P' || data[2] != (byte)'M' || data[3] != (byte)'C' )
                throw new InvalidDataException( "FCLCMBMODELPOS.DAT doesn't have the expected FPMC header." );
            return new VelvetPlacement( (byte[])data.Clone() );
        }

        public int Count => ( mData.Length - HeaderSize ) / EntrySize;

        private int Offset( int personaId ) => HeaderSize + ( personaId + 1 ) * EntrySize;

        public bool Has( int personaId ) => personaId + 1 < Count;

        public bool IsEmpty( int personaId ) => Has( personaId ) && mData.AsSpan( Offset( personaId ), EntrySize ).IndexOfAnyExcept( (byte)0 ) < 0;

        public float Scale( int personaId ) => BitConverter.ToSingle( mData, Offset( personaId ) + 16 );

        public void Copy( int fromPersona, int toPersona )
        {
            if ( !Has( fromPersona ) || !Has( toPersona ) ) return;
            Buffer.BlockCopy( mData, Offset( fromPersona ), mData, Offset( toPersona ), EntrySize );
        }

        public byte[] Serialize() => (byte[])mData.Clone();

        private static readonly Lazy<List<(int Id, float Height)>> MenuHeights = new( () =>
        {
            var list = new List<(int, float)>();
            using var stream = typeof( VelvetPlacement ).Assembly.GetManifestResourceStream( "PersonaSwapper.Core.Game.MenuModelHeights.txt" );
            if ( stream == null ) return list;
            using var reader = new StreamReader( stream );
            while ( reader.ReadLine() is { } line )
            {
                var parts = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
                if ( parts.Length == 2 && int.TryParse( parts[0], out var id ) &&
                     float.TryParse( parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h ) )
                    list.Add( (id, h) );
            }
            return list;
        } );

        /// <summary>
        /// The compendium persona whose menu model is closest in height to <paramref name="height"/>; its placement
        /// suits a model of that size (small personas sit closer to the camera, big ones further back).
        /// </summary>
        public int NearestBySize( float height, Func<int, bool>? usable = null )
        {
            var candidates = MenuHeights.Value.Where( x => !IsEmpty( x.Id ) && ( usable == null || usable( x.Id ) ) ).ToList();
            if ( candidates.Count == 0 ) return 6;
            if ( height <= 0 ) height = 280; // typical shown size
            return candidates.OrderBy( x => Math.Abs( Math.Log( x.Height / height ) ) ).First().Id;
        }

        /// <summary>A persona of median size, for placeholder entries.</summary>
        public int TypicalPersona() => NearestBySize( MenuHeights.Value.Select( x => x.Height ).OrderBy( x => x ).ElementAtOrDefault( MenuHeights.Value.Count / 2 ) );
    }
}
