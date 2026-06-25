// Circle33PromptVariablesTests.cs
//
// (3.3.0) Tests for dynamic prompt variable substitution.

using System;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PromptVariablesTests
{
    [Fact]
    public async Task Render_SubstitutesStaticVariables()
    {
        var r = new PromptVariableResolver()
            .Set("caller_name", "Sipho")
            .Set("business_name", "Acme");

        var output = await r.RenderAsync("Hi {{caller_name}}, welcome to {{business_name}}.");
        Assert.Equal("Hi Sipho, welcome to Acme.", output);
    }

    [Fact]
    public async Task Render_HandlesWhitespaceInPlaceholders()
    {
        var r = new PromptVariableResolver().Set("x", "value");
        var output = await r.RenderAsync("[{{  x  }}]");
        Assert.Equal("[value]", output);
    }

    [Fact]
    public async Task Render_CallsProviderForUnknownVariable()
    {
        int calls = 0;
        var r = new PromptVariableResolver()
            .SetProvider("now", (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<string?>("2026-06-23");
            });

        var output = await r.RenderAsync("Today is {{now}}.");
        Assert.Equal("Today is 2026-06-23.", output);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Render_DefaultMissing_ReplacesUnknownVariables()
    {
        var r = new PromptVariableResolver(defaultMissing: "(unknown)");
        var output = await r.RenderAsync("Hi {{caller_name}}.");
        Assert.Equal("Hi (unknown).", output);
    }

    [Fact]
    public async Task Render_NoVariables_ReturnsTemplateUnchanged()
    {
        var r = new PromptVariableResolver();
        var output = await r.RenderAsync("Hello world.");
        Assert.Equal("Hello world.", output);
    }

    [Fact]
    public async Task Render_RepeatedVariable_OnlyResolvesOnce()
    {
        int calls = 0;
        var r = new PromptVariableResolver()
            .SetProvider("name", (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<string?>("Sipho");
            });

        var output = await r.RenderAsync("Hi {{name}}, can I call you {{name}}?");
        Assert.Equal("Hi Sipho, can I call you Sipho?", output);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Render_StaticBeatsProvider()
    {
        var r = new PromptVariableResolver()
            .Set("city", "Joburg")
            .SetProvider("city", (_, _) => ValueTask.FromResult<string?>("Cape Town"));

        var output = await r.RenderAsync("Welcome to {{city}}.");
        Assert.Equal("Welcome to Joburg.", output);
    }

    [Fact]
    public async Task Render_EmptyTemplate_ReturnsEmpty()
    {
        var r = new PromptVariableResolver();
        Assert.Equal("", await r.RenderAsync(""));
    }

    [Fact]
    public void Set_EmptyName_Throws()
    {
        var r = new PromptVariableResolver();
        Assert.Throws<ArgumentException>(() => r.Set("", "x"));
    }

    [Fact]
    public void SetProvider_NullProvider_Throws()
    {
        var r = new PromptVariableResolver();
        Assert.Throws<ArgumentNullException>(() => r.SetProvider("x", null!));
    }
}
