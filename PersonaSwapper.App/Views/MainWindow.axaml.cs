using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PersonaSwapper.App.ViewModels;

namespace PersonaSwapper.App.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        private async void OnBrowseGameFolder( object? sender, RoutedEventArgs e )
        {
            var folder = await PickFolder( "Choose the P5R game folder (contains CPK/BASE.CPK)" );
            if ( folder == null || ViewModel == null ) return;
            ViewModel.GameFolder = folder;
            await ViewModel.LoadGameAsync();
        }

        private async void OnBrowseModsFolder( object? sender, RoutedEventArgs e )
        {
            var folder = await PickFolder( "Choose Reloaded-II's Mods folder" );
            if ( folder != null && ViewModel != null )
                ViewModel.ModsFolder = folder;
        }

        private void OnPackSearchSelected( object? sender, SelectionChangedEventArgs e )
        {
            if ( e.AddedItems.Count == 0 || e.AddedItems[0] is not string path ) return;
            var slot = ViewModel?.Editor?.SelectedSlot;
            if ( slot == null )
            {
                if ( ViewModel != null ) ViewModel.Status = "Select a slot row first, then pick the pack to use for it.";
                return;
            }
            slot.UsePack( path );
            if ( sender is AutoCompleteBox box ) box.Text = "";
        }

        private async System.Threading.Tasks.Task<string?> PickFolder( string title )
        {
            var result = await StorageProvider.OpenFolderPickerAsync( new FolderPickerOpenOptions { Title = title, AllowMultiple = false } );
            return result.FirstOrDefault()?.TryGetLocalPath();
        }
    }
}
