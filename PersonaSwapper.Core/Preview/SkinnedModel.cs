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

            // Triangles referencing vertices that don't exist would make the GPU read out of bounds.
            var vertexCount = (uint)Positions.Length;
            var valid = new List<uint>( Indices.Length );
            for ( var i = 0; i + 2 < Indices.Length; i += 3 )
            {
                if ( Indices[i] < vertexCount && Indices[i + 1] < vertexCount && Indices[i + 2] < vertexCount )
                {
                    valid.Add( Indices[i] );
                    valid.Add( Indices[i + 1] );
                    valid.Add( Indices[i + 2] );
                }
            }
            ValidIndices = valid.ToArray();
            DroppedTriangles = ( Indices.Length - ValidIndices.Length ) / 3;
        }

        /// <summary>Indices with any triangle that points past the vertex list removed.</summary>
        public uint[] ValidIndices { get; }

        public int DroppedTriangles { get; }

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

            Issues.AddRange( Pose.Issues );

            var nodeIndex = 0;
            foreach ( var node in model.Nodes )
            {
                if ( node.HasAttachments )
                    foreach ( var attachment in node.Attachments.Where( a => a.Type == NodeAttachmentType.Mesh ) )
                    {
                        var mesh = attachment.GetValue<Mesh>();
                        if ( mesh?.Vertices == null || mesh.Triangles == null )
                        {
                            Issues.Add( $"node '{node.Name}' has a mesh without vertices or triangles; skipped" );
                            continue;
                        }
                        var skinned = new SkinnedMesh( mesh, nodeIndex );
                        Meshes.Add( skinned );
                        var label = $"mesh {Meshes.Count - 1} on node '{node.Name}'";
                        if ( skinned.DroppedTriangles > 0 )
                            Issues.Add( $"{label}: dropped {skinned.DroppedTriangles} triangles with out-of-range vertex indices" );
                        if ( mesh.Normals != null && mesh.Normals.Length < mesh.Vertices.Length )
                            Issues.Add( $"{label}: {mesh.Normals.Length} normals for {mesh.Vertices.Length} vertices" );
                        if ( mesh.VertexWeights != null && mesh.VertexWeights.Length < mesh.Vertices.Length )
                            Issues.Add( $"{label}: {mesh.VertexWeights.Length} vertex weights for {mesh.Vertices.Length} vertices" );
                        if ( mesh.TexCoordsChannel0 != null && mesh.TexCoordsChannel0.Length != mesh.Vertices.Length )
                            Issues.Add( $"{label}: {mesh.TexCoordsChannel0.Length} UVs for {mesh.Vertices.Length} vertices" );
                        if ( mesh.VertexWeights != null && mesh.VertexWeights.Any( w => w.Indices != null && w.Indices.Any( b => b >= mBones.Count ) ) )
                            Issues.Add( $"{label}: vertex weights reference bones past the model's {mBones.Count} bones" );
                    }
                nodeIndex++;
            }
        }

        public ModelPack Pack { get; }

        public PoseEvaluator Pose { get; }

        public List<SkinnedMesh> Meshes { get; } = new();

        /// <summary>Problems found in the model data (handled, but worth logging).</summary>
        public List<string> Issues { get; } = new();

        /// <summary>Vertices that came out NaN/infinite in the last <see cref="Update"/> (bad weights or matrices in the data).</summary>
        public int NonFiniteVertices { get; private set; }

        public Vector3 BoundsMin { get; private set; }

        public Vector3 BoundsMax { get; private set; }

        public void Update( Animation? animation, float time )
        {
            Pose.Evaluate( animation, time );
            NonFiniteVertices = 0;
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
                    var normal = normals != null && i < normals.Length ? normals[i] : Vector3.UnitY;
                    Vector3 p, n;

                    if ( weights != null && i < weights.Length && weights[i].Weights != null && weights[i].Indices != null )
                    {
                        p = Vector3.Zero;
                        n = Vector3.Zero;
                        var w = weights[i];
                        var count = Math.Min( w.Weights.Length, w.Indices.Length );
                        for ( var j = 0; j < count; j++ )
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

                    if ( !float.IsFinite( p.X ) || !float.IsFinite( p.Y ) || !float.IsFinite( p.Z ) )
                    {
                        // Bad source data (NaN weights/matrices): park the vertex at the origin and keep it out of the bounds.
                        p = Vector3.Zero;
                        NonFiniteVertices++;
                        sm.Positions[i] = p;
                        sm.Normals[i] = Vector3.UnitY;
                        continue;
                    }
                    sm.Positions[i] = p;
                    sm.Normals[i] = n.LengthSquared() > 1e-12f && float.IsFinite( n.X ) ? Vector3.Normalize( n ) : Vector3.UnitY;
                    min = Vector3.Min( min, p );
                    max = Vector3.Max( max, p );
                }
            }

            if ( min.X > max.X ) { min = Vector3.Zero; max = Vector3.One; } // nothing finite to measure
            BoundsMin = min;
            BoundsMax = max;
        }
    }
}
