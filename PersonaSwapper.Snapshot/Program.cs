using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PersonaSwapper.App;
using PersonaSwapper.App.ViewModels;
using PersonaSwapper.App.Views;

namespace PersonaSwapper.Snapshot
{
    internal static class Program
    {
        // usage: snapshot <game-folder> <out-dir> <source-model-path> <persona-id>
        private static int Main( string[] args )
        {
            AppBuilder.Configure<App.App>()
                      .UseSkia()
                      .UseHeadless( new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false } )
                      .WithInterFont()
                      .SetupWithoutStarting();

            var outDir = args[1];
            Directory.CreateDirectory( outDir );

            var settings = new AppSettings { GameFolder = args[0], ModsFolder = Path.Combine( outDir, "mods" ) };
            Directory.CreateDirectory( settings.ModsFolder );
            var vm = new MainViewModel( settings );
            var window = new MainWindow { DataContext = vm, Width = 1560, Height = 940 };
            window.Show();

            Wait( vm.LoadGameAsync() );
            Pump( 0.3 );
            Capture( window, outDir, "1-loaded" );

            vm.SourceCategory = 2;
            vm.SelectedSource = vm.SourceModels.First( m => m.Path.Equals( args[2], StringComparison.OrdinalIgnoreCase ) );
            vm.SelectedDestination = vm.Destinations.First( p => p.Id == int.Parse( args[3] ) );
            WaitUntil( () => vm.Editor != null && !vm.IsBusy, 60 );
            Pump( 0.5 );
            Console.WriteLine( $"Status: {vm.Status}" );
            Capture( window, outDir, "2-persona-data" );

            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            tabs.SelectedIndex = 1;
            Pump( 0.5 );
            Capture( window, outDir, "3-animations" );

            // Scroll the persona tab to the skills/affinities section.
            tabs.SelectedIndex = 0;
            Pump( 0.3 );
            var scroller = tabs.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault( s => s.Extent.Height > s.Viewport.Height + 50 );
            if ( scroller != null )
            {
                scroller.Offset = new Vector( 0, 560 );
                Pump( 0.3 );
                Capture( window, outDir, "4-skills" );
                scroller.Offset = new Vector( 0, 1050 );
                Pump( 0.3 );
                Capture( window, outDir, "5-affinities" );
            }

            // Export through the UI command and check the result.
            vm.ExportCommand.Execute( null );
            WaitUntil( () => !vm.IsBusy && vm.Status.StartsWith( "Export", StringComparison.Ordinal ), 60 );
            Console.WriteLine( $"Export: {vm.Status}" );
            return 0;
        }

        private static void Capture( Window window, string dir, string name )
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            var path = Path.Combine( dir, name + ".png" );
            frame?.Save( path );
            Console.WriteLine( $"saved {path}" );
        }

        private static void Pump( double seconds )
        {
            var sw = Stopwatch.StartNew();
            while ( sw.Elapsed.TotalSeconds < seconds )
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                System.Threading.Thread.Sleep( 10 );
            }
        }

        private static void Wait( Task task )
        {
            while ( !task.IsCompleted )
            {
                Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep( 10 );
            }
            task.GetAwaiter().GetResult();
        }

        private static void WaitUntil( Func<bool> condition, double timeoutSeconds )
        {
            var sw = Stopwatch.StartNew();
            while ( !condition() )
            {
                if ( sw.Elapsed.TotalSeconds > timeoutSeconds )
                    throw new TimeoutException( "Timed out waiting for the UI." );
                Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep( 10 );
            }
        }
    }
}
