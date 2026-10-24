using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using PersonaSwapper.Core.Swap;

namespace PersonaSwapper.App.ViewModels
{
    /// <summary>An animation pack choice, labelled with where it comes from.</summary>
    public sealed record PackOption( string Path, string Label )
    {
        public override string ToString() => Label;
    }

    /// <summary>One row of the animation table: a destination slot and where its motion comes from.</summary>
    public sealed partial class AnimationSlotViewModel : ObservableObject
    {
        private static readonly Regex sNumbered = new( @"^(BPS|BEM|GT)(\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled );
        private static readonly Regex sCharacterGap = new( @"^[A-Z]{2,3}(\d{4})_(\d{3})", RegexOptions.IgnoreCase | RegexOptions.Compiled );

        private readonly SwapEditorViewModel mOwner;
        private bool mUpdating;

        public AnimationSlotViewModel( SwapEditorViewModel owner, SlotAssignment slot )
        {
            mOwner = owner;
            Slot = slot;

            foreach ( var option in RelevantPacks() )
                PackOptions.Add( option );
            if ( !PackOptions.Any( o => o.Path.Equals( slot.SourcePackPath, StringComparison.OrdinalIgnoreCase ) ) )
                PackOptions.Insert( 0, Label( slot.SourcePackPath ) );

            _selectedPack = PackOptions.First( o => o.Path.Equals( slot.SourcePackPath, StringComparison.OrdinalIgnoreCase ) );
            LoadAnimationOptions();
            UpdateCoverage();
        }

        public SlotAssignment Slot { get; }

        public string PackName => Slot.Pack == AnimationPackKind.Battle ? "Battle" : "Secondary";

        public string SlotLabel => $"{PackName} {Slot.Slot}" + ( Slot.Pack == AnimationPackKind.Battle ? BattleHint( Slot.Slot ) : "" );

        public ObservableCollection<PackOption> PackOptions { get; } = new();

        public ObservableCollection<AnimationInfo> AnimationOptions { get; } = new();

        [ObservableProperty] private PackOption _selectedPack;
        [ObservableProperty] private AnimationInfo? _selectedAnimation;
        [ObservableProperty] private string _coverage = "";
        [ObservableProperty] private bool _coverageWarning;

        public bool Empty
        {
            get => Slot.Empty;
            set { Slot.Empty = value; OnPropertyChanged(); OnPropertyChanged( nameof( IsActive ) ); UpdateCoverage(); ShowInPreview(); }
        }

        public bool IsActive => !Slot.Empty;

        public bool Retarget
        {
            get => Slot.Retarget;
            set { Slot.Retarget = value; OnPropertyChanged(); UpdateCoverage(); ShowInPreview(); }
        }

        /// <summary>Adds any pack from the game (typed or picked in the search box) to this row's choices and selects it.</summary>
        public void UsePack( string path )
        {
            if ( !PackOptions.Any( o => o.Path.Equals( path, StringComparison.OrdinalIgnoreCase ) ) )
                PackOptions.Insert( 0, Label( path ) );
            SelectedPack = PackOptions.First( o => o.Path.Equals( path, StringComparison.OrdinalIgnoreCase ) );
        }

        partial void OnSelectedPackChanged( PackOption value )
        {
            if ( mUpdating || value == null ) return;
            Slot.SourcePackPath = value.Path;
            Slot.RigModelPath = RigModelFor( value.Path );
            Slot.Retarget = Slot.RigModelPath != null &&
                            !Slot.RigModelPath.Equals( mOwner.Project.SourceModel.Path, StringComparison.OrdinalIgnoreCase );
            OnPropertyChanged( nameof( Retarget ) );
            LoadAnimationOptions();
            UpdateCoverage();
            ShowInPreview();
        }

        partial void OnSelectedAnimationChanged( AnimationInfo? value )
        {
            if ( mUpdating || value == null ) return;
            Slot.SourceIndex = value.Index;
            UpdateCoverage();
            ShowInPreview();
        }

        public void ShowInPreview()
        {
            if ( mOwner.SelectedSlot == this )
                mOwner.Preview( Slot );
        }

        private void LoadAnimationOptions()
        {
            mUpdating = true;
            try
            {
                AnimationOptions.Clear();
                foreach ( var info in AnimationCache.Shared.Info( mOwner.Project.Game.Archive, Slot.SourcePackPath ) )
                    AnimationOptions.Add( info );
                var index = Math.Clamp( Slot.SourceIndex, 0, Math.Max( 0, AnimationOptions.Count - 1 ) );
                Slot.SourceIndex = index;
                SelectedAnimation = AnimationOptions.Count > 0 ? AnimationOptions[index] : null;
            }
            catch ( Exception e )
            {
                Coverage = $"Can't read: {e.Message}";
            }
            finally
            {
                mUpdating = false;
            }
        }

        private void UpdateCoverage()
        {
            if ( Slot.Empty )
            {
                Coverage = "empty";
                CoverageWarning = false;
                return;
            }

            try
            {
                var source = AnimationCache.Shared.Model( mOwner.Project.Game.Archive, mOwner.Project.SourceModel.Path );
                SwapBuilder.BuildSlotAnimation( mOwner.Project, Slot, source.Model!, out var report );
                Coverage = report.Controllers == 0 ? "no motion" : $"{report.Kept}/{report.Controllers}";
                CoverageWarning = report.Controllers > 0 && report.Coverage < 0.5;
            }
            catch ( Exception e )
            {
                Coverage = e.Message;
                CoverageWarning = true;
            }
        }

        /// <summary>The source model's own packs (the animations it plays in game) first, then the destination persona's.</summary>
        private IEnumerable<PackOption> RelevantPacks()
        {
            var project = mOwner.Project;
            var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
            foreach ( var path in SwapProject.OwnAnimationPacks( project.SourceModel ) )
                if ( seen.Add( path ) ) yield return Label( path );
            foreach ( var kind in new[] { AnimationPackKind.Battle, AnimationPackKind.Secondary } )
            {
                var path = project.DestinationPackPath( kind );
                if ( path != null && seen.Add( path ) ) yield return Label( path );
            }
        }

        private PackOption Label( string path )
        {
            var project = mOwner.Project;
            var name = Path.GetFileName( path );
            var folder = path.Substring( 0, path.LastIndexOf( '/' ) );
            if ( IsOwnPack( path ) )
                return new PackOption( path, SwapProject.IsBattlePack( path ) ? $"{name}  · own, battle" : $"{name}  · own" );
            if ( folder.Equals( project.Destination.Folder, StringComparison.OrdinalIgnoreCase ) )
                return new PackOption( path, $"{name}  · {project.Destination.Name}'s (retarget)" );
            return new PackOption( path, $"{name}  · other" );
        }

        /// <summary>The model a pack's animations were authored for, from the game's naming conventions.</summary>
        private string? RigModelFor( string packPath )
        {
            var archive = mOwner.Project.Game.Archive;
            var folder = packPath.Substring( 0, packPath.LastIndexOf( '/' ) );
            var file = Path.GetFileNameWithoutExtension( packPath );

            // Packs that belong to the source model animate it (a character's costumes share one skeleton).
            if ( IsOwnPack( packPath ) )
                return mOwner.Project.SourceModel.Path;

            var m = sNumbered.Match( file );
            if ( m.Success )
            {
                var id = m.Groups[2].Value;
                var candidate = m.Groups[1].Value.ToUpperInvariant() == "BEM" ? $"{folder}/EM{id}.GMD" : $"{folder}/PS{id}.GMD";
                if ( archive.Contains( candidate ) ) return candidate;
            }

            var c = sCharacterGap.Match( file );
            if ( c.Success )
            {
                var candidate = $"{folder}/C{c.Groups[1].Value}_{c.Groups[2].Value}_00.GMD";
                if ( archive.Contains( candidate ) ) return candidate;
            }

            return archive.Find( folder, ".GMD" ).OrderBy( p => p, StringComparer.OrdinalIgnoreCase ).FirstOrDefault();
        }

        private bool IsOwnPack( string path )
            => mOwner.Project.SourceModel.AnimationPaths.Contains( path, StringComparer.OrdinalIgnoreCase );

        // From comparing all 302 personas: slots 1-4 are unused, 0 is the longest (looping) motion.
        private static string BattleHint( int slot ) => slot switch
        {
            0 => " · idle",
            1 or 2 or 3 or 4 => " · unused",
            5 => " · appear",
            _ => " · action",
        };
    }
}
