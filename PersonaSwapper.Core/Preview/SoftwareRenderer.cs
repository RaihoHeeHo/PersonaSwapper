using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;

namespace PersonaSwapper.Core.Preview
{
    /// <summary>
    /// Tiny z-buffered, flat-lit triangle rasterizer that writes PNGs. Used to check poses and
    /// skinning without a GPU (tests, thumbnails); the app's live preview uses OpenGL.
    /// </summary>
    public static class SoftwareRenderer
    {
        public static byte[] RenderPng( SkinnedModel model, int width, int height, float yawDegrees = 25, float pitchDegrees = -10 )
        {
            var rgb = new byte[width * height * 3];
            var depth = new float[width * height];
            Array.Fill( depth, float.MaxValue );
            for ( var i = 0; i < width * height; i++ )
            {
                var y = i / width;
                var shade = (byte)( 40 + 20 * y / height );
                rgb[i * 3] = shade; rgb[i * 3 + 1] = shade; rgb[i * 3 + 2] = (byte)( shade + 8 );
            }

            var center = ( model.BoundsMin + model.BoundsMax ) / 2;
            var size = model.BoundsMax - model.BoundsMin;
            var radius = Math.Max( size.Length() / 2, 1e-3f );
            var rotation = Matrix4x4.CreateRotationY( yawDegrees * MathF.PI / 180 ) * Matrix4x4.CreateRotationX( pitchDegrees * MathF.PI / 180 );
            var scale = 0.9f * Math.Min( width, height ) / ( 2 * radius );
            var light = Vector3.Normalize( new Vector3( 0.4f, 0.6f, 1f ) );

            foreach ( var mesh in model.Meshes )
            {
                var projected = new Vector3[mesh.Positions.Length];
                for ( var i = 0; i < projected.Length; i++ )
                {
                    var v = Vector3.Transform( mesh.Positions[i] - center, rotation );
                    projected[i] = new Vector3( width / 2f + v.X * scale, height / 2f - v.Y * scale, -v.Z );
                }

                for ( var t = 0; t + 2 < mesh.Indices.Length; t += 3 )
                {
                    var i0 = (int)mesh.Indices[t]; var i1 = (int)mesh.Indices[t + 1]; var i2 = (int)mesh.Indices[t + 2];
                    if ( i0 >= projected.Length || i1 >= projected.Length || i2 >= projected.Length ) continue;
                    var n = Vector3.TransformNormal( mesh.Normals[i0] + mesh.Normals[i1] + mesh.Normals[i2], rotation );
                    var intensity = n.LengthSquared() > 0 ? Math.Abs( Vector3.Dot( Vector3.Normalize( n ), light ) ) : 0.5f;
                    var c = (byte)( 60 + 180 * intensity );
                    FillTriangle( projected[i0], projected[i1], projected[i2], width, height, rgb, depth, c, (byte)( c * 0.85f ), (byte)( c * 0.7f ) );
                }
            }

            return EncodePng( rgb, width, height );
        }

        private static void FillTriangle( Vector3 a, Vector3 b, Vector3 c, int w, int h, byte[] rgb, float[] depth, byte r, byte g, byte bl )
        {
            var minX = Math.Max( 0, (int)MathF.Floor( MathF.Min( a.X, MathF.Min( b.X, c.X ) ) ) );
            var maxX = Math.Min( w - 1, (int)MathF.Ceiling( MathF.Max( a.X, MathF.Max( b.X, c.X ) ) ) );
            var minY = Math.Max( 0, (int)MathF.Floor( MathF.Min( a.Y, MathF.Min( b.Y, c.Y ) ) ) );
            var maxY = Math.Min( h - 1, (int)MathF.Ceiling( MathF.Max( a.Y, MathF.Max( b.Y, c.Y ) ) ) );
            var area = ( b.X - a.X ) * ( c.Y - a.Y ) - ( c.X - a.X ) * ( b.Y - a.Y );
            if ( MathF.Abs( area ) < 1e-6f ) return;

            for ( var y = minY; y <= maxY; y++ )
            for ( var x = minX; x <= maxX; x++ )
            {
                var px = x + 0.5f; var py = y + 0.5f;
                var w0 = ( ( b.X - px ) * ( c.Y - py ) - ( c.X - px ) * ( b.Y - py ) ) / area;
                var w1 = ( ( c.X - px ) * ( a.Y - py ) - ( a.X - px ) * ( c.Y - py ) ) / area;
                var w2 = 1 - w0 - w1;
                if ( w0 < 0 || w1 < 0 || w2 < 0 ) continue;
                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                var idx = y * w + x;
                if ( z >= depth[idx] ) continue;
                depth[idx] = z;
                rgb[idx * 3] = r; rgb[idx * 3 + 1] = g; rgb[idx * 3 + 2] = bl;
            }
        }

        private static byte[] EncodePng( byte[] rgb, int width, int height )
        {
            using var output = new MemoryStream();
            output.Write( new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } );

            var header = new byte[13];
            WriteBE( header, 0, (uint)width ); WriteBE( header, 4, (uint)height );
            header[8] = 8; header[9] = 2; // 8-bit RGB
            WriteChunk( output, "IHDR", header );

            using var raw = new MemoryStream();
            for ( var y = 0; y < height; y++ )
            {
                raw.WriteByte( 0 );
                raw.Write( rgb, y * width * 3, width * 3 );
            }
            using var compressed = new MemoryStream();
            using ( var z = new ZLibStream( compressed, CompressionLevel.Fastest, leaveOpen: true ) )
                raw.WriteTo( z );
            WriteChunk( output, "IDAT", compressed.ToArray() );
            WriteChunk( output, "IEND", Array.Empty<byte>() );
            return output.ToArray();
        }

        private static void WriteChunk( Stream s, string type, byte[] data )
        {
            var len = new byte[4]; WriteBE( len, 0, (uint)data.Length ); s.Write( len );
            var typeBytes = System.Text.Encoding.ASCII.GetBytes( type );
            s.Write( typeBytes ); s.Write( data );
            var crc = Crc32( typeBytes, data );
            var crcBytes = new byte[4]; WriteBE( crcBytes, 0, crc ); s.Write( crcBytes );
        }

        private static void WriteBE( byte[] b, int o, uint v ) { b[o] = (byte)( v >> 24 ); b[o + 1] = (byte)( v >> 16 ); b[o + 2] = (byte)( v >> 8 ); b[o + 3] = (byte)v; }

        private static uint Crc32( byte[] a, byte[] b )
        {
            var crc = 0xFFFFFFFFu;
            foreach ( var arr in new[] { a, b } )
                foreach ( var x in arr )
                {
                    crc ^= x;
                    for ( var k = 0; k < 8; k++ ) crc = ( crc & 1 ) != 0 ? ( crc >> 1 ) ^ 0xEDB88320u : crc >> 1;
                }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
