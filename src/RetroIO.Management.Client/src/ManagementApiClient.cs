//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards
//

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Client;

/// <summary>
/// Typed HTTP client for the RetroFS management API.
/// </summary>
/// <remarks>
/// Every call returns the API's own <see cref="ApiResponse{T}"/> envelope, or null when the
/// request could not be completed at all (host down, DNS failure, timeout, malformed body).
/// A failed call raises <see cref="RequestFailed"/> so the caller can log or show it. The client
/// writes nothing to the console itself: a library has no business deciding where a host
/// application's diagnostics go, and the previous version's Console.WriteLine calls were
/// invisible in a windowed app anyway.
/// </remarks>
public sealed class ManagementApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    /// <summary>True when this instance created the client and must therefore dispose it.</summary>
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Raised when a request fails. First argument is the operation name, second the exception.
    /// </summary>
    /// <remarks>
    /// Handlers run on whatever thread awaited the call, so marshal to the UI thread yourself
    /// before touching controls.
    /// </remarks>
    public event Action<string, Exception>? RequestFailed;

    /// <summary>
    /// Wraps an <see cref="HttpClient"/> supplied by the caller. This is the constructor to use
    /// with IHttpClientFactory, which owns the handler lifetime and the connection pool.
    /// </summary>
    /// <param name="httpClient">Client whose BaseAddress is already the API root.</param>
    public ManagementApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = false;
    }

    /// <summary>
    /// Builds an <see cref="HttpClient"/> of its own for the given API root. Convenient for a
    /// desktop app holding one long-lived client; prefer the IHttpClientFactory constructor in
    /// anything that creates clients repeatedly.
    /// </summary>
    /// <param name="baseUrl">Root address of the management API.</param>
    /// <param name="allowUntrustedCertificates">
    /// When true, ANY server certificate is accepted, expired and self-signed ones included.
    /// It exists for a development server using a self-signed certificate on localhost and it
    /// defaults to false. The previous client had this permanently on in every configuration,
    /// which silently disabled TLS validation in release builds as well.
    /// </param>
    public ManagementApiClient(string baseUrl = "https://localhost:5001", bool allowUntrustedCertificates = false)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        // One handler, one client. The previous version built a client, threw it away and built a
        // second one, leaking the first along with its connection pool.
        HttpClientHandler handler = new HttpClientHandler();

        if (allowUntrustedCertificates)
        {
            handler.ServerCertificateCustomValidationCallback =
                (message, certificate, chain, errors) => true;
        }

        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        _ownsHttpClient = true;
    }

    // ============================ reachability ============================

    /// <summary>What a probe found at the configured address.</summary>
    public enum ProbeResult
    {
        /// <summary>Nothing answered: wrong address, or no server running there.</summary>
        NoServer = 0,

        /// <summary>Something answered, but it is not a RetroFS management API.</summary>
        NotRetroFs = 1,

        /// <summary>A RetroFS server answered and is asking for credentials.</summary>
        NeedsCredentials = 2,

        /// <summary>A RetroFS server answered and the current token is already accepted.</summary>
        Ready = 3,

        /// <summary>Answered, but the TLS certificate was refused.</summary>
        CertificateRejected = 4
    }

    /// <summary>
    /// Checks whether a RetroFS management API is answering, without sending any credentials.
    /// </summary>
    /// <returns>What was found.</returns>
    /// <remarks>
    /// This exists so the UI can establish that there IS a server before asking anyone for a user
    /// name and password. Asking first and failing afterwards tells the user their password was
    /// wrong when the truth is that nothing was listening.
    ///
    /// A 401 is the good answer here: it proves a RetroFS API is there and that authentication is
    /// switched on. Anything that is not a recognisable RetroFS response is reported as NotRetroFs
    /// rather than as a missing server, because "something else owns this port" needs a different
    /// fix from "start the server".
    /// </remarks>
    public async Task<ProbeResult> ProbeAsync()
    {
        try
        {
            using HttpRequestMessage request =
                new HttpRequestMessage(HttpMethod.Get, "/api/v1/server/status");

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return ProbeResult.NeedsCredentials;

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return ProbeResult.NeedsCredentials;

            if (response.IsSuccessStatusCode)
            {
                // Confirm it really is our API and not some other service on the port.
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return body.Contains("\"success\"", StringComparison.Ordinal)
                    ? ProbeResult.Ready
                    : ProbeResult.NotRetroFs;
            }

            return ProbeResult.NotRetroFs;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            // The server is there; we just refused its certificate. Telling the user to start a
            // server would be wrong, and so would telling them their password failed.
            return ProbeResult.CertificateRejected;
        }
        catch (HttpRequestException)
        {
            return ProbeResult.NoServer;
        }
        catch (TaskCanceledException)
        {
            return ProbeResult.NoServer;
        }
    }

    // ============================ authentication ============================

    /// <summary>The bearer token in use, or null when not logged in.</summary>
    /// <remarks>
    /// Held so the SignalR client can be handed the same token: the hub requires the identical
    /// policy the REST endpoints do.
    /// </remarks>
    public string? AccessToken { get; private set; }

    /// <summary>True once <see cref="LoginAsync"/> has succeeded.</summary>
    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken);

    /// <summary>
    /// Exchanges credentials for a bearer token and attaches it to every later request.
    /// </summary>
    /// <param name="username">RetroIDP user name.</param>
    /// <param name="password">Password.</param>
    /// <returns>
    /// The login envelope, or null when the server could not be reached. A wrong password comes
    /// back as an envelope with Success false, not as null.
    /// </returns>
    public async Task<ApiResponse<LoginResponse>?> LoginAsync(string username, string password)
    {
        ApiResponse<LoginResponse>? response = await SendAsync<LoginResponse>("login", async () =>
        {
            HttpResponseMessage message = await _httpClient
                .PostAsJsonAsync(ManagementAuth.LoginPath, new LoginRequest
                {
                    Username = username,
                    Password = password
                })
                .ConfigureAwait(false);

            // 401 still carries the envelope, so read the body either way rather than throwing on
            // the status: the caller wants "invalid username or password", not an exception.
            return await message.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>()
                .ConfigureAwait(false);
        });

        if (response is { Success: true, Data: not null })
        {
            SetAccessToken(response.Data.Token);
        }

        return response;
    }

    /// <summary>
    /// Attaches a token obtained elsewhere, or clears the current one when given null.
    /// </summary>
    /// <param name="token">Bearer token, or null to log out.</param>
    public void SetAccessToken(string? token)
    {
        AccessToken = token;

        _httpClient.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(token)
                ? null
                : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    // ============================ plumbing ============================
    // One place that knows how a request can fail, instead of the same try/catch copied into
    // every one of the twenty-odd calls below.

    /// <summary>
    /// Runs one request and turns any transport failure into null plus a
    /// <see cref="RequestFailed"/> event.
    /// </summary>
    /// <typeparam name="T">Payload type inside the response envelope.</typeparam>
    /// <param name="operation">Name used in the failure event.</param>
    /// <param name="send">The request to run.</param>
    /// <returns>The response envelope, or null if the request could not be completed.</returns>
    private async Task<ApiResponse<T>?> SendAsync<T>(string operation, Func<Task<ApiResponse<T>?>> send)
    {
        try
        {
            return await send().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            RequestFailed?.Invoke(operation, ex);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient reports its own timeout as a cancellation, not as a timeout.
            RequestFailed?.Invoke(operation, ex);
            return null;
        }
        catch (System.Text.Json.JsonException ex)
        {
            RequestFailed?.Invoke(operation, ex);
            return null;
        }
    }

    /// <summary>GET a JSON envelope.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="operation">Name used in the failure event.</param>
    /// <param name="url">Request URL, relative to the base address.</param>
    /// <returns>The response envelope, or null on failure.</returns>
    private Task<ApiResponse<T>?> GetAsync<T>(string operation, string url) =>
        SendAsync(operation, () => _httpClient.GetFromJsonAsync<ApiResponse<T>>(url));

    /// <summary>POST a body and read back a JSON envelope.</summary>
    /// <typeparam name="TBody">Request body type.</typeparam>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="operation">Name used in the failure event.</param>
    /// <param name="url">Request URL, relative to the base address.</param>
    /// <param name="body">Body to serialise.</param>
    /// <returns>The response envelope, or null on failure.</returns>
    private Task<ApiResponse<T>?> PostAsync<TBody, T>(string operation, string url, TBody body) =>
        SendAsync(operation, async () =>
        {
            HttpResponseMessage response = await _httpClient.PostAsJsonAsync(url, body).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<ApiResponse<T>>().ConfigureAwait(false);
        });

    /// <summary>POST with no body and read back a JSON envelope.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="operation">Name used in the failure event.</param>
    /// <param name="url">Request URL, relative to the base address.</param>
    /// <returns>The response envelope, or null on failure.</returns>
    private Task<ApiResponse<T>?> PostAsync<T>(string operation, string url) =>
        SendAsync(operation, async () =>
        {
            HttpResponseMessage response = await _httpClient.PostAsync(url, null).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<ApiResponse<T>>().ConfigureAwait(false);
        });

    /// <summary>PUT a body and read back a JSON envelope.</summary>
    /// <typeparam name="TBody">Request body type.</typeparam>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="operation">Name used in the failure event.</param>
    /// <param name="url">Request URL, relative to the base address.</param>
    /// <param name="body">Body to serialise.</param>
    /// <returns>The response envelope, or null on failure.</returns>
    private Task<ApiResponse<T>?> PutAsync<TBody, T>(string operation, string url, TBody body) =>
        SendAsync(operation, async () =>
        {
            HttpResponseMessage response = await _httpClient.PutAsJsonAsync(url, body).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<ApiResponse<T>>().ConfigureAwait(false);
        });

    /// <summary>DELETE and read back a JSON envelope.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="operation">Name used in the failure event.</param>
    /// <param name="url">Request URL, relative to the base address.</param>
    /// <returns>The response envelope, or null on failure.</returns>
    private Task<ApiResponse<T>?> DeleteAsync<T>(string operation, string url) =>
        SendAsync(operation, async () =>
        {
            HttpResponseMessage response = await _httpClient.DeleteAsync(url).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<ApiResponse<T>>().ConfigureAwait(false);
        });

    // ============================ server ============================

    /// <summary>Gets the server's run state.</summary>
    /// <returns>Status envelope, or null on failure.</returns>
    public Task<ApiResponse<ServerStatusResponse>?> GetServerStatusAsync() =>
        GetAsync<ServerStatusResponse>("get server status", "/api/v1/server/status");

    /// <summary>Starts the protocol servers.</summary>
    /// <returns>Action envelope, or null on failure.</returns>
    public Task<ApiResponse<ServerActionResponse>?> StartServerAsync() =>
        PostAsync<ServerActionResponse>("start server", "/api/v1/server/start");

    /// <summary>Stops the protocol servers.</summary>
    /// <returns>Action envelope, or null on failure.</returns>
    public Task<ApiResponse<ServerActionResponse>?> StopServerAsync() =>
        PostAsync<ServerActionResponse>("stop server", "/api/v1/server/stop");

    // ============================ users ============================

    /// <summary>Lists users.</summary>
    /// <returns>User list envelope, or null on failure.</returns>
    public Task<ApiResponse<UserListResponse>?> GetUsersAsync() =>
        GetAsync<UserListResponse>("get users", "/api/v1/users");

    /// <summary>Creates a user.</summary>
    /// <param name="request">User to create.</param>
    /// <returns>Created user envelope, or null on failure.</returns>
    public Task<ApiResponse<UserDto>?> CreateUserAsync(CreateUserRequest request) =>
        PostAsync<CreateUserRequest, UserDto>("create user", "/api/v1/users", request);

    /// <summary>Deletes a user.</summary>
    /// <param name="userId">Identifier of the user to delete.</param>
    /// <returns>Empty envelope, or null on failure.</returns>
    public Task<ApiResponse<object>?> DeleteUserAsync(string userId) =>
        DeleteAsync<object>("delete user", $"/api/v1/users/{userId}");

    /// <summary>Sets a user's password.</summary>
    /// <param name="userId">Identifier of the user.</param>
    /// <param name="newPassword">Password to set.</param>
    /// <returns>Empty envelope, or null on failure.</returns>
    public Task<ApiResponse<object>?> ChangePasswordAsync(string userId, string newPassword) =>
        PutAsync<ChangePasswordRequest, object>("change password", $"/api/v1/users/{userId}/password",
            new ChangePasswordRequest { NewPassword = newPassword });

    /// <summary>Updates a user.</summary>
    /// <param name="userId">Identifier of the user.</param>
    /// <param name="request">Fields to update.</param>
    /// <returns>Updated user envelope, or null on failure.</returns>
    public Task<ApiResponse<UserDto>?> UpdateUserAsync(string userId, UpdateUserRequest request) =>
        PutAsync<UpdateUserRequest, UserDto>("update user", $"/api/v1/users/{userId}", request);

    // ============================ groups ============================

    /// <summary>Lists groups.</summary>
    /// <returns>Group list envelope, or null on failure.</returns>
    public Task<ApiResponse<GroupListResponse>?> GetGroupsAsync() =>
        GetAsync<GroupListResponse>("get groups", "/api/v1/groups");

    /// <summary>Creates a group.</summary>
    /// <param name="request">Group to create.</param>
    /// <returns>Created group envelope, or null on failure.</returns>
    public Task<ApiResponse<GroupDto>?> CreateGroupAsync(CreateGroupRequest request) =>
        PostAsync<CreateGroupRequest, GroupDto>("create group", "/api/v1/groups", request);

    /// <summary>Deletes a group.</summary>
    /// <param name="groupId">Identifier of the group to delete.</param>
    /// <returns>Empty envelope, or null on failure.</returns>
    public Task<ApiResponse<object>?> DeleteGroupAsync(string groupId) =>
        DeleteAsync<object>("delete group", $"/api/v1/groups/{groupId}");

    // ============================ firewall ============================

    /// <summary>Lists firewall rules.</summary>
    /// <returns>Rule list envelope, or null on failure.</returns>
    public Task<ApiResponse<FirewallRulesResponse>?> GetFirewallRulesAsync() =>
        GetAsync<FirewallRulesResponse>("get firewall rules", "/api/v1/firewall/rules");

    /// <summary>Creates a firewall rule.</summary>
    /// <param name="request">Rule to create.</param>
    /// <returns>Created rule envelope, or null on failure.</returns>
    public Task<ApiResponse<FirewallRuleDto>?> CreateFirewallRuleAsync(CreateFirewallRuleRequest request) =>
        PostAsync<CreateFirewallRuleRequest, FirewallRuleDto>("create firewall rule",
            "/api/v1/firewall/rules", request);

    /// <summary>Deletes a firewall rule.</summary>
    /// <param name="ruleId">Identifier of the rule to delete.</param>
    /// <returns>Empty envelope, or null on failure.</returns>
    public Task<ApiResponse<object>?> DeleteFirewallRuleAsync(string ruleId) =>
        DeleteAsync<object>("delete firewall rule", $"/api/v1/firewall/rules/{ruleId}");

    // ============================ monitoring ============================

    /// <summary>Gets TNFS traffic counters.</summary>
    /// <returns>Statistics envelope, or null on failure.</returns>
    public Task<ApiResponse<TnfsStatisticsResponse>?> GetTnfsStatisticsAsync() =>
        GetAsync<TnfsStatisticsResponse>("get TNFS statistics", "/api/v1/tnfs/statistics");

    /// <summary>Gets recent Econet commands and file transfers.</summary>
    /// <param name="count">How many of each to return.</param>
    /// <returns>Activity envelope, or null on failure.</returns>
    public Task<ApiResponse<EconetActivityResponse>?> GetEconetActivityAsync(int count = 30) =>
        GetAsync<EconetActivityResponse>("get Econet activity", $"/api/v1/econet/activity?count={count}");

    /// <summary>Gets Econet traffic counters.</summary>
    /// <returns>Statistics envelope, or null on failure.</returns>
    public Task<ApiResponse<EconetStatisticsResponse>?> GetEconetStatisticsAsync() =>
        GetAsync<EconetStatisticsResponse>("get Econet statistics", "/api/v1/econet/statistics");

    /// <summary>Gets the Econet sessions currently logged in.</summary>
    /// <returns>Sessions envelope, or null on failure.</returns>
    public Task<ApiResponse<EconetSessionsResponse>?> GetEconetSessionsAsync() =>
        GetAsync<EconetSessionsResponse>("get Econet sessions", "/api/v1/econet/sessions");

    /// <summary>Gets the TNFS sessions currently mounted.</summary>
    /// <returns>Sessions envelope, or null on failure.</returns>
    public Task<ApiResponse<TnfsSessionsResponse>?> GetTnfsSessionsAsync() =>
        GetAsync<TnfsSessionsResponse>("get TNFS sessions", "/api/v1/tnfs/sessions");

    /// <summary>Gets recent log entries.</summary>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <returns>Log list envelope, or null on failure.</returns>
    public Task<ApiResponse<LogListResponse>?> GetLogsAsync(int limit = 100) =>
        GetAsync<LogListResponse>("get logs", $"/api/v1/logs?limit={limit}");

    // ============================ settings ============================

    /// <summary>Gets the TNFS settings.</summary>
    /// <returns>Settings envelope, or null on failure.</returns>
    public Task<ApiResponse<TnfsSettingsDto>?> GetTnfsSettingsAsync() =>
        GetAsync<TnfsSettingsDto>("get TNFS settings", "/api/v1/settings/tnfs");

    /// <summary>Updates the TNFS settings.</summary>
    /// <param name="request">Fields to update.</param>
    /// <returns>Updated settings envelope, or null on failure.</returns>
    public Task<ApiResponse<TnfsSettingsDto>?> UpdateTnfsSettingsAsync(UpdateTnfsSettingsRequest request) =>
        PutAsync<UpdateTnfsSettingsRequest, TnfsSettingsDto>("update TNFS settings",
            "/api/v1/settings/tnfs", request);

    /// <summary>Gets the Econet settings.</summary>
    /// <returns>Settings envelope, or null on failure.</returns>
    public Task<ApiResponse<EconetSettingsDto>?> GetEconetSettingsAsync() =>
        GetAsync<EconetSettingsDto>("get Econet settings", "/api/v1/settings/econet");

    /// <summary>Updates the Econet settings.</summary>
    /// <param name="request">Fields to update.</param>
    /// <returns>Updated settings envelope, or null on failure.</returns>
    public Task<ApiResponse<EconetSettingsDto>?> UpdateEconetSettingsAsync(UpdateEconetSettingsRequest request) =>
        PutAsync<UpdateEconetSettingsRequest, EconetSettingsDto>("update Econet settings",
            "/api/v1/settings/econet", request);

    // ============================ volumes ============================

    /// <summary>Lists mounted volumes.</summary>
    /// <returns>Volume list envelope, or null on failure.</returns>
    public Task<ApiResponse<VolumeListResponse>?> GetVolumesAsync() =>
        GetAsync<VolumeListResponse>("get volumes", "/api/v1/volumes");

    /// <summary>Disposes the HttpClient, but only if this instance was the one that created it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
