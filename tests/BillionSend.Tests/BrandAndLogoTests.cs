using System.Text;
using BillionSend.Tests.Support;

namespace BillionSend.Tests;

/// <summary>
/// What the API added with the brand defaults and the unsubscribe page's own settings: <c>/brand</c>
/// and its logo, a domain's logo upload and removal, <c>support_url</c> / <c>powered_by</c> /
/// <c>powered_by_effective</c> on a domain's <c>unsubscribe_page</c>, and the broadcasts'
/// <c>dormant_check</c> / <c>include_dormant</c>.
/// </summary>
public sealed class BrandAndLogoTests : MockTest
{
    private const string BrandKey = "GET /brand";
    private const string BrandLogoKey = "PUT /brand/logo";
    private const string DomainLogoKey = "PUT /domains/{id}/unsubscribe-page/logo";

    private static JsonObject Brand(Action<JsonObject>? change = null)
    {
        var brand = new JsonObject
        {
            ["object"] = "brand",
            ["logo_url"] = "https://assets.billionsend.com/logos/team-4f2c.png?v=1763600000",
            ["accent_color"] = "#2B44FF",
            ["font"] = "serif",
            ["company_address"] = "Acme Inc., 500 Market St, San Francisco, CA 94105",
            ["powered_by"] = false,
            ["powered_by_locked"] = false,
            ["show_topics"] = true,
            ["branding_locked"] = false,
        };
        change?.Invoke(brand);
        return brand;
    }

    [Fact]
    public async Task ReadsTheBrandWithItsNullableFieldsAndTheTwoReadOnlyLocks()
    {
        Mock.Reply(BrandKey, 200, Brand());
        Mock.Reply(BrandKey, 200, Brand(b =>
        {
            b["logo_url"] = null;
            b["accent_color"] = null;
            b["font"] = null;
            b["company_address"] = null;
            b["powered_by"] = true;
            b["powered_by_locked"] = true;
            b["branding_locked"] = true;
        }));
        using var client = Client();

        var set = await client.Brand.GetAsync();
        var free = await client.Brand.GetAsync();

        Assert.Equal(["GET /brand", "GET /brand"], Mock.Hits.Select(hit => hit.Key));
        Assert.All(Mock.Hits, hit => Assert.Null(hit.Body));
        Assert.Equal("brand", (string?)set!["object"]);
        Assert.Equal("#2B44FF", (string?)set["accent_color"]);
        Assert.Equal("serif", (string?)set["font"]);
        Assert.False((bool)set["powered_by"]!);
        Assert.False((bool)set["powered_by_locked"]!);
        Assert.Null(free!["logo_url"]);
        Assert.Null(free["accent_color"]);
        Assert.Null(free["font"]);
        Assert.Null(free["company_address"]);
        Assert.True((bool)free["powered_by"]!);
        Assert.True((bool)free["powered_by_locked"]!);
        Assert.True((bool)free["branding_locked"]!);
    }

    [Fact]
    public async Task UpdatesTheBrandWithTheFieldsGivenAndNullToClearOne()
    {
        Mock.Reply("PATCH /brand", 200, Brand(b => b["company_address"] = null));
        using var client = Client();

        var answer = await client.Brand.UpdateAsync(new JsonObject
        {
            ["accent_color"] = "#2B44FF",
            ["font"] = "serif",
            ["company_address"] = null,
            ["powered_by"] = false,
            ["show_topics"] = true,
        });

        var hit = Assert.Single(Mock.Hits);
        Assert.Equal("PATCH /brand", hit.Key);
        Assert.Equal("application/json", hit.Header("content-type"));
        Assert.Equal(
            """{"accent_color":"#2B44FF","font":"serif","company_address":null,"powered_by":false,"show_topics":true}""",
            hit.Body);
        Assert.Null(answer!["company_address"]);
        Assert.Equal("serif", (string?)answer["font"]);
    }

    [Fact]
    public async Task UpdatesOnlyTheFieldsOfAnAnonymousObject()
    {
        Mock.Reply("PATCH /brand", 200, Brand(b => b["show_topics"] = false));
        using var client = Client();

        var answer = await client.Brand.UpdateAsync(new { show_topics = false });

        Assert.Equal("""{"show_topics":false}""", Assert.Single(Mock.Hits).Body);
        Assert.False((bool)answer!["show_topics"]!);
    }

    [Fact]
    public async Task RefusesABrandWriteFromAKeyHeldToOneDomain()
    {
        Mock.Error("PATCH /brand", 403, "restricted_api_key", "This key is held to one domain.");
        using var client = Client();

        var error = await Assert.ThrowsAsync<PermissionDeniedException>(() => client.Brand.UpdateAsync(new { powered_by = false }));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("restricted_api_key", error.Name);
        Assert.Single(Mock.Hits);
    }

    // The upload as the server receives it, with the file's own bytes.
    private static Probe.Seen OneRequest(Probe probe) => Assert.Single(probe.Requests);

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Checks the multipart body part by part: exactly one part, named file, with this name, type and bytes.</summary>
    private static void AssertOneFilePart(Probe.Seen seen, string fileName, string contentType, byte[] bytes)
    {
        var header = seen.Header("content-type")!;
        Assert.StartsWith("multipart/form-data; boundary=", header);
        var boundary = header["multipart/form-data; boundary=".Length..].Trim('"');
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var body = seen.Body!;

        // --boundary CRLF headers CRLF CRLF bytes CRLF --boundary-- CRLF
        var first = IndexOf(body, delimiter);
        Assert.Equal(0, first);
        var headersEnd = IndexOf(body, "\r\n\r\n"u8.ToArray());
        var partHeaders = Encoding.UTF8.GetString(body, delimiter.Length, headersEnd - delimiter.Length);
        Assert.Contains($"name=\"file\"; filename=\"{fileName}\"", partHeaders);
        Assert.Contains($"Content-Type: {contentType}", partHeaders);
        Assert.Equal(1, partHeaders.Split("Content-Disposition:").Length - 1);

        var contentStart = headersEnd + 4;
        Assert.True(body.AsSpan(contentStart, bytes.Length).SequenceEqual(bytes), "the file's bytes are not sent as they are");
        var tail = Encoding.ASCII.GetString(body, contentStart + bytes.Length, body.Length - contentStart - bytes.Length);
        Assert.Equal($"\r\n--{boundary}--\r\n", tail);
        Assert.Equal(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), seen.Header("content-length"));
    }

    [Fact]
    public async Task UploadsTheTeamLogoAsOneFilePartAndReadsTheNewAddress()
    {
        var probe = new Probe
        {
            Answer = _ => Probe.Reply(200, """{"object":"logo","logo_url":"https://assets.billionsend.com/logos/team-4f2c.png?v=1763600123"}"""),
        };
        using var client = probe.Client();

        var answer = await client.Brand.Logo.UploadAsync(new Dictionary<string, object?>
        {
            ["file"] = new FileUpload("acme-logo.png", TestImages.Png, "image/png"),
        });

        var seen = OneRequest(probe);
        Assert.Equal("PUT", seen.Method);
        Assert.Equal("/brand/logo", seen.Target);
        AssertOneFilePart(seen, "acme-logo.png", "image/png", TestImages.Png);
        Assert.Equal("logo", (string?)answer!["object"]);
        Assert.Equal("https://assets.billionsend.com/logos/team-4f2c.png?v=1763600123", (string?)answer["logo_url"]);
    }

    [Fact]
    public async Task UploadsADomainLogoToThatDomainsPathAsAJpegFileFromDisk()
    {
        var bytes = TestImages.JpegHeader.Concat(Enumerable.Range(0, 200).Select(i => (byte)(i * 7))).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}-brand mark.jpg");
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var probe = new Probe
            {
                Answer = _ => Probe.Reply(200, """{"object":"logo","logo_url":"https://assets.billionsend.com/logos/dom-91ab.jpg?v=1763600456"}"""),
            };
            using var client = probe.Client();

            var file = await FileUpload.FromFileAsync(path, "image/jpeg");
            var answer = await client.Domains.UnsubscribePage.Logo.UploadAsync(Id, new Dictionary<string, object?> { ["file"] = file });

            var seen = OneRequest(probe);
            Assert.Equal("PUT", seen.Method);
            Assert.Equal($"/domains/{Id}/unsubscribe-page/logo", seen.Target);
            AssertOneFilePart(seen, Path.GetFileName(path), "image/jpeg", bytes);
            Assert.Equal("https://assets.billionsend.com/logos/dom-91ab.jpg?v=1763600456", (string?)answer!["logo_url"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ChecksTheUploadAgainstTheSchemaAndAnswersWhatTheMockSends()
    {
        Mock.ReplyExample(BrandLogoKey);
        Mock.ReplyExample(DomainLogoKey);
        using var client = Client();
        var form = new Dictionary<string, object?> { ["file"] = new FileUpload("logo.webp", "RIFF....WEBPVP8 "u8.ToArray(), "image/webp") };

        var team = await client.Brand.Logo.UploadAsync(form);
        var domain = await client.Domains.UnsubscribePage.Logo.UploadAsync(Id, form);

        foreach (var (hit, answer) in new[] { (Mock.HitsFor(BrandLogoKey).Single(), team), (Mock.HitsFor(DomainLogoKey).Single(), domain) })
        {
            var parsed = ContractMock.ParseMultipart(hit.Header("content-type")!, hit.Body!, out var error);
            Assert.Null(error);
            Assert.Equal(["file"], parsed!.Select(field => field.Key));
            Assert.Equal("logo.webp", (string?)parsed!["file"]);
            Assert.Contains("Content-Type: image/webp", hit.Body);
            Assert.True(JsonNode.DeepEquals(hit.Response, answer));
            Assert.Equal("logo", (string?)answer!["object"]);
            Assert.False(string.IsNullOrEmpty((string?)answer["logo_url"]));
        }

        Assert.Equal($"/domains/{Id}/unsubscribe-page/logo", Mock.HitsFor(DomainLogoKey).Single().Url.AbsolutePath);
    }

    [Fact]
    public async Task RemovesTheTeamLogoAndADomainLogoAndReadsDeletedTrue()
    {
        Mock.Reply("DELETE /brand/logo", 200, new JsonObject { ["object"] = "logo", ["deleted"] = true });
        Mock.Reply("DELETE /domains/{id}/unsubscribe-page/logo", 200, new JsonObject { ["object"] = "logo", ["deleted"] = true });
        using var client = Client();

        var team = await client.Brand.Logo.DeleteAsync();
        var domain = await client.Domains.UnsubscribePage.Logo.DeleteAsync(Id);

        Assert.Equal(["DELETE /brand/logo", "DELETE /domains/{id}/unsubscribe-page/logo"], Mock.Hits.Select(hit => hit.Key));
        Assert.Equal($"/domains/{Id}/unsubscribe-page/logo", Mock.Hits[1].Url.AbsolutePath);
        Assert.All(Mock.Hits, hit => Assert.Null(hit.Body));
        foreach (var answer in new[] { team, domain })
        {
            Assert.Equal("logo", (string?)answer!["object"]);
            Assert.True((bool)answer["deleted"]!);
        }
    }

    [Fact]
    public async Task ThrowsTheTypeOfEachDeclaredLogoUploadRefusal()
    {
        Mock.Error(BrandLogoKey, 404, "not_found", "This deployment stores no logos.");
        Mock.Error(BrandLogoKey, 422, "validation_error", "The file is not a PNG, JPEG or WebP image.");
        Mock.Error(DomainLogoKey, 422, "validation_error", "The file is larger than 2 MiB.");
        Mock.Error(DomainLogoKey, 403, "restricted_api_key", "Refused to a sending-only key.");
        using var client = Client();
        var form = new Dictionary<string, object?> { ["file"] = new FileUpload("logo.png", TestImages.Png, "image/png") };

        Assert.Equal(404, (await Assert.ThrowsAsync<NotFoundException>(() => client.Brand.Logo.UploadAsync(form))).StatusCode);
        var unsupported = await Assert.ThrowsAsync<ValidationException>(() => client.Brand.Logo.UploadAsync(form));
        Assert.Equal("The file is not a PNG, JPEG or WebP image.", unsupported.Message);
        await Assert.ThrowsAsync<ValidationException>(() => client.Domains.UnsubscribePage.Logo.UploadAsync(Id, form));
        var forbidden = await Assert.ThrowsAsync<PermissionDeniedException>(() => client.Domains.UnsubscribePage.Logo.UploadAsync(Id, form));
        Assert.Equal("restricted_api_key", forbidden.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatsARateLimitedLogoUploadWithTheSameFileButNotAServerFailure(bool teamLogo)
    {
        var attempts = 0;
        var probe = new Probe
        {
            Answer = _ => ++attempts switch
            {
                1 => RateLimited(),
                _ => Probe.Reply(200, """{"object":"logo","logo_url":"https://assets.billionsend.com/logos/x.png?v=2"}"""),
            },
        };
        using var client = probe.Client();
        Task<JsonNode?> Upload() => teamLogo
            ? client.Brand.Logo.UploadAsync(new Dictionary<string, object?> { ["file"] = new FileUpload("logo.png", TestImages.Png, "image/png") })
            : client.Domains.UnsubscribePage.Logo.UploadAsync(Id, new Dictionary<string, object?> { ["file"] = new FileUpload("logo.png", TestImages.Png, "image/png") });

        await Upload();

        Assert.Equal(2, probe.Requests.Count);
        Assert.Equal(probe.Requests[0].Target, probe.Requests[1].Target);
        // A second form has its own boundary; the file part inside is the same bytes.
        AssertOneFilePart(probe.Requests[0], "logo.png", "image/png", TestImages.Png);
        AssertOneFilePart(probe.Requests[1], "logo.png", "image/png", TestImages.Png);
        Assert.Null(probe.Requests[0].Header("idempotency-key"));

        var failing = new Probe { Answer = _ => Probe.Reply(503, """{"statusCode":503,"name":"application_error","message":"Try again later."}""") };
        using var other = failing.Client();
        await Assert.ThrowsAsync<ApiException>(() => teamLogo
            ? other.Brand.Logo.UploadAsync(new Dictionary<string, object?> { ["file"] = new FileUpload("logo.png", TestImages.Png, "image/png") })
            : other.Domains.UnsubscribePage.Logo.UploadAsync(Id, new Dictionary<string, object?> { ["file"] = new FileUpload("logo.png", TestImages.Png, "image/png") }));
        Assert.Single(failing.Requests);

        static HttpResponseMessage RateLimited()
        {
            var response = Probe.Reply(429, """{"statusCode":429,"name":"rate_limit_exceeded","message":"Slow down"}""");
            response.Headers.TryAddWithoutValidation("retry-after", "0");
            return response;
        }
    }

    private static JsonObject UnsubscribePage(Action<JsonObject>? change = null)
    {
        var page = new JsonObject
        {
            ["brand_name"] = "Acme",
            ["message"] = "You will not get our newsletter any more.",
            ["success_message"] = "You are unsubscribed.",
            ["redirect_url"] = null,
            ["support_url"] = "https://acme.dev/help",
            ["powered_by"] = null,
            ["powered_by_effective"] = true,
            ["background_color"] = "#FFFFFF",
            ["text_color"] = "#111111",
            ["accent_color"] = "#2B44FF",
            ["logo_radius"] = "gentle",
            ["logo_url"] = null,
        };
        change?.Invoke(page);
        return page;
    }

    [Fact]
    public async Task ReadsTheUnsubscribePageSettingsOfADomainIncludingTheReadOnlyEffectiveFlag()
    {
        Mock.ReplyExample("GET /domains/{id}", domain => domain["unsubscribe_page"] = UnsubscribePage());
        Mock.ReplyExample("GET /domains/{id}", domain => domain["unsubscribe_page"] = UnsubscribePage(page =>
        {
            page["logo_url"] = "https://assets.billionsend.com/logos/dom-91ab.png?v=3";
            page["support_url"] = null;
            page["powered_by"] = false;
            page["powered_by_effective"] = true; // a Free team's choice to hide it is kept but not applied
        }));
        using var client = Client();

        var following = (await client.Domains.GetAsync(Id))!["unsubscribe_page"]!;
        var own = (await client.Domains.GetAsync(Id))!["unsubscribe_page"]!;

        Assert.Null(following["logo_url"]);
        Assert.Equal("https://acme.dev/help", (string?)following["support_url"]);
        Assert.Null(following["powered_by"]);
        Assert.True((bool)following["powered_by_effective"]!);
        Assert.Equal("https://assets.billionsend.com/logos/dom-91ab.png?v=3", (string?)own["logo_url"]);
        Assert.Null(own["support_url"]);
        Assert.False((bool)own["powered_by"]!);
        Assert.True((bool)own["powered_by_effective"]!);
    }

    [Fact]
    public async Task WritesTheSupportLinkAndPoweredByOfAnUnsubscribePageAndClearsThemWithNull()
    {
        Mock.ReplyExample("PATCH /domains/{id}");
        Mock.ReplyExample("PATCH /domains/{id}");
        using var client = Client();

        await client.Domains.UpdateAsync(Id, new { unsubscribe_page = new { support_url = "https://acme.dev/help", powered_by = false } });
        await client.Domains.UpdateAsync(Id, new
        {
            unsubscribe_page = new JsonObject { ["support_url"] = null, ["powered_by"] = null },
        });

        var hits = Mock.HitsFor("PATCH /domains/{id}");
        Assert.Equal("""{"unsubscribe_page":{"support_url":"https://acme.dev/help","powered_by":false}}""", hits[0].Body);
        Assert.Equal("""{"unsubscribe_page":{"support_url":null,"powered_by":null}}""", hits[1].Body);
    }

    [Fact]
    public async Task SendsTheDormantContactSwitchesOfABroadcastAndReadsThemBack()
    {
        Mock.ReplyExample("POST /broadcasts");
        Mock.ReplyExample("PATCH /broadcasts/{id}");
        Mock.ReplyExample("GET /broadcasts/{id}", broadcast =>
        {
            broadcast["dormant_check"] = false;
            broadcast["include_dormant"] = true;
        });
        Mock.ReplyExample("GET /broadcasts/{id}", broadcast =>
        {
            broadcast["dormant_check"] = true;
            broadcast["include_dormant"] = false;
        });
        using var client = Client();

        var create = Body("POST /broadcasts");
        create["dormant_check"] = false;
        create["include_dormant"] = true;
        await client.Broadcasts.CreateAsync(create);
        await client.Broadcasts.UpdateAsync(Id, new { dormant_check = true, include_dormant = false });
        var optedOut = await client.Broadcasts.GetAsync(Id);
        var defaults = await client.Broadcasts.GetAsync(Id);

        var created = Mock.HitsFor("POST /broadcasts").Single().Json!;
        Assert.False((bool)created["dormant_check"]!);
        Assert.True((bool)created["include_dormant"]!);
        Assert.Equal("""{"dormant_check":true,"include_dormant":false}""", Mock.HitsFor("PATCH /broadcasts/{id}").Single().Body);
        Assert.False((bool)optedOut!["dormant_check"]!);
        Assert.True((bool)optedOut["include_dormant"]!);
        Assert.True((bool)defaults!["dormant_check"]!);
        Assert.False((bool)defaults["include_dormant"]!);
    }
}
