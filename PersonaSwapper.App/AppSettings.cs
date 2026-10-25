using System;
using System.IO;
using System.Text.Json;

namespace PersonaSwapper.App
{
    /// <summary>Remembered folders, stored in the user's application-support folder.</summary>
    public sealed class AppSettings
    {
        public string GameFolder { get; set; } = DefaultGameFolder();

        public string ModsFolder { get; set; } = "";

        public string Author { get; set; } = Environment.UserName;

        /// <summary>The Custom Bonus Tweaks mod is installed (menus load PSZ; exports never touch enemy models).</summary>
        public bool CustomBonusTweaks { get; set; }

        private static string SettingsPath
            => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ), "PersonaSwapper", "settings.json" );

        public static AppSettings Load()
        {
            try
            {
                if ( File.Exists( SettingsPath ) )
                    return JsonSerializer.Deserialize<AppSettings>( File.ReadAllText( SettingsPath ) ) ?? new AppSettings();
            }
            catch ( Exception )
            {
                // Corrupt settings just fall back to defaults.
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory( Path.GetDirectoryName( SettingsPath )! );
                File.WriteAllText( SettingsPath, JsonSerializer.Serialize( this, new JsonSerializerOptions { WriteIndented = true } ) );
            }
            catch ( Exception )
            {
                // Settings are a convenience; failing to save them isn't fatal.
            }
        }

        /// <summary>Steam's default P5R install location on macOS (via NotProton) or Windows/Linux.</summary>
        public static string DefaultGameFolder()
        {
            var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
            var candidates = new[]
            {
                Path.Combine( home, "Library", "Application Support", "Steam", "steamapps", "common", "P5R" ),
                Path.Combine( home, ".local", "share", "Steam", "steamapps", "common", "P5R" ),
                Path.Combine( home, ".steam", "steam", "steamapps", "common", "P5R" ),
                Path.Combine( home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "common", "P5R" ), // Flatpak Steam
                @"C:\Program Files (x86)\Steam\steamapps\common\P5R",
            };
            foreach ( var candidate in candidates )
                if ( Directory.Exists( candidate ) )
                    return candidate;
            return candidates[0];
        }

        /// <summary>Reloaded-II's Mods folder inside the game folder, where it was installed for NotProton.</summary>
        public static string DefaultModsFolder( string gameFolder ) => Path.Combine( gameFolder, "Reloaded-II", "Mods" );
    }
}
