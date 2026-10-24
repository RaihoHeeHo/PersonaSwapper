using System;
using System.IO;
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

                case "previewcheck":
                    // previewcheck <start> <count> [filter]
                    return PreviewCheck( game, int.Parse( args[2] ), int.Parse( args[3] ), args.Length > 4 ? args[4] : null );

                case "zcompare":
                    ZCompare( game, args.Skip( 2 ).Select( int.Parse ).ToArray() );
                    return 0;

                case "helpers":
                    Helpers( game );
                    return 0;

                case "packextras":
                    PackExtras( game );
                    return 0;

                case "validatebase":
                    ValidateBase( game );
                    return 0;

                case "swapmatrix":
                    SwapMatrix( game, args.Skip( 2 ).ToArray() );
                    return 0;

                case "ids":
                    Ids( game, args[2], args[3] );
                    return 0;

                case "gaproundtrip":
                    GapRoundTrip( game, args.Length > 2 ? args[2] : "PERSONA" );
                    return 0;

                case "resave":
                    System.IO.File.WriteAllBytes( args[3], SwapBuilder.Save( AnimationCache.Shared.FreshPack( game.Archive, args[2] ) ) );
                    System.IO.File.WriteAllBytes( args[3] + ".orig", game.Archive.Read( args[2] ) );
                    return 0;

                case "identity":
                    Identity( game, args.Skip( 2 ).Select( int.Parse ).ToArray() );
                    return 0;

                case "materials":
                    Materials( game, args.Skip( 2 ).ToArray() );
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

        /// <summary>Runs everything the preview does for a model, across many models, and reports what breaks.</summary>
        private static int PreviewCheck( GameData game, int start, int count, string? filter )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            var models = game.Catalog.Models
                .Where( m => filter == null || m.Path.Contains( filter, StringComparison.OrdinalIgnoreCase ) )
                .Skip( start ).Take( count ).ToList();
            int failures = 0, withIssues = 0;
            var sw = Stopwatch.StartNew();
            foreach ( var model in models )
            {
                var stage = "parse";
                try
                {
                    var pack = AnimationCache.Shared.Model( game.Archive, model.Path );
                    if ( pack.Model == null ) { Console.WriteLine( $"NOMODEL {model.Path}" ); continue; }
                    stage = "build";
                    var skinned = new SkinnedModel( pack );
                    stage = "bind pose";
                    skinned.Update( null, 0 );
                    var ownPack = SwapProject.OwnAnimationPacks( model ).FirstOrDefault();
                    if ( ownPack != null )
                    {
                        stage = $"animation {System.IO.Path.GetFileName( ownPack )}";
                        var anims = AnimationCache.Shared.FreshPack( game.Archive, ownPack ).Animations;
                        var anim = anims?.FirstOrDefault( a => a.Controllers?.Count > 0 );
                        if ( anim != null )
                            foreach ( var t in new[] { 0f, anim.Duration * 0.37f, anim.Duration * 0.81f } )
                                skinned.Update( anim, t );
                    }
                    stage = "textures";
                    var undecodable = 0;
                    if ( pack.Textures != null )
                        foreach ( var (_, tex) in pack.Textures )
                            if ( TextureDecoding.TryDecode( tex ) == null ) undecodable++;
                    var bad = !float.IsFinite( skinned.BoundsMin.X ) || !float.IsFinite( skinned.BoundsMax.Y ) || skinned.NonFiniteVertices > 0;
                    if ( skinned.NonFiniteVertices > 0 ) skinned.Issues.Add( $"{skinned.NonFiniteVertices} non-finite vertices" );
                    if ( skinned.Issues.Count > 0 || undecodable > 0 || bad )
                    {
                        withIssues++;
                        Console.WriteLine( $"ISSUES {model.Path}: " + string.Join( "; ", skinned.Issues.Take( 4 ) ) +
                                           ( undecodable > 0 ? $"; {undecodable} undecodable textures" : "" ) + ( bad ? "; non-finite bounds" : "" ) );
                    }
                }
                catch ( Exception e )
                {
                    failures++;
                    Console.WriteLine( $"FAIL {model.Path} at {stage}: {e.GetType().Name}: {e.Message}" );
                    Console.WriteLine( "     " + ( e.StackTrace ?? "" ).Split( '\n' ).FirstOrDefault()?.Trim() );
                }
                AnimationCache.Shared.Clear(); // keep memory flat across thousands of models
            }
            Console.WriteLine( $"Checked {models.Count} models in {sw.Elapsed.TotalSeconds:0}s: {failures} failed, {withIssues} with data issues" );
            return 0;
        }

        private static HashSet<int> HelperIds( GFDLibrary.Models.Model model )
        {
            var ids = new HashSet<int>();
            foreach ( var node in model.Nodes )
                if ( node.Properties != null && node.Properties.TryGetValue( "gfdHelperID", out var prop ) )
                    ids.Add( Convert.ToInt32( prop.GetValue() ) );
            return ids;
        }

        /// <summary>How the battle (PS), compendium (PSZ) and high-detail (PSZX) models of personas differ.</summary>
        private static void ZCompare( GameData game, int[] ids )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            var personas = ids.Length > 0 ? game.Catalog.Personas.Where( p => ids.Contains( p.Id ) ) : game.Catalog.Personas.Take( 12 );
            foreach ( var persona in personas )
            {
                Console.WriteLine( $"#{persona.Id}" );
                HashSet<string>? psNodes = null;
                foreach ( var variant in new[] { "PS", "PSZ", "PSZX" } )
                {
                    var path = $"{persona.Folder}/{variant}{persona.Id:D4}.GMD";
                    if ( !game.Archive.Contains( path ) ) { Console.WriteLine( $"  {variant}: missing" ); continue; }
                    var bytes = game.Archive.Read( path );
                    var pack = (ModelPack)Resource.Load( new System.IO.MemoryStream( bytes ), false );
                    var nodes = pack.Model?.Nodes.Select( n => n.Name ).ToHashSet() ?? new HashSet<string>();
                    psNodes ??= nodes;
                    var anims = pack.AnimationPack?.Animations?.Count ?? 0;
                    var animNames = pack.AnimationPack?.Animations?.SelectMany( a => a.Controllers ?? new() ).Select( c => c.TargetName ).Distinct().ToList() ?? new();
                    Console.WriteLine( $"  {variant}: {bytes.Length} bytes v{pack.Version:X8}, {nodes.Count} nodes ({nodes.Except( psNodes ).Count()} not in PS, {psNodes.Except( nodes ).Count()} PS nodes missing), " +
                                       $"{pack.Textures?.Count ?? 0} textures, {pack.Materials?.Count ?? 0} materials, embedded anims {anims} ({animNames.Count( n => !nodes.Contains( n ) )} of {animNames.Count} targets missing), " +
                                       $"F8={pack.ChunkType000100F8 != null} F9={pack.ChunkType000100F9 != null}, helpers [{string.Join( ",", pack.Model != null ? HelperIds( pack.Model ).OrderBy( x => x ) : Enumerable.Empty<int>() )}]" );
                    if ( variant != "PS" )
                        Console.WriteLine( $"     extra nodes: {string.Join( ", ", nodes.Except( psNodes ).Take( 8 ) )}" );
                }
                var gt = $"{persona.Folder}/GT{persona.Id:D4}.GAP";
                if ( game.Archive.Contains( gt ) )
                {
                    var gtPack = ((ModelPack)Resource.Load( new System.IO.MemoryStream( game.Archive.Read( gt ) ), false )).AnimationPack;
                    var targets = gtPack.Animations.SelectMany( a => a.Controllers ?? new() ).Where( c => c.TargetKind == TargetKind.Node ).Select( c => c.TargetName ).Distinct().ToList();
                    Console.WriteLine( $"  GT: {gtPack.Animations.Count} anims, {targets.Count} bones, {targets.Count( t => !psNodes!.Contains( t ) )} not in PS" );
                }
                AnimationCache.Shared.Clear();
            }
        }

        /// <summary>Which gfdHelperID helper nodes each kind of model carries.</summary>
        private static void Helpers( GameData game )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            var groups = new (string Name, IEnumerable<ModelEntry> Models)[]
            {
                ("persona PS", game.Catalog.Personas.Select( p => game.Catalog.Models.First( m => m.Path == p.BattleModelPath ) )),
                ("enemy EM", game.Catalog.Models.Where( m => m.Category == ModelCategory.Enemy && m.FileName.StartsWith( "EM" ) )),
                ("character _051/_000", game.Catalog.Models.Where( m => m.Category == ModelCategory.Character && ( m.FileName.Contains( "_051_" ) || m.FileName.Contains( "_000_" ) ) ).Take( 150 )),
            };
            foreach ( var (name, models) in groups )
            {
                var freq = new Dictionary<int, int>();
                var n = 0; var none = 0;
                foreach ( var m in models )
                {
                    try
                    {
                        var model = AnimationCache.Shared.Model( game.Archive, m.Path ).Model;
                        if ( model == null ) continue;
                        n++;
                        var ids = HelperIds( model );
                        if ( ids.Count == 0 ) none++;
                        foreach ( var id in ids ) freq[id] = freq.TryGetValue( id, out var c ) ? c + 1 : 1;
                    }
                    catch { }
                    AnimationCache.Shared.Clear();
                }
                Console.WriteLine( $"{name}: {n} models, {none} without any helpers" );
                Console.WriteLine( "  " + string.Join( ", ", freq.OrderBy( x => x.Key ).Select( x => $"{x.Key}:{x.Value}" ) ) );
            }
        }

        /// <summary>Blend animations, pack-level and per-animation extra data in persona packs.</summary>
        private static void PackExtras( GameData game )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            int packs = 0, withBlend = 0, withBit29 = 0, a28 = 0, a29 = 0, a31 = 0;
            var effectBones = new Dictionary<string, int>();
            foreach ( var persona in game.Catalog.Personas )
            {
                foreach ( var path in new[] { persona.BattleAnimationPath, $"{persona.Folder}/GT{persona.Id:D4}.GAP" } )
                {
                    if ( path == null || !game.Archive.Contains( path ) ) continue;
                    var pack = AnimationCache.Shared.FreshPack( game.Archive, path );
                    packs++;
                    if ( pack.BlendAnimations?.Count > 0 ) withBlend++;
                    if ( pack.Bit29Data != null ) withBit29++;
                    foreach ( var a in pack.Animations ?? new List<Animation>() )
                    {
                        if ( a.Field10 != null ) { a28++; foreach ( var e in a.Field10 ) effectBones[e.Field04 ?? ""] = effectBones.TryGetValue( e.Field04 ?? "", out var c ) ? c + 1 : 1; }
                        if ( a.Field14 != null ) a29++;
                        if ( a.Field1C != null ) a31++;
                    }
                }
            }
            Console.WriteLine( $"{packs} persona packs: {withBlend} with blend animations, {withBit29} with pack Bit29 data; animations with bit28 (EPL) {a28}, bit29 {a29}, bit31 {a31}" );
            var bit31Names = new Dictionary<string, int>();
            var bit31Kinds = new Dictionary<int, int>();
            foreach ( var persona in game.Catalog.Personas.Take( 120 ) )
            {
                if ( persona.BattleAnimationPath == null ) continue;
                var model = AnimationCache.Shared.Model( game.Archive, persona.BattleModelPath ).Model!;
                var names = new HashSet<string>( model.Nodes.Select( n => n.Name ) );
                foreach ( var a in AnimationCache.Shared.FreshPack( game.Archive, persona.BattleAnimationPath ).Animations )
                    if ( a.Field1C != null )
                    {
                        var key = ( names.Contains( a.Field1C.Field04 ?? "" ) ? "[node] " : "[not a node] " ) + a.Field1C.Field04;
                        bit31Names[key] = bit31Names.TryGetValue( key, out var c ) ? c + 1 : 1;
                        bit31Kinds[a.Field1C.Field00] = bit31Kinds.TryGetValue( a.Field1C.Field00, out var k ) ? k + 1 : 1;
                    }
                AnimationCache.Shared.Clear();
            }
            Console.WriteLine( "bit31 names: " + string.Join( ", ", bit31Names.OrderByDescending( x => x.Value ).Take( 12 ).Select( x => $"{x.Key}:{x.Value}" ) ) );
            Console.WriteLine( "bit31 Field00: " + string.Join( ", ", bit31Kinds.Select( x => $"{x.Key}:{x.Value}" ) ) );
            Console.WriteLine( "EPL attach points: " + string.Join( ", ", effectBones.OrderByDescending( x => x.Value ).Take( 15 ).Select( x => $"{x.Key}:{x.Value}" ) ) );
        }

        /// <summary>The game's own persona packs checked against their own models (should be clean).</summary>
        private static void ValidateBase( GameData game )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            int packs = 0, bad = 0;
            foreach ( var persona in game.Catalog.Personas )
            {
                var model = AnimationCache.Shared.Model( game.Archive, persona.BattleModelPath ).Model!;
                foreach ( var path in new[] { persona.BattleAnimationPath, $"{persona.Folder}/GT{persona.Id:D4}.GAP" } )
                {
                    if ( path == null || !game.Archive.Contains( path ) ) continue;
                    packs++;
                    var problems = SwapBuilder.FindInvalidReferences( AnimationCache.Shared.FreshPack( game.Archive, path ), model, System.IO.Path.GetFileName( path ) );
                    if ( problems.Count > 0 )
                    {
                        bad++;
                        if ( bad <= 8 ) Console.WriteLine( $"{path}: {problems.Count} problems, e.g. {problems[0]}" );
                    }
                }
                AnimationCache.Shared.Clear();
            }
            Console.WriteLine( $"{packs} base-game persona packs, {bad} with references that don't match their own model" );
        }

        /// <summary>Builds swaps for every source x destination pair given (paths and persona IDs) and reports validation.</summary>
        private static void SwapMatrix( GameData game, string[] items )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            var sources = items.Where( i => i.Contains( '/' ) ).ToList();
            var dests = items.Where( i => !i.Contains( '/' ) ).Select( int.Parse ).ToList();
            foreach ( var sourcePath in sources )
                foreach ( var destId in dests )
                {
                    var source = game.Catalog.Models.First( m => m.Path.Equals( sourcePath, StringComparison.OrdinalIgnoreCase ) );
                    var dest = game.Catalog.Personas.First( p => p.Id == destId );
                    var project = new SwapProject( game, source, dest );
                    var build = SwapBuilder.Build( project );
                    var model = AnimationCache.Shared.Model( game.Archive, source.Path ).Model!;
                    var problems = 0;
                    foreach ( var f in build.Files.Where( f => f.Key.EndsWith( ".GAP" ) ) )
                    {
                        using var stream = new System.IO.MemoryStream( f.Value );
                        var pack = Resource.Load<AnimationPack>( stream );
                        var found = SwapBuilder.FindInvalidReferences( pack, model, System.IO.Path.GetFileName( f.Key ) );
                        problems += found.Count;
                        foreach ( var line in found.Take( 4 ) ) Console.WriteLine( "    " + line );
                    }
                    var orig = AnimationCache.Shared.FreshPack( game.Archive, dest.BattleAnimationPath! );
                    Console.WriteLine( $"{source.FileName,-20} -> {dest.Id,3} {dest.Name,-16} blend {orig.BlendAnimations?.Count ?? 0,2}, bit29 {( orig.Bit29Data != null ? "y" : "n" )}: " +
                                       $"{problems} invalid refs after re-reading; {build.Warnings.Count} warnings" +
                                       ( build.Warnings.Count > 0 ? $" ({string.Join( " | ", build.Warnings.Select( w => w.Length > 90 ? w[..90] + "…" : w ) )})" : "" ) );
                }
        }

        /// <summary>Controller target IDs/names in a pack vs. the model's node order.</summary>
        private static void Ids( GameData game, string modelPath, string packPath )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            var model = AnimationCache.Shared.Model( game.Archive, modelPath ).Model!;
            var nodes = model.Nodes.ToList();
            Console.WriteLine( $"{nodes.Count} nodes. First 12: " + string.Join( ", ", nodes.Take( 12 ).Select( ( n, i ) => $"{i}:{n.Name}" ) ) );
            var anim = AnimationCache.Shared.FreshPack( game.Archive, packPath ).Animations.First( a => a.Controllers?.Count > 0 );
            var byName = nodes.Select( ( n, i ) => (n.Name, i) ).GroupBy( x => x.Name ).ToDictionary( g => g.Key, g => g.First().i );
            int same = 0, plusOne = 0, missing = 0, other = 0;
            foreach ( var c in anim.Controllers.Where( c => c.TargetKind == TargetKind.Node ) )
            {
                if ( !byName.TryGetValue( c.TargetName, out var index ) ) { missing++; continue; }
                if ( index == c.TargetId ) same++; else if ( index + 1 == c.TargetId ) plusOne++; else other++;
            }
            Console.WriteLine( $"{anim.Controllers.Count} controllers: id==dfsIndex {same}, id==dfsIndex+1 {plusOne}, name not in model {missing}, other {other}" );
            Console.WriteLine( "First 15 controllers: " + string.Join( ", ", anim.Controllers.Take( 15 ).Select( c => $"{c.TargetId}:{c.TargetName}" ) ) );
            var missingNames = anim.Controllers.Where( c => !byName.ContainsKey( c.TargetName ) ).Select( c => $"{c.TargetId}:{c.TargetName}" ).Take( 10 );
            Console.WriteLine( "Not in model: " + string.Join( ", ", missingNames ) );
            var maxId = anim.Controllers.Max( c => c.TargetId );
            Console.WriteLine( $"Max target id {maxId} (model has {nodes.Count} nodes)" );
        }

        /// <summary>Re-saves game GAPs unchanged through GFDLibrary and compares bytes.</summary>
        private static void GapRoundTrip( GameData game, string filter )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            var paths = game.Archive.Paths.Where( p => p.EndsWith( ".GAP", StringComparison.OrdinalIgnoreCase ) && p.Contains( filter, StringComparison.OrdinalIgnoreCase ) ).Take( 400 ).ToList();
            int same = 0, differ = 0, failed = 0;
            foreach ( var path in paths )
            {
                var original = game.Archive.Read( path );
                try
                {
                    var pack = AnimationCache.Shared.FreshPack( game.Archive, path );
                    var saved = SwapBuilder.Save( pack );
                    if ( saved.AsSpan().SequenceEqual( original ) ) { same++; continue; }
                    differ++;
                    if ( differ <= 6 )
                    {
                        var firstDiff = 0;
                        while ( firstDiff < Math.Min( saved.Length, original.Length ) && saved[firstDiff] == original[firstDiff] ) firstDiff++;
                        Console.WriteLine( $"DIFF {path}: {original.Length} -> {saved.Length} bytes, first difference at 0x{firstDiff:X}" );
                        Console.WriteLine( $"   orig: {Convert.ToHexString( original, Math.Max( 0, firstDiff - 8 ), Math.Min( 32, original.Length - Math.Max( 0, firstDiff - 8 ) ) )}" );
                        Console.WriteLine( $"   save: {Convert.ToHexString( saved, Math.Max( 0, firstDiff - 8 ), Math.Min( 32, saved.Length - Math.Max( 0, firstDiff - 8 ) ) )}" );
                    }
                }
                catch ( Exception e )
                {
                    failed++;
                    if ( failed <= 3 ) Console.WriteLine( $"FAIL {path}: {e.Message}" );
                }
                AnimationCache.Shared.Clear();
            }
            Console.WriteLine( $"{paths.Count} GAPs matching '{filter}': {same} identical, {differ} different, {failed} failed" );
        }

        /// <summary>A persona swapped onto itself must export files identical to the game's.</summary>
        private static void Identity( GameData game, int[] ids )
        {
            PersonaSwapper.Core.Diagnostics.Log.EchoToConsole = false;
            int identical = 0, total = 0;
            foreach ( var id in ids.Length > 0 ? ids : game.Catalog.Personas.Select( p => p.Id ).Take( 60 ).ToArray() )
            {
                var dest = game.Catalog.Personas.FirstOrDefault( p => p.Id == id );
                if ( dest == null ) continue;
                var source = game.Catalog.Models.First( m => m.Path.Equals( dest.BattleModelPath, StringComparison.OrdinalIgnoreCase ) );
                var build = SwapBuilder.Build( new SwapProject( game, source, dest ) );
                foreach ( var (path, data) in build.Files )
                {
                    total++;
                    var same = game.Archive.Read( path ).AsSpan().SequenceEqual( data );
                    if ( same ) identical++;
                    else
                    {
                        Console.WriteLine( $"  persona {id}: {path} differs ({game.Archive.GetSize( path )} -> {data.Length} bytes)" );
                        var dump = Environment.GetEnvironmentVariable( "PSWAP_DUMP" );
                        if ( dump != null )
                        {
                            Directory.CreateDirectory( dump );
                            var name = Path.GetFileName( path );
                            File.WriteAllBytes( Path.Combine( dump, name + ".orig" ), game.Archive.Read( path ) );
                            File.WriteAllBytes( Path.Combine( dump, name + ".new" ), data );
                        }
                    }
                }
                AnimationCache.Shared.Clear();
            }
            Console.WriteLine( $"Identity swaps: {identical} of {total} exported files identical to the game's" );
        }

        /// <summary>Material alpha settings vs. how much of each diffuse texture is low-alpha.</summary>
        private static void Materials( GameData game, string[] filters )
        {
            var models = game.Catalog.Models.Where( m => filters.Length == 0 ? m.Category == ModelCategory.Enemy : filters.Any( f => m.Path.Contains( f, StringComparison.OrdinalIgnoreCase ) ) ).Take( 40 ).ToList();
            var tally = new Dictionary<string, (int Count, int LowAlphaTextures)>();
            foreach ( var model in models )
            {
                var pack = AnimationCache.Shared.Model( game.Archive, model.Path );
                if ( pack.Materials == null ) continue;
                foreach ( var (name, material) in pack.Materials )
                {
                    var key = $"draw={material.DrawMethod} alphaTest={( material.Flags & GFDLibrary.Materials.MaterialFlags.AlphaTest ) != 0} transp={( material.Flags & GFDLibrary.Materials.MaterialFlags.Transparency ) != 0}";
                    double low = 0;
                    var texName = material.DiffuseMap?.Name;
                    if ( texName != null && pack.Textures != null && pack.Textures.TryGetValue( texName, out var tex ) )
                    {
                        var decoded = TextureDecoding.TryDecode( tex );
                        if ( decoded != null )
                        {
                            var n = decoded.Width * decoded.Height; var c = 0;
                            for ( var i = 0; i < n; i++ ) if ( decoded.Rgba[i * 4 + 3] < 77 ) c++;
                            low = (double)c / n;
                        }
                    }
                    var t = tally.TryGetValue( key, out var v ) ? v : (Count: 0, LowAlphaTextures: 0);
                    tally[key] = (t.Count + 1, t.LowAlphaTextures + ( low > 0.05 ? 1 : 0 ));
                    if ( filters.Length > 0 )
                        Console.WriteLine( $"  {model.FileName} {name}: {key} clip={material.AlphaClip} mode={material.AlphaClipMode} tex={texName} lowAlpha={low:P0}" );
                }
            }
            Console.WriteLine( $"{models.Count} models" );
            foreach ( var (k, v) in tally.OrderByDescending( x => x.Value.Count ) )
                Console.WriteLine( $"  {k}: {v.Count} materials, {v.LowAlphaTextures} with >5% low-alpha texels" );
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
