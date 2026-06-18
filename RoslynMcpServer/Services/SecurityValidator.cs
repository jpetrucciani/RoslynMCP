using System.Text.RegularExpressions;

namespace RoslynMcpServer.Services
{
    public class SecurityValidator
    {
        private readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".sln",
            ".csproj",
        };

        public bool ValidateSolutionPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (ContainsTraversalSegment(path))
                return false;

            string fullPath;
            try
            {
                if (!Path.IsPathFullyQualified(path))
                    return false;

                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            // Check file extension
            var extension = Path.GetExtension(fullPath);
            if (!_allowedExtensions.Contains(extension))
                return false;

            // Verify file exists and is accessible
            try
            {
                return File.Exists(fullPath);
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsTraversalSegment(string path)
        {
            return path.Split(new[] { '/', '\\' }).Any(segment => segment == "..");
        }

        public string SanitizeSearchPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "*";

            // Remove potentially dangerous characters
            return Regex.Replace(pattern, @"[^\w*?.]", "");
        }
    }
}
