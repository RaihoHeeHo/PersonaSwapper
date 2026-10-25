using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonaSwapper.Core.Diagnostics;
using PersonaSwapper.Core.Messages;
using PersonaSwapper.Core.Swap;
using PersonaSwapper.Core.Tables;

namespace PersonaSwapper.App.ViewModels
{
    /// <summary>The two tables (persona data, animations) for one swap.</summary>
    public sealed partial class SwapEditorViewModel : ObservableObject
    {
        private readonly PreviewViewModel mPreview;

        public SwapEditorViewModel( SwapProject project, Lookups lookups, PreviewViewModel preview )
        {
            Project = project;
            Lookups = lookups;
            mPreview = preview;

            _name = project.PersonaName;
            if ( project.Destination.IsFreeSlot )
            {
                _modId = $"p5rpc.personaswap.new.{Slug( project.PersonaName )}";
                _modName = $"New Persona: {project.PersonaName} (slot {project.Destination.Id})";
            }
            else
            {
                _modId = $"p5rpc.personaswap.{Slug( project.Destination.Name )}";
                _modName = $"Persona Swap: {project.SourceModel.DisplayName.Split( '—' )[0].Trim()} as {project.Destination.Name}";
            }
            _selectedRecruit = lookups.RecruitableShadows[0];

            _originalLore = Unwrap( project.LoreText );
            _lore = _originalLore;
            var (sourceHeight, destinationHeight) = project.ModelHeights();
            SuggestedScale = project.SuggestMatchingScale();
            OriginalScale = project.OriginalModelScale;
            ScaleHint = project.Destination.IsFreeSlot
                ? $"New persona: 100% shows the model at its own size ({sourceHeight:0} units tall). Typical personas are 80–350 units tall."
                : sourceHeight > 0 && destinationHeight > 0
                ? $"Source model is {sourceHeight:0} units tall; {project.Destination.Name}'s original model is {destinationHeight:0} at {OriginalScale}%. " +
                  $"{SuggestedScale}% keeps the same on-screen size."
                : "Couldn't measure the models.";
            UpdateLorePreview();

            for ( var i = 0; i < PersonaSkillsEntry.SkillSlotCount; i++ )
                SkillRows.Add( new SkillRowViewModel( this, i ) );
            foreach ( var element in Enum.GetValues<Element>() )
                AffinityRows.Add( new AffinityRowViewModel( project.Affinities, element ) );
            foreach ( var slot in project.Slots )
                SlotRows.Add( new AnimationSlotViewModel( this, slot ) );
        }

        public SwapProject Project { get; }

        public Lookups Lookups { get; }

        public ObservableCollection<SkillRowViewModel> SkillRows { get; } = new();

        public ObservableCollection<AffinityRowViewModel> AffinityRows { get; } = new();

        public ObservableCollection<AnimationSlotViewModel> SlotRows { get; } = new();

        public string Title => Project.Destination.IsFreeSlot
            ? $"{Project.SourceModel.DisplayName}  →  new persona #{Project.Destination.Id}"
            : $"{Project.SourceModel.DisplayName}  →  #{Project.Destination.Id} {Project.Destination.Name}";

        public bool IsNewPersona => Project.Destination.IsFreeSlot;

        public string NewPersonaHint => $"This adds a new persona in unused slot {Project.Destination.Id} (a placeholder the game ships with). " +
                                        "Set its arcana and level: normal fusion may produce it like any other persona of that arcana " +
                                        "(not yet confirmed in game). To be sure you can get it, pick a Shadow below to recruit it from.";

        public IReadOnlyList<IdName> RecruitOptions => Lookups.RecruitableShadows;

        [ObservableProperty] private IdName _selectedRecruit;

        partial void OnSelectedRecruitChanged( IdName value )
        {
            Project.RecruitFromEnemy = value?.Id ?? 0;
            OnPropertyChanged( nameof( HasMenuModel ) );
            OnPropertyChanged( nameof( MenuModelLabel ) );
            OnPropertyChanged( nameof( MenuModelHint ) );
            OnPropertyChanged( nameof( RecruitHint ) );
        }

        public string RecruitHint => Project.RecruitFromEnemy == 0
            ? "Negotiating with the chosen Shadow gives this persona instead of its usual one."
            : $"Negotiating with this Shadow now gives {Project.PersonaName} instead of " +
              $"{Project.Game.Names.Get( Core.Tables.NameTable.Personas, Project.RecruitOriginalPersona )}. The Shadow keeps its own look.";

        // ---- Mod ----

        [ObservableProperty] private string _modId;
        [ObservableProperty] private string _modName;

        // ---- Identity ----

        [ObservableProperty] private string _name;
        [ObservableProperty] private string? _nameError;

        partial void OnNameChanged( string value )
        {
            try
            {
                AtlusText.Encode( value );
                Project.PersonaName = value;
                NameError = null;
            }
            catch ( ArgumentException e )
            {
                NameError = e.Message;
            }
        }

        public IReadOnlyList<IdName> ArcanaOptions => Lookups.Arcana;

        public IdName Arcana
        {
            get => Lookups.Find( Lookups.Arcana, Project.Stats.Arcana );
            set { if ( value != null ) { Project.Stats.Arcana = (byte)value.Id; OnPropertyChanged(); } }
        }

        public IReadOnlyList<IdName> InheritanceOptions => Lookups.Inheritance;

        public IdName Inheritance
        {
            get => Lookups.Find( Lookups.Inheritance, Project.Stats.Inheritance );
            set { if ( value != null ) { Project.Stats.Inheritance = (ushort)value.Id; OnPropertyChanged(); } }
        }

        public decimal Level
        {
            get => Project.Stats.Level;
            set { Project.Stats.Level = ClampByte( value, 1, 99 ); OnPropertyChanged(); foreach ( var row in SkillRows ) row.Refresh(); }
        }

        // ---- Stats ----

        public decimal Strength { get => Project.Stats.Strength; set { Project.Stats.Strength = ClampByte( value, 1, 99 ); OnPropertyChanged(); } }
        public decimal Magic { get => Project.Stats.Magic; set { Project.Stats.Magic = ClampByte( value, 1, 99 ); OnPropertyChanged(); } }
        public decimal Endurance { get => Project.Stats.Endurance; set { Project.Stats.Endurance = ClampByte( value, 1, 99 ); OnPropertyChanged(); } }
        public decimal Agility { get => Project.Stats.Agility; set { Project.Stats.Agility = ClampByte( value, 1, 99 ); OnPropertyChanged(); } }
        public decimal Luck { get => Project.Stats.Luck; set { Project.Stats.Luck = ClampByte( value, 1, 99 ); OnPropertyChanged(); } }

        public decimal GrowthStrength { get => Project.Skills.GrowthStrength; set { Project.Skills.GrowthStrength = ClampByte( value, 0, 255 ); OnPropertyChanged(); } }
        public decimal GrowthMagic { get => Project.Skills.GrowthMagic; set { Project.Skills.GrowthMagic = ClampByte( value, 0, 255 ); OnPropertyChanged(); } }
        public decimal GrowthEndurance { get => Project.Skills.GrowthEndurance; set { Project.Skills.GrowthEndurance = ClampByte( value, 0, 255 ); OnPropertyChanged(); } }
        public decimal GrowthAgility { get => Project.Skills.GrowthAgility; set { Project.Skills.GrowthAgility = ClampByte( value, 0, 255 ); OnPropertyChanged(); } }
        public decimal GrowthLuck { get => Project.Skills.GrowthLuck; set { Project.Skills.GrowthLuck = ClampByte( value, 0, 255 ); OnPropertyChanged(); } }

        // ---- Flags ----

        public bool TreasureDemon { get => Has( PersonaFlags.TreasureDemon ); set => SetFlag( PersonaFlags.TreasureDemon, value ); }
        public bool NoNormalFusion { get => Has( PersonaFlags.NoNormalFusion ); set => SetFlag( PersonaFlags.NoNormalFusion, value ); }
        public bool NotRegisterable { get => Has( PersonaFlags.NotRegisterable ); set => SetFlag( PersonaFlags.NotRegisterable, value ); }
        public bool StoryPersona { get => Has( PersonaFlags.StoryPersona ); set => SetFlag( PersonaFlags.StoryPersona, value ); }
        public bool PartyPersona { get => Has( PersonaFlags.PartyPersona ); set => SetFlag( PersonaFlags.PartyPersona, value ); }
        public bool Dlc { get => Has( PersonaFlags.Dlc ); set => SetFlag( PersonaFlags.Dlc, value ); }

        // ---- Battle model scale ----

        public short SuggestedScale { get; }

        public short OriginalScale { get; }

        public string ScaleHint { get; }

        public bool HasMenuModel => Project.MenuModelPath != null;

        public bool ReplaceMenuModel
        {
            get => Project.ReplaceMenuModel;
            set { Project.ReplaceMenuModel = value; OnPropertyChanged(); }
        }

        public string MenuModelLabel => Project.MenuModelPath == null ? "" :
            $"Also use it in the persona menu and compendium (replaces {System.IO.Path.GetFileName( Project.MenuModelPath )})";

        private string PersonaLabel => Project.Destination.IsFreeSlot ? Project.PersonaName : Project.Destination.Name;

        public string MenuModelHint => Project.Destination.IsFreeSlot
            ? $"Menus, the compendium and fusion load ENEMY/{Project.Destination.Id:D4}/EM{Project.Destination.Id:D4}.GMD; the export writes the new model there (no Shadow uses that folder)."
            : Project.MenuModelPath == null
            ? $"{PersonaLabel} has no Shadow model (ENEMY/{Project.Destination.Id:D4}/EM{Project.Destination.Id:D4}.GMD)."
            : $"The game shows {PersonaLabel}'s Shadow model ({System.IO.Path.GetFileName( Project.MenuModelPath )}) in menus and the compendium. " +
              "That file is also the Shadow in battle, so the Shadow turns into the new model too" +
              ( Project.SourceModel.Category == PersonaSwapper.Core.Game.ModelCategory.Enemy ? " (with the source's own animations)." : ", playing its own animations." );

        public string MatchScaleLabel => $"Match original size ({SuggestedScale}%)";

        public decimal ModelScale
        {
            get => Project.ModelScale;
            set { Project.ModelScale = (short)Math.Clamp( (int)Math.Round( value ), 10, 1000 ); OnPropertyChanged(); }
        }

        [RelayCommand]
        private void MatchScale() => ModelScale = SuggestedScale;

        [RelayCommand]
        private void ResetScale() => ModelScale = OriginalScale;

        // ---- Lore ----

        private readonly string _originalLore;

        public bool HasLore => Project.HasLore;

        /// <summary>Lore as one paragraph; it's re-wrapped to the compendium's line width when saved.</summary>
        [ObservableProperty] private string _lore;
        [ObservableProperty] private string _lorePreview = "";
        [ObservableProperty] private string _loreInfo = "";
        [ObservableProperty] private bool _loreTooLong;

        partial void OnLoreChanged( string value )
        {
            try
            {
                Project.LoreText = value;
            }
            catch ( ArgumentException e )
            {
                LoreInfo = e.Message;
                LoreTooLong = true;
                return;
            }
            UpdateLorePreview();
        }

        [RelayCommand]
        private void RestoreLore() => Lore = _originalLore;

        private void UpdateLorePreview()
        {
            var lines = PersonaLore.Wrap( Lore, PersonaLore.LineWidth );
            LorePreview = string.Join( "\n", lines );
            LoreTooLong = lines.Count > PersonaLore.MaxLines;
            LoreInfo = LoreTooLong
                ? $"{lines.Count} lines: the game's longest entries have {PersonaLore.MaxLines}, so the end may be cut off."
                : $"{lines.Count} of {PersonaLore.MaxLines} lines";
        }

        private static string Unwrap( string text ) => string.Join( " ", text.Split( '\n' ).Select( l => l.Trim() ).Where( l => l.Length > 0 ) );

        // ---- Preview ----

        [ObservableProperty] private AnimationSlotViewModel? _selectedSlot;

        partial void OnSelectedSlotChanged( AnimationSlotViewModel? value ) => value?.ShowInPreview();

        public void PreviewFirstSlot() => SelectedSlot = SlotRows.FirstOrDefault( s => !s.Empty ) ?? SlotRows.FirstOrDefault();

        internal void Preview( SlotAssignment slot )
        {
            try
            {
                Log.Step( $"Preview slot {slot.Pack} {slot.Slot}: {( slot.Empty ? "empty" : $"{slot.SourcePackPath} #{slot.SourceIndex}{( slot.Retarget ? " (retarget)" : "" )}" )}" );
                var source = AnimationCache.Shared.Model( Project.Game.Archive, Project.SourceModel.Path );
                var animation = slot.Empty ? null : SwapBuilder.BuildSlotAnimation( Project, slot, source.Model!, out _ );
                mPreview.Show( Project.Game, Project.SourceModel.Path, animation );
            }
            catch ( Exception e )
            {
                Log.Error( $"Can't build slot {slot.Pack} {slot.Slot} animation for preview", e );
                mPreview.Show( Project.Game, Project.SourceModel.Path, null );
                mPreview.Message = $"Can't preview this slot: {e.Message} (see log)";
            }
        }

        private bool Has( PersonaFlags flag ) => ( Project.Stats.Flags & flag ) != 0;

        private void SetFlag( PersonaFlags flag, bool on, [System.Runtime.CompilerServices.CallerMemberName] string? property = null )
        {
            Project.Stats.Flags = on ? Project.Stats.Flags | flag : Project.Stats.Flags & ~flag;
            OnPropertyChanged( property );
        }

        internal static byte ClampByte( decimal value, int min, int max ) => (byte)Math.Clamp( (int)Math.Round( value ), min, max );

        private static string Slug( string text )
        {
            var sb = new StringBuilder();
            foreach ( var c in text.ToLowerInvariant() )
                if ( char.IsLetterOrDigit( c ) && c < 128 ) sb.Append( c );
            return sb.Length > 0 ? sb.ToString() : "persona";
        }
    }

    public sealed partial class SkillRowViewModel : ObservableObject
    {
        private readonly SwapEditorViewModel mOwner;

        public SkillRowViewModel( SwapEditorViewModel owner, int index )
        {
            mOwner = owner;
            Index = index;
        }

        public int Index { get; }

        public int Number => Index + 1;

        private PersonaSkillSlot Slot => mOwner.Project.Skills.GetSlot( Index );

        public static IReadOnlyList<string> KindOptions { get; } = new[] { "—", "Skill", "Trait" };

        public int KindIndex
        {
            get => Slot.Kind switch { LearnableKind.Skill => 1, LearnableKind.Trait => 2, _ => 0 };
            set
            {
                var kind = value switch { 1 => LearnableKind.Skill, 2 => LearnableKind.Trait, _ => LearnableKind.Nothing };
                if ( kind == Slot.Kind ) return;
                var options = kind == LearnableKind.Skill ? mOwner.Lookups.Skills : mOwner.Lookups.Traits;
                var id = kind == LearnableKind.Nothing ? (short)0 : (short)( options.FirstOrDefault()?.Id ?? 0 );
                mOwner.Project.Skills.SetSlot( Index, new PersonaSkillSlot( kind == LearnableKind.Nothing ? (byte)0 : Slot.LevelsAfterBase, kind, id ) );
                Refresh();
            }
        }

        public IReadOnlyList<IdName> Options => Slot.Kind switch
        {
            LearnableKind.Skill => mOwner.Lookups.Skills,
            LearnableKind.Trait => mOwner.Lookups.Traits,
            _ => Array.Empty<IdName>(),
        };

        public IdName? Selected
        {
            get
            {
                if ( Slot.IsEmpty ) return null;
                return Options.FirstOrDefault( o => o.Id == Slot.Id )
                       ?? new IdName( Slot.Id, Slot.Kind == LearnableKind.Trait
                                          ? mOwner.Project.Names.Get( NameTable.Traits, Slot.Id )
                                          : mOwner.Project.Names.Get( NameTable.Skills, Slot.Id ) );
            }
            set
            {
                if ( value == null || Slot.IsEmpty ) return;
                mOwner.Project.Skills.SetSlot( Index, Slot with { Id = (short)value.Id } );
                OnPropertyChanged();
            }
        }

        public bool IsUsed => !Slot.IsEmpty;

        /// <summary>Levels after the persona's base level; 0 = known from the start.</summary>
        public decimal LevelsAfterBase
        {
            get => Slot.LevelsAfterBase;
            set
            {
                if ( Slot.IsEmpty ) return;
                mOwner.Project.Skills.SetSlot( Index, Slot with { LevelsAfterBase = SwapEditorViewModel.ClampByte( value, 0, 98 ) } );
                OnPropertyChanged();
                OnPropertyChanged( nameof( LearnedAt ) );
            }
        }

        public string LearnedAt => Slot.IsEmpty ? "" : Slot.LevelsAfterBase == 0 ? "Innate" : $"Lv {mOwner.Project.Stats.Level + Slot.LevelsAfterBase}";

        public void Refresh()
        {
            OnPropertyChanged( nameof( KindIndex ) );
            OnPropertyChanged( nameof( Options ) );
            OnPropertyChanged( nameof( Selected ) );
            OnPropertyChanged( nameof( IsUsed ) );
            OnPropertyChanged( nameof( LevelsAfterBase ) );
            OnPropertyChanged( nameof( LearnedAt ) );
        }
    }

    public sealed partial class AffinityRowViewModel : ObservableObject
    {
        private readonly AffinitiesEntry mEntry;

        public AffinityRowViewModel( AffinitiesEntry entry, Element element )
        {
            mEntry = entry;
            Element = element;
        }

        public Element Element { get; }

        public static IReadOnlyList<Affinity> AffinityOptions { get; } = Enum.GetValues<Affinity>();

        public Affinity Affinity
        {
            get => mEntry.GetAffinity( Element );
            set { mEntry.SetAffinity( Element, value ); OnPropertyChanged(); }
        }

        /// <summary>Damage multiplier in percent (stored in 5% steps; 100% = normal).</summary>
        public decimal MultiplierPercent
        {
            get => mEntry.GetMultiplier( Element ) * 5;
            set { mEntry.SetMultiplier( Element, SwapEditorViewModel.ClampByte( value / 5, 0, 255 ) ); OnPropertyChanged(); }
        }
    }
}
