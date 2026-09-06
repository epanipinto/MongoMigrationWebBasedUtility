using OnlineMongoMigrationProcessor.Workers;
using Xunit;

namespace MongoMigrationWebApp.Tests;

/// <summary>
/// Progress-checkpoint cadence for the MongoDriver copy path.
///
/// The regression these guard against: with only the doc-count threshold, a collection smaller
/// than <c>_saveProgressEveryNDocs</c> never completes a full checkpoint interval, so it reports
/// dumpPercent 0 for its entire run and then jumps to 100. Measured live on a 24,796-document
/// collection that took 93.8 minutes.
/// </summary>
public class DocumentCopyProgressCadenceTests
{
    private const int PageSize = 5000;
    private const int SaveEvery = 250_000;

    [Theory]
    [InlineData(24_796)]   // row C - the collection that reported 0% for 93.8 minutes
    [InlineData(2_752)]    // row B
    [InlineData(17_007)]   // AiringsByZipCode
    [InlineData(83_773)]   // fs.chunks
    public void Reports_progress_for_collections_below_the_doc_threshold(long docCount)
    {
        int pagesPerSave = DocumentCopyWorker.CalculatePagesPerSave(docCount, PageSize, SaveEvery);
        long totalPages = docCount / PageSize;

        Assert.True(
            pagesPerSave <= totalPages || totalPages == 0,
            $"{docCount:N0} docs = {totalPages} pages but checkpoints every {pagesPerSave} pages, " +
            "so progress would never be written mid-run.");
    }

    [Fact]
    public void Sub_threshold_collection_checkpoints_at_least_once_before_completion()
    {
        // 24,796 docs at 5,000/page = 4 full pages. The old cadence was 50, so batchCount never
        // reached it and the in-loop UpdateProgress never fired.
        int pagesPerSave = DocumentCopyWorker.CalculatePagesPerSave(24_796, PageSize, SaveEvery);
        Assert.True(pagesPerSave <= 4, $"expected <= 4 pages between checkpoints, got {pagesPerSave}");
    }

    [Fact]
    public void Large_collections_keep_the_doc_count_cadence()
    {
        // 10M docs: the doc threshold (50 pages = 250k docs) must still win so disk I/O stays bounded.
        int pagesPerSave = DocumentCopyWorker.CalculatePagesPerSave(10_000_000, PageSize, SaveEvery);
        Assert.Equal(SaveEvery / PageSize, pagesPerSave);
    }

    [Fact]
    public void Never_returns_a_non_positive_cadence()
    {
        // batchCount % pagesPerSave would throw on zero.
        foreach (long n in new long[] { 0, 1, 99, 5000 })
        {
            Assert.True(DocumentCopyWorker.CalculatePagesPerSave(n, PageSize, SaveEvery) >= 1);
        }
        Assert.True(DocumentCopyWorker.CalculatePagesPerSave(1000, 0, 0) >= 1);
    }
}
