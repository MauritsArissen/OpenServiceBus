using OpenServiceBus.Core.Configuration;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Core.Tests;

/// <summary>
/// The shared dedup-window resolution every send path uses (issue #29), plus config.json
/// projection of the new topic-level duplicate-detection properties.
/// </summary>
public class DuplicateDetectionHelperTests
{
    [Fact]
    public void EffectiveWindow_DedupDisabled_IsNull_RegardlessOfConfiguredWindow()
    {
        DuplicateDetection.EffectiveWindow(false, null).ShouldBeNull();
        DuplicateDetection.EffectiveWindow(false, TimeSpan.FromMinutes(5)).ShouldBeNull();
    }

    [Fact]
    public void EffectiveWindow_DedupEnabledWithoutWindow_DefaultsToTenMinutes()
    {
        DuplicateDetection.EffectiveWindow(true, null).ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void EffectiveWindow_DedupEnabledWithWindow_UsesTheConfiguredWindow()
    {
        DuplicateDetection.EffectiveWindow(true, TimeSpan.FromSeconds(30)).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void LoadFromJson_TopicWithDuplicateDetection_ProjectsBothProperties()
    {
        const string json = """
                            {
                              "UserConfig": {
                                "Namespaces": [
                                  {
                                    "Name": "ns",
                                    "Topics": [
                                      {
                                        "Name": "events",
                                        "Properties": {
                                          "RequiresDuplicateDetection": true,
                                          "DuplicateDetectionHistoryTimeWindow": "PT5M"
                                        }
                                      }
                                    ]
                                  }
                                ]
                              }
                            }
                            """;

        var result = EmulatorConfigLoader.LoadFromJson(json);

        result.Topics.Count.ShouldBe(1);
        result.Topics[0].RequiresDuplicateDetection.ShouldBeTrue();
        result.Topics[0].DuplicateDetectionHistoryTimeWindow.ShouldBe(TimeSpan.FromMinutes(5));
        result.Warnings.Count.ShouldBe(0);
    }

    [Fact]
    public void LoadFromJson_TopicWithoutDedupProperties_DefaultsToDisabled()
    {
        const string json = """
                            {
                              "UserConfig": {
                                "Namespaces": [
                                  { "Name": "ns", "Topics": [ { "Name": "plain" } ] }
                                ]
                              }
                            }
                            """;

        var result = EmulatorConfigLoader.LoadFromJson(json);

        result.Topics.Count.ShouldBe(1);
        result.Topics[0].RequiresDuplicateDetection.ShouldBeFalse();
        result.Topics[0].DuplicateDetectionHistoryTimeWindow.ShouldBeNull();
    }
}
