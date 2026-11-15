using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SabhaMujic.Contracts;

namespace SabhaMujic.Data.Internal;

/// <summary>Input validation, case folding and SQLite error classification shared by the repositories.</summary>
internal static partial class DataRules
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintForeignKey = 787;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteConstraintUnique = 2067;

    /// <summary>Case-folded key for case-insensitive uniqueness and search (works for non-ASCII text too).</summary>
    public static string FoldKey(string value) => value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    /// <summary>Trims and validates a required single-line text value; throws InvalidArgument when invalid.</summary>
    public static string RequireText(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw Invalid($"{field} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw Invalid($"{field} must be at most {maxLength} characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw Invalid($"{field} must not contain control characters.");
        }

        return trimmed;
    }

    /// <summary>Like <see cref="RequireText"/> but null/blank becomes null.</summary>
    public static string? OptionalText(string? value, string field, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireText(value, field, maxLength);

    /// <summary>Normalizes a token/code hash (64 hex chars, as produced by <c>Tokens.HashToken</c>) to uppercase.</summary>
    public static string RequireTokenHash(string? hash, string field = "Token hash")
    {
        if (hash is null || !TokenHash().IsMatch(hash))
        {
            throw Invalid($"{field} must be 64 hexadecimal characters.");
        }

        return hash.ToUpperInvariant();
    }

    public static void RequireFinite(double value, string field, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw Invalid($"{field} must be between {min} and {max}.");
        }
    }

    /// <summary>Escapes <c>\ % _</c> for use with <c>LIKE … ESCAPE '\'</c> so user input is matched literally.</summary>
    public static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    public static SabhaMujicException Invalid(string message) => new(ErrorCode.InvalidArgument, message);

    public static SabhaMujicException NotFound(string message) => new(ErrorCode.NotFound, message);

    public static SabhaMujicException Conflict(string message) => new(ErrorCode.Conflict, message);

    public static bool IsUniqueViolation(SqliteException ex) =>
        ex.SqliteErrorCode == SqliteConstraint &&
        ex.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey;

    public static bool IsForeignKeyViolation(SqliteException ex) =>
        ex.SqliteErrorCode == SqliteConstraint && ex.SqliteExtendedErrorCode == SqliteConstraintForeignKey;

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenHash();
}
