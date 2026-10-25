using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PersonaSwapper.Core.Tables;

namespace PersonaSwapper.Core.Messages
{
    /// <summary>
    /// Compendium lore: FACILITY/DATMYTH.BMD (and an identical copy as datMyth.bmd inside INIT/DATMSG.PAK).
    /// One single-page message per persona, named myth_&lt;id:000&gt;. Each page is a short run of control codes
    /// (kept as-is) followed by plain text lines and a NUL.
    /// </summary>
    public sealed class PersonaLore
    {
        public const string BmdPath = "FACILITY/DATMYTH.BMD";
        public const string PakPath = "INIT/DATMSG.PAK";
        public const string PakEntryName = "datMyth.bmd";

        /// <summary>
        /// Mergeable lore for Persona Essentials' BMD emulator (mod-folder relative). The emulator matches a .msg to
        /// every BMD whose path contains its name, so this one file covers FACILITY/DATMYTH.BMD and the copy in
        /// INIT/DATMSG.PAK; messages from every mod are compiled onto the game's original file.
        /// </summary>
        public const string MsgPath = "FEmulator/BMD/DATMYTH.msg";

        /// <summary>Stand-in for the copy inside INIT/DATMSG.PAK, so the PAK emulator gives the BMD emulator an entry to merge into.</summary>
        public const string PakStandInPath = "FEmulator/PAK/INIT/DATMSG.PAK/datMyth.bmd";

        /// <summary>Line width the game's compendium text uses (99% of lines are 22 characters or fewer).</summary>
        public const int LineWidth = 22;

        /// <summary>The longest lore entries in the game have 10 lines.</summary>
        public const int MaxLines = 10;

        public PersonaLore( MessageScript script ) => Script = script;

        public MessageScript Script { get; }

        public static string DialogName( int personaId ) => $"myth_{personaId:D3}";

        public bool Has( int personaId ) => Script.Find( DialogName( personaId ) ) != null;

        /// <summary>The lore text with line breaks as stored in game.</summary>
        public string Get( int personaId )
        {
            var dialog = Script.Find( DialogName( personaId ) );
            if ( dialog == null || dialog.PageOffsets.Count == 0 )
                return "";
            var (prefixLength, bodyEnd) = Split( dialog.Text, dialog.PageOffsets[0] );
            var body = dialog.Text.AsSpan( prefixLength, bodyEnd - prefixLength ).ToArray();
            return AtlusText.Decode( body ).TrimEnd( '\n' );
        }

        /// <summary>Replaces the lore text, word-wrapping it to the compendium's line width.</summary>
        public void Set( int personaId, string text )
        {
            var dialog = Script.Find( DialogName( personaId ) )
                         ?? throw new KeyNotFoundException( $"No lore entry for persona {personaId}." );
            if ( dialog.PageOffsets.Count != 1 )
                throw new InvalidDataException( "Lore entries are expected to have exactly one page." );

            var (prefixLength, _) = Split( dialog.Text, dialog.PageOffsets[0] );
            var lines = Wrap( text, LineWidth );
            var body = AtlusText.Encode( string.Join( "\n", lines ) + "\n" );

            var buffer = new byte[prefixLength + body.Length + 1];
            dialog.Text.AsSpan( 0, prefixLength ).CopyTo( buffer );
            body.CopyTo( buffer, prefixLength );
            dialog.Text = buffer; // page 0 still starts at offset 0; NUL terminator is the last byte
        }

        /// <summary>Lore entries whose text differs from <paramref name="original"/> (the game's file).</summary>
        public List<Dialog> ChangedFrom( PersonaLore original )
        {
            var changed = new List<Dialog>();
            foreach ( var dialog in Script.Dialogs )
            {
                var before = original.Script.Find( dialog.Name );
                if ( before == null || !before.Text.AsSpan().SequenceEqual( dialog.Text ) || !before.PageOffsets.SequenceEqual( dialog.PageOffsets ) )
                    changed.Add( dialog );
            }
            return changed;
        }

        /// <summary>
        /// Writes messages in Atlus Script Tools' .msg syntax, byte-exact: control codes become [f table index args…],
        /// line breaks [n], non-ASCII glyphs [x …], and each page ends with [e] (the compiler adds the NUL).
        /// </summary>
        public static string ToMsg( IEnumerable<Dialog> dialogs )
        {
            var sb = new StringBuilder();
            foreach ( var dialog in dialogs )
            {
                sb.Append( "[msg " ).Append( dialog.Name ).Append( "]\r\n" );
                for ( var p = 0; p < dialog.PageOffsets.Count; p++ )
                {
                    var start = dialog.PageOffsets[p];
                    var end = p + 1 < dialog.PageOffsets.Count ? dialog.PageOffsets[p + 1] : dialog.Text.Length;
                    AppendPage( sb, dialog.Text, start, end );
                    sb.Append( "[e]" );
                }
                sb.Append( "\r\n\r\n" );
            }
            return sb.ToString();
        }

        private static void AppendPage( StringBuilder sb, byte[] text, int start, int end )
        {
            var i = start;
            while ( i < end )
            {
                var b = text[i];
                if ( b == 0 )
                    break; // page terminator
                if ( b >= 0xF0 )
                {
                    var length = Math.Max( 2, ( b & 0x0F ) * 2 );
                    var id = i + 1 < end ? text[i + 1] : (byte)0;
                    sb.Append( "[f " ).Append( id >> 5 ).Append( ' ' ).Append( id & 0x1F );
                    for ( var a = i + 2; a + 1 < i + length && a + 1 < end; a += 2 )
                        sb.Append( ' ' ).Append( ( ( text[a] - 1 ) & 0xFF ) | ( ( ( text[a + 1] - 1 ) & 0xFF ) << 8 ) );
                    sb.Append( ']' );
                    i += length;
                }
                else if ( b == 0x0A )
                {
                    sb.Append( "[n]" );
                    i++;
                }
                else if ( b == (byte)'[' || b == (byte)']' )
                {
                    sb.Append( '\\' ).Append( (char)b );
                    i++;
                }
                else if ( b >= 0x20 && b < 0x7F )
                {
                    sb.Append( (char)b );
                    i++;
                }
                else if ( b >= 0x80 && i + 1 < end )
                {
                    sb.Append( "[x " ).Append( b ).Append( ' ' ).Append( text[i + 1] ).Append( ']' );
                    i += 2;
                }
                else
                {
                    sb.Append( "[x " ).Append( b ).Append( ']' );
                    i++;
                }
            }
        }

        /// <summary>Greedy word wrap; the user's own line breaks are kept.</summary>
        public static List<string> Wrap( string text, int width )
        {
            var lines = new List<string>();
            foreach ( var paragraph in text.Replace( "\r\n", "\n" ).Replace( '\r', '\n' ).Split( '\n' ) )
            {
                var line = new StringBuilder();
                foreach ( var word in paragraph.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
                {
                    if ( line.Length > 0 && line.Length + 1 + word.Length > width )
                    {
                        lines.Add( line.ToString() );
                        line.Clear();
                    }
                    if ( line.Length > 0 ) line.Append( ' ' );
                    line.Append( word );
                }
                lines.Add( line.ToString() );
            }
            while ( lines.Count > 0 && lines[^1].Length == 0 )
                lines.RemoveAt( lines.Count - 1 );
            return lines;
        }

        /// <summary>Length of the leading control codes (tokens start at 0xF0; length = low nibble × 2) and end of the text.</summary>
        private static (int PrefixLength, int BodyEnd) Split( byte[] text, int pageStart )
        {
            var at = pageStart;
            while ( at < text.Length && text[at] >= 0xF0 )
                at += Math.Max( 2, ( text[at] & 0x0F ) * 2 );
            var end = Array.IndexOf( text, (byte)0, Math.Min( at, text.Length ) );
            return (Math.Min( at, text.Length ), end < 0 ? text.Length : end);
        }
    }
}
