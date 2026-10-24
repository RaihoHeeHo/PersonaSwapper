using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PersonaSwapper.Core.Tables
{
    /// <summary>
    /// BATTLE/TABLE/NAME.TBL (EN.CPK). Pairs of segments: a big-endian u16 offset list, then the
    /// NUL-terminated strings those offsets point into. Strings are kept as raw bytes so the game's
    /// non-ASCII encoding round-trips exactly.
    /// </summary>
    public sealed class NameTable
    {
        // Section indices (pairs of segments), identified from the P5R file.
        public const int Arcana = 0;
        public const int Skills = 1;
        public const int Enemies = 3;
        public const int Personas = 4;
        public const int Traits = 5;

        public NameTable( SegmentedTable table )
        {
            Table = table;
            if ( table.Segments.Count % 2 != 0 )
                throw new InvalidDataException( "NAME.TBL should have an even number of segments." );

            for ( var i = 0; i < table.Segments.Count; i += 2 )
                Sections.Add( ParseSection( table.Segments[i], table.Segments[i + 1] ) );
        }

        public SegmentedTable Table { get; }

        public List<List<byte[]>> Sections { get; } = new();

        public static NameTable Parse( byte[] data ) => new( SegmentedTable.Parse( data ) );

        public string Get( int section, int index )
        {
            if ( section < 0 || section >= Sections.Count )
                return string.Empty;
            var list = Sections[section];
            return index >= 0 && index < list.Count ? AtlusText.Decode( list[index] ) : string.Empty;
        }

        public int Count( int section ) => section >= 0 && section < Sections.Count ? Sections[section].Count : 0;

        public void Set( int section, int index, string value ) => Sections[section][index] = AtlusText.Encode( value );

        public byte[] Serialize()
        {
            for ( var s = 0; s < Sections.Count; s++ )
            {
                var (offsets, strings) = BuildSection( Sections[s] );
                Table.Segments[s * 2] = offsets;
                Table.Segments[s * 2 + 1] = strings;
            }
            return Table.Serialize();
        }

        private static List<byte[]> ParseSection( byte[] offsets, byte[] strings )
        {
            var list = new List<byte[]>( offsets.Length / 2 );
            for ( var i = 0; i + 1 < offsets.Length; i += 2 )
            {
                var start = BinaryPrimitives.ReadUInt16BigEndian( offsets.AsSpan( i, 2 ) );
                var end = start;
                while ( end < strings.Length && strings[end] != 0 )
                    end++;
                list.Add( start <= strings.Length ? strings.AsSpan( start, end - start ).ToArray() : Array.Empty<byte>() );
            }
            return list;
        }

        private static (byte[] Offsets, byte[] Strings) BuildSection( List<byte[]> names )
        {
            var offsets = new byte[names.Count * 2];
            using var strings = new MemoryStream();
            for ( var i = 0; i < names.Count; i++ )
            {
                if ( strings.Length > ushort.MaxValue )
                    throw new InvalidDataException( "NAME.TBL section is too large (string offsets are 16-bit)." );
                BinaryPrimitives.WriteUInt16BigEndian( offsets.AsSpan( i * 2, 2 ), (ushort)strings.Length );
                strings.Write( names[i] );
                strings.WriteByte( 0 );
            }
            return (offsets, strings.ToArray());
        }
    }

    /// <summary>
    /// Minimal Atlus text codec: ASCII passes through; any byte >= 0x80 starts a two-byte game glyph,
    /// shown as {XX YY} so it survives editing unchanged.
    /// </summary>
    public static class AtlusText
    {
        public static string Decode( byte[] bytes )
        {
            var sb = new StringBuilder( bytes.Length );
            for ( var i = 0; i < bytes.Length; i++ )
            {
                var b = bytes[i];
                if ( b < 0x80 )
                {
                    sb.Append( (char)b );
                }
                else if ( i + 1 < bytes.Length )
                {
                    var pair = (b << 8) | bytes[i + 1];
                    sb.Append( KnownGlyphs.TryGetValue( pair, out var c ) ? c.ToString() : $"{{{b:X2} {bytes[i + 1]:X2}}}" );
                    i++;
                }
                else
                {
                    sb.Append( $"{{{b:X2}}}" );
                }
            }
            return sb.ToString();
        }

        public static byte[] Encode( string text )
        {
            var output = new List<byte>( text.Length );
            for ( var i = 0; i < text.Length; i++ )
            {
                var c = text[i];
                if ( c == '{' )
                {
                    var close = text.IndexOf( '}', i );
                    if ( close > i )
                    {
                        foreach ( var hex in text.Substring( i + 1, close - i - 1 ).Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
                            output.Add( Convert.ToByte( hex, 16 ) );
                        i = close;
                        continue;
                    }
                }

                if ( ReverseGlyphs.TryGetValue( c, out var pair ) )
                {
                    output.Add( (byte)( pair >> 8 ) );
                    output.Add( (byte)pair );
                }
                else if ( c < 0x80 )
                {
                    output.Add( (byte)c );
                }
                else
                {
                    throw new ArgumentException( $"Character '{c}' can't be written to a game name. Use plain ASCII." );
                }
            }
            return output.ToArray();
        }

        // Glyphs confirmed from the game's own strings.
        private static readonly Dictionary<int, char> KnownGlyphs = new()
        {
            [0x849D] = 'è', // Arsène
        };

        private static readonly Dictionary<char, int> ReverseGlyphs = Reverse( KnownGlyphs );

        private static Dictionary<char, int> Reverse( Dictionary<int, char> map )
        {
            var reversed = new Dictionary<char, int>();
            foreach ( var (k, v) in map )
                reversed[v] = k;
            return reversed;
        }
    }
}
