using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Export;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LucidCartographer.Tests.Export;

public class ExportPipelineTests
{
    private static Poi PoiWithUrl(int id, string? url) => new() { Id = id, Name = $"P{id}", GoogleMapsUrl = url };

    // ---- ExportJobQueue (channel-backed) ----

    [Fact]
    public async Task Enqueue_WritesToChannel_AndPublishesQueuedWithCollectionId()
    {
        var status = new ExportJobStatusService();
        var sut = new ExportJobQueue(status);

        sut.Enqueue(new ExportJobPayload { CollectionId = 7, ListName = "Trip" });

        status.Current.State.Should().Be(ExportJobState.Queued);
        status.Current.Message.Should().Contain("Trip");
        status.Current.CollectionId.Should().Be(7);

        sut.Reader.TryRead(out var payload).Should().BeTrue();
        payload!.CollectionId.Should().Be(7);
        payload.ListName.Should().Be("Trip");
        await Task.CompletedTask;
    }

    // ---- ExportJobStatusService ----

    [Fact]
    public void StatusService_ReplaysLatestToNewSubscribers()
    {
        var status = new ExportJobStatusService();
        status.Publish(new ExportJobStatus(ExportJobState.Running, "halfway"));

        ExportJobStatus? seen = null;
        using var _ = status.Changes.Subscribe(s => seen = s);

        seen!.State.Should().Be(ExportJobState.Running);
        seen.Message.Should().Be("halfway");
    }

    // ---- ExportJobProcessor ----

    private static ExportJobProcessor CreateProcessor(
        IReadOnlyList<Poi> pois, IGoogleMapsListExporter exporter, ExportJobStatusService status)
    {
        var poi = new Mock<IPoiService>();
        poi.Setup(p => p.GetPoisByCollectionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pois);
        return new ExportJobProcessor(poi.Object, exporter, status, NullLogger<ExportJobProcessor>.Instance);
    }

    [Fact]
    public async Task Run_FiltersNonNullUrls_AndCompletesWithReport()
    {
        IReadOnlyList<string>? passedUrls = null;
        var exporter = new Mock<IGoogleMapsListExporter>();
        exporter.Setup(e => e.ExportAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<Action<ExportProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, Action<ExportProgress>?, CancellationToken>(
                (_, urls, _, _) => passedUrls = urls)
            .ReturnsAsync((string name, IReadOnlyList<string> urls, Action<ExportProgress>? _, CancellationToken _) =>
                new ExportRunReport(name,
                    [.. urls.Select(u => new ExportPlaceResult(u, null, ExportOutcome.Added, null))]));

        var status = new ExportJobStatusService();
        var pois = new List<Poi> { PoiWithUrl(1, "u1"), PoiWithUrl(2, null), PoiWithUrl(3, "u3") };
        var sut = CreateProcessor(pois, exporter.Object, status);

        await sut.RunAsync(new ExportJobPayload { CollectionId = 1, ListName = "Trip" }, CancellationToken.None);

        passedUrls.Should().Equal("u1", "u3"); // null GoogleMapsUrl filtered out
        status.Current.State.Should().Be(ExportJobState.Completed);
        status.Current.Result!.Added.Should().Be(2);
        status.Current.CollectionId.Should().Be(1);
    }

    [Fact]
    public async Task Run_NoEligiblePlaces_CompletesWithoutCallingExporter()
    {
        var exporter = new Mock<IGoogleMapsListExporter>();
        var status = new ExportJobStatusService();
        var pois = new List<Poi> { PoiWithUrl(1, null), PoiWithUrl(2, "") };
        var sut = CreateProcessor(pois, exporter.Object, status);

        await sut.RunAsync(new ExportJobPayload { CollectionId = 1, ListName = "Trip" }, CancellationToken.None);

        exporter.Verify(e => e.ExportAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<Action<ExportProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
        status.Current.State.Should().Be(ExportJobState.Completed);
        status.Current.Message.Should().Contain("no places");
    }

    [Fact]
    public async Task Run_ExporterThrows_PublishesFailed()
    {
        var exporter = new Mock<IGoogleMapsListExporter>();
        exporter.Setup(e => e.ExportAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<Action<ExportProgress>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var status = new ExportJobStatusService();
        var sut = CreateProcessor([PoiWithUrl(1, "u1")], exporter.Object, status);

        await sut.RunAsync(new ExportJobPayload { CollectionId = 1, ListName = "Trip" }, CancellationToken.None);

        status.Current.State.Should().Be(ExportJobState.Failed);
        status.Current.Error.Should().Be("boom");
    }

    [Fact]
    public async Task Run_Cancelled_PublishesFailed_AndRethrows()
    {
        var exporter = new Mock<IGoogleMapsListExporter>();
        exporter.Setup(e => e.ExportAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<Action<ExportProgress>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var status = new ExportJobStatusService();
        var sut = CreateProcessor([PoiWithUrl(1, "u1")], exporter.Object, status);

        var act = async () => await sut.RunAsync(
            new ExportJobPayload { CollectionId = 1, ListName = "Trip" }, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        status.Current.State.Should().Be(ExportJobState.Failed);
    }

    // ---- GoogleBrowserLock ----

    [Fact]
    public async Task BrowserLock_TryAcquire_FailsWhileHeld_SucceedsAfterRelease()
    {
        var sut = new GoogleBrowserLock();

        var first = await sut.TryAcquireAsync();
        first.Should().NotBeNull();

        (await sut.TryAcquireAsync()).Should().BeNull(); // busy

        first!.Dispose();
        var third = await sut.TryAcquireAsync();
        third.Should().NotBeNull();
        third!.Dispose();
    }
}
