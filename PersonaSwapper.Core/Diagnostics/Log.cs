using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace PersonaSwapper.Core.Diagnostics
{
    /// <summary>
    /// Append-only diagnostic log. Every line is flushed to disk immediately, so if the app dies (even in native
    /// code, e.g. the GPU driver) the last lines show what it was doing. Location:
    /// macOS ~/Library/Logs/PersonaSwapper/persona-swapper.log; elsewhere &lt;app data&gt;/PersonaSwapper/logs.
    /// The previous session's log is kept as persona-swapper.previous.log.
    /// </summary>
    public static class Log
    {
        private static readonly object sLock = new();
        private static StreamWriter? sWriter;
        private static readonly Stopwatch sClock = Stopwatch.StartNew();

        public static string Folder { get; } = DefaultFolder();

        public static string FilePath => Path.Combine( Folder, "persona-swapper.log" );

        public static string PreviousFilePath => Path.Combine( Folder, "persona-swapper.previous.log" );

        /// <summary>Also echo lines to the console (Terminal when started from the .command launcher).</summary>
        public static bool EchoToConsole { get; set; } = true;

        public static void Start( string appName )
        {
            lock ( sLock )
            {
                if ( sWriter != null ) return;
                try
                {
                    Directory.CreateDirectory( Folder );
                    if ( File.Exists( FilePath ) )
                        File.Copy( FilePath, PreviousFilePath, overwrite: true );
                    sWriter = new StreamWriter( new FileStream( FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ), new UTF8Encoding( false ) )
                    {
                        AutoFlush = true,
                    };
                }
                catch ( Exception e )
                {
                    Console.Error.WriteLine( $"Couldn't open log file {FilePath}: {e.Message}" );
                }
            }

            Info( $"{appName} started. .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, " +
                  $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, log: {FilePath}" );
        }

        public static void Info( string message, [CallerMemberName] string caller = "" ) => Write( "INFO ", caller, message );

        public static void Warn( string message, [CallerMemberName] string caller = "" ) => Write( "WARN ", caller, message );

        public static void Error( string message, Exception? exception = null, [CallerMemberName] string caller = "" )
            => Write( "ERROR", caller, exception == null ? message : $"{message}\n{Describe( exception )}" );

        /// <summary>A step about to happen; if the app dies here this is the last line in the log.</summary>
        public static void Step( string message, [CallerMemberName] string caller = "" ) => Write( "STEP ", caller, message );

        /// <summary>Records an unhandled exception as a clearly marked crash report.</summary>
        public static void Crash( string source, Exception exception )
        {
            var report = new StringBuilder();
            report.AppendLine( "================ CRASH ================" );
            report.AppendLine( $"Source: {source}" );
            report.AppendLine( Describe( exception ) );
            report.Append( "=======================================" );
            Write( "CRASH", source, report.ToString() );
        }

        public static string Describe( Exception exception )
        {
            var sb = new StringBuilder();
            var depth = 0;
            for ( var e = exception; e != null && depth < 8; e = e.InnerException, depth++ )
            {
                if ( depth > 0 ) sb.AppendLine( "--- inner exception ---" );
                sb.AppendLine( $"{e.GetType().FullName}: {e.Message}" );
                sb.AppendLine( e.StackTrace ?? "(no stack trace)" );
            }
            if ( exception is AggregateException aggregate )
                foreach ( var inner in aggregate.InnerExceptions )
                    sb.AppendLine( $"--- aggregate member ---\n{inner.GetType().FullName}: {inner.Message}\n{inner.StackTrace}" );
            return sb.ToString().TrimEnd();
        }

        private static void Write( string level, string caller, string message )
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} +{sClock.Elapsed.TotalSeconds,8:0.000}s [{level}] [T{Thread.CurrentThread.ManagedThreadId,2}] {caller}: {message}";
            lock ( sLock )
            {
                try { sWriter?.WriteLine( line ); }
                catch ( Exception ) { /* logging must never throw */ }
            }
            if ( EchoToConsole )
            {
                try { Console.Error.WriteLine( line ); }
                catch ( Exception ) { }
            }
        }

        private static string DefaultFolder()
        {
            var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
            if ( OperatingSystem.IsMacOS() )
                return Path.Combine( home, "Library", "Logs", "PersonaSwapper" );
            return Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ), "PersonaSwapper", "logs" );
        }
    }
}
