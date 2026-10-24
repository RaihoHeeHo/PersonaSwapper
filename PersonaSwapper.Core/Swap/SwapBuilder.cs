using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;
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
                    output.Animations[slot.Slot] = animation;
                    result.Slots.Add( report );
                    if ( report.Controllers > 0 && report.Coverage < 0.5 )
                        result.Warnings.Add( $"{group.Key} slot {slot.Slot}: only {report.Kept} of {report.Controllers} animated bones exist on the source model." );
                }

                result.Files[packPath] = Save( output );
            }

            // 3. Tables: only the ones that differ from the game's originals.
            AddIfChanged( result, game, GameData.PersonaTablePath, project.Personas.Serialize() );
            AddIfChanged( result, game, GameData.UnitTablePath, project.Units.Serialize() );
            AddIfChanged( result, game, GameData.NameTablePath, project.Names.Serialize() );

            return result;
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

            // Point controllers at the source model's node indices; drops controllers for bones it doesn't have.
            animation.FixTargetIds( sourceModel );
            var kept = animation.Controllers?.Count( c => c.TargetKind == TargetKind.Node ) ?? 0;

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
