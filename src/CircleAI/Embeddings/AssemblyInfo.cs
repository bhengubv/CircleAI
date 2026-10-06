// AssemblyInfo.cs — CircleAI.Embeddings
//
// Exposes internal types (IEmbeddingBackend, LlamaEmbeddingBackend) to the
// test project so that TextEmbedder can be constructed with a fake backend.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CircleAI.Tests")]
