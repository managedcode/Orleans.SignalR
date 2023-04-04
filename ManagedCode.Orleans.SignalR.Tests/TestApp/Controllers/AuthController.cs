using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp.Controllers;

[Route("/[controller]")]
[ApiController]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<string>> OkAction([FromQuery]string user = null)
    {
        // create a secret key for signing the token
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("your_secret_key_here"));

        // create the signing credentials using the secret key
        var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        if (string.IsNullOrEmpty(user))
            user = Guid.NewGuid().ToString();
        
        // create a list of claims for the token
        var claims = new[] {
            new Claim(ClaimTypes.Name, user),
            new Claim(ClaimTypes.Email, user),
            new Claim(ClaimTypes.NameIdentifier, user)
        };

        // create the token descriptor with the claims, expiration time, and signing credentials
        var tokenDescriptor = new SecurityTokenDescriptor {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = signingCredentials
        };

        // create the JWT token handler
        var tokenHandler = new JwtSecurityTokenHandler();

        // create the JWT token
        var token = tokenHandler.CreateJwtSecurityToken(tokenDescriptor);

        // write the token as a string
        var jwtToken = tokenHandler.WriteToken(token);

        // return the token to the client
        return Ok(jwtToken);
    }
}