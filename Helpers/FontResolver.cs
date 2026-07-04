using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;

namespace MidFD.Helpers
{
    public static class FontResolver
    {
        private static readonly string[] MonospaceCandidates = new[]
        {
            "BIZ UDゴシック",
            "BIZ UDGothic",
            "ＭＳ ゴシック",
            "MS Gothic",
            "Consolas"
        };

        private static string? _resolvedMonospaceFont;

        public static string ResolveMonospaceFontFamily()
        {
            if (_resolvedMonospaceFont != null)
            {
                return _resolvedMonospaceFont;
            }

            try
            {
                using (var collection = new InstalledFontCollection())
                {
                    var installedNames = new HashSet<string>(
                        collection.Families.Select(f => f.Name),
                        StringComparer.OrdinalIgnoreCase
                    );

                    foreach (var candidate in MonospaceCandidates)
                    {
                        if (installedNames.Contains(candidate))
                        {
                            _resolvedMonospaceFont = candidate;
                            return candidate;
                        }
                    }
                }
            }
            catch
            {
                // Fallback if InstalledFontCollection fails
            }

            try
            {
                using (var family = FontFamily.GenericMonospace)
                {
                    _resolvedMonospaceFont = family.Name;
                    return family.Name;
                }
            }
            catch
            {
                _resolvedMonospaceFont = "Consolas";
                return "Consolas";
            }
        }

        public static bool IsFontInstalled(string? familyName, out string normalizedName)
        {
            normalizedName = string.Empty;
            if (string.IsNullOrWhiteSpace(familyName))
            {
                return false;
            }

            string trimmed = familyName.Trim();
            try
            {
                using (var collection = new InstalledFontCollection())
                {
                    foreach (var family in collection.Families)
                    {
                        if (string.Equals(family.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                        {
                            normalizedName = family.Name;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Fallback or ignore
            }
            return false;
        }

        public static Font CreateFont(string familyName, float size, FontStyle style = FontStyle.Regular)
        {
            if (IsFontInstalled(familyName, out string normalizedName))
            {
                try
                {
                    return new Font(normalizedName, size, style);
                }
                catch
                {
                    // Fallback
                }
            }

            string fallbackName = ResolveMonospaceFontFamily();
            try
            {
                return new Font(fallbackName, size, style);
            }
            catch
            {
                return new Font(FontFamily.GenericMonospace, size, style);
            }
        }
    }
}
