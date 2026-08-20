using OpenServiceBus.Explorer.Helpers.Variables;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// Tests the {{$datetime}} variables processor that replace payload variables
/// by the formatted UTC datetime.
/// </summary>
public class DateTimeVariablesProcessorTests
{
    [Fact]
    public void Process_DateTimeVariable()
    {
        // Arrange
        string payload = """
            {
                "myVar": "{{$datetime}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;
        var myVar = (string?)node["myVar"];
        DateTime.TryParse(myVar, out var datetime).ShouldBeTrue();
        datetime.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void Process_MultipleDateTimeVariables()
    {
        // Arrange
        string payload = """
            {
                "myVar": "{{$datetime}}",
                "anotherProp": 1,
                "mySecondVar": "{{$datetime}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;

        var myVar = (string?)node["myVar"];
        DateTime.TryParse(myVar, out var datetime).ShouldBeTrue();
        datetime.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));

        var myVar2 = (string?)node["mySecondVar"];
        DateTime.TryParse(myVar2, out var datetime2).ShouldBeTrue();
        datetime2.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void Process_DateTimeVariable_WithRfc1123Format()
    {
        // Arrange
        string payload = """
            {
                "myVar": "{{$datetime rfc1123}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;
        var myVar = (string?)node["myVar"];
        DateTime.TryParseExact(myVar, DateTimeVariableProcessor.Rfc1123Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var datetime).ShouldBeTrue();
        datetime.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void Process_DateTimeVariable_WithIso8601Format()
    {
        // Arrange
        string payload = """
            {
                "myVar": "{{$datetime iso8601}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;
        var myVar = (string?)node["myVar"];
        DateTime.TryParseExact(myVar, DateTimeVariableProcessor.Iso8601Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var datetime).ShouldBeTrue();
        datetime.ToUniversalTime().ShouldBeInRange(DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Theory]
    [InlineData("3d", 3)]
    [InlineData("-4d", -4)]
    public void Process_DateTimeVariable_WithOffset(string offsetStr, int offsetInt)
    {
        // Arrange
        string payload = $$$"""
            {
                "myVar": "{{$datetime {{{offsetStr}}}}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;
        var myVar = (string?)node["myVar"];
        DateTime.TryParseExact(myVar, DateTimeVariableProcessor.DefaultFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var datetime).ShouldBeTrue();
        datetime.ShouldBeInRange(DateTime.UtcNow.AddDays(offsetInt).AddSeconds(-5), DateTime.UtcNow.AddDays(offsetInt).AddSeconds(5));
    }
}
