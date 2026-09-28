using System;
using System.Security.Cryptography;

namespace EditorTogether
{
    internal static class RoomCode
    {
        // 32 symbols keeps generation unbiased with a simple byte & 31 mask.
        // Ambiguous I/O/0/1 are intentionally omitted for codes that may be read aloud.
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const int DefaultLength = 6;

        public static string Generate()
        {
            var bytes = new byte[DefaultLength];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var chars = new char[DefaultLength];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = Alphabet[bytes[i] & 31];
            return new string(chars);
        }

        public static string Normalize(string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0) return string.Empty;

            // Generated codes are case-insensitive in the UI. Preserve legacy/custom
            // room names containing other symbols instead of silently rewriting them.
            string upper = trimmed.ToUpperInvariant();
            for (int i = 0; i < upper.Length; i++)
            {
                if (Alphabet.IndexOf(upper[i]) < 0)
                    return trimmed;
            }
            return upper;
        }
    }
}
