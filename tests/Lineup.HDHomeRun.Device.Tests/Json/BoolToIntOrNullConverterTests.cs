using System.Text.Json;
using Lineup.HDHomeRun.Device.Json;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests.Json;

public class BoolToIntOrNullConverterTests
{
    private readonly JsonSerializerOptions _options;

    public BoolToIntOrNullConverterTests()
    {
        _options = new JsonSerializerOptions();
        _options.Converters.Add(new BoolToIntOrNullConverter());
    }

    #region Deserialization Tests

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

    [Fact]
    public void RoundTrip_PreservesTrue()
    {
        // Arrange
        var original = new TestClass { Value = true };

        // Act
        var json = JsonSerializer.Serialize(original, _options);
        var result = JsonSerializer.Deserialize<TestClass>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Value);
    }

    [Fact]
    public void RoundTrip_PreservesFalse()
    {
        // Arrange
        var original = new TestClass { Value = false };

        // Act
        var json = JsonSerializer.Serialize(original, _options);
        var result = JsonSerializer.Deserialize<TestClass>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Value);
    }

    #endregion

    private class TestClass
    {
        public bool Value { get; set; }
    }
}
