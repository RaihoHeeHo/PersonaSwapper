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

            foreach ( var path in RelevantPacks() )
                PackOptions.Add( path );
            if ( !PackOptions.Contains( slot.SourcePackPath, StringComparer.OrdinalIgnoreCase ) )
                PackOptions.Insert( 0, slot.SourcePackPath );

            _selectedPack = PackOptions.First( p => p.Equals( slot.SourcePackPath, StringComparison.OrdinalIgnoreCase ) );
            LoadAnimationOptions();
            UpdateCoverage();
        }

        public SlotAssignment Slot { get; }

        public string PackName => Slot.Pack == AnimationPackKind.Battle ? "Battle" : "Secondary";

        public string SlotLabel => $"{PackName} {Slot.Slot}" + ( Slot.Pack == AnimationPackKind.Battle ? BattleHint( Slot.Slot ) : "" );

        public ObservableCollection<string> PackOptions { get; } = new();

        public ObservableCollection<AnimationInfo> AnimationOptions { get; } = new();

        [ObservableProperty] private string _selectedPack;
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
            if ( !PackOptions.Contains( path, StringComparer.OrdinalIgnoreCase ) )
                PackOptions.Insert( 0, path );
            SelectedPack = PackOptions.First( p => p.Equals( path, StringComparison.OrdinalIgnoreCase ) );
        }

        partial void OnSelectedPackChanged( string value )
        {
            if ( mUpdating || string.IsNullOrEmpty( value ) ) return;
            Slot.SourcePackPath = value;
            Slot.RigModelPath = RigModelFor( value );
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

        /// <summary>Packs worth offering first: the source model's folder and the destination persona's folder.</summary>
        private IEnumerable<string> RelevantPacks()
        {
            var project = mOwner.Project;
            var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
            foreach ( var path in project.SourceModel.AnimationPaths )
                if ( seen.Add( path ) ) yield return path;
            foreach ( var kind in new[] { AnimationPackKind.Battle, AnimationPackKind.Secondary } )
            {
                var path = project.DestinationPackPath( kind );
                if ( path != null && seen.Add( path ) ) yield return path;
            }
        }

        /// <summary>The model a pack's animations were authored for, from the game's naming conventions.</summary>
        private string? RigModelFor( string packPath )
        {
            var archive = mOwner.Project.Game.Archive;
            var folder = packPath.Substring( 0, packPath.LastIndexOf( '/' ) );
            var file = Path.GetFileNameWithoutExtension( packPath );

            if ( folder.Equals( mOwner.Project.SourceModel.Path.Substring( 0, mOwner.Project.SourceModel.Path.LastIndexOf( '/' ) ), StringComparison.OrdinalIgnoreCase ) &&
                 mOwner.Project.SourceModel.Category is not Core.Game.ModelCategory.Character )
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
