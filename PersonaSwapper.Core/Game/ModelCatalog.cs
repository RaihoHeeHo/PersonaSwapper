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

        /// <summary>
        /// An unused persona slot ("RESERVE" / "P5 Unused") with no files: a swap onto it adds a new persona.
        /// <see cref="BattleModelPath"/> and <see cref="BattleAnimationPath"/> are where its files will go.
        /// </summary>
        public bool IsFreeSlot { get; init; }

        /// <summary>
        /// For a free slot: the game has an ENEMY/&lt;id&gt; folder (a Shadow model with this number). Such slots are only
        /// usable with Custom Bonus Tweaks, which makes the menus load PSZ instead of the enemy model.
        /// </summary>
        public bool HasEnemyFolder { get; init; }

        /// <summary>For a Custom Bonus Tweaks slot: it was an unused story/party copy of a party persona (untested).</summary>
        public bool WasStoryCopy { get; init; }

        /// <summary>The slot's name in the game's tables (e.g. "RESERVE", "Captain Kidd").</summary>
        public string OriginalName { get; init; } = "";

        /// <summary>For a free slot: the existing persona whose packs serve as the starting point (slot layout, flags).</summary>
        public PersonaEntry? Template { get; init; }

        /// <summary>Battle model to read from the game: the persona's own, or the template's for a free slot.</summary>
        public string ExistingBattleModelPath => Template?.BattleModelPath ?? BattleModelPath;

        public override string ToString() => IsFreeSlot
            ? WasStoryCopy ? $"{Id:D4}  + new persona (unused story copy of {OriginalName}; Custom Bonus Tweaks, untested)"
              : HasEnemyFolder ? $"{Id:D4}  + new persona (free slot, needs Custom Bonus Tweaks)" : $"{Id:D4}  + new persona (free slot)"
            : $"{Id:D4}  {Name}";
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

        /// <summary>
        /// Unused persona slots that can become new personas (see <see cref="FindFreeSlots"/>). Those with
        /// <see cref="PersonaEntry.HasEnemyFolder"/> are only usable with Custom Bonus Tweaks.
        /// </summary>
        public IReadOnlyList<PersonaEntry> FreeSlots { get; private set; } = Array.Empty<PersonaEntry>();

        /// <summary>Persona whose packs a new persona starts from when the source isn't a persona itself.</summary>
        public const int DefaultTemplateId = 6; // Pixie: standard 11-slot battle pack and 3-slot GT pack

        /// <summary>
        /// Free slots: placeholder entries the game ships with ("RESERVE", "P5 Unused", "P6 Unused") that have no persona
        /// or enemy model folder, no Shadow recruits them (UNIT.TBL), and aren't party or story personas. Their table rows already
        /// exist (stats, skills, lore, scale) but are flagged NotRegisterable so they never appear.
        /// </summary>
        public void FindFreeSlots( GameArchive archive, PersonaTable personas, UnitTable units, NameTable names )
        {
            // The menus load ENEMY/<id>/EM<id>.GMD for persona <id>, so a new persona needs that file; only slots whose
            // enemy folder doesn't exist can get one without changing a real Shadow.
            var enemyFolders = archive.Paths.Where( p => p.StartsWith( "MODEL/CHARACTER/ENEMY/", StringComparison.OrdinalIgnoreCase ) )
                .Select( p => p.Split( '/' ) ).Where( parts => parts.Length > 3 && parts[3].Length == 4 && parts[3].All( char.IsDigit ) )
                .Select( parts => int.Parse( parts[3] ) ).ToHashSet();
            var withFolder = Personas.Select( p => p.Id ).ToHashSet();
            // Persona IDs a real Shadow recruits. Placeholder enemy rows ("Not Used", "RESERVE", blank) also point at
            // persona IDs, but no encounter uses them, so they don't make a slot unsafe.
            var recruited = new HashSet<int>();
            for ( var unit = 1; unit < units.EnemyVisuals.Count; unit++ )
            {
                var persona = units.EnemyVisuals[unit].PersonaId;
                if ( persona == 0 ) continue;
                var enemy = names.Get( NameTable.Enemies, unit )?.Trim() ?? "";
                var deadEnemy = enemy.Length == 0 || enemy.Equals( "Not Used", StringComparison.OrdinalIgnoreCase ) ||
                                enemy.StartsWith( "RESERVE", StringComparison.OrdinalIgnoreCase ) || enemy.Contains( "Unused", StringComparison.OrdinalIgnoreCase );
                if ( !deadEnemy ) recruited.Add( persona );
            }
            var template = Personas.FirstOrDefault( p => p.Id == DefaultTemplateId ) ?? Personas.FirstOrDefault( p => p.BattleAnimationPath != null );
            var slots = new List<PersonaEntry>();
            for ( var id = 1; id < personas.Stats.Count; id++ )
            {
                var name = names.Get( NameTable.Personas, id )?.Trim() ?? "";
                var placeholder = name.Equals( "RESERVE", StringComparison.OrdinalIgnoreCase ) || name.EndsWith( " Unused", StringComparison.OrdinalIgnoreCase );
                var flags = personas.Stats[id].Flags;
                if ( !placeholder || withFolder.Contains( id ) || recruited.Contains( id ) ||
                     flags.HasFlag( PersonaFlags.PartyPersona ) || flags.HasFlag( PersonaFlags.StoryPersona ) )
                    continue;
                var folder = $"MODEL/CHARACTER/PERSONA/{id:D4}";
                slots.Add( new PersonaEntry( id, "New persona", folder, $"{folder}/PS{id:D4}.GMD", $"{folder}/BPS{id:D4}_000.GAP" )
                {
                    IsFreeSlot = true,
                    Template = template,
                    HasEnemyFolder = enemyFolders.Contains( id ),
                } );
            }
            // With Custom Bonus Tweaks the menus load PSZ, so persona IDs that have no persona model but whose number has
            // a Shadow/boss model (the unused story copies of the party's personas, 221-238) can be used too.
            foreach ( var id in enemyFolders.Where( id => id > 0 && id < personas.Stats.Count && !withFolder.Contains( id ) &&
                                                          slots.All( s => s.Id != id ) &&
                                                          archive.Contains( $"MODEL/CHARACTER/ENEMY/{id:D4}/EM{id:D4}.GMD" ) ).OrderBy( x => x ) )
            {
                var folder = $"MODEL/CHARACTER/PERSONA/{id:D4}";
                slots.Add( new PersonaEntry( id, "New persona", folder, $"{folder}/PS{id:D4}.GMD", $"{folder}/BPS{id:D4}_000.GAP" )
                {
                    IsFreeSlot = true,
                    Template = template,
                    HasEnemyFolder = true,
                    WasStoryCopy = personas.Stats[id].Flags.HasFlag( PersonaFlags.StoryPersona ) || personas.Stats[id].Flags.HasFlag( PersonaFlags.PartyPersona ),
                    OriginalName = names.Get( NameTable.Personas, id )?.Trim() ?? "",
                } );
            }
            FreeSlots = slots.OrderBy( x => x.Id ).ToList();
        }

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
