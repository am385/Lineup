using Lineup.Extensibility.Streaming;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies provider-neutral acquisition failures use a stable public HTTP contract.</summary>
public sealed class StreamAcquisitionHttpMapperTests
{
    /// <summary>Verifies temporary source unavailability maps to retryable HTTP 503 semantics.</summary>
    [Fact]
    public void Map_UnavailableSource_ReturnsServiceUnavailable()
    {
        // Arrange
        var deviceId = PhysicalDeviceId.New();
        var candidate = new StreamAcquisitionCandidateFailure
        {
            DeviceId = deviceId,
            ChannelId = new SourceChannelId(deviceId, "2.1"),
            Category = PhysicalStreamFailureCategory.Unavailable,
            Code = "source_unavailable",
            Message = "The physical source could not be opened.",
            Retryable = true
        };
        var exception = StreamAcquisitionException.Create([candidate], [new IOException("diagnostic detail")]);

        // Act
        var failure = StreamAcquisitionHttpMapper.Map(exception);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failure.StatusCode);
        Assert.Equal("source_unavailable", failure.Code);
        Assert.Equal("Every equivalent physical source is currently unavailable.", failure.Error);
    }

    /// <summary>Verifies a stale source-channel route maps to the stable not-found contract.</summary>
    [Fact]
    public void Map_MissingSource_ReturnsNotFound()
    {
        // Arrange
        var deviceId = PhysicalDeviceId.New();
        var candidate = new StreamAcquisitionCandidateFailure
        {
            DeviceId = deviceId,
            ChannelId = new SourceChannelId(deviceId, "2.1"),
            Category = PhysicalStreamFailureCategory.NotFound,
            Code = "source_not_found",
            Message = "The physical source channel is no longer available.",
            Retryable = false
        };
        var exception = StreamAcquisitionException.Create([candidate], [new KeyNotFoundException()]);

        // Act
        var failure = StreamAcquisitionHttpMapper.Map(exception);

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, failure.StatusCode);
        Assert.Equal("source_not_found", failure.Code);
    }

    /// <summary>Verifies signal and media-data failures remain distinguishable while using retryable HTTP 503 semantics.</summary>
    [Theory]
    [InlineData(PhysicalStreamFailureCategory.NoSignal, "source_no_signal", "Every equivalent physical source is currently unable to lock a usable signal.")]
    [InlineData(PhysicalStreamFailureCategory.NoData, "source_no_video_data", "Every equivalent physical source is currently returning no video data.")]
    public void Map_ReceptionFailure_ReturnsDistinctServiceUnavailable(
        PhysicalStreamFailureCategory category,
        string code,
        string expectedError)
    {
        // Arrange
        var deviceId = PhysicalDeviceId.New();
        var candidate = new StreamAcquisitionCandidateFailure
        {
            DeviceId = deviceId,
            ChannelId = new SourceChannelId(deviceId, "2.1"),
            Category = category,
            Code = code,
            Message = "Safe adapter detail.",
            Retryable = true
        };
        var exception = StreamAcquisitionException.Create([candidate], [new IOException("diagnostic detail")]);

        // Act
        var failure = StreamAcquisitionHttpMapper.Map(exception);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failure.StatusCode);
        Assert.Equal(code, failure.Code);
        Assert.Equal(expectedError, failure.Error);
    }
}
