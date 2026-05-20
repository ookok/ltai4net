using LTAI.Vector.Knowledge;
using Xunit;

namespace LTAI.Tests;

public class CodeSearchRerankerTests
{
    [Fact]
    public void Rerank_DefinitionBoost()
    {
        var docs = new List<Bm25ScoredDoc>
        {
            new() { Id = "src/auth.py", Content = "class AuthManager: def authenticate(self, token): return True", Bm25Score = 0.9, VectorScore = 0.9, RrfScore = 0.9 },
            new() { Id = "src/main.py", Content = "auth.verify(token)", Bm25Score = 0.1, VectorScore = 0.1, RrfScore = 0.1 }
        };

        var reranked = CodeSearchReranker.Rerank(docs, "authentication");

        Assert.Equal(2, reranked.Count);
        Assert.Equal("src/auth.py", reranked[0].Id);
    }

    [Fact]
    public void NoisePenalty_TestFilesDownranked()
    {
        var docs = new List<Bm25ScoredDoc>
        {
            new() { Id = "test_auth.py", Content = "def test_login():", Bm25Score = 0.8, VectorScore = 0.8, RrfScore = 0.8 },
            new() { Id = "src/auth.py", Content = "def login(user, pwd):", Bm25Score = 0.6, VectorScore = 0.6, RrfScore = 0.6 }
        };

        var reranked = CodeSearchReranker.Rerank(docs, "login");

        Assert.Equal("src/auth.py", reranked[0].Id);
    }

    [Fact]
    public void ClassifyQueryType_SymbolVsNatural()
    {
        Assert.Equal("symbol", CodeSearchReranker.ClassifyQueryType("getUserById"));
        Assert.Equal("symbol", CodeSearchReranker.ClassifyQueryType("Foo::bar"));
        Assert.Equal("natural", CodeSearchReranker.ClassifyQueryType("how is authentication handled"));
        Assert.Equal("natural", CodeSearchReranker.ClassifyQueryType("find the login function"));
    }

    [Fact]
    public void Rerank_StemMatchBonus()
    {
        var docs = new List<Bm25ScoredDoc>
        {
            new() { Id = "a.cs", Content = "parseConfig", Bm25Score = 0.5, VectorScore = 0.3, RrfScore = 0.4 },
            new() { Id = "b.cs", Content = "other stuff", Bm25Score = 0.5, VectorScore = 0.3, RrfScore = 0.4 }
        };

        var reranked = CodeSearchReranker.Rerank(docs, "parse config");

        Assert.Equal("a.cs", reranked[0].Id);
    }

    [Fact]
    public void Rerank_EmptyList()
    {
        var reranked = CodeSearchReranker.Rerank(new List<Bm25ScoredDoc>(), "query");
        Assert.Empty(reranked);
    }
}
