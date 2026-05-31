using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Mcp;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

public class ImageDownloadHelperTests
{
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
        0x00, 0x00, 0x00, 0x0D                          // padding past the 12-byte minimum
    ];

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    // Content that reports no length (forces the streaming read-cap path).
    private sealed class NoLengthContent : HttpContent
    {
        private readonly byte[] _data;
        public NoLengthContent(byte[] data, string contentType)
        {
            _data = data;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_data, 0, _data.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static IHttpClientFactory FactoryReturning(HttpStatusCode status, byte[]? body, string? contentType, long? contentLengthOverride = null)
        => new StubHttpClientFactory(new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(status);
            if (body is not null)
            {
                var content = new ByteArrayContent(body);
                if (contentType is not null) content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                if (contentLengthOverride is not null) content.Headers.ContentLength = contentLengthOverride;
                resp.Content = content;
            }
            return resp;
        }));

    private static IHttpClientFactory FactoryReturningNoLength(byte[] body, string contentType)
        => new StubHttpClientFactory(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new NoLengthContent(body, contentType) }));

    private static async Task<IDbContextFactory<AppDbContext>> SeedPoiAsync(bool withImage = false)
    {
        var factory = TestDbHelper.CreateFactory();
        await using var db = await factory.CreateDbContextAsync();
        db.Pois.Add(new Poi { Id = 1, Name = "P", Latitude = 1, Longitude = 1, ImageUrl = withImage ? "old" : null, AddedDate = DateTime.UtcNow });
        if (withImage)
        {
            db.PoiImages.Add(new PoiImage { PoiId = 1, Data = [1, 2, 3], ContentType = "image/jpeg" });
        }
        await db.SaveChangesAsync();
        return factory;
    }

    // ---- DownloadAsync (network + validation, no DB) ----

    [Fact]
    public async Task Download_ValidPng_ReturnsBytes()
    {
        var http = FactoryReturning(HttpStatusCode.OK, PngBytes, "image/png");
        var img = await ImageDownloadHelper.DownloadAsync(http, "https://example.com/p.png", CancellationToken.None);
        img.Should().NotBeNull();
        img!.Bytes.Should().Equal(PngBytes);
        img.ContentType.Should().Be("image/png");
        img.Url.Should().Be("https://example.com/p.png");
    }

    [Fact]
    public async Task Download_Blank_ReturnsNull()
    {
        var http = FactoryReturning(HttpStatusCode.OK, PngBytes, "image/png");
        (await ImageDownloadHelper.DownloadAsync(http, "", CancellationToken.None)).Should().BeNull();
        (await ImageDownloadHelper.DownloadAsync(http, null, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("text/html")]   // wrong content-type
    public async Task Download_NonImageContentType_Throws(string ct)
    {
        var http = FactoryReturning(HttpStatusCode.OK, PngBytes, ct);
        var act = () => ImageDownloadHelper.DownloadAsync(http, "https://example.com/x", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Download_ImageContentTypeButNotRealImage_Throws()
    {
        var html = "<!DOCTYPE html><html></html>"u8.ToArray();
        var http = FactoryReturning(HttpStatusCode.OK, html, "image/png");
        var act = () => ImageDownloadHelper.DownloadAsync(http, "https://example.com/fake.png", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://host/x.png")]
    [InlineData("not-a-url")]
    public async Task Download_BadScheme_Throws(string url)
    {
        var http = FactoryReturning(HttpStatusCode.OK, PngBytes, "image/png");
        var act = () => ImageDownloadHelper.DownloadAsync(http, url, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Download_OversizeContentLength_Throws()
    {
        var http = FactoryReturning(HttpStatusCode.OK, PngBytes, "image/png", contentLengthOverride: ImageDownloadHelper.MaxBytes + 1);
        var act = () => ImageDownloadHelper.DownloadAsync(http, "https://example.com/big.png", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Download_OversizeStreamWithoutContentLength_Throws()
    {
        // No Content-Length header → exercises the ReadCappedAsync streaming cap.
        var big = new byte[ImageDownloadHelper.MaxBytes + 1024];
        Array.Copy(PngBytes, big, PngBytes.Length);
        var http = FactoryReturningNoLength(big, "image/png");
        var act = () => ImageDownloadHelper.DownloadAsync(http, "https://example.com/stream.png", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Download_HttpError_Throws()
    {
        var http = FactoryReturning(HttpStatusCode.NotFound, null, null);
        var act = () => ImageDownloadHelper.DownloadAsync(http, "https://example.com/missing.png", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- ApplyAsync (atomic DB write of bytes + ImageUrl) ----

    [Fact]
    public async Task Apply_Store_WritesBytesAndImageUrlAtomically()
    {
        var dbFactory = await SeedPoiAsync();
        var img = new DownloadedImage(PngBytes, "image/png", "https://example.com/p.png");

        var url = await ImageDownloadHelper.ApplyAsync(dbFactory, 1, img, CancellationToken.None);

        url.Should().Be("https://example.com/p.png");
        await using var db = await dbFactory.CreateDbContextAsync();
        (await db.Pois.FindAsync(1))!.ImageUrl.Should().Be("https://example.com/p.png");
        var stored = await db.PoiImages.FindAsync(1);
        stored!.Data.Should().Equal(PngBytes);
        stored.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task Apply_Clear_RemovesBytesAndNullsImageUrl()
    {
        var dbFactory = await SeedPoiAsync(withImage: true);

        var url = await ImageDownloadHelper.ApplyAsync(dbFactory, 1, null, CancellationToken.None);

        url.Should().BeNull();
        await using var db = await dbFactory.CreateDbContextAsync();
        (await db.Pois.FindAsync(1))!.ImageUrl.Should().BeNull();
        (await db.PoiImages.AnyAsync(i => i.PoiId == 1)).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_UnknownPoi_Throws()
    {
        var dbFactory = await SeedPoiAsync();
        var img = new DownloadedImage(PngBytes, "image/png", "https://example.com/p.png");
        var act = () => ImageDownloadHelper.ApplyAsync(dbFactory, 999, img, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
