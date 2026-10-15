using DocSearch.Application.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace DocSearch.Api.Tests;

[ApiController]
[Route("api/test-errors")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "ASP.NET Core controller action methods")]
public sealed class TestErrorsController : ControllerBase
{
    [HttpGet("unhandled")]
    public IActionResult ThrowUnhandled()
    {
        throw new InvalidOperationException("Sensitive internal database connection failed at /var/secret/path");
    }

    [HttpGet("not-found")]
    public IActionResult ThrowNotFound()
    {
        throw new NotFoundException("Document 123 does not exist.");
    }

    [HttpGet("validation")]
    public IActionResult ThrowValidation()
    {
        throw new ValidationException(new Dictionary<string, string[]>
        {
            ["file"] = ["Only PDF files are accepted."]
        });
    }

    [HttpGet("conflict")]
    public IActionResult ThrowConflict()
    {
        throw new ConflictException("Document with same hash exists.", "document.duplicate");
    }

    [HttpGet("forbidden")]
    public IActionResult ThrowForbidden()
    {
        throw new ForbiddenException("You cannot edit this collection.");
    }

    [HttpGet("rate-limit")]
    public IActionResult ThrowRateLimit()
    {
        throw new RateLimitExceededException("Rate limit reached. Try in 60s.", 60);
    }
}
