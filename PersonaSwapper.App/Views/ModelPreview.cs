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
    /// each frame and draws them with a simple textured, lit shader.
    /// Navigation: drag to orbit; right/middle-drag or Shift+drag to pan; scroll or pinch to zoom; double-click (or F)
    /// to fit the whole animation in view; R to reset the view.
    /// </summary>
    public sealed class ModelPreview : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
    {
        /// <summary>
        /// The GL picture isn't something Avalonia's hit testing can see (the control paints no background), so without
        /// this every click, drag and scroll passed straight through to the panel behind it.
        /// </summary>
        public bool HitTest( Point point ) => new Rect( Bounds.Size ).Contains( point );

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

        private const float FieldOfView = 40 * MathF.PI / 180;
        private const float DefaultYaw = 25, DefaultPitch = -12;
        private float mYaw = DefaultYaw, mPitch = DefaultPitch, mZoom = 1;
        private Point? mLastPointer;
        private bool mPanning;
        private Vector3 mCenter;       // framing center (from the model's bounds)
        private Vector3 mPan;          // user pan offset from the framing center
        private float mRadius = 1;
        private float mLastDistance = 1;
        private volatile bool mFitRequested, mResetRequested;
        private bool mUserNavigated; // until the user moves the camera, switching animations re-frames automatically
        private bool mFrameOnNextRender;
        private Vector3 mFollowOffset;   // framing center relative to the live bounds center (follow mode)
        private Vector3 mFollowCenter;   // smoothed live center
        private bool mFollowSnap = true;

        /// <summary>
        /// Keep the camera on the model as the animation moves it (battle animations can carry a model 30 times its
        /// own size away). Off: the camera stays put, framed on the whole animation.
        /// </summary>
        public bool Follow
        {
            get => mFollow;
            set { mFollow = value; mFitRequested = true; RequestNextFrameRendering(); }
        }
        private bool mFollow = true;

        public ModelPreview()
        {
            Focusable = true;
            // macOS trackpad pinch.
            AddHandler( Gestures.PointerTouchPadGestureMagnifyEvent, OnMagnify );
        }

        /// <summary>Camera state, for logs and tests.</summary>
        public string CameraDescription => $"yaw {mYaw:0.#}, pitch {mPitch:0.#}, zoom {mZoom:0.###}, pan {mPan}";

        /// <summary>Back to the default angle, framed on the whole animation.</summary>
        public void ResetView()
        {
            mYaw = DefaultYaw;
            mPitch = DefaultPitch;
            mZoom = 1;
            mPan = Vector3.Zero;
            mResetRequested = true; // re-framed at the next frame, where the model is posed
            RequestNextFrameRendering();
        }

        /// <summary>Re-centre and zoom so the model stays in view for the whole animation (keeps the angle).</summary>
        public void FitToPose()
        {
            mFitRequested = true;
            RequestNextFrameRendering();
        }

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

            if ( mResetRequested || mFitRequested || mFrameOnNextRender )
            {
                if ( mResetRequested || mFitRequested ) mUserNavigated = false;
                mResetRequested = mFitRequested = mFrameOnNextRender = false;
                FrameAnimation();
                mPan = Vector3.Zero;
                mZoom = 1;
            }

            // Camera orbiting the framing center plus the user's pan.
            var distance = mRadius * 2.6f * mZoom;
            mLastDistance = distance;
            Vector3 target;
            if ( mFollow )
            {
                var live = ( mSkinned.BoundsMin + mSkinned.BoundsMax ) / 2 + mFollowOffset;
                mFollowCenter = mFollowSnap ? live : Vector3.Lerp( mFollowCenter, live, 1 - MathF.Exp( -(float)delta * 12 ) );
                mFollowSnap = false;
                target = mFollowCenter + mPan;
            }
            else
                target = mCenter + mPan;
            var orbit = Orbit();
            var eye = target + Vector3.Transform( new Vector3( 0, 0, distance ), orbit );
            var view = Matrix4x4.CreateLookAt( eye, target, Vector3.UnitY );
            // Generous depth range so big or far-off parts aren't clipped when zoomed in or out.
            var near = Math.Max( distance * 0.005f, mRadius * 0.0005f );
            var far = distance + mRadius * 40;
            var projection = Matrix4x4.CreatePerspectiveFieldOfView( FieldOfView, (float)width / height, near, far );
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
                if ( !mUserNavigated ) mFrameOnNextRender = true;
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
                FrameAnimation();
                mPan = Vector3.Zero;
                mZoom = 1;
                mUserNavigated = false;
                Log.Info( $"Bounds {mSkinned.BoundsMin} - {mSkinned.BoundsMax}; framing center {mCenter}, radius {mRadius:0.##}" );
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

        // ---- Navigation ----

        /// <summary>
        /// Frames the model over the whole current animation (sampled), so it stays in view while it plays even when
        /// the motion moves it far from its resting pose. Stray vertices are ignored (see <see cref="SkinnedModel.FramingBox"/>).
        /// </summary>
        private void FrameAnimation()
        {
            if ( mSkinned == null ) return;
            var min = new Vector3( float.MaxValue );
            var max = new Vector3( float.MinValue );
            var largest = 0f;
            var offset = Vector3.Zero;
            var samples = mAnimation != null && mAnimation.Duration > 0 ? 8 : 0;
            for ( var k = 0; k <= samples; k++ )
            {
                mSkinned.Update( mAnimation, samples == 0 ? 0 : mAnimation!.Duration * k / samples );
                var (a, b) = mSkinned.FramingBox();
                min = Vector3.Min( min, a );
                max = Vector3.Max( max, b );
                largest = Math.Max( largest, ( b - a ).Length() / 2 );
                offset += ( a + b ) / 2 - ( mSkinned.BoundsMin + mSkinned.BoundsMax ) / 2;
            }
            mSkinned.Update( mAnimation, (float)mAnimationTime );
            if ( mFollow )
            {
                // Size from the biggest pose; position follows the model every frame.
                mFollowOffset = offset / ( samples + 1 );
                mRadius = Math.Max( 1e-3f, largest );
                mFollowSnap = true;
            }
            else
            {
                mCenter = ( min + max ) / 2;
                mRadius = Math.Max( 1e-3f, ( max - min ).Length() / 2 );
            }
        }

        private Matrix4x4 Orbit() => Matrix4x4.CreateRotationX( mPitch * MathF.PI / 180 ) * Matrix4x4.CreateRotationY( mYaw * MathF.PI / 180 );

        protected override void OnPointerPressed( PointerPressedEventArgs e )
        {
            base.OnPointerPressed( e );
            Focus();
            var point = e.GetCurrentPoint( this );
            if ( e.ClickCount == 2 && point.Properties.IsLeftButtonPressed )
            {
                FitToPose();
                return;
            }
            mPanning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed ||
                       e.KeyModifiers.HasFlag( KeyModifiers.Shift ) || e.KeyModifiers.HasFlag( KeyModifiers.Alt );
            mLastPointer = e.GetPosition( this );
            e.Pointer.Capture( this );
            e.Handled = true;
        }

        protected override void OnPointerMoved( PointerEventArgs e )
        {
            base.OnPointerMoved( e );
            if ( mLastPointer is not { } last ) return;
            var p = e.GetPosition( this );
            var dx = (float)( p.X - last.X );
            var dy = (float)( p.Y - last.Y );
            mUserNavigated = true;
            if ( mPanning )
                Pan( dx, dy );
            else
            {
                mYaw -= dx * 0.5f;
                mPitch = Math.Clamp( mPitch - dy * 0.4f, -89, 89 );
            }
            mLastPointer = p;
            RequestNextFrameRendering();
        }

        /// <summary>Moves the target so the model follows the pointer (screen pixels).</summary>
        private void Pan( float dx, float dy )
        {
            var height = (float)Math.Max( 1, Bounds.Height );
            var worldPerPixel = 2 * mLastDistance * MathF.Tan( FieldOfView / 2 ) / height;
            var orbit = Orbit();
            var right = Vector3.TransformNormal( Vector3.UnitX, orbit );
            var up = Vector3.TransformNormal( Vector3.UnitY, orbit );
            mPan += ( -dx * right + dy * up ) * worldPerPixel;
        }

        protected override void OnPointerReleased( PointerReleasedEventArgs e )
        {
            base.OnPointerReleased( e );
            mLastPointer = null;
            mPanning = false;
            e.Pointer.Capture( null );
        }

        protected override void OnPointerWheelChanged( PointerWheelEventArgs e )
        {
            base.OnPointerWheelChanged( e );
            // Mouse wheels send whole notches; trackpads send small fractional steps, so scale by the amount.
            var amount = Math.Clamp( (float)e.Delta.Y, -5f, 5f );
            Zoom( MathF.Exp( -amount * 0.12f ) );
            e.Handled = true;
        }

        private void OnMagnify( object? sender, PointerDeltaEventArgs e )
        {
            Zoom( 1f / Math.Max( 0.2f, 1f + (float)e.Delta.X ) );
            e.Handled = true;
        }

        private void Zoom( float factor )
        {
            mZoom = Math.Clamp( mZoom * factor, 0.02f, 40f );
            mUserNavigated = true;
            RequestNextFrameRendering();
        }

        protected override void OnKeyDown( KeyEventArgs e )
        {
            base.OnKeyDown( e );
            switch ( e.Key )
            {
                case Avalonia.Input.Key.F: FitToPose(); break;
                case Avalonia.Input.Key.R: ResetView(); break;
                case Avalonia.Input.Key.OemPlus: case Avalonia.Input.Key.Add: Zoom( 0.85f ); break;
                case Avalonia.Input.Key.OemMinus: case Avalonia.Input.Key.Subtract: Zoom( 1 / 0.85f ); break;
                case Avalonia.Input.Key.Left: mYaw += 10; RequestNextFrameRendering(); break;
                case Avalonia.Input.Key.Right: mYaw -= 10; RequestNextFrameRendering(); break;
                case Avalonia.Input.Key.Up: mPitch = Math.Clamp( mPitch + 10, -89, 89 ); RequestNextFrameRendering(); break;
                case Avalonia.Input.Key.Down: mPitch = Math.Clamp( mPitch - 10, -89, 89 ); RequestNextFrameRendering(); break;
                default: return;
            }
            e.Handled = true;
        }

        private sealed class AvaloniaBindingsContext : OpenTK.IBindingsContext
        {
            private readonly GlInterface mGl;
            public AvaloniaBindingsContext( GlInterface gl ) => mGl = gl;
            public IntPtr GetProcAddress( string procName ) => mGl.GetProcAddress( procName );
        }
    }
}
