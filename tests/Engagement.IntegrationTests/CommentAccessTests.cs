using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text.Json;
using TikTokFeed.Engagement.Application.Abstractions.Repositories;
using TikTokFeed.Engagement.Application.Abstractions.Services;
using TikTokFeed.Engagement.Domain.Entities;
using TikTokFeed.Engagement.Infrastructure.Persistence;
using TikTokFeed.Engagement.Infrastructure.Persistence.Repositories;
using TikTokFeed.Engagement.IntegrationTests.Infrastructure;
using Xunit;

namespace TikTokFeed.Engagement.IntegrationTests;

public sealed class CommentAccessTests : IClassFixture<EngagementTestFactory>
{
    private readonly EngagementTestFactory _factory;

    public CommentAccessTests(EngagementTestFactory factory) => _factory = factory;

    [Theory]
    [InlineData("PENDING", true)]
    [InlineData("PENDING", false)]
    [InlineData("REJECTED", true)]
    [InlineData("REJECTED", false)]
    [InlineData("PRIVATE", false)]
    [InlineData("MISSING", false)]
    [InlineData("ANONYMOUS", false)]
    public async Task Comments_Denied_DoesNotReadRepository(string scenario, bool isOwner)
    {
        var owner = Guid.NewGuid();
        VideoView video = _factory.Content.Add(
            owner,
            scenario is "PENDING" or "REJECTED" ? scenario : "APPROVED",
            isPublic: scenario != "PRIVATE");
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            EngagementDbContext db = scope.ServiceProvider.GetRequiredService<EngagementDbContext>();
            db.Comments.Add(new Comment(video.VideoId, owner, "private content", null));
            await db.SaveChangesAsync();
        }

        int reads = 0;
        using var app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICommentRepository>();
            services.AddScoped<ICommentRepository>(provider => new ObservedCommentRepository(
                provider.GetRequiredService<EngagementDbContext>(), () => reads++));
        }));
        using HttpClient client = app.CreateClient();
        if (scenario != "ANONYMOUS")
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", TestTokens.Issue(isOwner ? owner : Guid.NewGuid(), "reader"));
        }

        Guid videoId = scenario == "MISSING" ? Guid.NewGuid() : video.VideoId;
        HttpResponseMessage response = await client.GetAsync($"/api/v1/videos/{videoId}/comments");

        HttpStatusCode expected = scenario switch
        {
            "MISSING" => HttpStatusCode.NotFound,
            "ANONYMOUS" => HttpStatusCode.Unauthorized,
            _ => HttpStatusCode.Forbidden,
        };
        response.StatusCode.Should().Be(expected);
        string code = scenario switch
        {
            "MISSING" => "NOT_FOUND",
            "ANONYMOUS" => "UNAUTHORIZED",
            "PRIVATE" => "FORBIDDEN",
            _ => "VIDEO_NOT_APPROVED",
        };
        (await TestJson.ErrorCode(response)).Should().Be(code);
        reads.Should().Be(0);
    }

    [Theory]
    [InlineData(true, "stranger")]
    [InlineData(false, "owner")]
    [InlineData(false, "follower")]
    public async Task Comments_Allowed_PreservesResponseAndReplies(bool isPublic, string role)
    {
        var owner = Guid.NewGuid();
        Guid viewer = role == "owner" ? owner : Guid.NewGuid();
        if (role == "follower")
        {
            _factory.Identity.Following[viewer] = new List<Guid> { owner };
        }

        VideoView video = _factory.Content.AddApproved(owner, isPublic);
        using HttpClient author = _factory.CreateClientFor(owner);
        string url = $"/api/v1/videos/{video.VideoId}/comments";
        HttpResponseMessage parent = await author.PostAsync(url, TestJson.Body(new { comment_text = "parent" }));
        parent.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonElement parentJson = await TestJson.Root(parent);
        Guid parentId = parentJson.GetProperty("comment_id").GetGuid();
        HttpResponseMessage reply = await author.PostAsync(
            url,
            TestJson.Body(new { comment_text = "reply", parent_comment_id = parentId }));
        reply.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonElement replyJson = await TestJson.Root(reply);

        using HttpClient client = _factory.CreateClientFor(viewer);
        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement comments = await TestJson.Root(response);
        comments.GetArrayLength().Should().Be(2);
        comments[0].GetProperty("comment_id").GetGuid().Should().Be(parentId);
        comments[0].GetProperty("replies_count").GetInt32().Should().Be(1);
        comments[1].GetProperty("replies_count").GetInt32().Should().Be(0);
        foreach (JsonProperty property in parentJson.EnumerateObject())
        {
            if (property.Name != "replies_count")
            {
                comments[0].GetProperty(property.Name).ToString().Should().Be(property.Value.ToString());
            }
        }

        comments[1].ToString().Should().Be(replyJson.ToString());
    }

    [Fact]
    public async Task Comments_AccessibleEmptyVideo_ReturnsEmptyArray()
    {
        VideoView video = _factory.Content.AddApproved(Guid.NewGuid());
        using HttpClient client = _factory.CreateClientFor(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync($"/api/v1/videos/{video.VideoId}/comments");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await TestJson.Root(response)).GetArrayLength().Should().Be(0);
    }

    private sealed class ObservedCommentRepository : CommentRepository, ICommentRepository
    {
        private readonly Action _onRead;

        public ObservedCommentRepository(EngagementDbContext context, Action onRead)
            : base(context) => _onRead = onRead;

        Task<IReadOnlyList<Comment>> ICommentRepository.GetByVideoAsync(Guid videoId, CancellationToken cancellationToken)
        {
            _onRead();
            return GetByVideoAsync(videoId, cancellationToken);
        }
    }
}
