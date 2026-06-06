# CircleAI.Tools

Tool-calling primitives — `ToolDefinition`, `ToolInvocation`,
`ToolResult`, `IToolBridge`. Used by `CircleAI.Companion.ICompanionSession.AgentAsync`
to surface model-issued tool calls back to the host application.

```bash
dotnet add package CircleAI.Tools
```

```csharp
using CircleAI.Tools;

IToolBridge bridge = new MyToolBridge();
var def = new ToolDefinition(
    name: "fetch_url",
    description: "Fetch a URL and return its text content",
    parameters: new[] { new ToolParameter("url", "string", required: true) });
```

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
