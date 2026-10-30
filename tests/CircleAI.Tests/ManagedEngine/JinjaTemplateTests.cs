// JinjaTemplateTests.cs
//
// JinjaLite against jinja2 itself. Two rulers, both rendered by jinja2 3.1.6 in Hugging
// Face's environment (trim_blocks, lstrip_blocks, loopcontrols, json.dumps tojson,
// raise_exception):
//   fixtures/bonsai2/2B/templates.json - the 2B's own chat template over 41 conversations,
//     7 of which it refuses;
//   fixtures/jinja/snippets.json - 63 small templates, each pinning one behaviour.
// Neither needs the model: the template's text is in the 2B's metadata fixture. And what is
// outside the subset is refused when parsed, never skipped.

using System.Text.Json;
using CircleAI.Inference.Managed.Text;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class JinjaTemplateTests(ITestOutputHelper output)
{
    private static JsonDocument Snippets() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "jinja", "snippets.json")));

    private static void Check(string name, Func<string> render, JsonElement expected)
    {
        if (expected.TryGetProperty("error", out var error))
        {
            var thrown = Assert.Throws<TemplateException>(() => render());
            // render_templates.py catches only raise_exception's TemplateError, so it names no type.
            var type = expected.TryGetProperty("error_type", out var t) ? t.GetString() : "TemplateError";
            var raised = type == "TemplateError";
            Assert.True(raised == thrown.RaisedByTemplate, $"{name}: jinja2 raised {type}, JinjaLite: {thrown.Message}");
            if (raised) Assert.Equal(error.GetString(), thrown.Message);
            return;
        }
        Assert.Equal(expected.GetProperty("rendered").GetString(), render());
    }

    [Fact]
    public void The_2B_template_renders_41_conversations_as_jinja2_does()
    {
        using var meta = Bonsai2Model.Fixture("meta.json");
        using var fixture = Bonsai2Model.Fixture("templates.json");
        var template = JinjaTemplate.Parse(meta.RootElement.GetProperty("keys").GetProperty("tokenizer.chat_template").GetString()!);

        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToList();
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var variables = new Dictionary<string, object?>
            {
                ["messages"] = c.GetProperty("messages"),
                // render_templates.py passes tools=None when a case has none.
                ["tools"] = c.TryGetProperty("tools", out var tools) ? tools : null,
                ["add_generation_prompt"] = c.GetProperty("add_generation_prompt").GetBoolean(),
                ["enable_thinking"] = c.GetProperty("enable_thinking").GetBoolean(),
            };
            Check(name, () => template.Render(variables), c);
        }

        Assert.Equal(41, cases.Count);
        output.WriteLine($"{cases.Count} conversations: every render byte for byte, every refusal word for word.");
    }

    public static TheoryData<string> SnippetNames
    {
        get
        {
            var names = new TheoryData<string>();
            using var doc = Snippets();
            foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray()) names.Add(c.GetProperty("name").GetString()!);
            return names;
        }
    }

    [Theory]
    [MemberData(nameof(SnippetNames))]
    public void A_snippet_renders_as_jinja2_does(string name)
    {
        using var doc = Snippets();
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        var variables = c.GetProperty("variables").EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        Check(name, () => JinjaTemplate.Parse(c.GetProperty("template").GetString()!).Render(variables), c);
    }

    [Theory]
    [InlineData("{{ 'x' | upper }}")]
    [InlineData("{{ 4 is divisibleby 2 }}")]
    [InlineData("{{ x is sameas(y) }}")]
    [InlineData("{% include 'other' %}")]
    [InlineData("{% filter upper %}x{% endfilter %}")]
    [InlineData("{% raw %}x{% endraw %}")]
    [InlineData("{{ strftime_now('%Y') }}")]
    [InlineData("{{ '{}'.format(1) }}")]
    [InlineData("{% for x in y recursive %}{% endfor %}")]
    [InlineData("{{ f(*args) }}")]
    [InlineData("{% set x | upper %}a{% endset %}")]
    public void What_is_outside_the_subset_is_refused_when_parsed(string template)
    {
        Assert.Throws<NotSupportedException>(() => JinjaTemplate.Parse(template));
    }

    [Theory]
    [InlineData("{% if %}x{% endif %}")]
    [InlineData("{{ x ")]
    [InlineData("{% for x in %}{% endfor %}")]
    [InlineData("{% if true %}never closed")]
    [InlineData("{% endif %}")]
    [InlineData("{{ (1, 2 }}")]
    [InlineData("{{ 'never closed }}")]
    public void Syntax_errors_are_template_errors(string template)
    {
        Assert.Throws<TemplateException>(() => JinjaTemplate.Parse(template));
    }
}
