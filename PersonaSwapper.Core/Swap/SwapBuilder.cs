using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using PersonaSwapper.Core.Diagnostics;
using PersonaSwapper.Core.Game;

namespace PersonaSwapper.Core.Swap
{
    /// <summary>What happened to one destination slot when building.</summary>
    public sealed record SlotReport( AnimationPackKind Pack, int Slot, string Source, int Controllers, int Kept, bool Retargeted )
    {
        public double Coverage => Controllers == 0 ? 1 : (double)Kept / Controllers;
    }

    public sealed class SwapBuildResult
    {
        /// <summary>Game-relative path ("MODEL/CHARACTER/...") to file contents.</summary>
        public Dictionary<string, byte[]> Files { get; } = new( StringComparer.OrdinalIgnoreCase );

        public List<SlotReport> Slots { get; } = new();

        public List<string> Warnings { get; } = new();
    }

    /// <summary>Turns a <see cref="SwapProject"/> into the game files that make the swap happen.</summary>
    public static class SwapBuilder
    {
        public static SwapBuildResult Build( SwapProject project )
        {
            var result = new SwapBuildResult();
            var game = project.Game;
            var cache = AnimationCache.Shared;
            var dest = project.Destination;
            var sourceModel = cache.Model( game.Archive, project.SourceModel.Path );

            if ( sourceModel.Model == null )
                throw new InvalidDataException( $"{project.SourceModel.Path} has no model data." );

            // 1. Models: the source GMD becomes the persona's battle model (and compendium models).
            var sourceBytes = cache.Bytes( game.Archive, project.SourceModel.Path );
            result.Files[dest.BattleModelPath] = sourceBytes;
            if ( project.ReplaceCompendiumModels )
            {
                foreach ( var variant in new[] { "PSZ", "PSZX" } )
                {
                    var path = $"{dest.Folder}/{variant}{dest.Id:D4}.GMD";
                    if ( game.Archive.Contains( path ) )
                        result.Files[path] = sourceBytes;
                }
            }

            // The persona menu and compendium show the Shadow model (EM), which is also the Shadow's battle model.
            if ( project.ReplaceMenuModel && project.MenuModelPath != null &&
                 !string.Equals( project.MenuModelPath, project.SourceModel.Path, StringComparison.OrdinalIgnoreCase ) )
            {
                result.Files[project.MenuModelPath] = sourceBytes;
                var ownShadowPack = project.SourceModel.Category == Game.ModelCategory.Enemy
                    ? SwapProject.OwnAnimationPacks( project.SourceModel ).FirstOrDefault( p => System.IO.Path.GetFileName( p ).StartsWith( "BEM", StringComparison.OrdinalIgnoreCase ) && p.EndsWith( "_000.GAP", StringComparison.OrdinalIgnoreCase ) )
                    : null;
                if ( project.ShadowAnimationPath != null && ownShadowPack != null )
                {
                    // An enemy source brings its own Shadow animations, so the Shadow version is fully that enemy.
                    result.Files[project.ShadowAnimationPath] = cache.Bytes( game.Archive, ownShadowPack );
                }
                else if ( project.ShadowAnimationPath != null )
                    result.Warnings.Add( $"{project.MenuModelPath} (shown in menus) is also {dest.Name}'s Shadow in battle; the Shadow will play its own animations on the new model." );
            }

            var duplicates = sourceModel.Model.Nodes.GroupBy( n => n.Name ).Where( g => g.Count() > 1 ).Select( g => g.Key ).ToList();
            if ( duplicates.Count > 0 )
                result.Warnings.Add( $"Source model has duplicate bone names ({string.Join( ", ", duplicates.Take( 5 ) )}); those bones may animate oddly." );

            // 2. Animation packs: start from the destination's own pack (keeps its flags/version/slot count), replace slots.
            foreach ( var group in project.Slots.GroupBy( s => s.Pack ) )
            {
                var packPath = project.DestinationPackPath( group.Key );
                if ( packPath == null )
                    continue;

                var output = cache.FreshPack( game.Archive, packPath );
                var contributors = new List<(string Path, bool Own)>();
                foreach ( var slot in group.OrderBy( s => s.Slot ) )
                {
                    if ( slot.Slot >= output.Animations.Count )
                    {
                        result.Warnings.Add( $"{group.Key} slot {slot.Slot} doesn't exist in {packPath}; skipped." );
                        continue;
                    }

                    if ( slot.Empty )
                    {
                        output.Animations[slot.Slot].Controllers?.Clear();
                        result.Slots.Add( new SlotReport( group.Key, slot.Slot, "(empty)", 0, 0, false ) );
                        continue;
                    }

                    var animation = BuildSlotAnimation( project, slot, sourceModel.Model, out var report );
                    contributors.Add( (slot.SourcePackPath, slot.RigModelPath == null ||
                        string.Equals( slot.RigModelPath, project.SourceModel.Path, StringComparison.OrdinalIgnoreCase )) );
                    output.Animations[slot.Slot] = animation;
                    result.Slots.Add( report );
                    if ( report.Controllers > 0 && report.Coverage < 0.5 )
                        result.Warnings.Add( $"{group.Key} slot {slot.Slot}: only {report.Kept} of {report.Controllers} animated bones exist on the source model." );
                }

                // Blend animations and pack-level extra data also reference bones by index. Prefer the source's own
                // (they match its skeleton), then make sure nothing points at bones the new model doesn't have.
                var extrasSource = ExtrasPackFor( project, group.Key );
                var sameModel = string.Equals( project.SourceModel.Path, dest.BattleModelPath, StringComparison.OrdinalIgnoreCase );
                if ( extrasSource != null && !sameModel )
                    TakeExtras( output, cache.FreshPack( game.Archive, extrasSource ), result, group.Key );
                Sanitize( output, sourceModel.Model, result, group.Key, remapExtras: extrasSource == null && !sameModel );
                Validate( output, sourceModel.Model, result, group.Key );

                // Bone names are written with a hash, and shipped files don't always agree on it (PS0039.GMD and
                // BPS0039_000.GAP hash "Bip01 L ﾎﾟﾆｰﾃｰﾙ 1" differently, and the game plays it fine). Re-reading
                // the files right before saving sets which hash each name gets: the model's first, then the pack
                // the output starts from, then the packs animations came from, the source's own packs last, so an
                // animation used unchanged is written exactly as shipped.
                RefreshNameHashes( sourceBytes );
                RefreshNameHashes( cache.Bytes( game.Archive, packPath ) );
                foreach ( var path in contributors.OrderBy( c => c.Own ).Select( c => c.Path ).Distinct( StringComparer.OrdinalIgnoreCase ) )
                    RefreshNameHashes( cache.Bytes( game.Archive, path ) );
                result.Files[packPath] = Save( output );
            }

            CheckHelpers( project, sourceModel.Model, result );

            // 3. Tables: only the ones that differ from the game's originals.
            AddIfChanged( result, game, GameData.PersonaTablePath, project.Personas.Serialize() );
            AddIfChanged( result, game, GameData.UnitTablePath, project.Units.Serialize() );
            AddIfChanged( result, game, GameData.NameTablePath, project.Names.Serialize() );
            AddIfChanged( result, game, GameData.VisualTablePath, project.Visual.Serialize() );

            // 4. Lore: the compendium reads FACILITY/DATMYTH.BMD; an identical copy lives in INIT/DATMSG.PAK.
            if ( project.Lore != null )
            {
                var bmd = project.Lore.Script.Serialize();
                if ( !game.Archive.Read( Messages.PersonaLore.BmdPath ).AsSpan().SequenceEqual( bmd ) )
                {
                    result.Files[Messages.PersonaLore.BmdPath] = bmd;
                    if ( game.Archive.Contains( Messages.PersonaLore.PakPath ) )
                    {
                        var pak = Messages.PakArchive.Parse( game.Archive.Read( Messages.PersonaLore.PakPath ) );
                        if ( pak.Get( Messages.PersonaLore.PakEntryName ) != null )
                        {
                            pak.Set( Messages.PersonaLore.PakEntryName, bmd );
                            result.Files[Messages.PersonaLore.PakPath] = pak.Serialize();
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>Sets controller indices to the model's node order for names it has; other controllers are kept as-is.</summary>
        public static void RemapTargetIds( Animation? animation, Model model )
        {
            if ( animation?.Controllers == null ) return;
            var index = new Dictionary<string, int>();
            var i = 0;
            foreach ( var node in model.Nodes )
                index.TryAdd( node.Name, i++ );
            foreach ( var c in animation.Controllers )
                if ( c.TargetKind == TargetKind.Node && index.TryGetValue( c.TargetName, out var id ) )
                    c.TargetId = id;
        }

        private static void RefreshNameHashes( byte[] bytes )
        {
            try
            {
                using var stream = new MemoryStream( bytes );
                Resource.Load( stream, false );
            }
            catch ( Exception e )
            {
                Log.Warn( $"Couldn't refresh bone name hashes from the source model: {e.Message}" );
            }
        }

        /// <summary>The source's own pack whose blend/extra data should replace the destination's, if any.</summary>
        private static string? ExtrasPackFor( SwapProject project, AnimationPackKind kind )
        {
            var own = SwapProject.OwnAnimationPacks( project.SourceModel );
            if ( kind == AnimationPackKind.Battle )
                return project.SourceModel.Category is Game.ModelCategory.Persona or Game.ModelCategory.Enemy
                    ? own.FirstOrDefault( SwapProject.IsBattlePack )
                    : null;
            return project.SourceModel.Category == Game.ModelCategory.Persona
                ? own.FirstOrDefault( p => Path.GetFileName( p ).StartsWith( "GT", StringComparison.OrdinalIgnoreCase ) )
                : null;
        }

        private static void TakeExtras( AnimationPack output, AnimationPack source, SwapBuildResult result, AnimationPackKind kind )
        {
            var replaced = 0;
            if ( output.BlendAnimations != null && source.BlendAnimations != null )
            {
                for ( var i = 0; i < output.BlendAnimations.Count && i < source.BlendAnimations.Count; i++ )
                {
                    output.BlendAnimations[i] = source.BlendAnimations[i];
                    replaced++;
                }
            }
            if ( output.Bit29Data != null && source.Bit29Data != null )
                output.Bit29Data = source.Bit29Data;
            if ( replaced > 0 )
                Log.Info( $"{kind} pack: took {replaced} blend animation(s) from the source's own pack" );
        }

        /// <summary>
        /// Removes the only bone references the game doesn't tolerate missing: bone-bound data blocks (never missing in
        /// shipped packs) and effect attachments. Controllers are never removed.
        /// </summary>
        /// <param name="remapExtras">True when the blend/extra animations still belong to the destination persona.</param>
        private static void Sanitize( AnimationPack pack, Model model, SwapBuildResult result, AnimationPackKind kind, bool remapExtras )
        {
            var names = new HashSet<string>( model.Nodes.Select( n => n.Name ) );
            int bit31Dropped = 0, effectsDropped = 0;

            void Fix( Animation? animation )
            {
                if ( animation == null ) return;

                if ( animation.Field1C != null && !names.Contains( animation.Field1C.Field04 ?? "" ) )
                {
                    animation.Field1C = null; // per-animation data bound to a bone this model doesn't have
                    bit31Dropped++;
                }
                if ( animation.Field10 != null )
                {
                    var kept = animation.Field10.Where( e => string.IsNullOrEmpty( e.Field04 ) || names.Contains( e.Field04 ) ).ToList();
                    effectsDropped += animation.Field10.Count - kept.Count;
                    animation.Field10 = kept.Count > 0 ? kept : null;
                }
            }

            foreach ( var a in pack.Animations ?? new List<Animation>() ) Fix( a );
            foreach ( var a in pack.BlendAnimations ?? new List<Animation>() ) { if ( remapExtras ) RemapTargetIds( a, model ); Fix( a ); }
            if ( pack.Bit29Data != null )
            {
                foreach ( var a in new[] { pack.Bit29Data.Field00, pack.Bit29Data.Field04, pack.Bit29Data.Field08, pack.Bit29Data.Field0C } )
                {
                    if ( remapExtras ) RemapTargetIds( a, model );
                    Fix( a );
                }
            }

            if ( bit31Dropped + effectsDropped > 0 )
                Log.Info( $"{kind} pack: removed {bit31Dropped} bone-bound data block(s) and {effectsDropped} effect attachment(s) " +
                          "naming bones the model doesn't have" );
        }

        /// <summary>Checks every bone reference in a finished pack against the model; problems become warnings.</summary>
        private static void Validate( AnimationPack pack, Model model, SwapBuildResult result, AnimationPackKind kind )
        {
            var problems = FindInvalidReferences( pack, model, kind.ToString() );
            if ( problems.Count == 0 )
            {
                Log.Info( $"{kind} pack validated: all bone references match the model" );
                return;
            }
            foreach ( var problem in problems.Take( 20 ) ) Log.Warn( problem );
            result.Warnings.Add( $"{kind} pack has {problems.Count} reference(s) to bones the model doesn't have (see log)." );
        }

        /// <summary>Every bone reference in the pack whose index or name doesn't match the model.</summary>
        public static List<string> FindInvalidReferences( AnimationPack pack, Model model, string kind )
        {
            var nodes = model.Nodes.ToList();
            var names = new HashSet<string>( nodes.Select( n => n.Name ) );
            var problems = new List<string>();

            void Check( Animation? animation, string label )
            {
                if ( animation == null ) return;
                foreach ( var c in animation.Controllers ?? new List<AnimationController>() )
                {
                    if ( c.TargetKind != TargetKind.Node ) continue;
                    // The game matches bones by name: shipped packs routinely have indices that don't line up with
                    // their models (e.g. all shifted by one), and names the model lacks are ignored. "RootNode" (persona
                    // packs) and "lookat" (character packs) are in shipped animations and in none of the models.
                    if ( !names.Contains( c.TargetName ) && c.TargetName is not ( "RootNode" or "lookat" ) )
                        problems.Add( $"{label}: controller targets '{c.TargetName}', which isn't in the model" );
                }
                if ( animation.Field1C != null && !names.Contains( animation.Field1C.Field04 ?? "" ) )
                    problems.Add( $"{label}: bone-bound data names missing bone '{animation.Field1C.Field04}'" );
            }

            for ( var i = 0; i < ( pack.Animations?.Count ?? 0 ); i++ ) Check( pack.Animations![i], $"{kind} animation {i}" );
            for ( var i = 0; i < ( pack.BlendAnimations?.Count ?? 0 ); i++ ) Check( pack.BlendAnimations![i], $"{kind} blend animation {i}" );
            if ( pack.Bit29Data != null )
            {
                Check( pack.Bit29Data.Field00, $"{kind} extra 0" );
                Check( pack.Bit29Data.Field04, $"{kind} extra 1" );
                Check( pack.Bit29Data.Field08, $"{kind} extra 2" );
                Check( pack.Bit29Data.Field0C, $"{kind} extra 3" );
            }

            return problems;
        }

        /// <summary>
        /// Persona models normally carry helper nodes (gfdHelperID) that effects and cameras anchor to.
        /// The game has personas without them, so this is a warning, not an error.
        /// </summary>
        private static void CheckHelpers( SwapProject project, Model sourceModel, SwapBuildResult result )
        {
            static HashSet<int> Ids( Model model )
            {
                var ids = new HashSet<int>();
                foreach ( var node in model.Nodes )
                    if ( node.Properties != null && node.Properties.TryGetValue( "gfdHelperID", out var prop ) )
                        try { ids.Add( Convert.ToInt32( prop.GetValue() ) ); } catch ( Exception ) { }
                return ids;
            }

            var original = AnimationCache.Shared.Model( project.Game.Archive, project.Destination.BattleModelPath ).Model;
            if ( original == null ) return;
            var missing = Ids( original ).Except( Ids( sourceModel ) ).OrderBy( x => x ).ToList();
            if ( missing.Count == 0 ) return;
            var message = $"Source model lacks helper node(s) {string.Join( ", ", missing )} that {project.Destination.Name}'s model has " +
                          "(anchors for effects and cameras). If the game crashes when this persona appears, this is a likely cause.";
            Log.Warn( message );
            result.Warnings.Add( message );
        }

        /// <summary>The animation one slot will play, already adapted to the source model.</summary>
        public static Animation BuildSlotAnimation( SwapProject project, SlotAssignment slot, Model sourceModel, out SlotReport report )
        {
            var game = project.Game;
            var cache = AnimationCache.Shared;
            var pack = cache.FreshPack( game.Archive, slot.SourcePackPath );
            if ( slot.SourceIndex < 0 || slot.SourceIndex >= pack.Animations.Count )
                throw new InvalidDataException( $"{slot.SourcePackPath} has no animation {slot.SourceIndex}." );

            var animation = pack.Animations[slot.SourceIndex];
            var controllers = animation.Controllers?.Count( c => c.TargetKind == TargetKind.Node ) ?? 0;

            var retargeted = false;
            if ( slot.Retarget && slot.RigModelPath != null &&
                 !string.Equals( slot.RigModelPath, project.SourceModel.Path, StringComparison.OrdinalIgnoreCase ) )
            {
                var rig = cache.Model( game.Archive, slot.RigModelPath ).Model;
                if ( rig != null && UniqueNames( rig ) && UniqueNames( sourceModel ) )
                {
                    animation.Retarget( rig, sourceModel, slot.FixArms );
                    retargeted = true;
                }
            }

            // The game matches bones by name and ignores names a model lacks, and shipped packs keep such controllers
            // (e.g. "RootNode"). So an animation made for the source model is used exactly as shipped; one made for
            // another model only gets its indices re-pointed where the name exists. Nothing is removed.
            var ownAnimation = slot.RigModelPath == null ||
                               string.Equals( slot.RigModelPath, project.SourceModel.Path, StringComparison.OrdinalIgnoreCase );
            if ( !ownAnimation )
                RemapTargetIds( animation, sourceModel );
            var names = new HashSet<string>( sourceModel.Nodes.Select( n => n.Name ) );
            var kept = animation.Controllers?.Count( c => c.TargetKind == TargetKind.Node && names.Contains( c.TargetName ) ) ?? 0;

            report = new SlotReport( slot.Pack, slot.Slot, $"{Path.GetFileName( slot.SourcePackPath )} #{slot.SourceIndex}", controllers, kept, retargeted );
            return animation;
        }

        public static byte[] Save( Resource resource )
        {
            using var stream = new MemoryStream();
            resource.Save( stream, true );
            return stream.ToArray();
        }

        private static bool UniqueNames( Model model ) => model.Nodes.Select( n => n.Name ).Distinct().Count() == model.Nodes.Count();

        private static void AddIfChanged( SwapBuildResult result, GameData game, string path, byte[] data )
        {
            if ( !game.Archive.Read( path ).AsSpan().SequenceEqual( data ) )
                result.Files[path] = data;
        }
    }
}
