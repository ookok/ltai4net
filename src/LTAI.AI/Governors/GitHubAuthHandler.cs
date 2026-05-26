namespace LTAI.AI.Governors;

internal sealed class GitHubAuthHandler : DelegatingHandler
{
    private readonly string _token;
    private readonly string _allowedHost;

    public GitHubAuthHandler(string token, string allowedHost)
    {
        _token = token;
        _allowedHost = allowedHost;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_token) &&
            request.RequestUri != null &&
            request.RequestUri.Host.Equals(_allowedHost, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("token", _token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
