using OpenServiceBus.Explorer.Helpers.Variables;
using System.Text.Json.Nodes;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// Tests the Guid variables processor that replace payload variables
/// by the computed Guid according to the specified options.
/// </summary>
public class GuidVariablesProcessorTests
{
    [Fact]
    public void Process_GuidVariable()
    {
        // Arrange
        string payload = """
            {
                "myVar": "{{$guid}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;
        var myVar = (string?)node["myVar"];
        Guid.TryParse(myVar, out var _).ShouldBeTrue();
    }

    [Fact]
    public void Process_GuidVariable_WithUpperCasingOption()
    {
        // Arrange
        string payload = """
            {
                "myVar": "{{$guid upper}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;
        var myVar = (string?)node["myVar"];
        Guid.TryParse(myVar, out var _).ShouldBeTrue();
        myVar.ShouldAllBe(c => !char.IsLetter(c) || char.IsUpper(c));
    }

    [Fact]
    public void Process_GuidVariable_WithLowerCasingOption()
    {
        // Arrange
        string payload = """
            {
                "myVar": "{{$guid lower}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;
        var myVar = (string?)node["myVar"];
        Guid.TryParse(myVar, out var _).ShouldBeTrue();
        myVar.ShouldAllBe(c => !char.IsLetter(c) || char.IsLower(c));
    }

    [Fact]
    public void Process_MultipleGuidVariables()
    {
        // Arrange
        string payload = """
            {
                "myVar": "{{$guid}}",
                "anotherProp": 1,
                "mySecondVar": "{{$guid}}"
            }
            """;

        // Act
        var updatedPayload = VariablesProcessor.Process(payload);

        // Assert
        var node = JsonNode.Parse(updatedPayload)!;
        var myVar = (string?)node["myVar"];
        Guid.TryParse(myVar, out var _).ShouldBeTrue();

        var mySecondVar = (string?)node["mySecondVar"];
        Guid.TryParse(mySecondVar, out var _).ShouldBeTrue();
    }
}
