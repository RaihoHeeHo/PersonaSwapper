using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonaSwapper.Core.Game;
using PersonaSwapper.Core.Swap;

namespace PersonaSwapper.App.ViewModels
{
    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly AppSettings mSettings;
        private int mEditorGeneration;

        public MainViewModel( AppSettings settings )
        {
            mSettings = settings;
            _gameFolder = settings.GameFolder;
            _modsFolder = string.IsNullOrWhiteSpace( settings.ModsFolder ) ? AppSettings.DefaultModsFolder( settings.GameFolder ) : settings.ModsFolder;
            _author = settings.Author;
        }

        public GameData? Game { get; private set; }

        public Lookups? Lookups { get; private set; }

        public PreviewViewModel Preview { get; } = new();

        public static IReadOnlyList<string> Categories { get; } = new[] { "All models", "Personas", "Shadows / bosses", "Characters", "Other" };

        public ObservableCollection<ModelEntry> SourceModels { get; } = new();

        public ObservableCollection<PersonaEntry> Destinations { get; } = new();

        [ObservableProperty] private string _gameFolder;
        [ObservableProperty] private string _modsFolder;
        [ObservableProperty] private string _author;
        [ObservableProperty] private string _status = "Loading game files…";
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private bool _isGameLoaded;

        [ObservableProperty] private string _sourceSearch = "";
        [ObservableProperty] private int _sourceCategory;
        [ObservableProperty] private ModelEntry? _selectedSource;

        [ObservableProperty] private string _destinationSearch = "";
        [ObservableProperty] private PersonaEntry? _selectedDestination;

        [ObservableProperty] private SwapEditorViewModel? _editor;

        public bool HasEditor => Editor != null;

        partial void OnEditorChanged( SwapEditorViewModel? value ) => OnPropertyChanged( nameof( HasEditor ) );

        partial void OnSourceSearchChanged( string value ) => RefreshSources();

        partial void OnSourceCategoryChanged( int value ) => RefreshSources();

        partial void OnDestinationSearchChanged( string value ) => RefreshDestinations();

        partial void OnSelectedSourceChanged( ModelEntry? value ) => _ = RebuildEditorAsync();

        partial void OnSelectedDestinationChanged( PersonaEntry? value ) => _ = RebuildEditorAsync();

        [RelayCommand]
        public async Task LoadGameAsync()
        {
            IsBusy = true;
            Status = $"Loading {GameFolder}…";
            try
            {
                var folder = GameFolder;
                var game = await Task.Run( () => GameData.Load( folder ) );
                Game?.Dispose();
                Game = game;
                Lookups = await Task.Run( () => new Lookups( game ) );
                AnimationCache.Shared.Clear();
                IsGameLoaded = true;
                RefreshSources();
                RefreshDestinations();
                Status = $"Loaded {game.Catalog.Models.Count:N0} models and {game.Catalog.Personas.Count} personas. Pick a source model and a destination persona.";

                mSettings.GameFolder = folder;
                if ( string.IsNullOrWhiteSpace( ModsFolder ) )
                    ModsFolder = AppSettings.DefaultModsFolder( folder );
                SaveSettings();
            }
            catch ( Exception e )
            {
                IsGameLoaded = false;
                Status = $"Couldn't load the game: {e.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task ExportAsync()
        {
            if ( Editor == null || Game == null )
                return;

            IsBusy = true;
            try
            {
                var editor = Editor;
                var modsFolder = ModsFolder;
                var info = new ModInfo( editor.ModId, editor.ModName, Author, $"{editor.Project.SourceModel.DisplayName} as persona {editor.Project.Destination.Id} ({editor.Project.Destination.Name})" );
                if ( !Regex.IsMatch( info.Id, @"^[A-Za-z0-9._-]+$" ) )
                    throw new InvalidOperationException( "Mod ID can only contain letters, digits, '.', '_' and '-'." );

                Status = "Building swap…";
                var (folder, build) = await Task.Run( () =>
                {
                    var result = SwapBuilder.Build( editor.Project );
                    return (ModWriter.Write( modsFolder, info, result ), result);
                } );

                SaveSettings();
                Status = $"Exported {build.Files.Count} files to {folder}." +
                         ( build.Warnings.Count > 0 ? $" {build.Warnings.Count} warning(s): {string.Join( " ", build.Warnings.Take( 2 ) )}" : " Enable the mod in Reloaded-II." );
            }
            catch ( Exception e )
            {
                Status = $"Export failed: {e.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RefreshSources()
        {
            SourceModels.Clear();
            if ( Game == null )
                return;

            var category = SourceCategory switch
            {
                1 => ModelCategory.Persona,
                2 => ModelCategory.Enemy,
                3 => ModelCategory.Character,
                4 => ModelCategory.Other,
                _ => (ModelCategory?)null,
            };
            var terms = SourceSearch.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
            foreach ( var model in Game.Catalog.Models )
            {
                if ( category != null && model.Category != category )
                    continue;
                if ( terms.All( t => model.DisplayName.Contains( t, StringComparison.OrdinalIgnoreCase ) ||
                                     model.Path.Contains( t, StringComparison.OrdinalIgnoreCase ) ) )
                    SourceModels.Add( model );
            }
        }

        private void RefreshDestinations()
        {
            Destinations.Clear();
            if ( Game == null )
                return;

            var terms = DestinationSearch.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
            foreach ( var persona in Game.Catalog.Personas )
                if ( terms.All( t => persona.ToString().Contains( t, StringComparison.OrdinalIgnoreCase ) ) )
                    Destinations.Add( persona );
        }

        private async Task RebuildEditorAsync()
        {
            var generation = ++mEditorGeneration;
            var source = SelectedSource;
            var destination = SelectedDestination;
            var game = Game;
            var lookups = Lookups;

            if ( game == null || lookups == null || source == null || destination == null )
            {
                Editor = null;
                if ( source != null && game != null )
                    Preview.Show( game, source.Path, null );
                return;
            }

            IsBusy = true;
            Status = $"Preparing {source.DisplayName} as {destination.Name}…";
            try
            {
                // Building the editor parses the animation packs for every slot; keep that off the UI thread.
                var editor = await Task.Run( () => new SwapEditorViewModel( new SwapProject( game, source, destination ), lookups, Preview ) );
                if ( generation != mEditorGeneration )
                    return; // selection changed while we were loading

                Editor = editor;
                Status = $"{source.DisplayName} → {destination.Name}. Edit the persona and animations, then Export.";
                Editor.PreviewFirstSlot();
            }
            catch ( Exception e )
            {
                Editor = null;
                Status = $"Couldn't prepare the swap: {e.Message}";
            }
            finally
            {
                if ( generation == mEditorGeneration )
                    IsBusy = false;
            }
        }

        private void SaveSettings()
        {
            mSettings.GameFolder = GameFolder;
            mSettings.ModsFolder = ModsFolder;
            mSettings.Author = Author;
            mSettings.Save();
        }

        public void Dispose()
        {
            SaveSettings();
            Preview.Dispose();
            Game?.Dispose();
        }
    }
}
