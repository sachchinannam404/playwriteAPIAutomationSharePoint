using System.Net;
using System.Text;
using ApiAutomation.Configuration;
using ApiAutomation.Core.Interfaces;
using ApiAutomation.Data.Repositories;
using Xunit;

namespace ApiAutomation.Tests;

public sealed class SharePointRestClientTests
{
    [Fact]
    public async Task Sends_bearer_token_on_request_for_json_post()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var client = new SharePointRestClient(Settings(), new FixedSecretResolver(), http);

        await client.PostJsonAsync("sites/site/lists/list/items", new { fields = new { Title = "result" } }, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer token", handler.LastRequest!.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Includes_graph_status_and_bounded_diagnostic_when_graph_rejects_request()
    {
        using var http = new HttpClient(new CapturingHandler(HttpStatusCode.TooManyRequests, "throttled"))
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        var client = new SharePointRestClient(Settings(), new FixedSecretResolver(), http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetJsonAsync("sites/site", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("throttled", error.Message);
    }

    private static SharePointSettings Settings() => new() { AccessTokenSecret = "GRAPH_TOKEN" };

    private sealed class FixedSecretResolver : ISecretResolver
    {
        public string? Resolve(string name) => name == "GRAPH_TOKEN" ? "token" : null;
    }

    private sealed class CapturingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
