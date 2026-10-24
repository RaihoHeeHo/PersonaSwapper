using System;
using System.Collections.Generic;
using System.Linq;
using PersonaSwapper.Core.Game;
using PersonaSwapper.Core.Tables;

namespace PersonaSwapper.App.ViewModels
{
    /// <summary>An ID with its in-game name, for dropdowns.</summary>
    public sealed record IdName( int Id, string Name )
    {
        public override string ToString() => $"{Id:D3}  {Name}";
    }

    /// <summary>Name lists built once per loaded game.</summary>
    public sealed class Lookups
    {
        public Lookups( GameData game )
        {
            Arcana = Enumerable.Range( 1, Math.Max( 0, game.Names.Count( NameTable.Arcana ) - 1 ) )
                .Select( i => new IdName( i, game.Names.Get( NameTable.Arcana, i ) ) )
                .Where( x => !string.IsNullOrWhiteSpace( x.Name ) && !x.Name.StartsWith( "RESERVE", StringComparison.OrdinalIgnoreCase ) )
                .ToList();
            Skills = Named( game, NameTable.Skills );
            Traits = Named( game, NameTable.Traits );
            AllAnimationPacks = game.Archive.Paths
                .Where( p => p.StartsWith( "MODEL/CHARACTER/", StringComparison.OrdinalIgnoreCase ) &&
                             p.EndsWith( ".GAP", StringComparison.OrdinalIgnoreCase ) )
                .OrderBy( p => p, StringComparer.OrdinalIgnoreCase )
                .ToList();
        }

        public IReadOnlyList<IdName> Arcana { get; }

        public IReadOnlyList<IdName> Skills { get; }

        public IReadOnlyList<IdName> Traits { get; }

        public IReadOnlyList<string> AllAnimationPacks { get; }

        public static IReadOnlyList<IdName> Inheritance { get; } = new[]
        {
            new IdName( 0, "None" ), new IdName( 1, "Physical" ), new IdName( 2, "Fire" ), new IdName( 3, "Ice" ),
            new IdName( 4, "Electric" ), new IdName( 5, "Wind" ), new IdName( 6, "Bless" ), new IdName( 7, "Curse" ),
            new IdName( 8, "Healing" ), new IdName( 10, "Ailment" ), new IdName( 12, "Almighty" ), new IdName( 14, "Nuke" ),
            new IdName( 15, "Psy" ),
        };

        public IdName Find( IReadOnlyList<IdName> list, int id ) => list.FirstOrDefault( x => x.Id == id ) ?? new IdName( id, "(unknown)" );

        private static IReadOnlyList<IdName> Named( GameData game, int section )
            => Enumerable.Range( 0, game.Names.Count( section ) )
                .Select( i => new IdName( i, game.Names.Get( section, i ) ) )
                .Where( x => !string.IsNullOrWhiteSpace( x.Name ) && x.Id > 0 &&
                             !x.Name.StartsWith( "BLANK", StringComparison.OrdinalIgnoreCase ) &&
                             !x.Name.StartsWith( "RESERVE", StringComparison.OrdinalIgnoreCase ) &&
                             !x.Name.Equals( "Unused", StringComparison.OrdinalIgnoreCase ) )
                .ToList();
    }
}
