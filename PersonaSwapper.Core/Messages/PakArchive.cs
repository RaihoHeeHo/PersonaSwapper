using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PersonaSwapper.Core.Messages
{
    /// <summary>
    /// The PAK flavour used by INIT/DATMSG.PAK: big-endian u32 entry count, then for each entry a 32-byte
    /// NUL-padded name, a big-endian u32 size and the data (entry data is padded to 32 bytes by the game).
    /// </summary>
    public sealed class PakArchive
    {
        private const int NameLength = 32;

        public List<(string Name, byte[] Data)> Entries { get; } = new();

        public static PakArchive Parse( byte[] data )
        {
            var pak = new PakArchive();
            var count = BinaryPrimitives.ReadInt32BigEndian( data );
            var at = 4;
            for ( var i = 0; i < count; i++ )
            {
                if ( at + NameLength + 4 > data.Length )
                    throw new InvalidDataException( "PAK entry table runs past the end of the file." );
                var nameEnd = Array.IndexOf( data, (byte)0, at, NameLength );
                var name = Encoding.ASCII.GetString( data, at, ( nameEnd < 0 ? at + NameLength : nameEnd ) - at );
                var size = BinaryPrimitives.ReadInt32BigEndian( data.AsSpan( at + NameLength ) );
                pak.Entries.Add( (name, data.AsSpan( at + NameLength + 4, size ).ToArray()) );
                at += NameLength + 4 + size;
            }
            return pak;
        }

        public byte[]? Get( string name ) => Entries.FirstOrDefault( e => e.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) ).Data;

        /// <summary>Replaces an entry's data, padding it to 32 bytes like the game's own files.</summary>
        public void Set( string name, byte[] data )
        {
            var index = Entries.FindIndex( e => e.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) );
            if ( index < 0 )
                throw new KeyNotFoundException( $"{name} isn't in this PAK." );
            var padded = new byte[( data.Length + 31 ) & ~31];
            data.CopyTo( padded, 0 );
            Entries[index] = (Entries[index].Name, padded);
        }

        public byte[] Serialize()
        {
            using var output = new MemoryStream();
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian( number, Entries.Count );
            output.Write( number );
            foreach ( var (name, data) in Entries )
            {
                var nameBytes = new byte[NameLength];
                Encoding.ASCII.GetBytes( name ).AsSpan( 0, Math.Min( name.Length, NameLength ) ).CopyTo( nameBytes );
                output.Write( nameBytes );
                BinaryPrimitives.WriteInt32BigEndian( number, data.Length );
                output.Write( number );
                output.Write( data );
            }
            return output.ToArray();
        }
    }
}
