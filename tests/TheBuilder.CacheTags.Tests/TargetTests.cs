using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NPoco;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.Tests;

/// <summary>Targets added in the backoffice: their rules, reading their app settings, and how they are kept and merged.</summary>
public sealed class TargetTests : IDisposable
{
    private static readonly string[] Types = ["webhook", "cloudflare", "fastly"];
    private const string Secret = "a-signing-secret-of-thirty-two-c";

    private static TargetDefinition Webhook(params NamedValue[] headers) => new()
    {
        Name = "vercel",
        Type = "webhook",
        Url = "https://www.example.com/api/cache-tags",
        Secret = new TargetValue(Secret),
        Headers = headers,
    };

    private static TargetDefinition Cloudflare(params NamedValue[] settings) => new()
    {
        Name = "cdn",
        Type = "cloudflare",
        Settings = settings,
    };

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void A_complete_webhook_and_cloudflare_target_are_valid()
    {
        Assert.Null(TargetValidator.Validate(Webhook(new NamedValue("x-vercel-protection-bypass", Setting: "Vercel:Bypass")), Types));
        Assert.Null(TargetValidator.Validate(Cloudflare(new NamedValue("ZoneId", "023e105f4ecef8ad9ca31a8372d0c353"), new NamedValue("ApiToken", Setting: "Cloudflare:Token")), Types));
    }

    [Theory]
    [InlineData("Vercel")]
    [InlineData("-vercel")]
    [InlineData("vercel_prod")]
    [InlineData("")]
    public void A_name_is_lowercase_letters_digits_and_hyphens(string name)
        => Assert.NotNull(TargetValidator.Validate(Webhook() with { Name = name }, Types));

    [Fact]
    public void A_name_is_at_most_100_characters()
    {
        Assert.Null(TargetValidator.Validate(Webhook() with { Name = new string('a', 100) }, Types));
        Assert.NotNull(TargetValidator.Validate(Webhook() with { Name = new string('a', 101) }, Types));
    }

    [Fact]
    public void The_type_must_be_registered()
        => Assert.Equal("No target type 'akamai' is registered.", TargetValidator.Validate(Webhook() with { Type = "akamai" }, Types));

    [Theory]
    [InlineData("http://www.example.com/api/cache-tags", false)]
    [InlineData("http://localhost:3000/api/cache-tags", true)]
    [InlineData("/api/cache-tags", false)]
    [InlineData("ftp://www.example.com/", false)]
    public void A_webhook_url_is_https_or_http_on_localhost(string url, bool valid)
        => Assert.Equal(valid, TargetValidator.Validate(Webhook() with { Url = url }, Types) is null);

    [Fact]
    public void A_typed_signing_secret_is_at_least_32_characters()
    {
        Assert.Equal(
            "The signing secret must be at least 32 characters.",
            TargetValidator.Validate(Webhook() with { Secret = new TargetValue(Secret[..31]) }, Types));
        Assert.NotNull(TargetValidator.Validate(Webhook() with { Secret = null }, Types));
    }

    [Fact]
    public void A_value_is_typed_or_names_a_setting_not_both_and_not_neither()
    {
        Assert.NotNull(TargetValidator.Validate(Webhook(new NamedValue("x-api-key", "key", "Gateway:Key")), Types));
        Assert.NotNull(TargetValidator.Validate(Webhook(new NamedValue("x-api-key")), Types));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("content-type")]
    [InlineData("X-Cache-Tags-Signature")]
    [InlineData("x-cache-tags-timestamp")]
    [InlineData("User-Agent")]
    [InlineData("Content-Encoding")]
    public void A_header_the_purge_sets_itself_is_refused(string name)
        => Assert.Equal($"The request header '{name}' is set by the purge itself.", TargetValidator.Validate(Webhook(new NamedValue(name, "x")), Types));

    [Theory]
    [InlineData("x api key")]
    [InlineData("x-api-key:")]
    [InlineData("")]
    public void A_header_name_is_a_token(string name)
        => Assert.NotNull(TargetValidator.Validate(Webhook(new NamedValue(name, "x")), Types));

    [Fact]
    public void A_header_is_named_once_whatever_its_case()
        => Assert.Equal(
            "The request header 'X-Api-Key' is there twice.",
            TargetValidator.Validate(Webhook(new NamedValue("x-api-key", "a"), new NamedValue("X-Api-Key", "b")), Types));

    [Fact]
    public void A_typed_header_value_is_one_line_of_at_most_1000_characters()
    {
        Assert.Null(TargetValidator.Validate(Webhook(new NamedValue("x-api-key", new string('k', 1000))), Types));
        Assert.NotNull(TargetValidator.Validate(Webhook(new NamedValue("x-api-key", new string('k', 1001))), Types));
        Assert.NotNull(TargetValidator.Validate(Webhook(new NamedValue("x-api-key", "key\r\nX-Injected: yes")), Types));
    }

    [Fact]
    public void A_target_sends_at_most_20_headers()
    {
        NamedValue[] Headers(int count) => Enumerable.Range(1, count).Select(i => new NamedValue($"x-header-{i}", "v")).ToArray();
        Assert.Null(TargetValidator.Validate(Webhook(Headers(20)), Types));
        Assert.NotNull(TargetValidator.Validate(Webhook(Headers(21)), Types));
    }

    [Fact]
    public void A_setting_name_has_only_characters_a_configuration_key_can_have()
    {
        Assert.Null(TargetValidator.Validate(Webhook(new NamedValue("x-api-key", Setting: "CacheTags__Gateway.Key-1")), Types));
        Assert.NotNull(TargetValidator.Validate(Webhook(new NamedValue("x-api-key", Setting: "sk_live 123")), Types));
    }

    [Fact]
    public void A_cloudflare_target_needs_its_zone_and_token_and_takes_no_headers()
    {
        Assert.Equal("A Cloudflare target needs the setting ApiToken.", TargetValidator.Validate(Cloudflare(new NamedValue("ZoneId", "zone")), Types));
        Assert.Equal(
            "A Cloudflare target sends no request headers.",
            TargetValidator.Validate(Cloudflare(new NamedValue("ZoneId", "zone"), new NamedValue("ApiToken", "token")) with { Headers = [new NamedValue("x-a", "b")] }, Types));
    }

    [Fact]
    public void A_webhook_takes_headers_rather_than_settings()
        => Assert.NotNull(TargetValidator.Validate(Webhook() with { Settings = [new NamedValue("Token", "x")] }, Types));

    [Fact]
    public void Keys_per_request_are_from_1_to_10000()
    {
        Assert.NotNull(TargetValidator.Validate(Webhook() with { MaxKeysPerRequest = 0 }, Types));
        Assert.Null(TargetValidator.Validate(Webhook() with { MaxKeysPerRequest = 10_000 }, Types));
        Assert.NotNull(TargetValidator.Validate(Webhook() with { MaxKeysPerRequest = 10_001 }, Types));
    }

    [Fact]
    public void A_configured_target_with_a_header_the_purge_sets_stops_startup_naming_the_target()
    {
        var options = new CacheTagsOptions();
        options.Targets["nextjs"] = new CacheTagsTargetOptions { Url = "https://www.example.com/", Secret = Secret, Headers = { ["Host"] = "elsewhere" } };

        Microsoft.Extensions.Options.ValidateOptionsResult result = new ConfiguredTargetHeaders().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal("CacheTags:Targets:nextjs: The request header 'Host' is set by the purge itself.", result.FailureMessage);
    }

    [Fact]
    public void Resolving_reads_each_named_app_setting_and_keeps_typed_values()
    {
        TargetDefinition target = Webhook(new NamedValue("x-api-key", Setting: "Gateway:Key"), new NamedValue("x-env", "production")) with
        {
            Secret = new TargetValue(Setting: "CacheTags:VercelSecret"),
        };

        ResolvedTarget resolved = TargetResolver.Resolve(target, Types, Configuration(("Gateway:Key", "k-123"), ("CacheTags:VercelSecret", Secret)), allowPrivateNetwork: false);

        Assert.Null(resolved.Problem);
        Assert.Equal(Secret, resolved.Options.Secret);
        Assert.Equal("k-123", resolved.Options.Headers["x-api-key"]);
        Assert.Equal("production", resolved.Options.Headers["x-env"]);
        Assert.True(resolved.Options.PublicNetworkOnly);
        Assert.False(TargetResolver.Resolve(target, Types, Configuration(("Gateway:Key", "k"), ("CacheTags:VercelSecret", Secret)), allowPrivateNetwork: true).Options.PublicNetworkOnly);
    }

    [Fact]
    public void A_missing_app_setting_is_a_problem_naming_the_header_and_never_the_setting()
    {
        TargetDefinition target = Webhook(new NamedValue("x-api-key", Setting: "sk_live_pasted_by_mistake"));

        ResolvedTarget resolved = TargetResolver.Resolve(target, Types, Configuration(), allowPrivateNetwork: false);

        Assert.Equal("The request header 'x-api-key' names an app setting that is not set on this server.", resolved.Problem);
        Assert.DoesNotContain("sk_live_pasted_by_mistake", resolved.Problem);
    }

    [Fact]
    public void A_stored_value_with_neither_a_value_nor_a_setting_is_a_problem_not_an_exception()
    {
        ResolvedTarget resolved = TargetResolver.Resolve(Webhook(new NamedValue("x-api-key")), Types, Configuration(), allowPrivateNetwork: false);

        Assert.NotNull(resolved.Problem);
    }

    [Fact]
    public void A_stored_row_this_version_cannot_read_is_a_target_with_a_problem()
    {
        _database.Execute("INSERT INTO theBuilderCacheTagsTarget (Name, Definition, CreatedUtc, UpdatedUtc) VALUES ('broken', '{not json', '1970-01-01', '1970-01-01')");
        TargetSql.Add(_database, Webhook(), DateTime.UnixEpoch);

        var stored = TargetSql.All(_database);
        ResolvedTarget broken = TargetResolver.Resolve(stored.Single(target => target.Name == "broken"), Types, Configuration(), allowPrivateNetwork: false);

        Assert.Equal(["broken", "vercel"], stored.Select(target => target.Name));
        Assert.Equal("The target's stored settings could not be read. Save it again.", broken.Problem);
    }

    [Theory]
    [InlineData("https://user:pass@hooks.example.com/purge?token=s3cr3t#x", "https://hooks.example.com/purge")]
    [InlineData("http://localhost:3000/api/cache-tags", "http://localhost:3000/api/cache-tags")]
    [InlineData("not a url", "")]
    public void A_configured_url_is_shown_without_its_query_or_credentials(string url, string shown)
        => Assert.Equal(shown, TheBuilder.CacheTags.ManagementApi.CacheTagsTargets.WithoutSecrets(url));

    [Fact]
    public void An_app_setting_with_a_line_break_is_a_problem_rather_than_a_second_header()
    {
        ResolvedTarget resolved = TargetResolver.Resolve(
            Webhook(new NamedValue("x-api-key", Setting: "Gateway:Key")),
            Types,
            Configuration(("Gateway:Key", "k\r\nX-Injected: yes")),
            allowPrivateNetwork: false);

        Assert.Equal("The request header 'x-api-key' reads an app setting with a line break in it.", resolved.Problem);
    }

    [Fact]
    public void A_signing_secret_setting_shorter_than_32_characters_is_a_problem()
    {
        ResolvedTarget resolved = TargetResolver.Resolve(
            Webhook() with { Secret = new TargetValue(Setting: "CacheTags:VercelSecret") },
            Types,
            Configuration(("CacheTags:VercelSecret", "short")),
            allowPrivateNetwork: false);

        Assert.Equal("The signing secret's app setting holds fewer than 32 characters.", resolved.Problem);
    }

    [Fact]
    public void The_webhook_sends_the_targets_headers_and_its_own_signature()
    {
        var request = new PurgeRequest { Id = Guid.Empty, ChangeId = Guid.Empty, TargetName = "vercel", Expire = ["content-a"], Stale = [] };

        using HttpRequestMessage message = WebhookTarget.Request(
            new Uri("https://www.example.com/api/cache-tags"),
            Secret,
            request,
            1792612800,
            new Dictionary<string, string> { ["x-vercel-protection-bypass"] = "bypass-1" });

        Assert.Equal(["bypass-1"], message.Headers.GetValues("x-vercel-protection-bypass"));
        Assert.Equal(
            [WebhookTarget.Sign(Secret, "POST", "/api/cache-tags", 1792612800, WebhookTarget.Body(request))],
            message.Headers.GetValues(WebhookTarget.SignatureHeader));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2002:a00:1::", false)]
    [InlineData("2001:db8::1", false)]
    public void Only_public_addresses_are_public(string address, bool isPublic)
        => Assert.Equal(isPublic, TargetNetwork.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void A_configured_target_wins_over_a_backoffice_one_of_the_same_name()
    {
        var configured = new Dictionary<string, CacheTagsTargetOptions> { ["Vercel"] = new() { Url = "https://configured.example.com/" } };
        var clashes = new List<string>();

        Dictionary<string, ResolvedTarget> merged = PurgeTargets.Merge(
            configured,
            [Webhook(), Webhook() with { Name = "preview" }],
            target => new ResolvedTarget(target.Name, new CacheTagsTargetOptions { Url = target.Url }, FromConfiguration: false),
            clashes.Add);

        Assert.Equal("https://configured.example.com/", merged["vercel"].Options.Url);
        Assert.True(merged["vercel"].FromConfiguration);
        Assert.False(merged["preview"].FromConfiguration);
        Assert.Equal(["vercel"], clashes);
    }

    [Fact]
    public void A_target_with_a_problem_still_gets_its_purges_queued_so_the_failure_shows()
    {
        var targets = new[]
        {
            new ResolvedTarget("vercel", new CacheTagsTargetOptions(), FromConfiguration: false, Problem: "The request header 'x-api-key' names an app setting that is not set on this server."),
            new ResolvedTarget("paused", new CacheTagsTargetOptions { Enabled = false }, FromConfiguration: false),
        };

        Assert.Equal(["vercel"], NewPurge.ToTargets(targets, ["content-a"], DateTime.UnixEpoch).Select(purge => purge.Target));
    }

    // The store's SQL, against SQLite.
    private readonly SqliteConnection _connection;
    private readonly Database _database;

    public TargetTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _database = new Database(_connection, DatabaseType.SQLite);
        _database.Execute("""
            CREATE TABLE theBuilderCacheTagsTarget (Name TEXT PRIMARY KEY, Definition TEXT NOT NULL, CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
            CREATE TABLE theBuilderCacheTagsOutbox (Id TEXT PRIMARY KEY, EventId TEXT NOT NULL, Target TEXT NOT NULL, Keys TEXT NOT NULL,
                State TEXT NOT NULL, Attempts INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, NotBeforeUtc TEXT NOT NULL, SentUtc TEXT NULL,
                LastError TEXT NULL, LeaseOwner TEXT NULL, LeaseUntilUtc TEXT NULL);
            """);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void A_stored_target_reads_back_with_its_headers_and_a_name_is_added_once()
    {
        TargetDefinition target = Webhook(new NamedValue("x-api-key", Setting: "Gateway:Key"));

        Assert.True(TargetSql.Add(_database, target, DateTime.UnixEpoch));
        Assert.False(TargetSql.Add(_database, target with { Url = "https://other.example.com/" }, DateTime.UnixEpoch));

        TargetDefinition stored = Assert.Single(TargetSql.All(_database));
        Assert.Equal("https://www.example.com/api/cache-tags", stored.Url);
        Assert.Equal(new NamedValue("x-api-key", Setting: "Gateway:Key"), Assert.Single(stored.Headers));
        Assert.Equal(new TargetValue(Secret), stored.Secret);
    }

    [Fact]
    public void Updating_replaces_the_target_and_updating_a_missing_one_does_nothing()
    {
        TargetSql.Add(_database, Webhook(), DateTime.UnixEpoch);

        Assert.True(TargetSql.Update(_database, Webhook() with { Enabled = false }, DateTime.UnixEpoch));
        Assert.False(TargetSql.Update(_database, Webhook() with { Name = "missing" }, DateTime.UnixEpoch));
        Assert.False(Assert.Single(TargetSql.All(_database)).Enabled);
    }

    [Fact]
    public void Deleting_a_target_sets_aside_its_unsent_purges_and_no_others()
    {
        TargetSql.Add(_database, Webhook(), DateTime.UnixEpoch);
        void Purge(string target, string state) => _database.Insert(new OutboxRow { Id = Guid.NewGuid(), EventId = Guid.NewGuid(), Target = target, State = state, CreatedUtc = DateTime.UnixEpoch, NotBeforeUtc = DateTime.UnixEpoch });
        Purge("vercel", OutboxStates.Pending);
        Purge("vercel", OutboxStates.Dead);
        Purge("vercel", OutboxStates.Sent);
        Purge("nextjs", OutboxStates.Pending);

        Assert.True(TargetSql.Delete(_database, "vercel"));
        Assert.False(TargetSql.Delete(_database, "vercel"));

        Assert.Empty(TargetSql.All(_database));
        var states = _database.Fetch<OutboxRow>("SELECT * FROM theBuilderCacheTagsOutbox").Select(row => $"{row.Target}:{row.State}").Order().ToList();
        Assert.Equal(["nextjs:Pending", "vercel:Discarded", "vercel:Discarded", "vercel:Sent"], states);
    }

    [Fact]
    public void Deleting_a_target_a_configured_one_shadows_keeps_the_purges_the_configured_one_owns()
    {
        TargetSql.Add(_database, Webhook(), DateTime.UnixEpoch);
        _database.Insert(new OutboxRow { Id = Guid.NewGuid(), EventId = Guid.NewGuid(), Target = "vercel", State = OutboxStates.Pending, CreatedUtc = DateTime.UnixEpoch, NotBeforeUtc = DateTime.UnixEpoch });

        Assert.True(TargetSql.Delete(_database, "vercel", keepPurges: true));

        Assert.Empty(TargetSql.All(_database));
        Assert.Equal(OutboxStates.Pending, _database.ExecuteScalar<string>("SELECT State FROM theBuilderCacheTagsOutbox"));
    }
}
