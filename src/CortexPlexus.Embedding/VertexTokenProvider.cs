using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;

namespace CortexPlexus.Embedding;

/// <summary>
/// Supplies OAuth2 access tokens for Vertex AI from a Google service-account
/// identity (ADR-029). Ported from the CortexBridge ADR-027 reference
/// implementation (<c>tools/vertex-oauth/VertexTokenProvider.cs</c>) — the
/// pattern is distributed by copy, not as a package, so a fix there does not
/// propagate here automatically.
///
/// <para><b>Why this exists.</b> The project-scoped Vertex endpoint
/// (<c>{loc}-aiplatform.googleapis.com/v1/projects/{p}/locations/{l}/...</c>)
/// rejects credentials passed on the query string, with
/// <c>401 UNAUTHENTICATED — "API keys are not supported by this API. Expected
/// OAuth2 access token or other authentication credentials that assert a
/// principal."</c> The credentials that DO work as a query parameter are Vertex
/// Express Mode ones, which can only be minted through the web console. A
/// service-account identity therefore has exactly one route in: an
/// <c>Authorization</c> header.</para>
///
/// <para><b>Why the library and not a hand-rolled JWT flow.</b> The underlying
/// <see cref="ServiceAccountCredential"/> signs the assertion, exchanges it at
/// the token endpoint, caches the result, and refreshes it shortly before the
/// ~1 h expiry. Callers may therefore call <see cref="GetAccessTokenAsync"/> on
/// every request without adding their own cache — measured 761 ms for the first
/// acquisition and 0 ms for the second (ADR-027 verification, 2026-07-20).</para>
///
/// <para><b>Never log the value returned by <see cref="GetAccessTokenAsync"/>.</b>
/// It is a bearer credential: whoever holds it can act as the service account
/// until it expires. Log <see cref="TokenFingerprint"/> instead when you need to
/// prove two calls reused one token.</para>
/// </summary>
public sealed class VertexTokenProvider
{
    /// <summary>
    /// The scope every Vertex AI call needs. Vertex does not define a narrower
    /// scope — <c>cloud-platform</c> is what Google documents for aiplatform,
    /// so scope cannot be used to constrain blast radius here. Constrain it
    /// with the service account's IAM roles instead.
    /// </summary>
    public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    private readonly string? _credentialPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleCredential? _credential;

    /// <param name="credentialPath">
    /// Path to a service-account JSON key file
    /// (<see cref="EmbeddingOptions.VertexCredentialPath"/>). When <c>null</c>,
    /// Application Default Credentials are used — i.e. the path in
    /// <c>GOOGLE_APPLICATION_CREDENTIALS</c>, or the ambient identity when
    /// running inside GCP.
    /// <para>
    /// Prefer the explicit path: ADC loads whatever credential shape the file
    /// declares, whereas the explicit branch below pins the type.
    /// </para>
    /// </param>
    public VertexTokenProvider(string? credentialPath = null)
    {
        _credentialPath = string.IsNullOrWhiteSpace(credentialPath) ? null : credentialPath;
    }

    /// <summary>
    /// Returns a currently-valid access token, minting one only when the cached
    /// token is absent or near expiry. Safe to call per request.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var cred = _credential ?? await LoadCredentialAsync(ct).ConfigureAwait(false);
        return await ((ITokenAccess)cred)
            .GetAccessTokenForRequestAsync(cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Loads (and memoises) the credential. Deliberately NOT a
    /// <c>Lazy&lt;Task&lt;T&gt;&gt;</c>: that shape caches a FAILED task too, so a
    /// credential file that is briefly unreadable — a bind mount not ready at
    /// container boot, a permission fixed thirty seconds later — poisons the
    /// process until it restarts. This retries on the next call and only
    /// memoises success, while the semaphore still collapses concurrent
    /// first-callers into one file read.
    /// </summary>
    private async Task<GoogleCredential> LoadCredentialAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credential is not null) return _credential;

            GoogleCredential cred;
            if (_credentialPath is null)
            {
                cred = await GoogleCredential.GetApplicationDefaultAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // Deliberately NOT GoogleCredential.FromFile — it is deprecated as
                // a "potential security risk". It accepts ANY credential shape the
                // JSON declares, and one of those shapes (external account) can be
                // configured to obtain its token by RUNNING AN EXTERNAL COMMAND. A
                // swapped or tampered credential file could therefore turn a token
                // fetch into arbitrary code execution.
                //
                // The generic factory pins the type up front, so a file that is not
                // a service-account key fails to load instead of loading as
                // something more powerful.
                var sa = await CredentialFactory
                    .FromFileAsync<ServiceAccountCredential>(_credentialPath, ct)
                    .ConfigureAwait(false);
                cred = sa.ToGoogleCredential();
            }

            // A service-account credential MUST be scoped before use; without
            // this the token request is rejected. User credentials arrive
            // pre-scoped, hence the guard rather than an unconditional call.
            if (cred.IsCreateScopedRequired)
                cred = cred.CreateScoped(CloudPlatformScope);

            _credential = cred;
            return cred;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A short, non-reversible fingerprint of a token, for proving in logs that
    /// two requests reused one token WITHOUT writing the token down.
    /// </summary>
    public static string TokenFingerprint(string token)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }
}

/// <summary>
/// Attaches the Vertex OAuth2 token to every outgoing request. Registered on the
/// <see cref="VertexEmbeddingService"/> <c>HttpClient</c> only when
/// <see cref="EmbeddingOptions.VertexUsesOAuth"/> is true — under express-mode
/// (API key on the query string) the handler must NOT be in the pipeline, since
/// there is no service-account identity to mint a token from.
/// </summary>
/// <remarks>
/// The rest of the connector needs no changes: URL shape, request bodies, model
/// ids and <c>outputDimensionality</c> all behave identically to the
/// query-parameter form — only the auth carrier moves.
/// </remarks>
public sealed class VertexAuthHandler : DelegatingHandler
{
    private readonly Func<CancellationToken, Task<string>> _tokenSource;

    public VertexAuthHandler(VertexTokenProvider provider)
        : this(provider.GetAccessTokenAsync) { }

    /// <summary>
    /// Test seam: a real <see cref="VertexTokenProvider"/> cannot mint a token
    /// without a live credential and a network round trip, so tests supply the
    /// token directly to assert the header this handler stamps.
    /// </summary>
    internal VertexAuthHandler(Func<CancellationToken, Task<string>> tokenSource)
    {
        _tokenSource = tokenSource;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenSource(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
