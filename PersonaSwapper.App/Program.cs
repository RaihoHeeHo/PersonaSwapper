using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Logging;
using PersonaSwapper.Core.Diagnostics;

namespace PersonaSwapper.App
{
    internal static class Program
    {
        [STAThread]
        public static int Main( string[] args )
        {
            Log.Start( "Persona Swapper" );

            // Last-resort handlers: anything that escapes is written to the log before the process exits.
            AppDomain.CurrentDomain.UnhandledException += ( _, e ) =>
            {
                if ( e.ExceptionObject is Exception ex ) Log.Crash( "AppDomain (fatal)", ex );
                else Log.Error( $"Fatal non-exception error: {e.ExceptionObject}" );
            };
            TaskScheduler.UnobservedTaskException += ( _, e ) =>
            {
                Log.Crash( "Background task", e.Exception );
                e.SetObserved(); // don't let a background failure take the app down
            };

            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime( args );
            }
            catch ( Exception ex )
            {
                Log.Crash( "Main", ex );
                throw;
            }
            finally
            {
                Log.Info( "Persona Swapper exited." );
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                         .UsePlatformDetect()
                         .WithInterFont()
                         // The 3D preview uses OpenGL; prefer GL rendering on macOS and Linux.
                         .With( new AvaloniaNativePlatformOptions
                         {
                             RenderingMode = new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software },
                         } )
                         .With( new X11PlatformOptions
                         {
                             RenderingMode = new[] { X11RenderingMode.Glx, X11RenderingMode.Software },
                         } )
                         .AfterSetup( _ => Logger.Sink = new AvaloniaLogSink() );
    }

    /// <summary>Sends Avalonia's own warnings and errors (bindings, rendering, OpenGL) to the app log.</summary>
    internal sealed class AvaloniaLogSink : ILogSink
    {
        public bool IsEnabled( LogEventLevel level, string area ) => level >= LogEventLevel.Warning;

        public void Log( LogEventLevel level, string area, object? source, string messageTemplate )
            => Write( level, area, source, messageTemplate );

        public void Log( LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues )
        {
            var message = messageTemplate;
            try
            {
                // Avalonia templates use {Name} placeholders filled in order.
                var index = 0;
                message = System.Text.RegularExpressions.Regex.Replace( messageTemplate, @"\{[^}]+\}",
                    m => index < propertyValues.Length ? propertyValues[index++]?.ToString() ?? "null" : m.Value );
            }
            catch ( Exception ) { }
            Write( level, area, source, message );
        }

        private static void Write( LogEventLevel level, string area, object? source, string message )
        {
            var text = $"[{area}] {message}{( source != null ? $" (source: {source.GetType().Name})" : "" )}";
            if ( level >= LogEventLevel.Error ) PersonaSwapper.Core.Diagnostics.Log.Error( text, null, "Avalonia" );
            else PersonaSwapper.Core.Diagnostics.Log.Warn( text, "Avalonia" );
        }
    }
}
