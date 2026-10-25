using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PersonaSwapper.App.Views
{
    public static class Converters
    {
        public static readonly IValueConverter PlayPause =
            new FuncValueConverter<bool, string>( playing => playing ? "Pause" : "Play" );

        public static readonly IValueConverter FileName =
            new FuncValueConverter<string?, string>( path => path == null ? "" : Path.GetFileName( path ) );

        public static readonly IValueConverter WarningBrush =
            new FuncValueConverter<bool, IBrush>( warn => warn ? new SolidColorBrush( Color.Parse( "#FFC83D" ) ) : new SolidColorBrush( Color.Parse( "#F4F2F0" ) ) );
    }
}
