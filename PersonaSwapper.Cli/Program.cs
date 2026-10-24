using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GFDLibrary;
using GFDLibrary.Animations;
using PersonaSwapper.Core.Game;
using PersonaSwapper.Core.Swap;
using PersonaSwapper.Core.Preview;
using PersonaSwapper.Core.Messages;
using PersonaSwapper.Core.Tables;

namespace PersonaSwapper.Cli
{
    internal static class Program
    {
        private static int Main( string[] args )
        {
            if ( args.Length < 2 )
            {
                Console.Error.WriteLine( "usage: pswap <game-or-cpk-folder> <catalog|persona <id>|roundtrip|slotstats>" );
                return 2;
            }

            var sw = Stopwatch.StartNew();
            using var game = GameData.Load( args[0] );
            Console.WriteLine( $"Loaded {game.Archive.Paths.Count} files, {game.Catalog.Models.Count} models, {game.Catalog.Personas.Count} personas in {sw.ElapsedMilliseconds} ms" );

            switch ( args[1] )
            {
                case "catalog":
                    foreach ( var group in game.Catalog.Models.GroupBy( m => m.Category ) )
                    {
                        Console.WriteLine( $"{group.Key}: {group.Count()} models" );
                        foreach ( var m in group.Take( 5 ) )
                            Console.WriteLine( $"  {m.DisplayName}  [{m.AnimationPaths.Count} GAP]" );
                    }
                    Console.WriteLine( "Personas (destinations):" );
                    foreach ( var p in game.Catalog.Personas.Take( 8 ) )
                        Console.WriteLine( $"  {p}  {p.BattleModelPath}  {p.BattleAnimationPath}" );
                    return 0;

                case "persona":
                    DumpPersona( game, int.Parse( args[2] ) );
                    return 0;

                case "roundtrip":
                    return RoundTrip( game ) ? 0 : 1;

                case "slotstats":
                    SlotStats( game );
                    return 0;

                case "swap":
                    return Swap( game, args[2], int.Parse( args[3] ), args[4] );

                case "render":
                    // render <model.GMD> <pack.GAP|-> <animIndex> <outPrefix> [times...]
                    Render( game, args[2], args[3], int.Parse( args[4] ), args[5], args.Skip( 6 ).Select( float.Parse ).DefaultIfEmpty( 0f ).ToArray() );
                    return 0;

                case "slotmap":
                    SlotMap( game, args.Skip( 2 ).Select( int.Parse ).ToArray() );
                    return 0;

                default:
                    Console.Error.WriteLine( $"unknown command {args[1]}" );
                    return 2;
            }
        }

        private static void DumpPersona( GameData game, int id )
        {
            var s = game.Personas.Stats[id];
            var k = game.Personas.Skills[id];
            var a = game.Units.PersonaAffinities[id];
            Console.WriteLine( $"#{id} {game.Names.Get( NameTable.Personas, id )}" );
            Console.WriteLine( $"  Arcana {s.Arcana} ({game.Names.Get( NameTable.Arcana, s.Arcana )}), Level {s.Level}, Flags {s.Flags}, Inherit {s.Inheritance}" );
            Console.WriteLine( $"  St {s.Strength} Ma {s.Magic} En {s.Endurance} Ag {s.Agility} Lu {s.Luck}" );
            Console.WriteLine( $"  Growth weights St {k.GrowthStrength} Ma {k.GrowthMagic} En {k.GrowthEndurance} Ag {k.GrowthAgility} Lu {k.GrowthLuck}" );
            foreach ( var slot in k.Slots.Where( x => !x.IsEmpty ) )
            {
                var name = slot.Kind == LearnableKind.Skill ? game.Names.Get( NameTable.Skills, slot.Id ) : game.Names.Get( NameTable.Traits, slot.Id );
                Console.WriteLine( $"    {( slot.LevelsAfterBase == 0 ? "innate" : $"Lv {s.Level + slot.LevelsAfterBase}" ),-7} {slot.Kind,-5} {slot.Id,4} {name}" );
            }
            var visual = VisualTable.Parse( game.Archive.Read( GameData.VisualTablePath ) );
            Console.WriteLine( $"  Battle model scale {visual.Personas[id].BattleModelScale}%" );
            var lore = new PersonaLore( MessageScript.Parse( game.Archive.Read( PersonaLore.BmdPath ) ) );
            Console.WriteLine( "  Lore: " + lore.Get( id ).Replace( "\n", " / " ) );
            Console.WriteLine( "  Affinities: " + string.Join( ", ", Enum.GetValues<Element>().Take( 10 ).Select( e => $"{e} {a.GetAffinity( e )}" ) ) );
        }

        private static bool RoundTrip( GameData game )
        {
            var ok = true;
            foreach ( var path in new[] { GameData.PersonaTablePath, GameData.UnitTablePath, GameData.NameTablePath, GameData.SkillTablePath, GameData.VisualTablePath } )
            {
                var original = game.Archive.Read( path );
                var rewritten = path == GameData.NameTablePath
                    ? NameTable.Parse( original ).Serialize()
                    : SegmentedTable.Parse( original ).Serialize();
                var same = original.AsSpan().SequenceEqual( rewritten );
                Console.WriteLine( $"{path}: {( same ? "identical" : $"DIFFERENT ({original.Length} vs {rewritten.Length} bytes)" )}" );
                ok &= same;
            }

            // Message script and PAK round-trips.
            var bmdBytes = game.Archive.Read( PersonaLore.BmdPath );
            var script = MessageScript.Parse( bmdBytes );
            var bmdSame = script.Serialize().AsSpan().SequenceEqual( bmdBytes );
            Console.WriteLine( $"{PersonaLore.BmdPath}: {( bmdSame ? "identical" : "DIFFERENT" )} ({script.Dialogs.Count} dialogs)" );
            ok &= bmdSame;

            // Relocation table: decoding the game's table must give exactly the pointer locations our writer records.
            var relOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian( bmdBytes.AsSpan( 0x10 ) );
            var relSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian( bmdBytes.AsSpan( 0x14 ) );
            var decoded = RelocationTable.Decode( bmdBytes.AsSpan( relOffset, relSize ).ToArray(), 0x20 );
            var reencoded = RelocationTable.Encode( decoded, 0x20 );
            var relOk = reencoded.AsSpan().SequenceEqual( bmdBytes.AsSpan( relOffset, relSize ) );
            Console.WriteLine( $"relocation table: {decoded.Count} pointers, re-encode {( relOk ? "identical" : "DIFFERENT" )}" );
            ok &= relOk;

            var pakBytes = game.Archive.Read( PersonaLore.PakPath );
            var pak = PakArchive.Parse( pakBytes );
            var pakSame = pak.Serialize().AsSpan().SequenceEqual( pakBytes );
            Console.WriteLine( $"{PersonaLore.PakPath}: {( pakSame ? "identical" : "DIFFERENT" )} ({pak.Entries.Count} entries)" );
            ok &= pakSame;

            // Lore edit survives a write/read cycle and leaves other entries alone.
            var lore = new PersonaLore( script );
            var before6 = lore.Get( 6 );
            lore.Set( 5, "A test entry long enough to need wrapping onto several lines of the compendium page." );
            var reread = new PersonaLore( MessageScript.Parse( script.Serialize() ) );
            var loreOk = reread.Get( 5 ).Split( '\n' ).All( l => l.Length <= PersonaLore.LineWidth ) &&
                         reread.Get( 5 ).Replace( "\n", " " ).StartsWith( "A test entry long enough" ) && reread.Get( 6 ) == before6;
            Console.WriteLine( $"lore edit: {( loreOk ? "ok" : "FAILED" )}\n{reread.Get( 5 )}" );
            ok &= loreOk;

            // Edit-and-restore check through the typed views.
            var bytes = game.Archive.Read( GameData.PersonaTablePath );
            var table = PersonaTable.Parse( bytes );
            var level = table.Stats[1].Level;
            table.Stats[1].Level = 99;
            var edited = PersonaTable.Parse( table.Serialize() );
            var editOk = edited.Stats[1].Level == 99 && edited.Stats[2].Level == table.Stats[2].Level;
            Console.WriteLine( $"PERSONA.TBL edit through typed view: {( editOk ? "ok" : "FAILED" )} (level {level} -> 99)" );
            return ok && editOk;
        }

        private static void Render( GameData game, string modelPath, string packPath, int index, string outPrefix, float[] times )
        {
            var pack = AnimationCache.Shared.Model( game.Archive, modelPath );
            Animation? animation = null;
            if ( packPath != "-" )
            {
                animation = AnimationCache.Shared.FreshPack( game.Archive, packPath ).Animations[index];
                Console.WriteLine( $"animation {index}: {animation.Duration}s, {animation.Controllers.Count} controllers" );
            }
            var skinned = new SkinnedModel( pack );
            foreach ( var t in times )
            {
                var sw = Stopwatch.StartNew();
                skinned.Update( animation, t );
                var skinMs = sw.ElapsedMilliseconds;
                var png = SoftwareRenderer.RenderPng( skinned, 480, 480 );
                var file = $"{outPrefix}_{t:0.00}.png";
                System.IO.File.WriteAllBytes( file, png );
                Console.WriteLine( $"{file}: {skinned.Meshes.Count} meshes, {skinned.Meshes.Sum( m => m.Positions.Length )} verts, skin {skinMs} ms, bounds {skinned.BoundsMin} - {skinned.BoundsMax}" );
            }
        }

        private static int Swap( GameData game, string sourcePath, int personaId, string modsFolder )
        {
            var source = game.Catalog.Models.FirstOrDefault( m => m.Path.Equals( sourcePath, StringComparison.OrdinalIgnoreCase ) )
                         ?? throw new ArgumentException( $"No model {sourcePath}" );
            var dest = game.Catalog.Personas.First( p => p.Id == personaId );
            var project = new SwapProject( game, source, dest );
            Console.WriteLine( $"Swap {source.DisplayName} -> {dest}" );
            foreach ( var slot in project.Slots )
                Console.WriteLine( $"  {slot.Pack,-9} slot {slot.Slot,2}: {( slot.Empty ? "(empty)" : $"{System.IO.Path.GetFileName( slot.SourcePackPath )} #{slot.SourceIndex}{( slot.Retarget ? " retarget" : "" )}" )}" );

            project.PersonaName = "Test Swap";
            project.Stats.Level = 5;
            var heights = project.ModelHeights();
            var suggested = project.SuggestMatchingScale();
            Console.WriteLine( $"Heights: source {heights.Source:0.#}, destination {heights.Destination:0.#}; original scale {project.OriginalModelScale}% -> suggested {suggested}%" );
            project.ModelScale = suggested;
            project.LoreText = "Swapped in by Persona Swapper. This text replaces the original compendium entry.";

            var sw = Stopwatch.StartNew();
            var build = SwapBuilder.Build( project );
            Console.WriteLine( $"Built {build.Files.Count} files in {sw.ElapsedMilliseconds} ms" );
            foreach ( var f in build.Files )
                Console.WriteLine( $"  {f.Key} ({f.Value.Length} bytes)" );
            foreach ( var r in build.Slots )
                Console.WriteLine( $"  {r.Pack,-9} slot {r.Slot,2} <- {r.Source,-24} bones {r.Kept}/{r.Controllers}{( r.Retargeted ? " (retargeted)" : "" )}" );
            foreach ( var w in build.Warnings )
                Console.WriteLine( $"  WARNING: {w}" );

            // Validate: every output reloads, tables carry the edits.
            foreach ( var f in build.Files.Where( f => f.Key.EndsWith( ".GAP" ) || f.Key.EndsWith( ".GMD" ) ) )
            {
                using var stream = new System.IO.MemoryStream( f.Value );
                var res = Resource.Load( stream, false );
                var anims = res is ModelPack mp ? mp.AnimationPack?.Animations?.Count ?? 0 : 0;
                Console.WriteLine( $"  reload ok: {f.Key} ({res.ResourceType}, {anims} animations)" );
            }
            var names = NameTable.Parse( build.Files[GameData.NameTablePath] );
            var personas = PersonaTable.Parse( build.Files[GameData.PersonaTablePath] );
            Console.WriteLine( $"  name now '{names.Get( NameTable.Personas, personaId )}', level {personas.Stats[personaId].Level}" );
            Console.WriteLine( $"  scale now {VisualTable.Parse( build.Files[GameData.VisualTablePath] ).Personas[personaId].BattleModelScale}%" );
            var outLore = new PersonaLore( MessageScript.Parse( build.Files[PersonaLore.BmdPath] ) );
            var pakLore = new PersonaLore( MessageScript.Parse( PakArchive.Parse( build.Files[PersonaLore.PakPath] ).Get( PersonaLore.PakEntryName )! ) );
            Console.WriteLine( $"  lore now: {outLore.Get( personaId ).Replace( "\n", " / " )}" );
            Console.WriteLine( $"  PAK copy matches: {pakLore.Get( personaId ) == outLore.Get( personaId )}" );

            var folder = ModWriter.Write( modsFolder, new ModInfo( "p5rpc.personaswap.test", "Persona Swap Test", "Persona Swapper", $"{source.DisplayName} as {dest.Name}" ), build );
            Console.WriteLine( $"Wrote mod to {folder}" );
            return 0;
        }

        /// <summary>For personas that are recruited Shadows, finds which enemy animation each persona slot copies.</summary>
        private static void SlotMap( GameData game, int[] personaIds )
        {
            var tally = new Dictionary<(int PersonaSlot, int EnemySlot), int>();
            var compared = 0;
            var ids = personaIds.Length > 0 ? personaIds : game.Catalog.Personas.Select( p => p.Id ).ToArray();
            foreach ( var pid in ids )
            {
                var persona = game.Catalog.Personas.FirstOrDefault( p => p.Id == pid );
                if ( persona?.BattleAnimationPath == null ) continue;

                // Enemy units that give this persona; use the first one with a battle GAP.
                var enemyModel = Enumerable.Range( 0, game.Units.EnemyVisuals.Count )
                    .Where( u => game.Units.EnemyVisuals[u].PersonaId == pid )
                    .Select( u => (int)game.Units.EnemyVisuals[u].ModelId )
                    .FirstOrDefault( m => game.Archive.Contains( $"MODEL/CHARACTER/ENEMY/{m:D4}/BEM{m:D4}_000.GAP" ) );
                if ( enemyModel == 0 ) continue;

                var personaPack = LoadPack( game, persona.BattleAnimationPath );
                var enemyPack = LoadPack( game, $"MODEL/CHARACTER/ENEMY/{enemyModel:D4}/BEM{enemyModel:D4}_000.GAP" );
                if ( personaPack?.Animations == null || enemyPack?.Animations == null ) continue;
                compared++;

                var line = new List<string>();
                for ( var ps = 0; ps < personaPack.Animations.Count; ps++ )
                {
                    var pa = personaPack.Animations[ps];
                    if ( pa.Controllers == null || pa.Controllers.Count == 0 ) continue;
                    var match = -1;
                    for ( var es = 0; es < enemyPack.Animations.Count && match < 0; es++ )
                        if ( SameMotion( pa, enemyPack.Animations[es] ) ) match = es;
                    line.Add( $"{ps}<-{( match >= 0 ? match.ToString() : "?" )}" );
                    if ( match >= 0 )
                        tally[(ps, match)] = tally.TryGetValue( (ps, match), out var c ) ? c + 1 : 1;
                }
                if ( personaIds.Length > 0 || compared <= 10 )
                    Console.WriteLine( $"persona {pid} vs enemy model {enemyModel}: {string.Join( "  ", line )}" );
            }

            Console.WriteLine( $"Compared {compared} persona/enemy pairs. Most common enemy slot per persona slot:" );
            foreach ( var g in tally.GroupBy( t => t.Key.PersonaSlot ).OrderBy( g => g.Key ) )
                Console.WriteLine( $"  persona slot {g.Key,2}: " + string.Join( ", ", g.OrderByDescending( x => x.Value ).Take( 3 ).Select( x => $"enemy {x.Key.EnemySlot} ({x.Value})" ) ) );
        }

        private static AnimationPack? LoadPack( GameData game, string path )
        {
            try
            {
                using var stream = new System.IO.MemoryStream( game.Archive.Read( path ) );
                return Resource.Load<AnimationPack>( stream );
            }
            catch { return null; }
        }

        /// <summary>Same duration and same first/last key on the first few node controllers.</summary>
        private static bool SameMotion( Animation a, Animation b )
        {
            if ( b.Controllers == null || b.Controllers.Count == 0 || Math.Abs( a.Duration - b.Duration ) > 0.001f )
                return false;
            var byName = b.Controllers.Where( c => c.TargetKind == TargetKind.Node ).GroupBy( c => c.TargetName ).ToDictionary( g => g.Key, g => g.First() );
            var checkedCount = 0;
            foreach ( var ca in a.Controllers.Where( c => c.TargetKind == TargetKind.Node ) )
            {
                if ( !byName.TryGetValue( ca.TargetName, out var cb ) ) continue;
                var la = ca.Layers.FirstOrDefault( l => l.HasPRSKeyFrames );
                var lb = cb.Layers.FirstOrDefault( l => l.HasPRSKeyFrames );
                if ( la == null || lb == null ) continue;
                if ( la.Keys.Count != lb.Keys.Count ) return false;
                var ka = (PRSKey)la.Keys[la.Keys.Count / 2];
                var kb = (PRSKey)lb.Keys[lb.Keys.Count / 2];
                if ( System.Numerics.Quaternion.Dot( ka.Rotation, kb.Rotation ) < 0.999f ) return false;
                if ( ++checkedCount >= 12 ) break;
            }
            return checkedCount >= 3;
        }

        private static void SlotStats( GameData game )
        {
            var used = new int[32];
            var total = new double[32];
            var files = 0;
            var maxSlots = 0;
            foreach ( var persona in game.Catalog.Personas.Where( p => p.BattleAnimationPath != null ) )
            {
                try
                {
                    using var stream = new System.IO.MemoryStream( game.Archive.Read( persona.BattleAnimationPath! ) );
                    var pack = Resource.Load<AnimationPack>( stream );
                    if ( pack?.Animations == null ) continue;
                    files++;
                    maxSlots = Math.Max( maxSlots, pack.Animations.Count );
                    for ( var i = 0; i < pack.Animations.Count && i < used.Length; i++ )
                    {
                        if ( pack.Animations[i].Controllers?.Count > 0 )
                        {
                            used[i]++;
                            total[i] += pack.Animations[i].Duration;
                        }
                    }
                }
                catch ( Exception e )
                {
                    Console.WriteLine( $"  {persona.BattleAnimationPath}: {e.GetType().Name} {e.Message}" );
                }
            }

            Console.WriteLine( $"{files} persona battle GAPs, up to {maxSlots} slots" );
            for ( var i = 0; i < maxSlots; i++ )
                Console.WriteLine( $"  slot {i,2}: used by {used[i],3} ({100.0 * used[i] / Math.Max( 1, files ),5:0.#}%), avg {( used[i] > 0 ? total[i] / used[i] : 0 ):0.##}s" );
        }
    }
}
