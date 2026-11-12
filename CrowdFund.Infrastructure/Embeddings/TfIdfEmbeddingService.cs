using System.Text.RegularExpressions;
using CrowdFund.Core.Interfaces;

namespace CrowdFund.Infrastructure.Embeddings;

/// <summary>
/// Lightweight TF-IDF cosine fallback when ONNX MiniLM is not available.
/// Suitable for demo scale.
/// </summary>
public class TfIdfEmbeddingService : IEmbeddingService
{
    private static readonly Regex TokenRegex = new(@"\b\w+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var tokens = TokenRegex.Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 2)
            .ToList();

        // Simple bag-of-words vector over a fixed vocabulary size for demo
        const int dim = 128;
        var vec = new float[dim];
        foreach (var t in tokens)
        {
            var idx = Math.Abs(t.GetHashCode()) % dim;
            vec[idx] += 1f;
        }
        // L2 normalize
        var norm = MathF.Sqrt(vec.Sum(x => x * x));
        if (norm > 0)
            for (int i = 0; i < dim; i++) vec[i] /= norm;

        return Task.FromResult(vec);
    }

    public double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}
