using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using PersonaSwapper.Core.Diagnostics;
using PersonaSwapper.Core.Game;

namespace PersonaSwapper.Core.Swap
{
    /// <summary>Summary of one animation in a pack, for listing in the UI.</summary>
    public sealed record AnimationInfo( int Index, float Duration, int ControllerCount )
    {
        public bool IsEmpty => ControllerCount == 0;

        public override string ToString() => IsEmpty ? $"{Index}: (empty)" : $"{Index}: {Duration:0.##}s";
    }

    /// <summary>
    /// Caches raw file bytes and parsed models. Animation packs are returned as fresh copies
    /// (re-parsed) because retargeting edits keyframes in place.
    /// </summary>
    public sealed class AnimationCache
    {
        public static AnimationCache Shared { get; } = new();

        private readonly ConcurrentDictionary<string, byte[]> mBytes = new( StringComparer.OrdinalIgnoreCase );
        private readonly ConcurrentDictionary<string, IReadOnlyList<AnimationInfo>> mInfo = new( StringComparer.OrdinalIgnoreCase );
        private readonly ConcurrentDictionary<string, ModelPack> mModels = new( StringComparer.OrdinalIgnoreCase );

        public byte[] Bytes( GameArchive archive, string path ) => mBytes.GetOrAdd( path, p => archive.Read( p ) );

        /// <summary>Parsed model, shared: treat as read-only.</summary>
        public ModelPack Model( GameArchive archive, string path )
            => mModels.GetOrAdd( path, p =>
            {
                var bytes = Bytes( archive, p );
                Log.Step( $"Parsing model {p} ({bytes.Length:N0} bytes)" );
                try
                {
                    using var stream = new MemoryStream( bytes );
                    var pack = Resource.Load<ModelPack>( stream ) ?? throw new InvalidDataException( $"{p} isn't a model." );
                    Log.Info( $"Parsed {p}: version 0x{pack.Version:X8}, model {( pack.Model != null ? "yes" : "no" )}, " +
                              $"{pack.Textures?.Count ?? 0} textures, {pack.Materials?.Count ?? 0} materials" );
                    return pack;
                }
                catch ( Exception e )
                {
                    Log.Error( $"Parsing {p} failed", e );
                    throw;
                }
            } );

        /// <summary>A private copy of a pack, safe to modify.</summary>
        public AnimationPack FreshPack( GameArchive archive, string path )
        {
            using var stream = new MemoryStream( Bytes( archive, path ) );
            Resource resource;
            try
            {
                resource = Resource.Load( stream, false );
            }
            catch ( Exception e )
            {
                Log.Error( $"Parsing animation pack {path} failed", e );
                throw;
            }
            return resource switch
            {
                AnimationPack pack => pack,
                ModelPack { AnimationPack: { } embedded } => embedded,
                _ => throw new InvalidDataException( $"{path} has no animation pack." ),
            };
        }

        public IReadOnlyList<AnimationInfo> Info( GameArchive archive, string path )
            => mInfo.GetOrAdd( path, p =>
            {
                var pack = FreshPack( archive, p );
                return ( pack.Animations ?? new List<Animation>() )
                    .Select( ( a, i ) => new AnimationInfo( i, a.Duration, a.Controllers?.Count ?? 0 ) )
                    .ToList();
            } );

        public int SlotCount( GameArchive archive, string path ) => Info( archive, path ).Count;

        public bool IsEmpty( GameArchive archive, string path, int index )
        {
            var info = Info( archive, path );
            return index >= info.Count || info[index].IsEmpty;
        }

        public void Clear()
        {
            mBytes.Clear();
            mInfo.Clear();
            mModels.Clear();
        }
    }
}
