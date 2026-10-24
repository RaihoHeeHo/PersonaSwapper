using System;
using System.Collections.Concurrent;
using PersonaSwapper.Core.Game;
using PersonaSwapper.Core.Swap;

namespace PersonaSwapper.Core.Preview
{
    /// <summary>Bind-pose measurements of models, cached by path.</summary>
    public static class ModelMetrics
    {
        private static readonly ConcurrentDictionary<string, float> sHeights = new( StringComparer.OrdinalIgnoreCase );

        /// <summary>Vertical extent of the skinned bind pose, in model units (0 if it can't be measured).</summary>
        public static float Height( GameData game, string modelPath )
            => sHeights.GetOrAdd( modelPath, path =>
            {
                try
                {
                    var skinned = new SkinnedModel( AnimationCache.Shared.Model( game.Archive, path ) );
                    skinned.Update( null, 0 );
                    return Math.Max( 0, skinned.BoundsMax.Y - skinned.BoundsMin.Y );
                }
                catch ( Exception )
                {
                    return 0;
                }
            } );
    }
}
