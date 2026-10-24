using System;
using System.Collections.Generic;
using System.Linq;
using PersonaSwapper.Core.Game;
using PersonaSwapper.Core.Tables;

namespace PersonaSwapper.Core.Swap
{
    /// <summary>Which of the destination persona's animation packs a slot belongs to.</summary>
    public enum AnimationPackKind
    {
        /// <summary>BPS&lt;id&gt;_000.GAP: battle animations (idle, appear, attacks...).</summary>
        Battle,

        /// <summary>GT&lt;id&gt;.GAP: the secondary pack stored with most personas.</summary>
        Secondary,
    }

    /// <summary>Where one destination animation slot gets its motion from.</summary>
    public sealed class SlotAssignment
    {
        public AnimationPackKind Pack { get; init; }

        /// <summary>Slot index in the destination pack.</summary>
        public int Slot { get; init; }

        /// <summary>True to leave the slot empty (no controllers).</summary>
        public bool Empty { get; set; }

        /// <summary>GAP (or GMD with embedded animations) in the game archives.</summary>
        public string SourcePackPath { get; set; } = "";

        public int SourceIndex { get; set; }

        /// <summary>The model the source animation was made for. Null = assume it was made for the swap's source model.</summary>
        public string? RigModelPath { get; set; }

        /// <summary>Make the motion relative to each model's bind pose (needed when the rig isn't the source model).</summary>
        public bool Retarget { get; set; }

        public bool FixArms { get; set; }

        public SlotAssignment Clone() => (SlotAssignment)MemberwiseClone();
    }

    /// <summary>
    /// Everything that defines one swap: which model becomes which persona, where each animation
    /// slot comes from, and the edited persona data. Tables here are private copies; the game's
    /// originals in <see cref="GameData"/> are never modified.
    /// </summary>
    public sealed class SwapProject
    {
        public SwapProject( GameData game, ModelEntry sourceModel, PersonaEntry destination )
        {
            Game = game;
            SourceModel = sourceModel;
            Destination = destination;

            Personas = PersonaTable.Parse( game.Archive.Read( GameData.PersonaTablePath ) );
            Units = UnitTable.Parse( game.Archive.Read( GameData.UnitTablePath ) );
            Names = NameTable.Parse( game.Archive.Read( GameData.NameTablePath ) );

            Slots = DefaultSlots( game, sourceModel, destination ).ToList();
        }

        public GameData Game { get; }

        public ModelEntry SourceModel { get; }

        public PersonaEntry Destination { get; }

        public PersonaTable Personas { get; }

        public UnitTable Units { get; }

        public NameTable Names { get; }

        public List<SlotAssignment> Slots { get; }

        /// <summary>Also replace the compendium/analysis models (PSZ, PSZX) with the source model.</summary>
        public bool ReplaceCompendiumModels { get; set; } = true;

        public PersonaStatsEntry Stats => Personas.Stats[Destination.Id];

        public PersonaSkillsEntry Skills => Personas.Skills[Destination.Id];

        public AffinitiesEntry Affinities => Units.PersonaAffinities[Destination.Id];

        public string PersonaName
        {
            get => Names.Get( NameTable.Personas, Destination.Id );
            set => Names.Set( NameTable.Personas, Destination.Id, value );
        }

        public string? DestinationPackPath( AnimationPackKind kind ) => kind switch
        {
            AnimationPackKind.Battle => Destination.BattleAnimationPath,
            _ => Game.Archive.Contains( $"{Destination.Folder}/GT{Destination.Id:D4}.GAP" ) ? $"{Destination.Folder}/GT{Destination.Id:D4}.GAP" : null,
        };

        /// <summary>
        /// Default slot mapping. Personas and Shadows share one slot layout (persona slot N copies enemy
        /// slot N in the base game), so a persona/enemy source uses its own animations slot-for-slot.
        /// Otherwise each slot keeps the destination persona's original motion, retargeted.
        /// </summary>
        public static IEnumerable<SlotAssignment> DefaultSlots( GameData game, ModelEntry source, PersonaEntry destination )
        {
            var ownBattlePack = FindOwnBattlePack( source );
            var destinationModel = destination.BattleModelPath;

            foreach ( var kind in new[] { AnimationPackKind.Battle, AnimationPackKind.Secondary } )
            {
                var destinationPack = kind == AnimationPackKind.Battle
                    ? destination.BattleAnimationPath
                    : ( game.Archive.Contains( $"{destination.Folder}/GT{destination.Id:D4}.GAP" ) ? $"{destination.Folder}/GT{destination.Id:D4}.GAP" : null );
                if ( destinationPack == null )
                    continue;

                var slotCount = AnimationCache.Shared.SlotCount( game.Archive, destinationPack );
                var ownPack = kind == AnimationPackKind.Battle ? ownBattlePack : FindOwnSecondaryPack( source );
                var ownCount = ownPack != null ? AnimationCache.Shared.SlotCount( game.Archive, ownPack ) : 0;

                for ( var slot = 0; slot < slotCount; slot++ )
                {
                    var originalIsEmpty = AnimationCache.Shared.IsEmpty( game.Archive, destinationPack, slot );
                    if ( originalIsEmpty )
                    {
                        yield return new SlotAssignment { Pack = kind, Slot = slot, Empty = true, SourcePackPath = destinationPack, SourceIndex = slot };
                    }
                    else if ( ownPack != null && slot < ownCount && !AnimationCache.Shared.IsEmpty( game.Archive, ownPack, slot ) )
                    {
                        yield return new SlotAssignment { Pack = kind, Slot = slot, SourcePackPath = ownPack, SourceIndex = slot, RigModelPath = source.Path };
                    }
                    else if ( kind == AnimationPackKind.Secondary && ownBattlePack != null &&
                              !AnimationCache.Shared.IsEmpty( game.Archive, ownBattlePack, 0 ) &&
                              !SharesSkeleton( game, destinationModel, source.Path ) )
                    {
                        // The destination's secondary motions target its own bones; a different skeleton
                        // would just stand still, so use the source's idle instead.
                        yield return new SlotAssignment { Pack = kind, Slot = slot, SourcePackPath = ownBattlePack, SourceIndex = 0, RigModelPath = source.Path };
                    }
                    else
                    {
                        yield return new SlotAssignment
                        {
                            Pack = kind, Slot = slot, SourcePackPath = destinationPack, SourceIndex = slot,
                            RigModelPath = destinationModel, Retarget = !string.Equals( destinationModel, source.Path, StringComparison.OrdinalIgnoreCase ),
                        };
                    }
                }
            }
        }

        /// <summary>True when at least half of model A's node names also exist in model B.</summary>
        public static bool SharesSkeleton( GameData game, string modelA, string modelB )
        {
            if ( string.Equals( modelA, modelB, StringComparison.OrdinalIgnoreCase ) )
                return true;
            var a = AnimationCache.Shared.Model( game.Archive, modelA ).Model;
            var b = AnimationCache.Shared.Model( game.Archive, modelB ).Model;
            if ( a == null || b == null )
                return false;
            var namesB = new HashSet<string>( b.Nodes.Select( n => n.Name ) );
            var total = a.Nodes.Count();
            return total > 0 && a.Nodes.Count( n => namesB.Contains( n.Name ) ) * 2 >= total;
        }

        private static string? FindOwnBattlePack( ModelEntry source )
        {
            var prefix = source.Category switch
            {
                ModelCategory.Persona => $"BPS{source.Id:D4}_000",
                ModelCategory.Enemy => $"BEM{source.Id:D4}_000",
                _ => null,
            };
            return prefix == null ? null : source.AnimationPaths.FirstOrDefault( p => p.EndsWith( prefix + ".GAP", StringComparison.OrdinalIgnoreCase ) );
        }

        private static string? FindOwnSecondaryPack( ModelEntry source )
            => source.Category == ModelCategory.Persona
                ? source.AnimationPaths.FirstOrDefault( p => p.EndsWith( $"GT{source.Id:D4}.GAP", StringComparison.OrdinalIgnoreCase ) )
                : null;
    }
}
