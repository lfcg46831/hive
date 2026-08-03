using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Hive.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hive.Api.Authorization;

internal sealed class StaticOrganizationBearerHandler :
    AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<OrganizationAuthorizationOptions> _authorizationOptions;

    public StaticOrganizationBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IOptionsMonitor<OrganizationAuthorizationOptions> authorizationOptions)
        : base(schemeOptions, loggerFactory, encoder)
    {
        _authorizationOptions = authorizationOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values) ||
            values.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!AuthenticationHeaderValue.TryParse(values[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return Task.FromResult(AuthenticateResult.Fail("A valid bearer token is required."));
        }

        var organizationIds = ResolveOrganizations(header.Parameter);
        if (organizationIds.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("The bearer token is not recognized."));
        }

        var claims = organizationIds.Select(static organizationId =>
            new Claim(
                OrganizationAuthorizationDefaults.OrganizationClaimType,
                organizationId.Value));
        var identity = new ClaimsIdentity(
            claims,
            OrganizationAuthorizationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            OrganizationAuthorizationDefaults.AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Bearer token required")
            .ExecuteAsync(Context)
            .ConfigureAwait(false);
    }

    private List<OrganizationId> ResolveOrganizations(string suppliedToken)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        var organizations = new List<OrganizationId>();
        foreach (var credential in _authorizationOptions.CurrentValue.Credentials)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(credential.Token);
            if (expectedBytes.Length == suppliedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
            {
                organizations.AddRange(credential.OrganizationIds.Select(OrganizationId.From));
            }
        }

        return organizations;
    }
}
