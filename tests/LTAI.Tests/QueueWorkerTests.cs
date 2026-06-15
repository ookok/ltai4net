// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  QueueWorkerTests — IndexQueueWorker + RetryQueueWorker tests
// ═══════════════════════════════════════════════════════════════

using Xunit;
using LTAI.Agent.Indexing;
using LTAI.Agent.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tests;

public class IndexQueueWorkerTests
{
    [Fact]
    public void Constructor_WithNullIndexer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new IndexQueueWorker(null!, new TaskQueue(), NullLogger<IndexQueueWorker>.Instance));
    }

    [Fact]
    public void Constructor_WithNullQueue_Throws()
    {
        var store = new SQLiteTaskStore(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..8] + "-idx.db");
        Assert.Throws<ArgumentNullException>(() =>
            new IndexQueueWorker(null!, null!, NullLogger<IndexQueueWorker>.Instance));
    }

    [Fact]
    public async Task TaskQueue_Construction_Works()
    {
        await using var queue = new TaskQueue();
        Assert.NotNull(queue);
        Assert.Equal(0, queue.EnqueuedCount);
    }
}

public class RetryQueueWorkerTests
{
    [Fact]
    public void Constructor_WithNullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RetryQueueWorker(null!, new TaskQueue(), NullLogger<RetryQueueWorker>.Instance));
    }

    [Fact]
    public void Constructor_WithNullQueue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RetryQueueWorker(new LTAI.AI.MultiProviderChatClient(new()), null!, NullLogger<RetryQueueWorker>.Instance));
    }
}
