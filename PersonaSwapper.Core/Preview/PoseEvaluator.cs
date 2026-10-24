using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GFDLibrary.Animations;
using GFDLibrary.Models;

namespace PersonaSwapper.Core.Preview
{
    /// <summary>
    /// Evaluates a model's node hierarchy at a point in an animation without modifying the model
    /// (parsed models are shared). Matrices follow GFDLibrary/System.Numerics: row vectors,
    /// local = R * S with translation, world = local * parentWorld.
    /// </summary>
    public sealed class PoseEvaluator
    {
        private readonly Node[] mNodes;
        private readonly int[] mParents;
        private readonly Dictionary<string, int> mIndexByName;
        private readonly Vector3[] mTranslations;
        private readonly Quaternion[] mRotations;
        private readonly Vector3[] mScales;

        public PoseEvaluator( Model model )
        {
            Model = model;
            mNodes = model.Nodes.ToArray();
            mParents = new int[mNodes.Length];
            var indexOf = new Dictionary<Node, int>( ReferenceEqualityComparer.Instance );
            for ( var i = 0; i < mNodes.Length; i++ )
                indexOf[mNodes[i]] = i;
            for ( var i = 0; i < mNodes.Length; i++ )
                mParents[i] = mNodes[i].Parent != null && indexOf.TryGetValue( mNodes[i].Parent, out var p ) ? p : -1;

            mIndexByName = new Dictionary<string, int>();
            for ( var i = 0; i < mNodes.Length; i++ )
                mIndexByName.TryAdd( mNodes[i].Name, i );

            mTranslations = new Vector3[mNodes.Length];
            mRotations = new Quaternion[mNodes.Length];
            mScales = new Vector3[mNodes.Length];
            World = new Matrix4x4[mNodes.Length];
        }

        public Model Model { get; }

        public int NodeCount => mNodes.Length;

        /// <summary>World matrix of each node (depth-first order, same indices as bones and controllers), after the last Evaluate.</summary>
        public Matrix4x4[] World { get; }

        public int IndexOf( Node node ) => mIndexByName.TryGetValue( node.Name, out var i ) ? i : -1;

        /// <summary>Poses the hierarchy at <paramref name="time"/> seconds (looping). Null animation = bind pose.</summary>
        public void Evaluate( Animation? animation, float time )
        {
            for ( var i = 0; i < mNodes.Length; i++ )
            {
                mTranslations[i] = mNodes[i].Translation;
                mRotations[i] = mNodes[i].Rotation;
                mScales[i] = mNodes[i].Scale;
            }

            if ( animation?.Controllers != null && animation.Duration > 0 )
            {
                var t = time % animation.Duration;
                if ( t < 0 ) t += animation.Duration;

                foreach ( var controller in animation.Controllers )
                {
                    if ( controller.TargetKind != TargetKind.Node || controller.Layers == null )
                        continue;
                    if ( !mIndexByName.TryGetValue( controller.TargetName, out var index ) )
                        continue;

                    foreach ( var layer in controller.Layers )
                    {
                        if ( !layer.HasPRSKeyFrames || layer.Keys == null || layer.Keys.Count == 0 )
                            continue;
                        Sample( layer, t, animation.Duration, ref mTranslations[index], ref mRotations[index], ref mScales[index] );
                    }
                }
            }

            for ( var i = 0; i < mNodes.Length; i++ )
            {
                var local = Matrix4x4.CreateFromQuaternion( Quaternion.Normalize( mRotations[i] ) ) * Matrix4x4.CreateScale( mScales[i] );
                local.Translation = mTranslations[i];
                // Depth-first order guarantees parents come before children.
                World[i] = mParents[i] >= 0 ? local * World[mParents[i]] : local;
            }
        }

        private static void Sample( AnimationLayer layer, float t, float duration, ref Vector3 translation, ref Quaternion rotation, ref Vector3 scale )
        {
            var keys = layer.Keys;

            // Last key at or before t (keys are time-sorted); wrap to the last key when t is before the first.
            var cur = -1;
            for ( var k = 0; k < keys.Count; k++ )
            {
                if ( keys[k].Time <= t ) cur = k;
                else break;
            }

            PRSKey a, b;
            float blend;
            if ( cur < 0 )
            {
                a = (PRSKey)keys[^1];
                b = (PRSKey)keys[0];
                var span = keys[0].Time + ( duration - keys[^1].Time );
                blend = span > 0 ? ( t + ( duration - keys[^1].Time ) ) / span : 0;
            }
            else if ( cur == keys.Count - 1 )
            {
                a = b = (PRSKey)keys[cur];
                blend = 0;
            }
            else
            {
                a = (PRSKey)keys[cur];
                b = (PRSKey)keys[cur + 1];
                var span = b.Time - a.Time;
                blend = span > 0 ? ( t - a.Time ) / span : 0;
            }
            blend = Math.Clamp( blend, 0f, 1f );

            if ( a.HasRotation )
                rotation = b.HasRotation ? Quaternion.Slerp( a.Rotation, b.Rotation, blend ) : a.Rotation;
            if ( a.HasPosition )
                translation = ( b.HasPosition ? Vector3.Lerp( a.Position, b.Position, blend ) : a.Position ) * layer.PositionScale;
            if ( a.HasScale )
                scale = ( b.HasScale ? Vector3.Lerp( a.Scale, b.Scale, blend ) : a.Scale ) * layer.ScaleScale;
        }
    }
}
