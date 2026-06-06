namespace LTAI.Hpo;

/// <summary>Persistent storage for studies and trials.</summary>
public interface IStudyStore
{
    Task InitializeAsync();
    Task SaveTrialAsync(string studyName, TrialRecord record);
    Task<IReadOnlyList<TrialRecord>> LoadTrialsAsync(string studyName);
}