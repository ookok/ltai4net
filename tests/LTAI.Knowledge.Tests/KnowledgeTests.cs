using LTAI.Core.Configuration;
using LTAI.Knowledge.Vector;
using LTAI.Knowledge.Vector.Embedding;
using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LTAI.Knowledge.Tests;

public class DocumentStoreTests : IDisposable
{
    private readonly DocumentStore _store;
    private readonly string _dbPath;

    public DocumentStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ltai_test_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.Configure<LTAIOptions>(_ => { });
        services.AddSingleton<IEmbeddingBackend, LocalEmbeddingBackend>();
        services.AddSingleton<IVectorStore, LTAI.Vector.VectorStore>();
        var sp = services.BuildServiceProvider();

        _store = new DocumentStore(_dbPath,
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<ILogger<DocumentStore>>());
    }

    [Fact]
    public void AddDocument_ReturnsId()
    {
        var id = _store.AddDocument("Test", "Hello world content", autoChunk: false);
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(12, id.Length);
    }

    [Fact]
    public void GetDocument_ReturnsInserted()
    {
        var id = _store.AddDocument("My Doc", "Some content here", "science", autoChunk: false);
        var doc = _store.GetDocument(id);
        Assert.NotNull(doc);
        Assert.Equal("My Doc", doc.Title);
        Assert.Equal("Some content here", doc.Content);
        Assert.Equal("science", doc.Domain);
    }

    [Fact]
    public void AddDocument_WithAutoChunk_CreatesChunks()
    {
        var longContent = new string('A', 2500);
        var id = _store.AddDocument("Long Doc", longContent, autoChunk: true);
        var chunks = _store.GetChunks(id);
        Assert.NotEmpty(chunks);
        Assert.True(chunks.Count >= 2);
    }

    [Fact]
    public void SearchFts_FindsDocument()
    {
        _store.AddDocument("AI Research", "Machine learning is transforming science", "ai", autoChunk: false);
        _store.AddDocument("Cooking Tips", "How to make pasta at home", "cooking", autoChunk: false);

        var results = _store.SearchFts("machine learning");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Title == "AI Research");
    }

    [Fact]
    public void SearchFts_WithDomainFilter()
    {
        _store.AddDocument("AI Paper", "Deep learning advances", "ai", autoChunk: false);
        _store.AddDocument("Cooking Book", "Deep frying techniques", "cooking", autoChunk: false);

        var results = _store.SearchFts("deep", "ai");
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("ai", r.Domain));
    }

    [Fact]
    public void ListDocuments_ByDomain()
    {
        _store.AddDocument("A", "content", "ai", autoChunk: false);
        _store.AddDocument("B", "content", "ai", autoChunk: false);
        _store.AddDocument("C", "content", "cooking", autoChunk: false);

        var aiDocs = _store.ListDocuments("ai");
        Assert.Equal(2, aiDocs.Count);
    }

    [Fact]
    public void AddRelation_CreatesLink()
    {
        var id1 = _store.AddDocument("Doc1", "content", autoChunk: false);
        var id2 = _store.AddDocument("Doc2", "content", autoChunk: false);

        var relId = _store.AddRelation(id1, id2, "references", 0.8);
        Assert.True(relId > 0);
    }

    [Fact]
    public void DeleteDocument_RemovesFromSearch()
    {
        var id = _store.AddDocument("To Delete", "Something to be removed", autoChunk: false);
        _store.DeleteDocument(id);

        var doc = _store.GetDocument(id);
        Assert.Null(doc);
    }

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        _store.AddDocument("Doc1", "content A", autoChunk: false);
        _store.AddDocument("Doc2", "content B", autoChunk: false);

        var stats = _store.GetStats();
        Assert.True(stats.TotalDocuments >= 2);
        Assert.True(stats.DatabaseSizeBytes > 0);
    }

    [Fact]
    public void SplitChunks_LongText()
    {
        var text = new string('X', 3000);
        var chunks = DocumentStore.SplitChunks(text);
        Assert.True(chunks.Count >= 3);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}

public class KnowledgeBaseTests : IDisposable
{
    private readonly KnowledgeBase _kb;
    private readonly string _dbPath;

    public KnowledgeBaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ltai_kb_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.Configure<LTAIOptions>(_ => { });
        services.AddSingleton<IEmbeddingBackend, LocalEmbeddingBackend>();
        services.AddSingleton<IVectorStore, LTAI.Vector.VectorStore>();
        services.AddSingleton<DocumentStore>(sp =>
            new DocumentStore(_dbPath,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<ILogger<DocumentStore>>()));
        services.AddSingleton<KnowledgeBase>();
        var sp = services.BuildServiceProvider();
        _kb = sp.GetRequiredService<KnowledgeBase>();
    }

    [Fact]
    public void AddAndRetrieve()
    {
        var id = _kb.AddKnowledge("KB Doc", "Knowledge base content for testing", "test", indexVector: false);
        var doc = _kb.Retrieve(id);
        Assert.NotNull(doc);
        Assert.Equal("KB Doc", doc.Title);
    }

    [Fact]
    public void Search_FindsByFts()
    {
        _kb.AddKnowledge("AI Safety", "Ensuring artificial intelligence is beneficial for humanity", "ai", indexVector: false);
        _kb.AddKnowledge("Cooking 101", "Basic cooking techniques for beginners", "cooking", indexVector: false);

        var results = _kb.Search("artificial intelligence");
        Assert.NotEmpty(results);
    }

    [Fact]
    public void SearchKeyword_MatchesAll()
    {
        _kb.AddKnowledge("ML Guide", "machine learning tutorial with examples", "tech", indexVector: false);
        _kb.AddKnowledge("DL Guide", "deep learning for computer vision", "tech", indexVector: false);

        var results = _kb.SearchKeyword(new[] { "learning" });
        Assert.NotEmpty(results);
    }

    [Fact]
    public void GetByDomain_Filters()
    {
        _kb.AddKnowledge("A", "x", "science", indexVector: false);
        _kb.AddKnowledge("B", "x", "science", indexVector: false);
        _kb.AddKnowledge("C", "x", "art", indexVector: false);

        var docs = _kb.GetByDomain("science");
        Assert.True(docs.Count >= 2);
    }

    [Fact]
    public void ListDocuments_All()
    {
        _kb.AddKnowledge("X1", "c1", indexVector: false);
        _kb.AddKnowledge("X2", "c2", indexVector: false);

        var docs = _kb.ListDocuments();
        Assert.True(docs.Count >= 2);
    }

    [Fact]
    public void Delete_RemovesDocument()
    {
        var id = _kb.AddKnowledge("Temp", "temporary", indexVector: false);
        _kb.Delete(id);
        Assert.Null(_kb.Retrieve(id));
    }

    [Fact]
    public void GetStats_HasDocuments()
    {
        _kb.AddKnowledge("S1", "stats test", indexVector: false);
        var stats = _kb.GetStats();
        Assert.True(stats.TotalDocuments >= 1);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
