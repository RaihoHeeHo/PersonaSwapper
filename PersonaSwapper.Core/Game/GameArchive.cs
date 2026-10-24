using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CriFsV2Lib;
using CriFsV2Lib.Definitions;
using CriFsV2Lib.Definitions.Structs;

namespace PersonaSwapper.Core.Game
{
    /// <summary>
    /// Read-only view of the game's CPK archives (BASE.CPK plus a language CPK such as EN.CPK).
    /// Files in the language CPK take priority, as they do in game. Paths are case-insensitive,
    /// use '/' separators and look like "MODEL/CHARACTER/PERSONA/0001/PS0001.GMD".
    /// </summary>
    public sealed class GameArchive : IDisposable
    {
        private readonly List<(FileStream Stream, ICpkReader Reader)> mArchives = new();
        private readonly Dictionary<string, (int Archive, CpkFile File)> mIndex = new( StringComparer.OrdinalIgnoreCase );
        private readonly object mLock = new();

        private GameArchive() { }

        /// <summary>All file paths, in archive order.</summary>
        public IReadOnlyCollection<string> Paths => mIndex.Keys;

        /// <param name="cpkPaths">Archives in priority order, lowest first (e.g. BASE.CPK, then EN.CPK).</param>
        public static GameArchive Open( params string[] cpkPaths )
        {
            var archive = new GameArchive();
            var decrypt = CriFsLib.Instance.GetKnownDecryptionFunction( KnownDecryptionFunction.P5R );
            try
            {
                foreach ( var path in cpkPaths )
                {
                    var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
                    var reader = CriFsLib.Instance.CreateCpkReader( stream, false, decrypt );
                    var index = archive.mArchives.Count;
                    archive.mArchives.Add( (stream, reader) );
                    foreach ( var file in reader.GetFiles() )
                        archive.mIndex[FullPath( file )] = (index, file); // later archives override earlier ones
                }
            }
            catch
            {
                archive.Dispose();
                throw;
            }
            return archive;
        }

        /// <summary>Finds BASE.CPK and a language CPK in a game folder or its CPK subfolder.</summary>
        public static GameArchive OpenGameFolder( string folder, string language = "EN" )
        {
            var cpkFolder = File.Exists( Path.Combine( folder, "BASE.CPK" ) ) ? folder : Path.Combine( folder, "CPK" );
            var basePath = Path.Combine( cpkFolder, "BASE.CPK" );
            if ( !File.Exists( basePath ) )
                throw new FileNotFoundException( $"Couldn't find BASE.CPK in {folder} or its CPK subfolder." );

            var paths = new List<string> { basePath };
            var languagePath = Path.Combine( cpkFolder, $"{language}.CPK" );
            if ( File.Exists( languagePath ) )
                paths.Add( languagePath );
            return Open( paths.ToArray() );
        }

        public bool Contains( string path ) => mIndex.ContainsKey( Normalize( path ) );

        public long GetSize( string path ) => mIndex.TryGetValue( Normalize( path ), out var entry ) ? entry.File.ExtractSize : -1;

        public byte[] Read( string path )
        {
            if ( !mIndex.TryGetValue( Normalize( path ), out var entry ) )
                throw new FileNotFoundException( $"{path} isn't in the game archives." );

            lock ( mLock )
            {
                using var data = mArchives[entry.Archive].Reader.ExtractFile( entry.File );
                return data.Span.ToArray();
            }
        }

        public IEnumerable<string> Find( string directoryPrefix, string? extension = null )
        {
            var prefix = Normalize( directoryPrefix ).TrimEnd( '/' ) + "/";
            return mIndex.Keys.Where( p => p.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) &&
                                           ( extension == null || p.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) ) );
        }

        public void Dispose()
        {
            foreach ( var (stream, reader) in mArchives )
            {
                reader.Dispose();
                stream.Dispose();
            }
            mArchives.Clear();
        }

        public static string Normalize( string path ) => path.Replace( '\\', '/' ).TrimStart( '/' );

        private static string FullPath( CpkFile file )
            => string.IsNullOrEmpty( file.Directory ) ? file.FileName : $"{file.Directory}/{file.FileName}";
    }
}
