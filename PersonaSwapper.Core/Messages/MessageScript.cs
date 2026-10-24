using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PersonaSwapper.Core.Messages
{
    public enum DialogKind
    {
        Message = 0,
        Selection = 1,
    }

    /// <summary>One message (pages of text) or selection (options) in a BMD.</summary>
    public sealed class Dialog
    {
        public DialogKind Kind { get; set; }

        /// <summary>The 24-byte identifier, e.g. "myth_001".</summary>
        public string Name { get; set; } = "";

        /// <summary>Message: speaker index (0xFFFF = none). Unused for selections.</summary>
        public ushort SpeakerId { get; set; } = 0xFFFF;

        /// <summary>Selection-only header fields.</summary>
        public short SelectionExt { get; set; }
        public short SelectionPattern { get; set; }
        public short SelectionReserved { get; set; }

        /// <summary>Start of each page/option, as offsets into <see cref="Text"/>.</summary>
        public List<int> PageOffsets { get; set; } = new();

        /// <summary>Encoded text for all pages, each page NUL-terminated.</summary>
        public byte[] Text { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Atlus "MSG1" message script (.BMD), the big-endian non-relocated variant P5R uses.
    /// Written from the format: 0x20-byte header; dialog headers; speaker table header; dialogs (4-byte aligned);
    /// speaker name pointers and names; then a compressed table locating every pointer field (relocation table).
    /// Pointers are offsets from the end of the header.
    /// </summary>
    public sealed class MessageScript
    {
        private const int HeaderSize = 0x20;
        private static readonly byte[] MagicBigEndian = { (byte)'1', (byte)'G', (byte)'S', (byte)'M' };

        public byte FileType { get; set; } = 7;
        public byte Format { get; set; }
        public short UserId { get; set; }
        public int ExtSize { get; set; }
        public byte Reserved { get; set; }
        public short Version { get; set; } = 2;
        public int SpeakerExt { get; set; }
        public int SpeakerReserved { get; set; }

        public List<Dialog> Dialogs { get; } = new();

        public List<byte[]> SpeakerNames { get; } = new();

        public static MessageScript Parse( byte[] data )
        {
            if ( data.Length < HeaderSize || !data.AsSpan( 8, 4 ).SequenceEqual( MagicBigEndian ) )
                throw new InvalidDataException( "Not a big-endian MSG1 message script." );
            if ( data[0x1C] != 0 )
                throw new InvalidDataException( "Relocated message scripts aren't supported." );

            var script = new MessageScript
            {
                FileType = data[0],
                Format = data[1],
                UserId = BinaryPrimitives.ReadInt16BigEndian( data.AsSpan( 2 ) ),
                ExtSize = I32( data, 0x0C ),
                Reserved = data[0x1D],
                Version = BinaryPrimitives.ReadInt16BigEndian( data.AsSpan( 0x1E ) ),
            };

            var dialogCount = I32( data, 0x18 );
            for ( var i = 0; i < dialogCount; i++ )
            {
                var kind = (DialogKind)I32( data, HeaderSize + i * 8 );
                var at = HeaderSize + I32( data, HeaderSize + i * 8 + 4 );
                var dialog = new Dialog { Kind = kind, Name = ReadName( data, at ) };
                int count, pointers;
                if ( kind == DialogKind.Message )
                {
                    count = BinaryPrimitives.ReadInt16BigEndian( data.AsSpan( at + 24 ) );
                    dialog.SpeakerId = BinaryPrimitives.ReadUInt16BigEndian( data.AsSpan( at + 26 ) );
                    pointers = at + 28;
                }
                else if ( kind == DialogKind.Selection )
                {
                    dialog.SelectionExt = BinaryPrimitives.ReadInt16BigEndian( data.AsSpan( at + 24 ) );
                    count = BinaryPrimitives.ReadInt16BigEndian( data.AsSpan( at + 26 ) );
                    dialog.SelectionPattern = BinaryPrimitives.ReadInt16BigEndian( data.AsSpan( at + 28 ) );
                    dialog.SelectionReserved = BinaryPrimitives.ReadInt16BigEndian( data.AsSpan( at + 30 ) );
                    pointers = at + 32;
                }
                else
                {
                    throw new InvalidDataException( $"Unknown dialog kind {(int)kind}." );
                }

                var sizeAt = pointers + count * 4;
                var textSize = I32( data, sizeAt );
                var textStart = sizeAt + 4;
                for ( var p = 0; p < count; p++ )
                    dialog.PageOffsets.Add( HeaderSize + I32( data, pointers + p * 4 ) - textStart );
                dialog.Text = data.AsSpan( textStart, textSize ).ToArray();
                script.Dialogs.Add( dialog );
            }

            var speakerHeader = HeaderSize + dialogCount * 8;
            var speakerArray = HeaderSize + I32( data, speakerHeader );
            var speakerCount = I32( data, speakerHeader + 4 );
            script.SpeakerExt = I32( data, speakerHeader + 8 );
            script.SpeakerReserved = I32( data, speakerHeader + 12 );
            for ( var s = 0; s < speakerCount; s++ )
            {
                var nameAt = HeaderSize + I32( data, speakerArray + s * 4 );
                var end = Array.IndexOf( data, (byte)0, nameAt );
                script.SpeakerNames.Add( data.AsSpan( nameAt, end - nameAt ).ToArray() );
            }

            return script;
        }

        public byte[] Serialize()
        {
            var output = new MemoryStream();
            var w = new BigEndianWriter( output );
            var relocations = new List<int>();

            w.Zeros( HeaderSize ); // patched at the end

            // Dialog headers; offsets patched once dialogs are placed.
            var dialogHeaderAt = (int)output.Position;
            foreach ( var dialog in Dialogs )
            {
                w.I32( (int)dialog.Kind );
                relocations.Add( (int)output.Position );
                w.I32( 0 );
            }

            // Speaker table header; name array pointer patched later.
            var speakerHeaderAt = (int)output.Position;
            relocations.Add( speakerHeaderAt );
            w.I32( 0 );
            w.I32( SpeakerNames.Count );
            w.I32( SpeakerExt );
            w.I32( SpeakerReserved );

            // Dialogs.
            for ( var i = 0; i < Dialogs.Count; i++ )
            {
                var dialog = Dialogs[i];
                w.Align( 4 );
                var dialogAt = (int)output.Position;
                w.Patch( dialogHeaderAt + i * 8 + 4, dialogAt - HeaderSize );

                w.Name( dialog.Name, 24 );
                if ( dialog.Kind == DialogKind.Message )
                {
                    w.I16( (short)dialog.PageOffsets.Count );
                    w.U16( dialog.SpeakerId );
                }
                else
                {
                    w.I16( dialog.SelectionExt );
                    w.I16( (short)dialog.PageOffsets.Count );
                    w.I16( dialog.SelectionPattern );
                    w.I16( dialog.SelectionReserved );
                }

                var textStart = (int)output.Position + dialog.PageOffsets.Count * 4 + 4;
                foreach ( var offset in dialog.PageOffsets )
                {
                    relocations.Add( (int)output.Position );
                    w.I32( textStart + offset - HeaderSize );
                }
                w.I32( dialog.Text.Length );
                w.Bytes( dialog.Text );
            }

            // Speaker names.
            w.Align( 4 );
            var speakerArrayAt = (int)output.Position;
            w.Patch( speakerHeaderAt, speakerArrayAt - HeaderSize );
            var nameAt = speakerArrayAt + SpeakerNames.Count * 4;
            foreach ( var name in SpeakerNames )
            {
                relocations.Add( (int)output.Position );
                w.I32( nameAt - HeaderSize );
                nameAt += name.Length + 1;
            }
            foreach ( var name in SpeakerNames )
            {
                w.Bytes( name );
                w.U8( 0 );
            }

            // Relocation table.
            w.Align( 4 );
            var relocationAt = (int)output.Position;
            var table = RelocationTable.Encode( relocations, HeaderSize );
            w.Bytes( table );
            var fileSize = (int)output.Position;

            // Header.
            output.Position = 0;
            w.U8( FileType );
            w.U8( Format );
            w.I16( UserId );
            w.I32( fileSize );
            w.Bytes( MagicBigEndian );
            w.I32( ExtSize );
            w.I32( relocationAt );
            w.I32( table.Length );
            w.I32( Dialogs.Count );
            w.U8( 0 ); // not relocated
            w.U8( Reserved );
            w.I16( Version );
            return output.ToArray();
        }

        public Dialog? Find( string name ) => Dialogs.FirstOrDefault( d => d.Name == name );

        private static int I32( byte[] data, int at ) => BinaryPrimitives.ReadInt32BigEndian( data.AsSpan( at, 4 ) );

        private static string ReadName( byte[] data, int at )
        {
            var end = at;
            while ( end < at + 24 && data[end] != 0 ) end++;
            return System.Text.Encoding.ASCII.GetString( data, at, end - at );
        }

        private sealed class BigEndianWriter
        {
            private readonly MemoryStream mStream;
            private readonly byte[] mBuffer = new byte[4];

            public BigEndianWriter( MemoryStream stream ) => mStream = stream;

            public void U8( byte value ) => mStream.WriteByte( value );
            public void I16( short value ) { BinaryPrimitives.WriteInt16BigEndian( mBuffer, value ); mStream.Write( mBuffer, 0, 2 ); }
            public void U16( ushort value ) { BinaryPrimitives.WriteUInt16BigEndian( mBuffer, value ); mStream.Write( mBuffer, 0, 2 ); }
            public void I32( int value ) { BinaryPrimitives.WriteInt32BigEndian( mBuffer, value ); mStream.Write( mBuffer, 0, 4 ); }
            public void Bytes( byte[] value ) => mStream.Write( value );
            public void Zeros( int count ) { for ( var i = 0; i < count; i++ ) mStream.WriteByte( 0 ); }
            public void Align( int alignment ) { while ( mStream.Position % alignment != 0 ) mStream.WriteByte( 0 ); }

            public void Name( string name, int length )
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes( name );
                for ( var i = 0; i < length; i++ )
                    mStream.WriteByte( i < bytes.Length ? bytes[i] : (byte)0 );
            }

            public void Patch( int at, int value )
            {
                var position = mStream.Position;
                mStream.Position = at;
                I32( value );
                mStream.Position = position;
            }
        }
    }

    /// <summary>
    /// The pointer-location table at the end of MSG1 files. Each pointer location is stored as a delta from the
    /// previous one: an even byte holds delta/2; an odd byte whose low 3 bits are all set means a run of
    /// consecutive 4-byte pointers (2 + bits 3-7 of them); any other odd byte is the low byte+1 of a 16-bit delta,
    /// followed by the high byte.
    /// </summary>
    public static class RelocationTable
    {
        public static List<int> Decode( byte[] table, int baseOffset )
        {
            var result = new List<int>();
            var sum = 0;
            for ( var i = 0; i < table.Length; i++ )
            {
                int value = table[i];
                if ( ( value & 1 ) == 0 )
                {
                    sum += value << 1;
                    result.Add( baseOffset + sum );
                }
                else if ( ( value & 7 ) == 7 )
                {
                    var run = 2 + ( value >> 3 );
                    for ( var r = 0; r < run; r++ )
                    {
                        sum += 4;
                        result.Add( baseOffset + sum );
                    }
                }
                else
                {
                    sum += ( value - 1 ) | ( table[++i] << 8 );
                    result.Add( baseOffset + sum );
                }
            }
            return result;
        }

        public static byte[] Encode( IList<int> locations, int baseOffset )
        {
            var output = new List<byte>();
            var sum = 0;
            var i = 0;
            while ( i < locations.Count )
            {
                // Length of a run of pointers each exactly 4 bytes after the previous one.
                var run = 0;
                while ( i + run < locations.Count && locations[i + run] - baseOffset == sum + 4 * ( run + 1 ) )
                    run++;

                if ( run >= 2 )
                {
                    while ( run >= 2 )
                    {
                        var count = Math.Min( run, 33 );
                        output.Add( (byte)( ( ( count - 2 ) << 3 ) | 7 ) );
                        sum += count * 4;
                        run -= count;
                        i += count;
                    }
                    continue;
                }

                var delta = locations[i] - baseOffset - sum;
                if ( ( delta & 1 ) == 0 && ( delta >> 1 ) <= 0xFF )
                {
                    output.Add( (byte)( delta >> 1 ) );
                }
                else
                {
                    output.Add( (byte)( ( delta & 0xFF ) + 1 ) );
                    output.Add( (byte)( ( delta >> 8 ) & 0xFF ) );
                }
                sum += delta;
                i++;
            }
            return output.ToArray();
        }
    }
}
