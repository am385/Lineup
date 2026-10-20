using System.ComponentModel;
using System.Text.Json;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Lineup.WebPlayer.VirtualOutputHost;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lineup.WebPlayer.Tests;

/// <summary>Verifies dedicated Web Player startup failures remain actionable to browser clients.</summary>
public sealed class DedicatedWorkerStartupFailureTests
{
    /// <summary>Verifies structured broker failures preserve stable status and failure codes.</summary>
    [Theory]
    [InlineData(VirtualOutputFailureCode.ChannelNotFound, 404, "channel_not_found")]
    [InlineData(VirtualOutputFailureCode.ResourceLimit, 429, "resource_limit")]
    [InlineData(VirtualOutputFailureCode.CapabilityDenied, 403, "capability_denied")]
    [InlineData(VirtualOutputFailureCode.InvalidRequest, 400, "invalid_request")]
    [InlineData(VirtualOutputFailureCode.Unauthorized, 503, "broker_unauthorized")]
    [InlineData(VirtualOutputFailureCode.StaleGeneration, 503, "stale_worker_generation")]
    [InlineData(VirtualOutputFailureCode.Unavailable, 503, "broker_unavailable")]
    [InlineData(VirtualOutputFailureCode.SourceUnavailable, 503, "source_unavailable")]
    [InlineData(VirtualOutputFailureCode.SourceNoSignal, 503, "source_no_signal")]
    [InlineData(VirtualOutputFailureCode.SourceNoData, 503, "source_no_video_data")]
    [InlineData(VirtualOutputFailureCode.SourceCapacityExceeded, 503, "source_capacity_exceeded")]
    [InlineData(VirtualOutputFailureCode.SourceNotFound, 404, "source_not_found")]
    [InlineData(VirtualOutputFailureCode.SourceInvalid, 400, "source_invalid")]
    [InlineData(VirtualOutputFailureCode.ProtectedContent, 422, "protected_content")]
    public void Map_BrokerFailure_ReturnsStructuredResponse(VirtualOutputFailureCode failureCode, int expectedStatus, string expectedCode)
    {
        // Arrange
        var exception = new VirtualOutputClientException(failureCode, "Safe broker failure.");

        // Act
        var failure = DedicatedWorkerStartupFailureMapper.Map(exception, DedicatedWorkerStartupStage.Broker);

        // Assert
        Assert.Equal(expectedStatus, failure.StatusCode);
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal("Safe broker failure.", failure.Error);
        Assert.Equal("broker", failure.Stage);
    }

    /// <summary>Verifies a readiness timeout produces an actionable gateway-timeout response.</summary>
    [Fact]
    public void Map_ReadinessTimeout_ReturnsGatewayTimeout()
    {
        // Arrange
        var exception = new TimeoutException("Internal timeout detail.");

        // Act
        var failure = DedicatedWorkerStartupFailureMapper.Map(exception, DedicatedWorkerStartupStage.Readiness);

        // Assert
        Assert.Equal(504, failure.StatusCode);
        Assert.Equal("media_startup_timeout", failure.Code);
        Assert.Equal("The Web Player timed out while preparing the media presentation.", failure.Error);
        Assert.Equal("readiness", failure.Stage);
    }

    /// <summary>Verifies media-process launch failures do not expose operating-system details.</summary>
    [Fact]
    public void Map_ProcessUnavailable_ReturnsSafeBadGatewayResponse()
    {
        // Arrange
        var exception = new Win32Exception(2, "Sensitive executable path.");

        // Act
        var failure = DedicatedWorkerStartupFailureMapper.Map(exception, DedicatedWorkerStartupStage.Process);

        // Assert
        Assert.Equal(502, failure.StatusCode);
        Assert.Equal("media_process_unavailable", failure.Code);
        Assert.Equal("The Web Player could not start its media processor.", failure.Error);
        Assert.DoesNotContain("Sensitive", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>Verifies invalid browser compatibility settings produce a client error without exposing validation internals.</summary>
    [Fact]
    public void Map_InvalidStreamRequest_ReturnsSafeBadRequestResponse()
    {
        // Arrange
        var exception = new ArgumentException("Detailed compatibility validation failure.");

        // Act
        var failure = DedicatedWorkerStartupFailureMapper.Map(exception, DedicatedWorkerStartupStage.Planning);

        // Assert
        Assert.Equal(400, failure.StatusCode);
        Assert.Equal("invalid_stream_request", failure.Code);
        Assert.Equal("The Web Player stream request contains unsupported or invalid settings.", failure.Error);
        Assert.DoesNotContain("Detailed", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>Verifies FFmpeg readiness failures do not expose captured process output.</summary>
    [Fact]
    public void Map_ProcessExit_ReturnsSafeBadGatewayResponse()
    {
        // Arrange
        var exception = new InvalidOperationException("ffmpeg stderr containing an internal URL");

        // Act
        var failure = DedicatedWorkerStartupFailureMapper.Map(exception, DedicatedWorkerStartupStage.Readiness);

        // Assert
        Assert.Equal(502, failure.StatusCode);
        Assert.Equal("media_startup_failed", failure.Code);
        Assert.Equal("The Web Player media processor exited before the presentation was ready.", failure.Error);
        Assert.DoesNotContain("internal URL", failure.Error, StringComparison.Ordinal);
    }

    /// <summary>Verifies the JSON contract uses the fields consumed by the Watch page.</summary>
    [Fact]
    public void Serialize_Failure_UsesBrowserErrorContract()
    {
        // Arrange
        var failure = DedicatedWorkerStartupFailureMapper.Map(
            new TimeoutException(),
            DedicatedWorkerStartupStage.Readiness);

        // Act
#pragma warning disable CA1869 // Cache and reuse 'JsonSerializerOptions' instances
        var json = JsonSerializer.Serialize(failure, new JsonSerializerOptions(JsonSerializerDefaults.Web));
#pragma warning restore CA1869 // Cache and reuse 'JsonSerializerOptions' instances

        // Assert
        using var document = JsonDocument.Parse(json);
        Assert.Equal("media_startup_timeout", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("The Web Player timed out while preparing the media presentation.", document.RootElement.GetProperty("error").GetString());
        Assert.Equal("readiness", document.RootElement.GetProperty("stage").GetString());
    }

    /// <summary>Verifies ASP.NET Core CORS applies the allowed-origin header to worker error responses.</summary>
    [Fact]
    public void ApplyCorsPolicy_ErrorResponse_IncludesAllowedOrigin()
    {
        // Arrange
        var policy = DedicatedWorkerCorsPolicy.Create();
        var options = new CorsOptions();
        options.AddDefaultPolicy(policy);
        var service = new CorsService(Options.Create(options), NullLoggerFactory.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers.Origin = "http://lineup.test";
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        // Act
        var result = service.EvaluatePolicy(context, policy);
        service.ApplyResult(result, context.Response);

        // Assert
        Assert.Equal("*", context.Response.Headers.AccessControlAllowOrigin);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }
}
