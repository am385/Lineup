using System.Globalization;

namespace Lineup.Core;

/// <summary>
/// Compares dotted channel numbers by their numeric components.
/// </summary>
public sealed class ChannelNumberComparer : IComparer<string?>
{
    /// <summary>
    /// Shared channel-number comparer.
    /// </summary>
    public static ChannelNumberComparer Instance { get; } = new();

    private ChannelNumberComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        var leftIsEmpty = string.IsNullOrWhiteSpace(left);
        var rightIsEmpty = string.IsNullOrWhiteSpace(right);
        if (leftIsEmpty && rightIsEmpty)
        {
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        if (leftIsEmpty)
        {
            return 1;
        }

        if (rightIsEmpty)
        {
            return -1;
        }

        var leftParts = left!.Split('.');
        var rightParts = right!.Split('.');
        if (TryParseParts(leftParts, out var leftNumbers) &&
            TryParseParts(rightParts, out var rightNumbers))
        {
            for (var index = 0; index < Math.Min(leftNumbers.Length, rightNumbers.Length); index++)
            {
                var comparison = leftNumbers[index].CompareTo(rightNumbers[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            var lengthComparison = leftNumbers.Length.CompareTo(rightNumbers.Length);
            if (lengthComparison != 0)
            {
                return lengthComparison;
            }
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static bool TryParseParts(string[] parts, out int[] numbers)
    {
        numbers = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        return true;
    }
}
