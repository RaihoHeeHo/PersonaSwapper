using System;
using PersonaSwapper.Core.Tables;

namespace PersonaSwapper.Core.Game
{
    /// <summary>Everything the swapper reads from the game, loaded once.</summary>
    public sealed class GameData : IDisposable
    {
        public const string PersonaTablePath = "BATTLE/TABLE/PERSONA.TBL";
        public const string UnitTablePath = "BATTLE/TABLE/UNIT.TBL";
        public const string SkillTablePath = "BATTLE/TABLE/SKILL.TBL";
        public const string VisualTablePath = "BATTLE/TABLE/VISUAL.TBL";
        public const string NameTablePath = "BATTLE/TABLE/NAME.TBL";

        private GameData( GameArchive archive, PersonaTable personas, UnitTable units, NameTable names, ModelCatalog catalog )
        {
            Archive = archive;
            Personas = personas;
            Units = units;
            Names = names;
            Catalog = catalog;
        }

        public GameArchive Archive { get; }

        /// <summary>Original (unmodified) tables as shipped with the game.</summary>
        public PersonaTable Personas { get; }

        public UnitTable Units { get; }

        public NameTable Names { get; }

        public ModelCatalog Catalog { get; }

        /// <param name="folder">The game folder, its CPK subfolder, or any folder holding BASE.CPK and EN.CPK.</param>
        public static GameData Load( string folder, string language = "EN" )
        {
            var archive = GameArchive.OpenGameFolder( folder, language );
            try
            {
                var personas = PersonaTable.Parse( archive.Read( PersonaTablePath ) );
                var units = UnitTable.Parse( archive.Read( UnitTablePath ) );
                var names = NameTable.Parse( archive.Read( NameTablePath ) );
                var catalog = ModelCatalog.Build( archive, names, units );
                return new GameData( archive, personas, units, names, catalog );
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }

        public void Dispose() => Archive.Dispose();
    }
}
