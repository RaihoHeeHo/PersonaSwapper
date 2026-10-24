using System;
using System.Collections.Generic;
using System.Linq;
using PersonaSwapper.Core.Game;
using PersonaSwapper.Core.Messages;
using PersonaSwapper.Core.Preview;
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
            Visual = VisualTable.Parse( game.Archive.Read( GameData.VisualTablePath ) );
            if ( game.Archive.Contains( PersonaLore.BmdPath ) )
                Lore = new PersonaLore( MessageScript.Parse( game.Archive.Read( PersonaLore.BmdPath ) ) );

            Slots = DefaultSlots( game, sourceModel, destination ).ToList();
            MenuModelPath = FindMenuModel( game, Units, destination.Id, out var shadowId );
            if ( MenuModelPath != null )
            {
                ShadowAnimationPath = $"MODEL/CHARACTER/ENEMY/{shadowId:D4}/BEM{shadowId:D4}_000.GAP";
                if ( !game.Archive.Contains( ShadowAnimationPath ) ) ShadowAnimationPath = null;
            }
        }

        /// <summary>
        /// The persona menu and the compendium show the persona's Shadow model, not PS/PSZX (verified in game: with
        /// ENEMY/0006/EM0006.GMD replaced, Pixie's menu model changed). The Shadow is the enemy whose UNIT.TBL visual
        /// entry gives this persona (normally the enemy with the same ID). Personas without a Shadow model use PS.
        /// </summary>
        public static string? FindMenuModel( GameData game, UnitTable units, int personaId, out int modelId )
        {
            modelId = -1;
            var visuals = units.EnemyVisuals;
            if ( personaId < visuals.Count && visuals[personaId].PersonaId == personaId )
                modelId = visuals[personaId].ModelId;
            else
            {
                var match = visuals.FirstOrDefault( v => v.PersonaId == personaId );
                if ( match != null ) modelId = match.ModelId;
            }
            if ( modelId <= 0 ) return null;
            var path = $"MODEL/CHARACTER/ENEMY/{modelId:D4}/EM{modelId:D4}.GMD";
            return game.Archive.Contains( path ) ? path : null;
        }

        /// <summary>The Shadow model the game shows for this persona in menus and the compendium, if it has one.</summary>
        public string? MenuModelPath { get; }

        /// <summary>The Shadow's battle animations (BEM), used with <see cref="MenuModelPath"/> in battle.</summary>
        public string? ShadowAnimationPath { get; }

        /// <summary>
        /// Also replace <see cref="MenuModelPath"/> so menus and the compendium show the new model. That file is the
        /// Shadow's battle model too, so the Shadow changes in battle as well.
        /// </summary>
        public bool ReplaceMenuModel { get; set; } = true;

        public GameData Game { get; }

        public ModelEntry SourceModel { get; }

        public PersonaEntry Destination { get; }

        public PersonaTable Personas { get; }

        public UnitTable Units { get; }

        public NameTable Names { get; }

        public VisualTable Visual { get; }

        /// <summary>Compendium lore, or null if the language CPK has no DATMYTH.BMD.</summary>
        public PersonaLore? Lore { get; }

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

        public bool HasLore => Lore?.Has( Destination.Id ) == true;

        public string LoreText
        {
            get => Lore?.Get( Destination.Id ) ?? "";
            set => Lore?.Set( Destination.Id, value );
        }

        /// <summary>Battle model scale in percent, from VISUAL.TBL.</summary>
        public short ModelScale
        {
            get => Visual.Personas[Destination.Id].BattleModelScale;
            set => Visual.Personas[Destination.Id].BattleModelScale = value;
        }

        /// <summary>The destination persona's scale in the unmodified game.</summary>
        public short OriginalModelScale => VisualTable.Parse( Game.Archive.Read( GameData.VisualTablePath ) ).Personas[Destination.Id].BattleModelScale;

        /// <summary>Bind-pose heights (model units) of the source model and the destination persona's original model.</summary>
        public (float Source, float Destination) ModelHeights()
            => (ModelMetrics.Height( Game, SourceModel.Path ), ModelMetrics.Height( Game, Destination.BattleModelPath ));

        /// <summary>
        /// Scale that makes the source model appear the same height in battle as the destination persona does
        /// in the base game, rounded to 5%.
        /// </summary>
        public short SuggestMatchingScale()
        {
            var (source, destination) = ModelHeights();
            if ( source <= 0 || destination <= 0 )
                return OriginalModelScale;
            var scale = OriginalModelScale * destination / source;
            return (short)Math.Clamp( (int)( Math.Round( scale / 5 ) * 5 ), 10, 1000 );
        }

        public string? DestinationPackPath( AnimationPackKind kind ) => kind switch
        {
            AnimationPackKind.Battle => Destination.BattleAnimationPath,
            _ => Game.Archive.Contains( $"{Destination.Folder}/GT{Destination.Id:D4}.GAP" ) ? $"{Destination.Folder}/GT{Destination.Id:D4}.GAP" : null,
        };

        /// <summary>
        /// Default slot mapping, always from the source model's own animations:
        /// - Personas and Shadows share one slot layout (persona slot N copies enemy slot N in the base game),
        ///   so their own battle pack is used slot-for-slot.
        /// - Otherwise (or for slots the source's pack lacks) the source's idle from its main pack.
        /// - Only a source with no animations of its own falls back to the destination's motions, retargeted.
        /// </summary>
        public static IEnumerable<SlotAssignment> DefaultSlots( GameData game, ModelEntry source, PersonaEntry destination )
        {
            var ownPacks = OwnAnimationPacks( source );
            var mainPack = ownPacks.FirstOrDefault( p => AnimationCache.Shared.Info( game.Archive, p ).Any( a => !a.IsEmpty ) );
            var idleIndex = mainPack == null ? 0 : AnimationCache.Shared.Info( game.Archive, mainPack ).First( a => !a.IsEmpty ).Index;
            var slotForSlot = source.Category is ModelCategory.Persona or ModelCategory.Enemy;
            var destinationModel = destination.BattleModelPath;

            foreach ( var kind in new[] { AnimationPackKind.Battle, AnimationPackKind.Secondary } )
            {
                var destinationPack = kind == AnimationPackKind.Battle
                    ? destination.BattleAnimationPath
                    : ( game.Archive.Contains( $"{destination.Folder}/GT{destination.Id:D4}.GAP" ) ? $"{destination.Folder}/GT{destination.Id:D4}.GAP" : null );
                if ( destinationPack == null )
                    continue;

                var slotCount = AnimationCache.Shared.SlotCount( game.Archive, destinationPack );
                var matchingPack = !slotForSlot ? null
                    : kind == AnimationPackKind.Battle ? ownPacks.FirstOrDefault( IsBattlePack )
                    : ownPacks.FirstOrDefault( p => System.IO.Path.GetFileName( p ).StartsWith( "GT", StringComparison.OrdinalIgnoreCase ) );

                for ( var slot = 0; slot < slotCount; slot++ )
                {
                    if ( AnimationCache.Shared.IsEmpty( game.Archive, destinationPack, slot ) )
                    {
                        yield return new SlotAssignment { Pack = kind, Slot = slot, Empty = true, SourcePackPath = destinationPack, SourceIndex = slot };
                    }
                    else if ( matchingPack != null && !AnimationCache.Shared.IsEmpty( game.Archive, matchingPack, slot ) )
                    {
                        yield return new SlotAssignment { Pack = kind, Slot = slot, SourcePackPath = matchingPack, SourceIndex = slot, RigModelPath = source.Path };
                    }
                    else if ( mainPack != null )
                    {
                        yield return new SlotAssignment { Pack = kind, Slot = slot, SourcePackPath = mainPack, SourceIndex = idleIndex, RigModelPath = source.Path };
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

        /// <summary>
        /// The animation packs the source model plays in game (GAPs stored with it), most useful first:
        /// battle packs before field/event packs, and for characters the model's own costume before others.
        /// </summary>
        public static IReadOnlyList<string> OwnAnimationPacks( ModelEntry source )
        {
            var file = System.IO.Path.GetFileNameWithoutExtension( source.Path );
            var costume = source.Category == ModelCategory.Character && file.Length >= 9 ? file.Substring( 6, 3 ) : null; // C0001_051_00
            int Rank( string path )
            {
                var name = System.IO.Path.GetFileName( path ).ToUpperInvariant();
                var battle = IsBattlePack( path ) ? 0 : 1;
                var sameCostume = costume == null || name.Contains( "_" + costume ) ? 0 : 1;
                return battle * 2 + sameCostume;
            }
            return source.AnimationPaths
                .OrderBy( Rank )
                .ThenBy( p => p, StringComparer.OrdinalIgnoreCase )
                .ToList();
        }

        /// <summary>Battle animation packs: persona BPS, Shadow BEM, character BB/BF/BE and shared BCMN.</summary>
        public static bool IsBattlePack( string path )
        {
            var name = System.IO.Path.GetFileName( path ).ToUpperInvariant();
            return name.StartsWith( "BPS" ) || name.StartsWith( "BEM" ) || name.StartsWith( "BB" ) || name.StartsWith( "BF" ) ||
                   name.StartsWith( "BE" ) || name.StartsWith( "BCMN" );
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
    }
}
