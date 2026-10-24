using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PersonaSwapper.App.ViewModels;
using PersonaSwapper.App.Views;

namespace PersonaSwapper.App
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load( this );

        public override void OnFrameworkInitializationCompleted()
        {
            if ( ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop )
            {
                var viewModel = new MainViewModel( AppSettings.Load() );
                desktop.MainWindow = new MainWindow { DataContext = viewModel };
                desktop.ShutdownRequested += ( _, _ ) => viewModel.Dispose();
                _ = viewModel.LoadGameAsync();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
