namespace LTAI.Hpo;

/// <summary>Pruner decides whether to stop a trial early based on intermediate values.</summary>
public interface IPruner
{
    bool ShouldPrune(Trial trial);
}