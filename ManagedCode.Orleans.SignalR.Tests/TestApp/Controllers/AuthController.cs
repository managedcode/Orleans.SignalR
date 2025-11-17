using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp.Controllers;

[Route("/[controller]")]
[ApiController]
[AllowAnonymous]
public class AuthController(JwtSecurityTokenHandler tokenHandler) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<string>> OkAction([FromQuery] string? user = null)
    {
        await Task.Yield();

        if (string.IsNullOrEmpty(user))
        {
            user = Guid.NewGuid().ToString("N");
        }

        var claims = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user),
            new Claim(ClaimTypes.Email, user),
            new Claim(ClaimTypes.NameIdentifier, user)
        });

        SignIn(new ClaimsPrincipal(claims));

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = claims,
            Expires = DateTime.UtcNow.AddDays(7),
            Issuer = "YourIssuer",
            Audience = "YourAudience",
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(HttpHostProgram.GetEncryptionKey()), SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);

        return tokenHandler.WriteToken(token);
    }
}
