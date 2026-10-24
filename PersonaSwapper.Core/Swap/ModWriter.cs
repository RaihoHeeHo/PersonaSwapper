using System;
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
            Directory.CreateDirectory( dataRoot );

            foreach ( var (gamePath, data) in build.Files )
            {
                var target = Path.Combine( dataRoot, gamePath.Replace( '/', Path.DirectorySeparatorChar ) );
                Directory.CreateDirectory( Path.GetDirectoryName( target )! );
                File.WriteAllBytes( target, data );
                Diagnostics.Log.Info( $"Wrote {gamePath} ({data.Length:N0} bytes)" );
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
            Diagnostics.Log.Info( $"Exported mod {info.Id} to {modFolder} ({build.Files.Count} files)" );
            return modFolder;
        }

        private static string SafeName( string id ) => new( id.Select( c => char.IsLetterOrDigit( c ) ? char.ToUpperInvariant( c ) : '_' ).ToArray() );
    }
}
