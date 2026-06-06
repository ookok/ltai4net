namespace LTAI.Hpo;

/// <summary>Sampler interface — one method per suggest type.</summary>
public interface ISampler
{
    float SampleFloat(Trial trial, string name, float low, float high, bool log);
    int SampleInt(Trial trial, string name, int low, int high);
    T SampleCategorical<T>(Trial trial, string name, T[] choices) where T : notnull;
}
