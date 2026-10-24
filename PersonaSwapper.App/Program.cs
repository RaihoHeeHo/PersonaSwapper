using System;
using Avalonia;

namespace PersonaSwapper.App
{
    internal static class Program
    {
        [STAThread]
        public static void Main( string[] args ) => BuildAvaloniaApp().StartWithClassicDesktopLifetime( args );

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
                         .LogToTrace();
    }
}
