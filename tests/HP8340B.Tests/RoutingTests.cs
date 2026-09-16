using HP8340B.Bench.Model;
using HP8340B.Instruments.Model;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-22: the routing model. One physical build serves S1-S4, with switch changes instead of
/// re-cabling — but only if each leg carries what it is actually capable of.
/// </summary>
public class RoutingTests
{
    private static RoutingMap Map() =>
        RoutingMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "routing.json"));

    private static RoutedLeg Leg(string switchModel) =>
        new() { Id = "TEST", Name = "test leg", SwitchModel = switchModel, Slot = 1, Channels = [100] };

    // --- Frequency limits, which is the point ---------------------------------------------------

    [Theory]
    [InlineData("33311C", 26.5e9)]
    [InlineData("33311B", 18e9)]
    [InlineData("44472A", 100e6)]
    public void EachLegTakesItsLimitFromItsSwitch(string model, double expected) =>
        Assert.Equal(expected, Leg(model).MaxHz);

    [Fact]
    public void AnUnknownSwitchIsUnratedRatherThanAssumedGood()
    {
        // Silence about a rating must not read as "fine at any frequency".
        var leg = Leg("something nobody wrote down");

        Assert.Equal(0, leg.MaxHz);
        Assert.Equal(TraceabilityClass.NotPossible, leg.TraceabilityAt(1e9));
    }

    [Fact]
    public void AnEighteenGigahertzLegCannotCarryBandFour()
    {
        // The rule the issue asked for. The switch does not stop passing signal above its rating
        // — it passes it with an insertion loss and a match nobody has characterised, which is a
        // plausible number rather than an obvious failure.
        var leg = Leg("33311B");

        Assert.Equal(TraceabilityClass.Spec, leg.TraceabilityAt(17e9));
        Assert.Equal(TraceabilityClass.NotPossible, leg.TraceabilityAt(24e9));

        Assert.False(leg.Covers(BandId.Band4));
    }

    [Fact]
    public void AnEighteenGigahertzLegCannotCarryAllOfBandThreeEither()
    {
        // Easy to get wrong, and worth a test of its own: band 3 runs 13.5 to 20.0 GHz, so an
        // 18 GHz switch runs out 2 GHz before the top of it. The natural assumption is that an
        // 18 GHz leg is fine for everything except band 4, and it is not.
        var leg = Leg("33311B");

        Assert.True(leg.Covers(BandId.Band2));                             // 7.0 - 13.5 GHz, fine
        Assert.False(leg.Covers(BandId.Band3));                            // 13.5 - 20.0 GHz, is not

        Assert.Equal(TraceabilityClass.Spec, leg.TraceabilityAt(17.9e9));
        Assert.Equal(TraceabilityClass.NotPossible, leg.TraceabilityAt(19e9));
    }

    [Fact]
    public void OnlyThe26GigahertzSwitchCoversEveryBand()
    {
        var leg = Leg("33311C");
        Assert.All(Bands.All, b => Assert.True(leg.Covers(b.Id)));
    }

    [Fact]
    public void ALegCanOnlyEverMakeTheClaimWorse()
    {
        // A Spec sensor reading routed through an 18 GHz leg at 24 GHz is not Spec.
        var leg = Leg("33311B");

        Assert.Equal(TraceabilityClass.Spec,
            RoutingRules.Combine(TraceabilityClass.Spec, leg, 10e9));

        Assert.Equal(TraceabilityClass.NotPossible,
            RoutingRules.Combine(TraceabilityClass.Spec, leg, 24e9));

        // And a leg that is fine cannot improve a Relative instrument reading.
        Assert.Equal(TraceabilityClass.Relative,
            RoutingRules.Combine(TraceabilityClass.Relative, Leg("33311C"), 24e9));
    }

    [Fact]
    public void RoutingAMeasurementTooHighIsRefusedWithTheReason()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RoutingRules.Guard(Leg("33311B"), 24e9));

        Assert.Contains("18", ex.Message);
        Assert.Contains("plausible number", ex.Message);
        Assert.Contains("33311C", ex.Message);
    }

    [Fact]
    public void AnUnratedLegSaysSoRatherThanQuotingAZero()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RoutingRules.Guard(Leg("mystery switch"), 1e9));

        Assert.Contains("unrated", ex.Message);
    }

    [Fact]
    public void AGoodLegPassesTheGuard() => RoutingRules.Guard(Leg("33311C"), 24e9);

    // --- The shipped map ----------------------------------------------------------------------------

    [Fact]
    public void TheShippedMapLoadsAndHasBothTrees()
    {
        var map = Map();

        Assert.NotEmpty(map.Sr);
        Assert.NotEmpty(map.Sv);
    }

    [Fact]
    public void EveryLegDeclaresASwitchAndAtLeastOneChannel()
    {
        Assert.All(Map().AllLegs, leg =>
        {
            Assert.False(string.IsNullOrWhiteSpace(leg.Id));
            Assert.False(string.IsNullOrWhiteSpace(leg.SwitchModel));
            Assert.NotEmpty(leg.Channels);
        });
    }

    [Fact]
    public void EveryLegsChannelsAreValid3499AChannelNumbers()
    {
        // Catches a typo in the data file at test time rather than at the bench.
        Assert.All(Map().AllLegs, leg =>
            Assert.All(leg.Channels, HP8340B.Instruments.Agilent3499A.ValidateChannel));
    }

    [Fact]
    public void TheLfLegsAreOnTheVhfMultiplexerNotAnRfSwitch()
    {
        // The DUT's 0-10 V sweep ramp and the detector's video output are DC. The coaxial
        // switches are not DC-blocking, so DC belongs on the 44472A legs.
        Assert.All(Map().Sv, leg => Assert.Contains("44472", leg.SwitchModel));
    }

    [Fact]
    public void ExactlyTheLegsThroughA33311CCanCarryBandFour()
    {
        var map = Map();
        var bandFour = map.BandFourCapableLegs().ToList();

        Assert.NotEmpty(bandFour);
        Assert.All(bandFour, leg => Assert.Contains("33311C", leg.SwitchModel));

        // And the 18 GHz legs are excluded, which is the whole point.
        Assert.DoesNotContain(bandFour, leg => leg.SwitchModel.Contains("33311B"));
    }

    [Fact]
    public void TheSharedSwitchIsDeclaredPerCampaign()
    {
        // The second 33311C feeds the detector for adjustment work and the sensor for
        // verification. It cannot do both at once, so only one is offered at a time.
        var map = Map();

        var adjustment = map.LegsFor(RoutingCampaign.Adjustment).Select(l => l.Id).ToList();
        var verification = map.LegsFor(RoutingCampaign.Verification).Select(l => l.Id).ToList();

        Assert.Contains("SR-B", adjustment);
        Assert.DoesNotContain("SR-B", verification);

        Assert.Contains("SR-C", verification);
        Assert.DoesNotContain("SR-C", adjustment);

        // A leg wired for either shows up in both.
        Assert.Contains("SR-A", adjustment);
        Assert.Contains("SR-A", verification);
    }

    [Fact]
    public void TheMapCarriesTheDcAndPowerNotesForTheHookUpCard()
    {
        var map = Map();

        Assert.False(string.IsNullOrWhiteSpace(map.DcLimitNote));
        Assert.False(string.IsNullOrWhiteSpace(map.PowerRatingNote));
        Assert.Contains("1 W", map.PowerRatingNote!);
    }

    [Fact]
    public void TheShippedMapIsHonestThatItsNumbersArePlaceholders()
    {
        // The slot and channel numbers are decision D-11 and cannot come off the bus. Shipping
        // them unflagged would let somebody route a real measurement through an invented channel.
        var map = Map();

        Assert.Contains(map.AllLegs, leg => leg.Note?.Contains("PLACEHOLDER") == true);
    }

    [Fact]
    public void AnUnknownLegIsRefusedWithTheListOfRealOnes()
    {
        var map = Map();

        Assert.Null(map.Leg("SR-ZZ"));

        var ex = Assert.Throws<ArgumentException>(() => map.Require("SR-ZZ"));
        Assert.Contains("SR-A", ex.Message);
    }

    [Fact]
    public void ALegDescribesItselfForAHookUpCard()
    {
        var description = Map().Require("SR-A").Describe();

        Assert.Contains("SR-A", description);
        Assert.Contains("33311C", description);
        Assert.Contains("GHz", description);
    }

    [Fact]
    public void AnUnratedLegDescribesItselfAsUnrated() =>
        Assert.Contains("UNRATED", Leg("mystery").Describe());
}
