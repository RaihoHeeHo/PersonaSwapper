using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonaSwapper.Core.Swap
{
    public sealed record ModInfo( string Id, string Name, string Author, string Description, string Version = "1.0.0" );

    /// <summary>
    /// Writes a Reloaded-II mod folder for P5R. Files go under P5REssentials/CPK/&lt;mod&gt;.CPK/ with their
    /// in-game paths; Persona Essentials loads them in place of the originals and merges .TBL edits with other mods.
    /// Lore goes under FEmulator/ as a .msg that Persona Essentials merges with other mods' messages.
    /// </summary>
    public static class ModWriter
    {
        // Persona Essentials (which pulls in CRI FS V2 Hook) — the mod every P5R file-replacement mod depends on.
        public const string PersonaEssentialsModId = "p5rpc.modloader";

        public static string Write( string modsFolder, ModInfo info, SwapBuildResult build )
        {
            if ( !Regex.IsMatch( info.Id, @"^[A-Za-z0-9._-]+$" ) )
                throw new ArgumentException( "Mod ID can only contain letters, digits, '.', '_' and '-'." );

            var modFolder = Path.Combine( modsFolder, info.Id );
            var dataRoot = Path.Combine( modFolder, "P5REssentials", "CPK", $"{SafeName( info.Id )}.CPK" );
            var emulatorRoot = Path.Combine( modFolder, "FEmulator" );
            Directory.CreateDirectory( dataRoot );

            var written = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
            void WriteFile( string root, string relative, byte[] data )
            {
                var target = Path.GetFullPath( Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) ) );
                Directory.CreateDirectory( Path.GetDirectoryName( target )! );
                File.WriteAllBytes( target, data );
                written.Add( target );
                Diagnostics.Log.Info( $"Wrote {relative} ({data.Length:N0} bytes)" );
            }
            foreach ( var (gamePath, data) in build.Files )
                WriteFile( dataRoot, gamePath, data );
            foreach ( var (path, data) in build.ModFiles )
                WriteFile( modFolder, path, data );

            // Re-exporting to the same mod: drop files an earlier export wrote that this one doesn't (e.g. the whole
            // INIT/DATMSG.PAK older versions replaced, which would stop lore from merging).
            foreach ( var root in new[] { dataRoot, emulatorRoot } )
            {
                if ( !Directory.Exists( root ) ) continue;
                foreach ( var file in Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories ) )
                {
                    if ( written.Contains( Path.GetFullPath( file ) ) ) continue;
                    File.Delete( file );
                    Diagnostics.Log.Info( $"Removed {Path.GetRelativePath( modFolder, file )} left over from an earlier export" );
                }
                foreach ( var dir in Directory.EnumerateDirectories( root, "*", SearchOption.AllDirectories ).OrderByDescending( d => d.Length ) )
                    if ( !Directory.EnumerateFileSystemEntries( dir ).Any() )
                        Directory.Delete( dir );
            }

            var config = new
            {
                ModId = info.Id,
                ModName = info.Name,
                ModAuthor = info.Author,
                ModVersion = info.Version,
                ModDescription = info.Description,
                ModDll = "",
                ModIcon = "",
                ModR2RManagedDll32 = "",
                ModR2RManagedDll64 = "",
                ModNativeDll32 = "",
                ModNativeDll64 = "",
                IsLibrary = false,
                ReleaseMetadataFileName = "Sewer56.Update.ReleaseMetadata.json",
                PluginData = new { },
                IsUniversalMod = false,
                ModDependencies = new[] { PersonaEssentialsModId },
                OptionalDependencies = Array.Empty<string>(),
                SupportedAppId = new[] { "p5r.exe" },
            };
            File.WriteAllText( Path.Combine( modFolder, "ModConfig.json" ),
                               JsonSerializer.Serialize( config, new JsonSerializerOptions { WriteIndented = true } ) );
            Diagnostics.Log.Info( $"Exported mod {info.Id} to {modFolder} ({build.FileCount} files)" );
            return modFolder;
        }

        private static string SafeName( string id ) => new( id.Select( c => char.IsLetterOrDigit( c ) ? char.ToUpperInvariant( c ) : '_' ).ToArray() );
    }
}
