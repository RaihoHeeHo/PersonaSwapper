using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GFDLibrary;
using GFDLibrary.Animations;
using PersonaSwapper.Core.Game;
using PersonaSwapper.Core.Swap;

namespace PersonaSwapper.App.ViewModels
{
    /// <summary>What the 3D preview should draw: a model and (optionally) an animation playing on it.</summary>
    public sealed partial class PreviewViewModel : ObservableObject, IDisposable
    {
        [ObservableProperty] private ModelPack? _model;
        [ObservableProperty] private Animation? _animation;
        [ObservableProperty] private string _message = "Pick a source model to preview it.";
        [ObservableProperty] private bool _isPlaying = true;
        [ObservableProperty] private double _speed = 1.0;

        /// <summary>Raised when the model or animation changes so the view can rebuild GPU data / restart playback.</summary>
        public event EventHandler? SceneChanged;

        public string? ModelPath { get; private set; }

        public void Show( GameData game, string modelPath, Animation? animation )
        {
            try
            {
                if ( !string.Equals( ModelPath, modelPath, StringComparison.OrdinalIgnoreCase ) )
                {
                    Model = AnimationCache.Shared.Model( game.Archive, modelPath );
                    ModelPath = modelPath;
                }
                Animation = animation;
                Message = animation == null ? "Bind pose (no animation)" : $"Playing {animation.Duration:0.##}s animation";
            }
            catch ( Exception e )
            {
                Model = null;
                ModelPath = null;
                Animation = null;
                Message = $"Can't load {modelPath}: {e.Message}";
            }
            SceneChanged?.Invoke( this, EventArgs.Empty );
        }

        [RelayCommand]
        private void TogglePlay() => IsPlaying = !IsPlaying;

        public void Dispose() => SceneChanged = null;
    }
}
