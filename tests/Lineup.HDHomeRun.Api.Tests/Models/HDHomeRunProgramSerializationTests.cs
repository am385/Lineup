using System.Text.Json;
using Lineup.HDHomeRun.Api.Models;
using Xunit;

namespace Lineup.HDHomeRun.Api.Tests.Models;

/// <summary>
/// Verifies the serialized HDHomeRun programme contract.
/// </summary>
public sealed class HDHomeRunProgramSerializationTests
{
    /// <summary>
    /// Verifies internal supplemental and derived metadata are excluded from JSON.
    /// </summary>
    [Fact]
    public void Serialize_InternalXmltvMetadata_OmitsInternalProperties()
    {
        // Arrange
        var programme = new HDHomeRunProgram
        {
            Title = "Show",
            SupplementalXml = "<secret />",
            Metadata = new XmltvProgrammeMetadata
            {
                Ratings = [new XmltvRating("TV-14", "MPAA", [])]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(programme);

        // Assert
        Assert.DoesNotContain(nameof(HDHomeRunProgram.SupplementalXml), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(HDHomeRunProgram.Metadata), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TV-14", json, StringComparison.Ordinal);
    }
}
