namespace CrowdFund.Core.Interfaces;

public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    double CosineSimilarity(float[] a, float[] b);
}
