// NeuronRouterTests.cs — the concierge decision table + gate guardrail.

using CircleAI.Hosting.Neuron;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class NeuronRouterTests
{
    [Fact]
    public void PlainQuery_RoutesToGeneralist()
    {
        var router = new HeuristicNeuronRouter();
        var d = router.Route(new RouteContext("what's the weather like today?"));
        Assert.Equal(Organ.Generalist, d.Organ);
        Assert.Equal(ChatCapability.Default, d.Capability);
    }

    [Fact]
    public void Image_RoutesToVisionSpecialist()
    {
        var router = new HeuristicNeuronRouter();
        var d = router.Route(new RouteContext("what is in this photo?", HasImage: true));
        Assert.Equal(Organ.Specialist, d.Organ);
        Assert.Equal(ChatCapability.Vision, d.Capability);
    }

    [Fact]
    public void ReasoningCue_RoutesToReasoningSpecialist()
    {
        var router = new HeuristicNeuronRouter();
        var d = router.Route(new RouteContext("please debug this stack trace"));
        Assert.Equal(Organ.Specialist, d.Organ);
        Assert.Equal(ChatCapability.Reasoning, d.Capability);
    }

    [Fact]
    public void LongPrompt_RoutesToLongContextSpecialist()
    {
        var router = new HeuristicNeuronRouter(longContextChars: 50);
        var d = router.Route(new RouteContext(new string('x', 60)));
        Assert.Equal(Organ.Specialist, d.Organ);
        Assert.Equal(ChatCapability.LongContext, d.Capability);
    }

    [Fact]
    public void Gate_VetoesSpecialist_FallsBackToGeneralist()
    {
        var gate = new NeuronGate(allowSpecialist: _ => false);
        var router = new HeuristicNeuronRouter(gate);
        var d = router.Route(new RouteContext("solve this equation")); // would be a specialist
        Assert.Equal(Organ.Generalist, d.Organ);
    }

    [Fact]
    public void Gate_NullPolicy_AllowsSpecialist()
    {
        var router = new HeuristicNeuronRouter(new NeuronGate());
        var d = router.Route(new RouteContext("prove this theorem"));
        Assert.Equal(Organ.Specialist, d.Organ);
    }
}
