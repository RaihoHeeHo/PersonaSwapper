using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PersonaSwapper.App.ViewModels;
using PersonaSwapper.App.Views;
using PersonaSwapper.Core.Diagnostics;

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

                // Exceptions on the UI thread (including the preview's render callback) are logged and the app
                // keeps running, with a pointer to the log in the status bar.
                Dispatcher.UIThread.UnhandledException += ( _, e ) =>
                {
                    Log.Crash( "UI thread", e.Exception );
                    viewModel.Status = $"Error: {e.Exception.GetType().Name}: {e.Exception.Message}  (details in {Log.FilePath})";
                    e.Handled = true;
                };

                _ = viewModel.LoadGameAsync();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
