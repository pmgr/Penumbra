using System.IO;

namespace Penumbra
{
    public static class StringPathExtensions
    {
        private static readonly char[] _invalid = Path.GetInvalidFileNameChars();

        public static string ReplaceInvalidPathSymbols( this string s, string replacement = "_" )
            => string.Join( replacement, s.Split( _invalid ) );

        public static string RemoveInvalidPathSymbols( this string s )
            => string.Concat( s.Split( _invalid ) );
    }
}