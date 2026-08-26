using KnockBox.Server.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The admin API's mutation guard: which requests are allowed to reach a handler at all.
///
/// This is the control that stands between an unclaimed portal and whoever can get the operator's
/// browser to make one request. The session cookie is <c>SameSite=Strict</c> and the origin is meant to
/// be loopback-bound, but neither helps for <c>auth/setup</c>: claim-on-first-use needs no cookie, and
/// the operator's browser is INSIDE the loopback boundary. The content-type check is what is left.
/// </summary>
public class AdminWriteGuardTests
{
    private static (int Status, string Error)? Guard(
        AdminApi.MediaKind media, string? contentType, long? contentLength = 0, string? site = null) =>
        AdminApi.WriteGuardRefusal(media, contentType, contentLength, site);

    [Fact]
    public void Refuses_a_text_plain_body_on_the_auth_routes()
    {
        // The CSRF shape: an HTML form with enctype="text/plain", a field named
        // {"password":"attacker-chosen","x":" and a value "}, encodes to a body that AdminPasswordRequest
        // parses happily (unknown members are ignored). It is a simple request, so there is no preflight
        // to stop it, and setup needs no cookie — so a page the operator merely visits could claim their
        // portal, permanently, from inside the loopback binding.
        var refusal = Guard(AdminApi.MediaKind.JsonRequired, "text/plain", contentLength: 64);
        Assert.NotNull(refusal);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, refusal!.Value.Status);
    }

    [Fact]
    public void Refuses_a_form_encoded_body_on_the_auth_routes()
    {
        Assert.NotNull(Guard(AdminApi.MediaKind.JsonRequired, "application/x-www-form-urlencoded", 20));
        Assert.NotNull(Guard(AdminApi.MediaKind.JsonRequired, "multipart/form-data; boundary=x", 20));
    }

    [Fact]
    public void Refuses_a_bodyless_post_on_the_auth_routes()
    {
        // An empty HTML form posts Content-Length: 0 with a form content type, which the optional-body
        // rule below lets through. These three routes have no no-arguments case, so the type is demanded
        // outright and logout-CSRF closes with it.
        Assert.NotNull(Guard(AdminApi.MediaKind.JsonRequired, contentType: null, contentLength: 0));
        Assert.NotNull(Guard(AdminApi.MediaKind.JsonRequired, "application/x-www-form-urlencoded", 0));
    }

    [Fact]
    public void Allows_json_on_the_auth_routes()
    {
        Assert.Null(Guard(AdminApi.MediaKind.JsonRequired, "application/json", 64));
        // A charset parameter is normal and must not change the answer.
        Assert.Null(Guard(AdminApi.MediaKind.JsonRequired, "application/json; charset=utf-8", 64));
    }

    [Fact]
    public void Refuses_a_chunked_body_that_is_not_json()
    {
        // Transfer-Encoding: chunked carries NO Content-Length. The optional-body rule used to read that
        // absence as "no body" and wave the request through — after which ReadJson also skipped it and
        // every handler substituted its all-defaulted record. For POST /admin/api/lobbies/close that
        // record means "close every lobby on the server", answered with success and a plausible count.
        Assert.NotNull(Guard(AdminApi.MediaKind.Json, "text/plain", contentLength: null));
        Assert.NotNull(Guard(AdminApi.MediaKind.Json, contentType: null, contentLength: null));
        Assert.Null(Guard(AdminApi.MediaKind.Json, "application/json", contentLength: null));
    }

    [Fact]
    public void Still_allows_a_body_less_request_on_the_optional_json_routes()
    {
        // Several mutations take no arguments at all, and the portal posts nothing for them.
        Assert.Null(Guard(AdminApi.MediaKind.Json, contentType: null, contentLength: 0));
    }

    [Fact]
    public void Requires_octet_stream_for_the_upload_route()
    {
        Assert.NotNull(Guard(AdminApi.MediaKind.Package, "application/json", contentLength: null));
        Assert.Null(Guard(AdminApi.MediaKind.Package, "application/octet-stream", contentLength: null));
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public void Refuses_a_request_that_says_it_came_from_elsewhere(string site)
    {
        var refusal = Guard(AdminApi.MediaKind.Json, "application/json", 10, site);
        Assert.NotNull(refusal);
        Assert.Equal(StatusCodes.Status403Forbidden, refusal!.Value.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("same-origin")]
    [InlineData("SAME-ORIGIN")]
    public void Allows_a_request_that_omits_or_matches_Sec_Fetch_Site(string? site)
    {
        // Absent passes on purpose: curl and the CI smoke test send no such header, and a header a client
        // may simply omit cannot be the boundary. The content type is.
        Assert.Null(Guard(AdminApi.MediaKind.Json, "application/json", 10, site));
    }
}
