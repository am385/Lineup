using System.Text.Json;
using Lineup.HDHomeRun.Device.Json;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests.Json;

/// <summary>
/// Represents bool to int or null converter tests.
/// </summary>
public class BoolToIntOrNullConverterTests
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoolToIntOrNullConverterTests"/> class.
    /// </summary>
    public BoolToIntOrNullConverterTests()
    {
        _options = new JsonSerializerOptions();
        _options.Converters.Add(new BoolToIntOrNullConverter());
    }

    #region Deserialization Tests

    /// <summary>
    /// Performs the read_returns true_when value is1 operation.
    /// </summary>
    [Fact]
    public void Read_ReturnsTrue_WhenValueIs1()
    {
        // Arrange
        var json = """{"Value":1}""";

        // Act
        var result = JsonSerializer.Deserialize<TestClass>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Value);
    }

    /// <summary>
    /// Performs the read_returns false_when value is0 operation.
    /// </summary>
    [Fact]
    public void Read_ReturnsFalse_WhenValueIs0()
    {
        // Arrange
        var json = """{"Value":0}""";

        // Act
        var result = JsonSerializer.Deserialize<TestClass>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Value);
    }

    /// <summary>
    /// Performs the read_returns false_when value is null operation.
    /// </summary>
    [Fact]
    public void Read_ReturnsFalse_WhenValueIsNull()
    {
        // Arrange
        var json = """{"Value":null}""";

        // Act
        var result = JsonSerializer.Deserialize<TestClass>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Value);
    }

    /// <summary>
    /// Performs the read_returns false_when value is non one number operation.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(-1)]
    public void Read_ReturnsFalse_WhenValueIsNonOneNumber(int value)
    {
        // Arrange
        var json = $$$"""{"Value":{{{value}}}}""";

        // Act
        var result = JsonSerializer.Deserialize<TestClass>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Value);
    }

    #endregion

    #region Serialization Tests

    /// <summary>
    /// Performs the write_outputs1_when value is true operation.
    /// </summary>
    [Fact]
    public void Write_Outputs1_WhenValueIsTrue()
    {
        // Arrange
        var obj = new TestClass { Value = true };

        // Act
        var json = JsonSerializer.Serialize(obj, _options);

        // Assert
        Assert.Equal("""{"Value":1}""", json);
    }

    /// <summary>
    /// Performs the write_outputs null_when value is false operation.
    /// </summary>
    [Fact]
    public void Write_OutputsNull_WhenValueIsFalse()
    {
        // Arrange
        var obj = new TestClass { Value = false };

        // Act
        var json = JsonSerializer.Serialize(obj, _options);

        // Assert
        Assert.Equal("""{"Value":null}""", json);
    }

    #endregion

    #region Round-trip Tests

    /// <summary>
    /// Performs the round trip_preserves true operation.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesTrue()
    {
        // Arrange
        var original = new TestClass { Value = true };

        var json = JsonSerializer.Serialize(original, _options);
        // Act
        var result = JsonSerializer.Deserialize<TestClass>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Value);
    }

    /// <summary>
    /// Performs the round trip_preserves false operation.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesFalse()
    {
        // Arrange
        var original = new TestClass { Value = false };

        var json = JsonSerializer.Serialize(original, _options);
        // Act
        var result = JsonSerializer.Deserialize<TestClass>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Value);
    }

    #endregion

    private class TestClass
    {
        /// <summary>
        /// Gets or sets value.
        /// </summary>
        public bool Value { get; set; }
    }
}
