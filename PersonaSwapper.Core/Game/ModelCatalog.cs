using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PersonaSwapper.Core.Tables;

namespace PersonaSwapper.Core.Game
{
    public enum ModelCategory
    {
        Persona,
        Enemy,
        Character,
        Other,
    }

    /// <summary>A GMD in the game, with the animation packs stored next to it.</summary>
    public sealed record ModelEntry(
        ModelCategory Category,
        int Id,
        string Path,
        string DisplayName,
        IReadOnlyList<string> AnimationPaths,
        int? PersonaEquivalent = null )
    {
        /// <summary>For a Shadow model: true when it's the Shadow form of a persona that has its own persona model.</summary>
        public bool IsPersonaShadow => PersonaEquivalent != null;

        public string FileName => System.IO.Path.GetFileName( Path );

        public override string ToString() => DisplayName;
    }

    /// <summary>A persona that has a model folder, i.e. one that can be the destination of a swap.</summary>
    public sealed record PersonaEntry( int Id, string Name, string Folder, string BattleModelPath, string? BattleAnimationPath )
    {
        public string FolderPath => Folder;

        public override string ToString() => $"{Id:D4}  {Name}";
    }

    /// <summary>Index of every model in the game with readable names.</summary>
    public sealed class ModelCatalog
    {
        private static readonly Regex sPersonaFile = new( @"^MODEL/CHARACTER/PERSONA/(\d{4})/", RegexOptions.IgnoreCase | RegexOptions.Compiled );
        private static readonly Regex sEnemyFile = new( @"^MODEL/CHARACTER/ENEMY/(\d{4})/", RegexOptions.IgnoreCase | RegexOptions.Compiled );
        private static readonly Regex sCharacterFile = new( @"^MODEL/CHARACTER/(\d{4})/", RegexOptions.IgnoreCase | RegexOptions.Compiled );

        // Model folder IDs for the cast; other character folders show their ID.
        private static readonly Dictionary<int, string> sCharacterNames = new()
        {
            [1] = "Joker", [2] = "Ryuji", [3] = "Morgana", [4] = "Ann", [5] = "Yusuke",
            [6] = "Makoto", [7] = "Haru", [8] = "Futaba", [9] = "Akechi", [10] = "Kasumi",
        };

        private static readonly Lazy<Dictionary<int, int>> PersonaShadows = new( LoadPersonaShadows );

        private static Dictionary<int, int> LoadPersonaShadows()
        {
            var map = new Dictionary<int, int>();
            using var stream = typeof( ModelCatalog ).Assembly.GetManifestResourceStream( "PersonaSwapper.Core.Game.PersonaShadows.txt" );
            if ( stream == null ) return map;
            using var reader = new StreamReader( stream );
            while ( reader.ReadLine() is { } line )
            {
                var parts = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
                if ( parts.Length == 2 && int.TryParse( parts[0], out var model ) && int.TryParse( parts[1], out var persona ) )
                    map.TryAdd( model, persona );
            }
            return map;
        }

        public IReadOnlyList<ModelEntry> Models { get; private set; } = Array.Empty<ModelEntry>();

        public IReadOnlyList<PersonaEntry> Personas { get; private set; } = Array.Empty<PersonaEntry>();

        public static ModelCatalog Build( GameArchive archive, NameTable? names, UnitTable? units )
        {
            var allPaths = archive.Paths.Where( p => p.StartsWith( "MODEL/CHARACTER/", StringComparison.OrdinalIgnoreCase ) ).ToList();
            var gapsByFolder = allPaths
                .Where( p => p.EndsWith( ".GAP", StringComparison.OrdinalIgnoreCase ) )
                .GroupBy( p => p.Substring( 0, p.LastIndexOf( '/' ) ), StringComparer.OrdinalIgnoreCase )
                .ToDictionary( g => g.Key, g => (IReadOnlyList<string>)g.OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).ToList(),
                               StringComparer.OrdinalIgnoreCase );

            // Enemy model folder -> enemy names that use it (several Shadows can share a model).
            var enemyNamesByModel = new Dictionary<int, List<string>>();
            if ( units != null && names != null )
            {
                for ( var unit = 1; unit < units.EnemyVisuals.Count; unit++ )
                {
                    var name = names.Get( NameTable.Enemies, unit );
                    if ( string.IsNullOrWhiteSpace( name ) || name.Equals( "Not Used", StringComparison.OrdinalIgnoreCase ) )
                        continue;
                    var modelId = units.EnemyVisuals[unit].ModelId;
                    if ( !enemyNamesByModel.TryGetValue( modelId, out var list ) )
                        enemyNamesByModel[modelId] = list = new List<string>();
                    if ( !list.Contains( name ) )
                        list.Add( name );
                }
            }

            // Enemy model folder -> the persona it's the Shadow form of (same textures as that persona's own model).
            // Bosses also name a persona in UNIT.TBL (Kamoshida names Arsène) without being one, so the table alone
            // can't tell; the list was computed from the game files with `pswap <game> shadowmap`.
            var personaByModel = PersonaShadows.Value;

            // Character animations live in subfolders (BATTLE, FIELD, EVENT, LIFE) and in COMMON_ANIM/BCMN<id>, ACMN<id>.
            var characterGaps = new Dictionary<int, IReadOnlyList<string>>();
            IReadOnlyList<string> CharacterGaps( int id ) =>
                characterGaps.TryGetValue( id, out var cached ) ? cached : characterGaps[id] = allPaths
                    .Where( p => p.EndsWith( ".GAP", StringComparison.OrdinalIgnoreCase ) &&
                                 ( p.StartsWith( $"MODEL/CHARACTER/{id:D4}/", StringComparison.OrdinalIgnoreCase ) ||
                                   p.StartsWith( $"MODEL/CHARACTER/COMMON_ANIM/BCMN{id:D4}", StringComparison.OrdinalIgnoreCase ) ||
                                   p.StartsWith( $"MODEL/CHARACTER/COMMON_ANIM/ACMN{id:D4}", StringComparison.OrdinalIgnoreCase ) ) )
                    .OrderBy( p => p, StringComparer.OrdinalIgnoreCase )
                    .ToList();

            var models = new List<ModelEntry>();
            foreach ( var path in allPaths.Where( p => p.EndsWith( ".GMD", StringComparison.OrdinalIgnoreCase ) ) )
            {
                var folder = path.Substring( 0, path.LastIndexOf( '/' ) );
                var gaps = gapsByFolder.TryGetValue( folder, out var g ) ? g : Array.Empty<string>();
                var file = System.IO.Path.GetFileName( path );
                var characterMatch = sCharacterFile.Match( path );
                if ( characterMatch.Success && !folder.Contains( "/EVENT", StringComparison.OrdinalIgnoreCase ) )
                    gaps = CharacterGaps( int.Parse( characterMatch.Groups[1].Value ) );

                Match m;
                if ( ( m = sPersonaFile.Match( path ) ).Success )
                {
                    var id = int.Parse( m.Groups[1].Value );
                    var name = names?.Get( NameTable.Personas, id );
                    models.Add( new ModelEntry( ModelCategory.Persona, id, path, $"{Label( name, id )} — {file}", gaps ) );
                }
                else if ( ( m = sEnemyFile.Match( path ) ).Success )
                {
                    var id = int.Parse( m.Groups[1].Value );
                    var label = enemyNamesByModel.TryGetValue( id, out var list )
                        ? string.Join( " / ", list.Take( 3 ) ) + ( list.Count > 3 ? $" (+{list.Count - 3})" : "" )
                        : names?.Get( NameTable.Enemies, id );
                    models.Add( new ModelEntry( ModelCategory.Enemy, id, path, $"{Label( label, id )} — {file}", gaps,
                                                personaByModel.TryGetValue( id, out var persona ) ? persona : null ) );
                }
                else if ( ( m = sCharacterFile.Match( path ) ).Success )
                {
                    var id = int.Parse( m.Groups[1].Value );
                    var label = sCharacterNames.TryGetValue( id, out var n ) ? n : $"Character {id:D4}";
                    models.Add( new ModelEntry( ModelCategory.Character, id, path, $"{label} — {file}", gaps ) );
                }
                else
                {
                    models.Add( new ModelEntry( ModelCategory.Other, -1, path, path.Substring( "MODEL/CHARACTER/".Length ), gaps ) );
                }
            }

            var personas = models
                .Where( x => x.Category == ModelCategory.Persona )
                .GroupBy( x => x.Id )
                .Select( grp =>
                {
                    var folder = grp.First().Path.Substring( 0, grp.First().Path.LastIndexOf( '/' ) );
                    var battleModel = $"{folder}/PS{grp.Key:D4}.GMD";
                    if ( !archive.Contains( battleModel ) )
                        return null;
                    var battleAnim = $"{folder}/BPS{grp.Key:D4}_000.GAP";
                    return new PersonaEntry( grp.Key, Label( names?.Get( NameTable.Personas, grp.Key ), grp.Key, withId: false ),
                                             folder, battleModel, archive.Contains( battleAnim ) ? battleAnim : null );
                } )
                .Where( x => x != null )
                .Select( x => x! )
                .OrderBy( x => x.Id )
                .ToList();

            return new ModelCatalog
            {
                Models = models.OrderBy( x => x.Category ).ThenBy( x => x.Id ).ThenBy( x => x.Path, StringComparer.OrdinalIgnoreCase ).ToList(),
                Personas = personas,
            };
        }

        private static string Label( string? name, int id, bool withId = true )
        {
            var clean = string.IsNullOrWhiteSpace( name ) ? "(unnamed)" : name.Trim();
            return withId ? $"{id:D4} {clean}" : clean;
        }
    }
}
