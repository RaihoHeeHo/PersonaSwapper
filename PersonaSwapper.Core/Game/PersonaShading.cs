using System;
using System.IO;

namespace PersonaSwapper.Core.Game
{
    /// <summary>
    /// INIT/SHDPERSONA.PDD: per-persona display settings ("5DTF" header, 20 bytes, then 465 records of 76 bytes;
    /// record <c>id</c> belongs to persona <c>id</c>). The float 48 bytes into a record is 1.0 for all 264 compendium
    /// personas and 0.0 for the 130 unused slots. With it at 0.0 the game crashed opening the fusion screen or the
    /// compendium once a new persona was in a free slot; the community's Alraune mod (slot 351) sets it (to 0.75).
    /// </summary>
    public sealed class PersonaShading
    {
        public const string Path = "INIT/SHDPERSONA.PDD";
        private const int HeaderSize = 20;
        private const int RecordSize = 76;
        private const int RequiredField = 48;

        private readonly byte[] mData;

        private PersonaShading( byte[] data ) => mData = data;

        public static PersonaShading Parse( byte[] data )
        {
            if ( data.Length < HeaderSize + RecordSize || data[0] != (byte)'5' || data[1] != (byte)'D' || data[2] != (byte)'T' || data[3] != (byte)'F' )
                throw new InvalidDataException( "SHDPERSONA.PDD doesn't have the expected 5DTF header." );
            return new PersonaShading( (byte[])data.Clone() );
        }

        public int Count => ( mData.Length - HeaderSize ) / RecordSize;

        private int FieldOffset( int personaId ) => HeaderSize + personaId * RecordSize + RequiredField;

        public bool Has( int personaId ) => personaId >= 0 && personaId < Count;

        public float Value( int personaId ) => Has( personaId ) ? BitConverter.ToSingle( mData, FieldOffset( personaId ) ) : 0;

        /// <summary>True when the record still has the unused-slot 0.0.</summary>
        public bool NeedsFix( int personaId ) => Has( personaId ) && Value( personaId ) == 0f;

        public void Fix( int personaId, float value = 1f )
        {
            if ( !Has( personaId ) ) return;
            BitConverter.GetBytes( value ).CopyTo( mData, FieldOffset( personaId ) );
        }

        public byte[] Serialize() => (byte[])mData.Clone();
    }
}
