using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;

namespace PersonaSwapper.Core.Preview
{
    /// <summary>One drawable mesh with world-space vertices for the current pose.</summary>
    public sealed class SkinnedMesh
    {
        internal SkinnedMesh( Mesh mesh, int nodeIndex )
        {
            Mesh = mesh;
            NodeIndex = nodeIndex;
            Positions = new Vector3[mesh.VertexCount];
            Normals = new Vector3[mesh.VertexCount];
            Indices = mesh.Triangles?.SelectMany( t => new[] { t.A, t.B, t.C } ).ToArray() ?? Array.Empty<uint>();
            TexCoords = mesh.TexCoordsChannel0;
        }

        public Mesh Mesh { get; }

        /// <summary>The node the mesh is attached to (used for unskinned meshes).</summary>
        public int NodeIndex { get; }

        public string? MaterialName => Mesh.MaterialName;

        public Vector3[] Positions { get; }

        public Vector3[] Normals { get; }

        public Vector2[]? TexCoords { get; }

        public uint[] Indices { get; }
    }

    /// <summary>
    /// A model posed on the CPU: every frame, <see cref="Update"/> evaluates the animation and skins
    /// all meshes into world space (the same math as GFD Studio's viewer), ready to upload or rasterize.
    /// </summary>
    public sealed class SkinnedModel
    {
        private readonly List<Bone> mBones;

        public SkinnedModel( ModelPack pack )
        {
            Pack = pack;
            var model = pack.Model ?? throw new ArgumentException( "Model pack has no model." );
            Pose = new PoseEvaluator( model );
            mBones = model.Bones ?? new List<Bone>();

            var nodeIndex = 0;
            foreach ( var node in model.Nodes )
            {
                if ( node.HasAttachments )
                    foreach ( var attachment in node.Attachments.Where( a => a.Type == NodeAttachmentType.Mesh ) )
                    {
                        var mesh = attachment.GetValue<Mesh>();
                        if ( mesh?.Vertices != null && mesh.Triangles != null )
                            Meshes.Add( new SkinnedMesh( mesh, nodeIndex ) );
                    }
                nodeIndex++;
            }
        }

        public ModelPack Pack { get; }

        public PoseEvaluator Pose { get; }

        public List<SkinnedMesh> Meshes { get; } = new();

        public Vector3 BoundsMin { get; private set; }

        public Vector3 BoundsMax { get; private set; }

        public void Update( Animation? animation, float time )
        {
            Pose.Evaluate( animation, time );
            var world = Pose.World;
            var min = new Vector3( float.MaxValue );
            var max = new Vector3( float.MinValue );

            foreach ( var sm in Meshes )
            {
                var mesh = sm.Mesh;
                var vertices = mesh.Vertices;
                var normals = mesh.Normals;
                var weights = mesh.VertexWeights;
                var nodeWorld = world[sm.NodeIndex];

                for ( var i = 0; i < vertices.Length; i++ )
                {
                    var position = vertices[i];
                    var normal = normals != null ? normals[i] : Vector3.UnitY;
                    Vector3 p, n;

                    if ( weights != null && i < weights.Length && weights[i].Weights != null )
                    {
                        p = Vector3.Zero;
                        n = Vector3.Zero;
                        var w = weights[i];
                        for ( var j = 0; j < w.Weights.Length; j++ )
                        {
                            var weight = w.Weights[j];
                            if ( weight == 0 ) continue;
                            var boneIndex = w.Indices[j];
                            if ( boneIndex >= mBones.Count ) continue;
                            var bone = mBones[boneIndex];
                            if ( bone.NodeIndex >= world.Length ) continue;
                            var m = bone.InverseBindMatrix * world[bone.NodeIndex];
                            p += Vector3.Transform( position, m ) * weight;
                            n += Vector3.TransformNormal( normal, m ) * weight;
                        }
                    }
                    else
                    {
                        p = Vector3.Transform( position, nodeWorld );
                        n = Vector3.TransformNormal( normal, nodeWorld );
                    }

                    sm.Positions[i] = p;
                    sm.Normals[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize( n ) : Vector3.UnitY;
                    min = Vector3.Min( min, p );
                    max = Vector3.Max( max, p );
                }
            }

            BoundsMin = min;
            BoundsMax = max;
        }
    }
}
