using System.Runtime.CompilerServices;
using GFDLibrary.IO;

namespace PersonaSwapper.Core
{
    internal static class Startup
    {
        /// <summary>
        /// Runs when PersonaSwapper.Core loads. P5R PC names are UTF-8; byte-exact decoding makes every re-saved
        /// animation pack keep the game's exact bone names and hashes (Shift-JIS decoding corrupted names with
        /// Japanese characters, e.g. "Bip01 脊椎", so re-saved animations named bones the model doesn't have).
        /// </summary>
        [ModuleInitializer]
        internal static void Initialize() => GfdTextEncoding.UseByteExact();
    }
}
