using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using GFDLibrary;
using GFDLibrary.Animations;
using OpenTK.Graphics.OpenGL4;
using PersonaSwapper.App.ViewModels;
using PersonaSwapper.Core.Diagnostics;
using PersonaSwapper.Core.Preview;

namespace PersonaSwapper.App.Views
{
    /// <summary>
    /// OpenGL viewport that plays the selected animation on the source model.
    /// Skinning happens on the CPU (<see cref="SkinnedModel"/>); this control uploads the posed vertices
    /// each frame and draws them with a simple textured, lit shader. Drag to orbit, scroll to zoom.
    /// </summary>
    public sealed class ModelPreview : OpenGlControlBase
    {
        /// <summary>How a mesh's material uses texture alpha (P5R stores other data in alpha on most opaque materials).</summary>
        private enum AlphaUse
        {
            Ignore = 0,   // opaque: alpha is not transparency
            Clip = 1,     // alpha test against the material's threshold
            Blend = 2,    // alpha blending
            Additive = 3, // glow/effect meshes: added on top
        }

        private sealed class GpuMesh
        {
            public SkinnedMesh Mesh = null!;
            public int Vao, PositionBuffer, NormalBuffer, UvBuffer, IndexBuffer, IndexCount;
            public string? TextureName;
            public AlphaUse Alpha;
            public float AlphaReference;
            public bool DoubleSided = true;
        }

        private readonly List<GpuMesh> mMeshes = new();
        private readonly Dictionary<string, int> mTextures = new();
        private readonly ConcurrentDictionary<string, DecodedTexture?> mDecoded = new();
        private readonly Stopwatch mClock = Stopwatch.StartNew();

        private PreviewViewModel? mViewModel;
        private SkinnedModel? mSkinned;
        private ModelPack? mBuiltFor;
        private Animation? mAnimation;
        private volatile bool mSceneDirty = true;
        private double mAnimationTime;
        private double mLastFrame;

        private int mProgram, mMvpLocation, mViewLocation, mTextureLocation, mHasTextureLocation, mAlphaModeLocation, mAlphaRefLocation;
        private bool mGlReady;
        private string? mGlError;

        private float mYaw = 25, mPitch = -12, mZoom = 1;
        private Point? mLastPointer;
        private Vector3 mCenter;
        private float mRadius = 1;

        protected override void OnDataContextChanged( EventArgs e )
        {
            base.OnDataContextChanged( e );
            if ( mViewModel != null ) mViewModel.SceneChanged -= OnSceneChanged;
            mViewModel = DataContext as PreviewViewModel;
            if ( mViewModel != null ) mViewModel.SceneChanged += OnSceneChanged;
            OnSceneChanged( this, EventArgs.Empty );
        }

        private void OnSceneChanged( object? sender, EventArgs e )
        {
            mSceneDirty = true;
            mAnimationTime = 0;
            RequestNextFrameRendering();
        }

        protected override void OnOpenGlInit( GlInterface gl )
        {
            try
            {
                Log.Step( $"OpenGL init: context {gl.ContextInfo.Version.Type} {gl.ContextInfo.Version.Major}.{gl.ContextInfo.Version.Minor}" );
                GL.LoadBindings( new AvaloniaBindingsContext( gl ) );
                Log.Info( $"GL_VERSION={GL.GetString( StringName.Version )}, GL_RENDERER={GL.GetString( StringName.Renderer )}, " +
                          $"GL_VENDOR={GL.GetString( StringName.Vendor )}, GLSL={GL.GetString( StringName.ShadingLanguageVersion )}" );
                var es = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
                mProgram = BuildProgram( es );
                Log.Info( "Preview shaders compiled." );
                mMvpLocation = GL.GetUniformLocation( mProgram, "uMvp" );
                mViewLocation = GL.GetUniformLocation( mProgram, "uView" );
                mTextureLocation = GL.GetUniformLocation( mProgram, "uTexture" );
                mHasTextureLocation = GL.GetUniformLocation( mProgram, "uHasTexture" );
                mAlphaModeLocation = GL.GetUniformLocation( mProgram, "uAlphaMode" );
                mAlphaRefLocation = GL.GetUniformLocation( mProgram, "uAlphaRef" );
                mGlReady = true;
            }
            catch ( Exception ex )
            {
                mGlError = ex.Message;
                Log.Error( "OpenGL init failed", ex );
                if ( mViewModel != null ) mViewModel.Message = $"3D preview unavailable: {ex.Message}";
            }
        }

        protected override void OnOpenGlDeinit( GlInterface gl )
        {
            if ( !mGlReady ) return;
            DeleteMeshes();
            foreach ( var texture in mTextures.Values ) GL.DeleteTexture( texture );
            mTextures.Clear();
            GL.DeleteProgram( mProgram );
            mGlReady = false;
        }

        protected override void OnOpenGlRender( GlInterface gl, int fb )
        {
            if ( !mGlReady ) return;
            try
            {
                Render( fb );
            }
            catch ( Exception ex )
            {
                // A model that fails to draw is dropped from the preview instead of taking the app down.
                Log.Error( $"Preview render failed for {mViewModel?.ModelPath ?? "(no model)"} " +
                           $"(animation: {( mAnimation == null ? "none" : $"{mAnimation.Duration:0.##}s" )}, t={mAnimationTime:0.00})", ex );
                mSkinned = null;
                try { DeleteMeshes(); } catch ( Exception ) { }
                if ( mViewModel != null )
                    mViewModel.Message = $"Preview error: {ex.GetType().Name}: {ex.Message} (see log)";
            }
        }

        private void Render( int fb )
        {
            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            var width = Math.Max( 1, (int)( Bounds.Width * scaling ) );
            var height = Math.Max( 1, (int)( Bounds.Height * scaling ) );

            GL.BindFramebuffer( FramebufferTarget.Framebuffer, fb );
            GL.Viewport( 0, 0, width, height );
            GL.ClearColor( 0.071f, 0.039f, 0.043f, 1f );
            GL.ClearDepth( 1.0 );
            GL.Clear( ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit );

            if ( mSceneDirty )
            {
                RebuildScene();
                CheckGlError( "after building the scene" );
            }
            if ( UploadDecodedTextures() )
                CheckGlError( "after uploading textures" );

            if ( mSkinned == null )
                return;

            // Advance playback.
            var now = mClock.Elapsed.TotalSeconds;
            var delta = Math.Min( 0.1, now - mLastFrame );
            mLastFrame = now;
            var playing = mViewModel?.IsPlaying ?? false;
            if ( playing && mAnimation != null )
                mAnimationTime += delta * ( mViewModel?.Speed ?? 1.0 );

            if ( mFirstFrame ) Log.Step( "First frame: skinning" );
            mSkinned.Update( mAnimation, (float)mAnimationTime );
            if ( mFirstFrame ) Log.Step( "First frame: drawing" );

            // Camera framed on the bind-pose bounds, orbiting the center.
            var distance = mRadius * 2.6f * mZoom;
            var orbit = Matrix4x4.CreateRotationX( mPitch * MathF.PI / 180 ) * Matrix4x4.CreateRotationY( mYaw * MathF.PI / 180 );
            var eye = mCenter + Vector3.Transform( new Vector3( 0, 0, distance ), orbit );
            var view = Matrix4x4.CreateLookAt( eye, mCenter, Vector3.UnitY );
            var projection = Matrix4x4.CreatePerspectiveFieldOfView( 40 * MathF.PI / 180, (float)width / height,
                                                                     Math.Max( 0.01f, distance - mRadius * 3 ), distance + mRadius * 3 );
            var mvp = view * projection;

            GL.Enable( EnableCap.DepthTest );
            GL.DepthFunc( DepthFunction.Less );
            GL.Disable( EnableCap.CullFace );
            GL.UseProgram( mProgram );
            UniformMatrix( mMvpLocation, mvp );
            UniformMatrix( mViewLocation, view );
            GL.Uniform1( mTextureLocation, 0 );
            GL.ActiveTexture( TextureUnit.Texture0 );

            foreach ( var gpu in mMeshes )
            {
                GL.BindBuffer( BufferTarget.ArrayBuffer, gpu.PositionBuffer );
                GL.BufferSubData( BufferTarget.ArrayBuffer, IntPtr.Zero, gpu.Mesh.Positions.Length * 12, gpu.Mesh.Positions );
                GL.BindBuffer( BufferTarget.ArrayBuffer, gpu.NormalBuffer );
                GL.BufferSubData( BufferTarget.ArrayBuffer, IntPtr.Zero, gpu.Mesh.Normals.Length * 12, gpu.Mesh.Normals );
            }

            // Solid (and alpha-tested) meshes first, then blended/additive ones without writing depth.
            GL.Disable( EnableCap.Blend );
            GL.DepthMask( true );
            foreach ( var gpu in mMeshes )
                if ( gpu.Alpha is AlphaUse.Ignore or AlphaUse.Clip )
                    DrawMesh( gpu );

            GL.Enable( EnableCap.Blend );
            GL.DepthMask( false );
            foreach ( var gpu in mMeshes )
            {
                if ( gpu.Alpha == AlphaUse.Blend )
                {
                    GL.BlendFunc( BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha );
                    DrawMesh( gpu );
                }
                else if ( gpu.Alpha == AlphaUse.Additive )
                {
                    GL.BlendFunc( BlendingFactor.SrcAlpha, BlendingFactor.One );
                    DrawMesh( gpu );
                }
            }
            GL.DepthMask( true );
            GL.Disable( EnableCap.Blend );

            GL.BindVertexArray( 0 );
            GL.BindTexture( TextureTarget.Texture2D, 0 );
            GL.UseProgram( 0 );
            GL.Disable( EnableCap.DepthTest );

            if ( mFirstFrame )
            {
                CheckGlError( "after the first frame" );
                Log.Info( "First frame drawn." );
                mFirstFrame = false;
            }

            if ( ( playing && mAnimation != null ) || mDecoded.Count > mTextures.Count )
                RequestNextFrameRendering();
        }

        private bool mFirstFrame;

        private static void CheckGlError( string where )
        {
            for ( var i = 0; i < 8; i++ )
            {
                var error = GL.GetError();
                if ( error == ErrorCode.NoError ) return;
                Log.Warn( $"OpenGL error {error} {where}" );
            }
        }

        private void DrawMesh( GpuMesh gpu )
        {
            var hasTexture = gpu.TextureName != null && mTextures.TryGetValue( gpu.TextureName, out var texture ) && texture != 0;
            GL.Uniform1( mHasTextureLocation, hasTexture ? 1 : 0 );
            GL.BindTexture( TextureTarget.Texture2D, hasTexture ? mTextures[gpu.TextureName!] : 0 );
            GL.Uniform1( mAlphaModeLocation, (int)gpu.Alpha );
            GL.Uniform1( mAlphaRefLocation, gpu.AlphaReference );
            GL.BindVertexArray( gpu.Vao );
            GL.DrawElements( PrimitiveType.Triangles, gpu.IndexCount, DrawElementsType.UnsignedInt, IntPtr.Zero );
        }

        private void RebuildScene()
        {
            mSceneDirty = false;
            var pack = mViewModel?.Model;
            mAnimation = mViewModel?.Animation;
            var modelPath = mViewModel?.ModelPath ?? "(none)";

            if ( ReferenceEquals( pack, mBuiltFor ) && mSkinned != null )
            {
                Log.Info( $"Preview animation changed on {modelPath}: {( mAnimation == null ? "bind pose" : $"{mAnimation.Duration:0.##}s, {mAnimation.Controllers?.Count ?? 0} controllers" )}" );
                mFirstFrame = true;
                return; // same model, only the animation changed
            }
            Log.Step( $"Preview loading {modelPath}" );
            mFirstFrame = true;

            DeleteMeshes();
            foreach ( var texture in mTextures.Values ) GL.DeleteTexture( texture );
            mTextures.Clear();
            mDecoded.Clear();
            mSkinned = null;
            mBuiltFor = pack;
            if ( pack?.Model == null )
                return;

            try
            {
                Log.Step( $"Building skinned model ({pack.Model.Nodes.Count()} nodes, {pack.Model.Bones?.Count ?? 0} bones, " +
                          $"{pack.Materials?.Count ?? 0} materials, {pack.Textures?.Count ?? 0} textures)" );
                mSkinned = new SkinnedModel( pack );
                foreach ( var issue in mSkinned.Issues )
                    Log.Warn( $"{modelPath}: {issue}" );
                Log.Step( $"Posing bind pose ({mSkinned.Meshes.Count} meshes, {mSkinned.Meshes.Sum( m => m.Positions.Length )} vertices)" );
                mSkinned.Update( null, 0 );
                mCenter = ( mSkinned.BoundsMin + mSkinned.BoundsMax ) / 2;
                mRadius = Math.Max( 1e-3f, ( mSkinned.BoundsMax - mSkinned.BoundsMin ).Length() / 2 );
                Log.Info( $"Bounds {mSkinned.BoundsMin} - {mSkinned.BoundsMax}" );
                if ( mSkinned.NonFiniteVertices > 0 )
                    Log.Warn( $"{modelPath}: {mSkinned.NonFiniteVertices} vertices are NaN/infinite in the bind pose (bad weights or matrices); hidden" );

                for ( var i = 0; i < mSkinned.Meshes.Count; i++ )
                {
                    var mesh = mSkinned.Meshes[i];
                    Log.Step( $"Uploading mesh {i}: {mesh.Positions.Length} vertices, {mesh.Indices.Length / 3} triangles, material '{mesh.MaterialName}'" );
                    mMeshes.Add( CreateGpuMesh( mesh, pack ) );
                }
                Log.Info( $"Preview ready: {mMeshes.Count} meshes uploaded for {modelPath}" );

                // Decode textures off the render thread; they're uploaded as they become ready.
                if ( pack.Textures != null )
                {
                    var textures = new List<(string, GFDLibrary.Textures.Texture)>();
                    foreach ( var (name, texture) in pack.Textures ) textures.Add( (name, texture) );
                    Task.Run( () =>
                    {
                        foreach ( var (name, texture) in textures )
                        {
                            if ( !ReferenceEquals( mBuiltFor, pack ) ) return;
                            try
                            {
                                var decoded = TextureDecoding.TryDecode( texture );
                                Log.Info( decoded == null
                                    ? $"Texture '{name}' ({texture.Format}, {texture.Data?.Length ?? 0} bytes): not decodable, drawn untextured"
                                    : $"Texture '{name}' decoded {decoded.Width}x{decoded.Height}" );
                                if ( !ReferenceEquals( mBuiltFor, pack ) ) return;
                                mDecoded[name] = decoded;
                            }
                            catch ( Exception ex )
                            {
                                Log.Error( $"Texture '{name}' decode crashed", ex );
                                mDecoded[name] = null;
                            }
                            Avalonia.Threading.Dispatcher.UIThread.Post( RequestNextFrameRendering );
                        }
                    } );
                }
            }
            catch ( Exception ex )
            {
                Log.Error( $"Preview couldn't build {modelPath}", ex );
                mSkinned = null;
                if ( mViewModel != null ) mViewModel.Message = $"Can't draw this model: {ex.Message} (see log)";
            }
        }

        private GpuMesh CreateGpuMesh( SkinnedMesh mesh, ModelPack pack )
        {
            var gpu = new GpuMesh { Mesh = mesh, IndexCount = mesh.Indices.Length };
            gpu.Vao = GL.GenVertexArray();
            GL.BindVertexArray( gpu.Vao );

            gpu.PositionBuffer = GL.GenBuffer();
            GL.BindBuffer( BufferTarget.ArrayBuffer, gpu.PositionBuffer );
            GL.BufferData( BufferTarget.ArrayBuffer, mesh.Positions.Length * 12, mesh.Positions, BufferUsageHint.DynamicDraw );
            GL.EnableVertexAttribArray( 0 );
            GL.VertexAttribPointer( 0, 3, VertexAttribPointerType.Float, false, 12, 0 );

            gpu.NormalBuffer = GL.GenBuffer();
            GL.BindBuffer( BufferTarget.ArrayBuffer, gpu.NormalBuffer );
            GL.BufferData( BufferTarget.ArrayBuffer, mesh.Normals.Length * 12, mesh.Normals, BufferUsageHint.DynamicDraw );
            GL.EnableVertexAttribArray( 1 );
            GL.VertexAttribPointer( 1, 3, VertexAttribPointerType.Float, false, 12, 0 );

            var uvs = mesh.TexCoords != null && mesh.TexCoords.Length == mesh.Positions.Length
                ? mesh.TexCoords
                : new Vector2[mesh.Positions.Length];
            gpu.UvBuffer = GL.GenBuffer();
            GL.BindBuffer( BufferTarget.ArrayBuffer, gpu.UvBuffer );
            GL.BufferData( BufferTarget.ArrayBuffer, uvs.Length * 8, uvs, BufferUsageHint.StaticDraw );
            GL.EnableVertexAttribArray( 2 );
            GL.VertexAttribPointer( 2, 2, VertexAttribPointerType.Float, false, 8, 0 );

            // Never hand the GPU an index past the vertex buffer: that reads out of bounds and can kill the process.
            var indices = mesh.ValidIndices;
            gpu.IndexCount = indices.Length;
            gpu.IndexBuffer = GL.GenBuffer();
            GL.BindBuffer( BufferTarget.ElementArrayBuffer, gpu.IndexBuffer );
            GL.BufferData( BufferTarget.ElementArrayBuffer, indices.Length * 4, indices, BufferUsageHint.StaticDraw );

            GL.BindVertexArray( 0 );

            if ( mesh.MaterialName != null && pack.Materials != null && pack.Materials.TryGetValue( mesh.MaterialName, out var material ) )
            {
                gpu.TextureName = material.DiffuseMap?.Name;
                var flags = material.Flags;
                if ( material.DrawMethod is GFDLibrary.Materials.MaterialDrawMethod.Add )
                    gpu.Alpha = AlphaUse.Additive;
                else if ( material.DrawMethod != GFDLibrary.Materials.MaterialDrawMethod.Opaque )
                    gpu.Alpha = AlphaUse.Blend;
                else if ( ( flags & GFDLibrary.Materials.MaterialFlags.AlphaTest ) != 0 )
                    gpu.Alpha = AlphaUse.Clip;
                else
                    gpu.Alpha = AlphaUse.Ignore;
                gpu.AlphaReference = material.AlphaClip > 0 ? Math.Clamp( material.AlphaClip / 255f, 0f, 1f ) : 0.5f;
            }
            return gpu;
        }

        /// <returns>True if anything was uploaded.</returns>
        private bool UploadDecodedTextures()
        {
            var uploaded = false;
            foreach ( var (name, decoded) in mDecoded )
            {
                if ( mTextures.ContainsKey( name ) ) continue;
                if ( decoded == null ) { mTextures[name] = 0; continue; }
                if ( decoded.Rgba.Length < decoded.Width * decoded.Height * 4 )
                {
                    Log.Warn( $"Texture '{name}' has {decoded.Rgba.Length} bytes for {decoded.Width}x{decoded.Height}; skipped" );
                    mTextures[name] = 0;
                    continue;
                }
                uploaded = true;

                var texture = GL.GenTexture();
                GL.BindTexture( TextureTarget.Texture2D, texture );
                GL.TexImage2D( TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, decoded.Width, decoded.Height, 0,
                               PixelFormat.Rgba, PixelType.UnsignedByte, decoded.Rgba );
                GL.GenerateMipmap( GenerateMipmapTarget.Texture2D );
                GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear );
                GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear );
                GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat );
                GL.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat );
                GL.BindTexture( TextureTarget.Texture2D, 0 );
                mTextures[name] = texture;
            }
            return uploaded;
        }

        private void DeleteMeshes()
        {
            foreach ( var gpu in mMeshes )
            {
                GL.DeleteBuffer( gpu.PositionBuffer );
                GL.DeleteBuffer( gpu.NormalBuffer );
                GL.DeleteBuffer( gpu.UvBuffer );
                GL.DeleteBuffer( gpu.IndexBuffer );
                GL.DeleteVertexArray( gpu.Vao );
            }
            mMeshes.Clear();
        }

        private static void UniformMatrix( int location, Matrix4x4 m )
        {
            // System.Numerics is row-major with row vectors; uploaded as-is, GLSL sees the transpose,
            // so "M * v" in the shader matches "v * M" here.
            Span<float> values = stackalloc float[16]
            {
                m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
            };
            GL.UniformMatrix4( location, 1, false, ref values[0] );
        }

        private static int BuildProgram( bool es )
        {
            var header = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
            var vertex = header + @"
in vec3 aPosition;
in vec3 aNormal;
in vec2 aUv;
uniform mat4 uMvp;
uniform mat4 uView;
out vec3 vNormal;
out vec2 vUv;
void main()
{
    gl_Position = uMvp * vec4(aPosition, 1.0);
    vNormal = (uView * vec4(aNormal, 0.0)).xyz;
    vUv = aUv;
}";
            var fragment = header + @"
in vec3 vNormal;
in vec2 vUv;
uniform sampler2D uTexture;
uniform int uHasTexture;
uniform int uAlphaMode;   // 0 ignore alpha, 1 clip, 2 blend, 3 additive
uniform float uAlphaRef;
out vec4 fragColor;
void main()
{
    vec4 albedo = uHasTexture != 0 ? texture(uTexture, vUv) : vec4(0.86, 0.80, 0.72, 1.0);
    if (uAlphaMode == 1 && albedo.a < uAlphaRef) discard;
    float alpha = (uAlphaMode >= 2) ? albedo.a : 1.0;
    vec3 n = normalize(vNormal);
    float light = abs(dot(n, normalize(vec3(0.35, 0.55, 0.75))));
    vec3 color = uAlphaMode == 3 ? albedo.rgb : albedo.rgb * (0.45 + 0.6 * light);
    fragColor = vec4(color, alpha);
}";
            var vs = Compile( ShaderType.VertexShader, vertex );
            var fs = Compile( ShaderType.FragmentShader, fragment );
            var program = GL.CreateProgram();
            GL.AttachShader( program, vs );
            GL.AttachShader( program, fs );
            GL.BindAttribLocation( program, 0, "aPosition" );
            GL.BindAttribLocation( program, 1, "aNormal" );
            GL.BindAttribLocation( program, 2, "aUv" );
            GL.LinkProgram( program );
            GL.GetProgram( program, GetProgramParameterName.LinkStatus, out var linked );
            if ( linked == 0 )
                throw new InvalidOperationException( "Shader link failed: " + GL.GetProgramInfoLog( program ) );
            GL.DeleteShader( vs );
            GL.DeleteShader( fs );
            return program;
        }

        private static int Compile( ShaderType type, string source )
        {
            var shader = GL.CreateShader( type );
            GL.ShaderSource( shader, source );
            GL.CompileShader( shader );
            GL.GetShader( shader, ShaderParameter.CompileStatus, out var ok );
            if ( ok == 0 )
                throw new InvalidOperationException( $"{type} compile failed: {GL.GetShaderInfoLog( shader )}" );
            return shader;
        }

        // ---- Orbit / zoom ----

        protected override void OnPointerPressed( PointerPressedEventArgs e )
        {
            base.OnPointerPressed( e );
            mLastPointer = e.GetPosition( this );
            e.Pointer.Capture( this );
        }

        protected override void OnPointerMoved( PointerEventArgs e )
        {
            base.OnPointerMoved( e );
            if ( mLastPointer is not { } last ) return;
            var p = e.GetPosition( this );
            mYaw -= (float)( p.X - last.X ) * 0.5f;
            mPitch = Math.Clamp( mPitch - (float)( p.Y - last.Y ) * 0.4f, -85, 85 );
            mLastPointer = p;
            RequestNextFrameRendering();
        }

        protected override void OnPointerReleased( PointerReleasedEventArgs e )
        {
            base.OnPointerReleased( e );
            mLastPointer = null;
            e.Pointer.Capture( null );
        }

        protected override void OnPointerWheelChanged( PointerWheelEventArgs e )
        {
            base.OnPointerWheelChanged( e );
            mZoom = Math.Clamp( mZoom * ( e.Delta.Y > 0 ? 0.9f : 1.1f ), 0.2f, 5f );
            RequestNextFrameRendering();
        }

        private sealed class AvaloniaBindingsContext : OpenTK.IBindingsContext
        {
            private readonly GlInterface mGl;
            public AvaloniaBindingsContext( GlInterface gl ) => mGl = gl;
            public IntPtr GetProcAddress( string procName ) => mGl.GetProcAddress( procName );
        }
    }
}
