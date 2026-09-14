// GlobalUsings.cs
//
// THIS PROJECT HAD NEVER PRODUCED A DLL.
//
// Every Browser*.cs implements a seam from CircleAI.Assistant - IBrain,
// IConversation, IDeviceFacts, twenty-one types in all - and not one of them
// had a using for it. Thirty-eight CS0246s, the whole web head, unbuilt.
//
// It survived because of where the OTHER head's services live. DeviceBrain and
// its siblings declare `namespace CircleAI.Assistant.Device`, and C# walks the
// enclosing namespaces on the way out - so CircleAI.Assistant resolves there for
// free and nobody writing one ever typed a using. These sit in
// CircleAI.Samples.Web.Client.Services, which encloses nothing, and got none.
//
// _Imports.razor already carried `@using CircleAI.Assistant` - TWICE, added by
// hand twice by somebody who did not check - but that file reaches .razor and
// nothing else.
//
// One line here rather than a using at the top of seventeen files: the next
// Browser service somebody writes gets it without having to know any of the
// above.

global using CircleAI.Assistant;
