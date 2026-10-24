using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared.ImageFiles;
using GFDLibrary.Textures;

namespace PersonaSwapper.Core.Preview
{
    public sealed record DecodedTexture( int Width, int Height, byte[] Rgba );

    /// <summary>
    /// Cross-platform texture decoding for the preview. P5R PC textures are DDS (BCn); BCnEncoder decodes
    /// them in managed code, avoiding GFDLibrary's Windows-only System.Drawing path.
    /// </summary>
    public static class TextureDecoding
    {
        public static DecodedTexture? TryDecode( Texture texture )
        {
            if ( texture.Data == null || texture.Data.Length < 128 || texture.Format != TextureFormat.DDS )
                return null;
            if ( BinaryPrimitives.ReadUInt32LittleEndian( texture.Data ) != 0x20534444 ) // "DDS "
                return null;

            try
            {
                var height = (int)BinaryPrimitives.ReadUInt32LittleEndian( texture.Data.AsSpan( 12 ) );
                var width = (int)BinaryPrimitives.ReadUInt32LittleEndian( texture.Data.AsSpan( 16 ) );
                using var stream = new MemoryStream( texture.Data );
                var dds = DdsFile.Load( stream );
                var pixels = new BcDecoder().Decode( dds );
                if ( pixels == null || pixels.Length < width * height )
                    return null;
                var rgba = MemoryMarshal.AsBytes( pixels.AsSpan( 0, width * height ) ).ToArray();
                return new DecodedTexture( width, height, rgba );
            }
            catch ( Exception )
            {
                return null; // unsupported variant: the preview falls back to flat shading
            }
        }
    }
}
