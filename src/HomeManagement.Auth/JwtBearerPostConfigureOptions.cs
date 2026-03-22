using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace HomeManagement.Auth;

/// <summary>
/// Injects token validation parameters into JwtBearer after JwtTokenService has been configured.
/// </summary>
public sealed class JwtBearerPostConfigureOptions : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly JwtTokenService _jwtTokenService;

    public JwtBearerPostConfigureOptions(JwtTokenService jwtTokenService)
    {
        _jwtTokenService = jwtTokenService;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        options.TokenValidationParameters = _jwtTokenService.GetValidationParameters();
    }
}