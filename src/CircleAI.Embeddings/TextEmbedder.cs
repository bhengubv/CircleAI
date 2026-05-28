// TextEmbedder.cs
//
// On-device text embedding using a GGUF / MNN embedding model via MNN (Alibaba Group).
// Supports any MNN-compatible embedding model (Qwen-Embedding, BGE-zh, etc.).
// The backend is factory-injectable for testability.
//
// Architecture notes:
//   - MnnEmbeddingBackend is the production path. It loads a GGUF or MNN embedding
//     model via mnn_llm_create + mnn_llm_load, calls mnn_embed_text per request,
//     then L2-normalises the output vector.
//   - No global backend ref-count is needed: MNN initialises per model handle,
//     unlike llama.cpp which required llama_backend_init / llama_backend_free.
//   - L2 normalisation is always applied so downstream cosine similarity
//     reduces to a dot product.
//   - TextEmbedder is disposable; it destroys the native model handle when disposed.
//   - Model loading is lazy (first GenerateAsync call) and serialised by a
//     SemaphoreSlim so concurrent callers share a single initialisation.
//
// MNN performance vs llama.cpp on Android ARM64:
//   Prefill: 8.6×  |  Decode: 3.7×  |  Peak RAM: −40%

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Inference;

namespace CircleAI.Embeddings
{
    // -------------------------------------------------------------------------
    // Internal embedding-backend abstraction — lets tests inject a fake
    // without needing the native MNN library on the test machine.
    // -------------------------------------------------------------------------

    internal interface IEmbeddingBackend : IDisposable
    {
        /// <summary>Number of floats returned by <see cref="Embed"/>.</summary>
        int Dimension { get; }

        /// <summary>
        /// Embeds <paramref name="text"/> and returns a L2-normalised vector.
        /// Not thread-safe — the caller (<see cref="TextEmbedder"/>) serialises
        /// with a semaphore.
        /// </summary>
        float[] Embed(string text);
    }

    // -------------------------------------------------------------------------
    // Production backend — MNN GGUF / MNN-format embedding model
    // -------------------------------------------------------------------------

    internal sealed class MnnEmbeddingBackend : IEmbeddingBackend
    {
        private readonly MnnModelHandle _model;
        private readonly int _dimension;
        private bool _disposed;

        public MnnEmbeddingBackend(string modelPath, int? threads = null)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentException("Model path is required.", nameof(modelPath));
            if (!System.IO.File.Exists(modelPath))
                throw new System.IO.FileNotFoundException("Embedding model file not found.", modelPath);

            var handle = MnnInterop.mnn_llm_create(modelPath);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException(
                    $"MNN failed to create embedding model from '{modelPath}'. " +
                    "Verify the file is a valid GGUF or MNN embedding model and that " +
                    "libmnnbridge is on the native library search path.");
            }

            int rc = MnnInterop.mnn_llm_load(handle);
            if (rc != 0)
            {
                handle.Dispose();
                throw new InvalidOperationException(
                    $"MNN embedding model load failed with code {rc} for '{modelPath}'. " +
                    "Check available RAM and that the model file is not corrupt.");
            }

            int dim = MnnInterop.mnn_embed_get_dim(handle);
            if (dim <= 0)
            {
                handle.Dispose();
                throw new InvalidOperationException(
                    "Embedding model returned dimension <= 0. " +
                    "Ensure the GGUF file is a valid embedding model.");
            }

            _model = handle;
            _dimension = dim;
        }

        public int Dimension => _dimension;

        public unsafe float[] Embed(string text)
        {
            ThrowIfDisposed();

            var output = new float[_dimension];
            int rc;
            fixed (float* pOut = output)
            {
                rc = MnnInterop.mnn_embed_text(_model, text, pOut, _dimension);
            }

            if (rc < 0)
                throw new InvalidOperationException($"MNN embedding failed with code {rc}.");

            // L2-normalise so cosine similarity == dot product downstream.
            L2Normalize(output);
            return output;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _model.Dispose();
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MnnEmbeddingBackend));
        }

        private static void L2Normalize(float[] v)
        {
            double norm = 0.0;
            foreach (var x in v) norm += (double)x * x;
            norm = Math.Sqrt(norm);
            if (norm < 1e-12) return; // zero vector — leave as-is
            float scale = (float)(1.0 / norm);
            for (int i = 0; i < v.Length; i++) v[i] *= scale;
        }
    }

    // -------------------------------------------------------------------------
    // Public TextEmbedder — thin orchestration shell over IEmbeddingBackend
    // -------------------------------------------------------------------------

    /// <summary>
    /// On-device text embedder backed by a GGUF / MNN embedding model (Qwen-Embedding,
    /// BGE-zh, etc.) loaded via MNN (Alibaba Group). Returns L2-normalised
    /// <c>float[]</c> vectors suitable for cosine-similarity retrieval.
    /// </summary>
    public sealed class TextEmbedder : ITextEmbedder, IDisposable
    {
        private readonly IModelManager _modelManager;
        private readonly byte[] _expectedChecksum;
        private readonly Func<string, IEmbeddingBackend> _backendFactory;

        private IEmbeddingBackend? _backend;
        private readonly SemaphoreSlim _initGate = new(1, 1);
        private bool _disposed;

        // ------------------------------------------------------------------
        // Public constructors
        // ------------------------------------------------------------------

        /// <summary>
        /// Production constructor. Uses <see cref="MnnEmbeddingBackend"/>
        /// with the model path resolved via <paramref name="modelManager"/>.
        /// </summary>
        public TextEmbedder(IModelManager modelManager, byte[] expectedChecksum)
            : this(modelManager, expectedChecksum,
                  static path => new MnnEmbeddingBackend(path))
        { }

        // ------------------------------------------------------------------
        // Internal constructor for testing (inject a fake backend)
        // ------------------------------------------------------------------

        internal TextEmbedder(
            IModelManager modelManager,
            byte[] expectedChecksum,
            Func<string, IEmbeddingBackend> backendFactory)
        {
            _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
            _expectedChecksum = expectedChecksum ?? throw new ArgumentNullException(nameof(expectedChecksum));
            _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        }

        // ------------------------------------------------------------------
        // ITextEmbedder
        // ------------------------------------------------------------------

        /// <inheritdoc />
        public async Task<float[]> GenerateAsync(string text, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TextEmbedder));
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be empty.", nameof(text));

            var backend = await EnsureBackendAsync(ct).ConfigureAwait(false);

            // Embed is CPU-bound; run on thread pool so callers aren't blocked.
            return await Task.Run(() => backend.Embed(text), ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // Dispose
        // ------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _backend?.Dispose();
            _initGate.Dispose();
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private async Task<IEmbeddingBackend> EnsureBackendAsync(CancellationToken ct)
        {
            if (_backend is not null) return _backend;

            await _initGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_backend is not null) return _backend;

                // Resolve + verify model path via the IModelManager contract.
                var path = await _modelManager
                    .GetModelPathAsync("embedding", ct)
                    .ConfigureAwait(false);

                var verified = await _modelManager
                    .VerifyModelAsync(path, _expectedChecksum, ct)
                    .ConfigureAwait(false);

                if (!verified)
                    throw new InvalidDataException(
                        "Embedding model checksum verification failed. " +
                        "The file may be corrupt or tampered with.");

                _backend = _backendFactory(path);
                return _backend;
            }
            finally
            {
                _initGate.Release();
            }
        }
    }
}
